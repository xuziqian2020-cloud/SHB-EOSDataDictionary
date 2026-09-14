using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHB.EosDataDictionary.Models;
using SHB.EosDataDictionary.Services;

namespace SHB.EosDataDictionary.Tests
{
    /// <summary>XMZADD 20260901 验证结构发布权限、原子扫描、差异批次和公开证据边界。</summary>
    [TestClass]
    public sealed class StructurePublishServiceTests
    {
        /// <summary>XMZADD 20260901 验证非发布者既不能进入结构维护也不能绕过界面直接发布。</summary>
        [TestMethod]
        public async Task NonPublisher_CannotOpenOrPublish()
        {
            var client = new StubGitHubDictionaryClient("100", new[] { "200" });
            string sourceRoot = CreateSourceRoot();
            try
            {
                StructurePublishService service = CreateService(client, CreateSnapshot(false), CreateSnapshot(true));
                StructurePublishRequest request = CreateRequest(sourceRoot, null);

                bool canOpen = await service.CanOpenAsync(CancellationToken.None);
                StructurePublishResult result = await service.PublishAsync(request, CancellationToken.None);

                Assert.IsFalse(canOpen);
                Assert.AreEqual(StructurePublishOutcome.NotAuthorized, result.Outcome);
                Assert.AreEqual(0, client.IssueCreateCount);
            }
            finally
            {
                Directory.Delete(sourceRoot, true);
            }
        }

        /// <summary>XMZADD 20260901 验证发布者可进入首次配置窗口，但连接或源码不完整时不能扫描发布。</summary>
        [TestMethod]
        public async Task PublisherMissingLocalConfiguration_CanOpenButCannotPublish()
        {
            var client = new StubGitHubDictionaryClient("100", new[] { "100" });
            StructurePublishService service = CreateService(client, CreateSnapshot(false), CreateSnapshot(true));
            var request = new StructurePublishRequest();

            Assert.IsTrue(await service.CanOpenAsync(CancellationToken.None));
            Assert.IsFalse(await service.CanPublishAsync(request, CancellationToken.None));
            StructurePublishResult result = await service.PublishAsync(request, CancellationToken.None);
            Assert.AreEqual(StructurePublishOutcome.NotConfigured, result.Outcome);
            Assert.AreEqual(0, client.IssueCreateCount);
        }

        /// <summary>XMZADD 20260901 验证元数据或源码扫描失败不会发布半成品，也不会改写上一份规范快照。</summary>
        [TestMethod]
        public async Task ScanFailure_DoesNotCreateIssueOrMutatePreviousSnapshot()
        {
            var client = new StubGitHubDictionaryClient("100", new[] { "100" });
            SnapshotData previous = CreateSnapshot(false);
            string originalName = previous.Tables[0].ObjectName;
            string sourceRoot = CreateSourceRoot();
            try
            {
                var service = new StructurePublishService(
                    client,
                    delegate(string scopeKey) { return previous; },
                    delegate(ConnectionProfile profile, CancellationToken cancellationToken)
                    {
                        throw new IOException("fake metadata failure");
                    },
                    delegate(string root, CancellationToken cancellationToken)
                    {
                        return new List<SourceEvidence>();
                    });

                StructurePublishResult result = await service.PublishAsync(
                    CreateRequest(sourceRoot, null), CancellationToken.None);

                Assert.AreEqual(StructurePublishOutcome.ScanFailed, result.Outcome);
                Assert.AreEqual(0, client.IssueCreateCount);
                Assert.AreEqual(originalName, previous.Tables[0].ObjectName);
                Assert.AreEqual(1, previous.Tables.Count);
            }
            finally
            {
                Directory.Delete(sourceRoot, true);
            }
        }

        /// <summary>XMZADD 20260902 验证源码读取失败会携带安全的来源阶段，供结构维护窗口提示用户调整源码目录而不泄露异常详情。</summary>
        [TestMethod]
        public async Task SourceAnalysisFailure_ReportsSourceFailureStage()
        {
            var client = new StubGitHubDictionaryClient("100", new[] { "100" });
            string sourceRoot = CreateSourceRoot();
            try
            {
                var service = new StructurePublishService(
                    client,
                    delegate(string scopeKey) { return CreateSnapshot(false); },
                    delegate(ConnectionProfile profile, CancellationToken cancellationToken) { return CreateSnapshot(true); },
                    delegate(string root, CancellationToken cancellationToken)
                    {
                        throw new InvalidDataException("源码总量超过扫描边界。");
                    });

                StructurePublishResult result = await service.PreviewAsync(
                    CreateRequest(sourceRoot, null), CancellationToken.None);
                System.Reflection.PropertyInfo failureStage = typeof(StructurePublishResult).GetProperty("FailureStage");

                Assert.AreEqual(StructurePublishOutcome.ScanFailed, result.Outcome);
                Assert.IsNotNull(failureStage);
                Assert.AreEqual("Source", failureStage.GetValue(result, null).ToString());
            }
            finally
            {
                Directory.Delete(sourceRoot, true);
            }
        }

        /// <summary>XMZADD 20260902 验证结构预览按阶段报告进度，使界面能说明正在处理的本机只读步骤。</summary>
        [TestMethod]
        public async Task Preview_ReportsReadOnlyScanStages()
        {
            var client = new StubGitHubDictionaryClient("100", new[] { "100" });
            string sourceRoot = CreateSourceRoot();
            try
            {
                var progress = new RecordingScanProgress();
                StructurePublishService service = CreateService(client, CreateSnapshot(false), CreateSnapshot(true));

                await service.PreviewAsync(CreateRequest(sourceRoot, null), CancellationToken.None, progress);

                Assert.IsTrue(progress.Contains(StructureScanStage.PreviousSnapshot));
                Assert.IsTrue(progress.Contains(StructureScanStage.DatabaseMetadata));
                Assert.IsTrue(progress.Contains(StructureScanStage.SourceEvidence));
                Assert.IsTrue(progress.Contains(StructureScanStage.MetadataEnrichment));
                Assert.IsTrue(progress.Contains(StructureScanStage.NameInference));
                Assert.IsTrue(progress.Contains(StructureScanStage.DifferenceAnalysis));
            }
            finally
            {
                Directory.Delete(sourceRoot, true);
            }
        }

        /// <summary>XMZADD 20260902 验证空远程仓库把超过 Issue 上限的首次结构转换为一次完整快照提交。</summary>
        [TestMethod]
        public async Task EmptyRemote_LargeInitialStructure_UsesOneFullSnapshotCommit()
        {
            var client = new StubGitHubDictionaryClient("100", new[] { "100" });
            client.Manifest = new SnapshotManifest
            {
                FormatVersion = 1,
                Revision = 0,
                SnapshotPath = "snapshot/latest.json.gz",
                GeneratedAtUtc = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            };
            string sourceRoot = CreateSourceRoot();
            try
            {
                StructurePublishService service = CreateService(
                    client,
                    new SnapshotData(),
                    CreateFullSnapshot(30));

                StructurePublishResult preview = await service.PreviewAsync(
                    CreateRequest(sourceRoot, null), CancellationToken.None);

                Assert.AreEqual(StructurePublishOutcome.ReadyToPublish, preview.Outcome);
                Assert.IsTrue(preview.IsFullSnapshot);
                Assert.AreEqual(30, preview.FullSnapshotTableCount);
                Assert.AreEqual(30, preview.FullSnapshotFieldCount);
                Assert.IsTrue(preview.FullSnapshotCompressedBytes > 0L);
                Assert.IsNull(preview.Batch);
                Assert.AreEqual(0, client.IssueCreateCount);

                StructurePublishResult published = await service.PublishPreviewAsync(
                    preview, CancellationToken.None);

                Assert.AreEqual(StructurePublishOutcome.Published, published.Outcome);
                Assert.AreEqual(1, client.FullSnapshotPublishCount);
                Assert.AreEqual(0, client.IssueCreateCount);
                Assert.AreEqual(new string('f', 40), published.CommitSha);
            }
            finally
            {
                Directory.Delete(sourceRoot, true);
            }
        }

        /// <summary>XMZADD 20260902 验证首次完整公开快照计数包含空表、技术表、人工排除表和扫描时未分类表，仅排除视图。</summary>
        [TestMethod]
        public async Task EmptyRemote_CompletePublicSnapshot_CountsEveryUserTableCategory()
        {
            var client = new StubGitHubDictionaryClient("100", new[] { "100" });
            client.Manifest = new SnapshotManifest
            {
                FormatVersion = 1,
                Revision = 0L,
                SnapshotPath = "snapshot/latest.json.gz",
                GeneratedAtUtc = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            };
            SnapshotData current = new SnapshotData
            {
                RefreshedAt = new DateTime(2026, 9, 2, 8, 0, 0, DateTimeKind.Utc)
            };
            TableMetadata emptyTable = CreateScannedTable("T_EMPTY_ORDER", 0L, true);
            emptyTable.Category = DictionaryTableCategory.Business;
            current.Tables.Add(emptyTable);
            TableMetadata technicalTable = CreateScannedTable("T_TECH_CACHE", 10L, true);
            technicalTable.Category = DictionaryTableCategory.Technical;
            current.Tables.Add(technicalTable);
            TableMetadata manuallyExcludedTable = CreateScannedTable("T_MANUAL_EXCLUDED", 10L, true);
            manuallyExcludedTable.Category = DictionaryTableCategory.Excluded;
            current.Tables.Add(manuallyExcludedTable);
            current.Tables.Add(CreateScannedTable("T_UNCLASSIFIED", 10L, true));
            TableMetadata view = CreateScannedTable("V_ORDER", 10L, true);
            view.ObjectType = "VIEW";
            current.Tables.Add(view);
            string sourceRoot = CreateSourceRoot();
            try
            {
                StructurePublishService service = CreateService(client, new SnapshotData(), current);

                StructurePublishResult preview = await service.PreviewAsync(
                    CreateRequest(sourceRoot, null), CancellationToken.None);

                Assert.AreEqual(StructurePublishOutcome.ReadyToPublish, preview.Outcome);
                Assert.IsTrue(preview.IsFullSnapshot);
                Assert.AreEqual(4, preview.FullSnapshotTableCount);
                Assert.AreEqual(4, preview.FullSnapshotFieldCount);
            }
            finally
            {
                Directory.Delete(sourceRoot, true);
            }
        }

        /// <summary>XMZADD 20260902 验证完整公开快照的无字段表错误会返回结构读取不完整原因，不向界面泄漏内部异常。</summary>
        [TestMethod]
        public async Task CompletePublicSnapshot_TableWithoutFields_ReturnsValidationFailure()
        {
            var client = new StubGitHubDictionaryClient("100", new[] { "100" });
            SnapshotData current = new SnapshotData();
            current.Tables.Add(CreateScannedTable("T_NO_FIELDS", 10L, false));
            string sourceRoot = CreateSourceRoot();
            try
            {
                StructurePublishService service = CreateService(client, new SnapshotData(), current);

                StructurePublishResult result = await service.PreviewAsync(
                    CreateRequest(sourceRoot, null), CancellationToken.None);

                Assert.AreEqual(StructurePublishOutcome.ValidationFailed, result.Outcome);
                Assert.AreEqual(StructurePublishValidationReason.FullSnapshotIncomplete, result.ValidationReason);
                Assert.AreEqual(0, client.IssueCreateCount);
                Assert.AreEqual(0, client.FullSnapshotPublishCount);
            }
            finally
            {
                Directory.Delete(sourceRoot, true);
            }
        }

        /// <summary>XMZADD 20260902 验证公开预览在元数据表集合缺失时直接返回完整快照无效，不得归一化为无变化。</summary>
        [TestMethod]
        public async Task Preview_MetadataTablesMissing_ReturnsFullSnapshotInvalid()
        {
            var client = new StubGitHubDictionaryClient("100", new[] { "100" });
            var current = new SnapshotData { Tables = null };
            string sourceRoot = CreateSourceRoot();
            try
            {
                StructurePublishService service = CreateService(client, new SnapshotData(), current);

                StructurePublishResult result = await service.PreviewAsync(
                    CreateRequest(sourceRoot, null), CancellationToken.None);

                Assert.AreEqual(StructurePublishOutcome.ValidationFailed, result.Outcome);
                Assert.AreEqual(StructurePublishValidationReason.FullSnapshotInvalid, result.ValidationReason);
                Assert.AreEqual(0, client.IssueCreateCount);
                Assert.AreEqual(0, client.FullSnapshotPublishCount);
            }
            finally
            {
                Directory.Delete(sourceRoot, true);
            }
        }

        /// <summary>XMZADD 20260902 验证公开裁剪保持固定InvalidDataException契约，并通过内部异常保留发布分类依据。</summary>
        [TestMethod]
        public void CompletePublicSnapshot_TableWithoutFields_PreservesPublicExceptionContract()
        {
            SnapshotData current = new SnapshotData();
            current.Tables.Add(CreateScannedTable("T_NO_FIELDS", 10L, false));

            InvalidDataException exception = Assert.ThrowsException<InvalidDataException>(delegate
            {
                new SnapshotPruningService().PruneCompletePublicSnapshot(current);
            });

            Assert.AreEqual("完整公开快照中的用户表没有可用字段。", exception.Message);
            Assert.IsNotNull(exception.InnerException);
            Assert.AreEqual("IncompletePublicSnapshotException", exception.InnerException.GetType().Name);
            Assert.IsInstanceOfType(exception.InnerException, typeof(IOException));
        }

        /// <summary>XMZADD 20260902 验证完整快照确认前远程已初始化时拒绝覆盖并要求重新扫描。</summary>
        [TestMethod]
        public async Task FullSnapshotPreview_RemoteRevisionChanged_DoesNotPublish()
        {
            var client = new StubGitHubDictionaryClient("100", new[] { "100" });
            client.Manifest = new SnapshotManifest
            {
                FormatVersion = 1,
                Revision = 0,
                SnapshotPath = "snapshot/latest.json.gz",
                GeneratedAtUtc = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            };
            string sourceRoot = CreateSourceRoot();
            try
            {
                StructurePublishService service = CreateService(client, new SnapshotData(), CreateFullSnapshot(30));
                StructurePublishResult preview = await service.PreviewAsync(
                    CreateRequest(sourceRoot, null), CancellationToken.None);
                client.Manifest = new SnapshotManifest
                {
                    FormatVersion = 1,
                    Revision = 1,
                    SnapshotSha256 = new string('a', 64),
                    SnapshotPath = "snapshot/revisions/000000001-" + new string('a', 64) + ".json.gz",
                    GeneratedAtUtc = DateTime.UtcNow
                };

                StructurePublishResult result = await service.PublishPreviewAsync(
                    preview, CancellationToken.None);

                Assert.AreEqual(StructurePublishOutcome.ValidationFailed, result.Outcome);
                Assert.AreEqual(StructurePublishValidationReason.RemoteSnapshotChanged, result.ValidationReason);
                Assert.AreEqual(0, client.FullSnapshotPublishCount);
            }
            finally
            {
                Directory.Delete(sourceRoot, true);
            }
        }

        /// <summary>XMZADD 20260902 验证扫描后即使清单仍为 Revision 0，只要远程分支头变化也不得覆盖其内容。</summary>
        [TestMethod]
        public async Task FullSnapshotPreview_RemoteHeadChangedAtRevisionZero_DoesNotPublish()
        {
            var client = new StubGitHubDictionaryClient("100", new[] { "100" });
            client.Manifest = new SnapshotManifest
            {
                FormatVersion = 1,
                Revision = 0L,
                SnapshotPath = "snapshot/latest.json.gz",
                GeneratedAtUtc = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            };
            client.HeadSha = new string('a', 40);
            string sourceRoot = CreateSourceRoot();
            try
            {
                StructurePublishService service = CreateService(client, new SnapshotData(), CreateFullSnapshot(30));
                StructurePublishResult preview = await service.PreviewAsync(
                    CreateRequest(sourceRoot, null), CancellationToken.None);
                client.HeadSha = new string('b', 40);

                StructurePublishResult result = await service.PublishPreviewAsync(
                    preview, CancellationToken.None);

                Assert.AreEqual(StructurePublishOutcome.ValidationFailed, result.Outcome);
                Assert.AreEqual(StructurePublishValidationReason.RemoteSnapshotChanged, result.ValidationReason);
                Assert.AreEqual(0, client.FullSnapshotPublishCount);
            }
            finally
            {
                Directory.Delete(sourceRoot, true);
            }
        }

        /// <summary>XMZADD 20260902 验证首次完整快照超过旧一万张表阈值时仍可完整预览且保留准确表数。</summary>
        [TestMethod]
        public async Task EmptyRemote_MoreThanTenThousandTables_PreparesFullSnapshot()
        {
            var client = new StubGitHubDictionaryClient("100", new[] { "100" });
            client.Manifest = new SnapshotManifest
            {
                FormatVersion = 1,
                Revision = 0,
                SnapshotPath = "snapshot/latest.json.gz",
                GeneratedAtUtc = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            };
            string sourceRoot = CreateSourceRoot();
            try
            {
                StructurePublishService service = CreateService(
                    client,
                    new SnapshotData(),
                    CreateFullSnapshot(10001));

                StructurePublishResult result = await service.PreviewAsync(
                    CreateRequest(sourceRoot, null), CancellationToken.None);

                Assert.AreEqual(StructurePublishOutcome.ReadyToPublish, result.Outcome);
                Assert.IsTrue(result.IsFullSnapshot);
                Assert.AreEqual(10001, result.FullSnapshotTableCount);
                Assert.AreEqual(10001, result.FullSnapshotFieldCount);
                Assert.IsTrue(result.FullSnapshotCompressedBytes > 0L);
                Assert.AreEqual(0, client.FullSnapshotPublishCount);
            }
            finally
            {
                Directory.Delete(sourceRoot, true);
            }
        }

        /// <summary>XMZADD 20260902 验证完整快照编码只把固定压缩容量异常映射为 GitHub 单文件超限原因。</summary>
        [TestMethod]
        public void FullSnapshotEncodingFailure_FixedCapacityError_UsesDedicatedReason()
        {
            MethodInfo mapper = typeof(StructurePublishService).GetMethod(
                "GetFullSnapshotEncodingFailureReason",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(mapper);

            var codec = new SnapshotCodec();
            SnapshotData snapshot = CreateFullSnapshot(1);
            snapshot.RefreshedAt = new DateTime(2026, 9, 2, 8, 0, 0, DateTimeKind.Utc);
            byte[] expectedContent = codec.Encode(snapshot);
            MethodInfo encodeWithLimit = typeof(SnapshotCodec).GetMethod(
                "Encode",
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                new[] { typeof(SnapshotData), typeof(int) },
                null);
            Assert.IsNotNull(encodeWithLimit);
            TargetInvocationException invocationException = Assert.ThrowsException<TargetInvocationException>(delegate
            {
                encodeWithLimit.Invoke(codec, new object[] { snapshot, expectedContent.Length - 1 });
            });
            var capacityException = invocationException.InnerException as SnapshotCompressedSizeExceededException;
            Assert.IsNotNull(capacityException);

            object sizeReason = mapper.Invoke(
                null,
                new object[] { capacityException });
            object invalidReason = mapper.Invoke(
                null,
                new object[] { new InvalidDataException("快照公开内容未通过安全校验。") });

            Assert.AreEqual(8, (int)StructurePublishValidationReason.FullSnapshotSizeExceeded);
            Assert.AreEqual(9, (int)StructurePublishValidationReason.FullSnapshotIncomplete);
            Assert.IsInstanceOfType(capacityException, typeof(IOException));
            Assert.AreEqual("SnapshotCompressedSizeExceededException", capacityException.GetType().Name);
            Assert.AreEqual("快照压缩内容超过安全大小限制。", capacityException.Message);
            Assert.AreEqual(StructurePublishValidationReason.FullSnapshotSizeExceeded, sizeReason);
            Assert.AreEqual(StructurePublishValidationReason.FullSnapshotInvalid, invalidReason);
        }

        /// <summary>XMZADD 20260902 验证完整快照预览缺少快照或表集合时保持格式无效原因，不误报容量边界。</summary>
        [TestMethod]
        public void FullSnapshotPreview_NullSnapshotOrTableCollection_UsesInvalidReason()
        {
            var client = new StubGitHubDictionaryClient("100", new[] { "100" });
            StructurePublishService service = CreateService(
                client,
                new SnapshotData(),
                new SnapshotData());
            MethodInfo creator = typeof(StructurePublishService).GetMethod(
                "CreateFullSnapshotPreview",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(creator);

            var snapshotWithoutTables = new SnapshotData { Tables = null };
            var nullSnapshotResult = creator.Invoke(
                service,
                new object[] { null, null, "100", new string('a', 40), false }) as StructurePublishResult;
            var nullTablesResult = creator.Invoke(
                service,
                new object[] { snapshotWithoutTables, null, "100", new string('a', 40), false }) as StructurePublishResult;

            Assert.IsNotNull(nullSnapshotResult);
            Assert.IsNotNull(nullTablesResult);
            Assert.AreEqual(StructurePublishValidationReason.FullSnapshotInvalid, nullSnapshotResult.ValidationReason);
            Assert.AreEqual(StructurePublishValidationReason.FullSnapshotInvalid, nullTablesResult.ValidationReason);
        }

        /// <summary>XMZADD 20260902 验证首次完整快照包含凭据赋值形态时在编码前拒绝公开发布。</summary>
        [TestMethod]
        public async Task EmptyRemote_SecretMetadata_DoesNotPrepareFullSnapshot()
        {
            var client = new StubGitHubDictionaryClient("100", new[] { "100" });
            client.Manifest = new SnapshotManifest
            {
                FormatVersion = 1,
                Revision = 0L,
                SnapshotPath = "snapshot/latest.json.gz",
                GeneratedAtUtc = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            };
            string sourceRoot = CreateSourceRoot();
            try
            {
                SnapshotData current = CreateFullSnapshot(1);
                current.Tables[0].Remark = new MetadataValue
                {
                    Value = "业务说明",
                    Description = "password=do-not-publish",
                    Evidence = new List<EvidenceItem>()
                };
                StructurePublishService service = CreateService(client, new SnapshotData(), current);

                StructurePublishResult result = await service.PreviewAsync(
                    CreateRequest(sourceRoot, null), CancellationToken.None);

                Assert.AreEqual(StructurePublishOutcome.ValidationFailed, result.Outcome);
                Assert.AreEqual(StructurePublishValidationReason.FullSnapshotInvalid, result.ValidationReason);
                Assert.AreEqual(0, client.FullSnapshotPublishCount);
            }
            finally
            {
                Directory.Delete(sourceRoot, true);
            }
        }

        /// <summary>XMZADD 20260902 验证完整快照会移除本机绝对证据路径与源码原文，只保留可公开的业务解释。</summary>
        [TestMethod]
        public async Task EmptyRemote_LocalEvidenceDetails_AreRemovedFromFullSnapshot()
        {
            var client = new StubGitHubDictionaryClient("100", new[] { "100" });
            client.Manifest = new SnapshotManifest
            {
                FormatVersion = 1,
                Revision = 0L,
                SnapshotPath = "snapshot/latest.json.gz",
                GeneratedAtUtc = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            };
            string sourceRoot = CreateSourceRoot();
            try
            {
                SnapshotData current = CreateFullSnapshot(1);
                current.Tables[0].Remark = new MetadataValue
                {
                    Value = "订单业务说明",
                    Evidence = new List<EvidenceItem>
                    {
                        new EvidenceItem
                        {
                            SourceType = "源码",
                            SourcePath = @"C:\Users\tester\OrderEntity.vb",
                            SourceLine = 12,
                            RuleName = "TestRule",
                            RawValue = "private source fragment",
                            OriginalText = "private original fragment",
                            Explanation = "订单业务说明来源"
                        }
                    }
                };
                StructurePublishService service = CreateService(client, new SnapshotData(), current);

                StructurePublishResult preview = await service.PreviewAsync(
                    CreateRequest(sourceRoot, null), CancellationToken.None);

                Assert.AreEqual(StructurePublishOutcome.ReadyToPublish, preview.Outcome);
                SnapshotData decoded = new SnapshotCodec().DecodeAndValidate(
                    preview.FullSnapshotContent,
                    preview.FullSnapshotManifest.SnapshotSha256);
                EvidenceItem evidence = decoded.Tables[0].Remark.Evidence[0];
                Assert.AreEqual(string.Empty, evidence.SourcePath);
                Assert.IsNull(evidence.RawValue);
                Assert.IsNull(evidence.OriginalText);
            }
            finally
            {
                Directory.Delete(sourceRoot, true);
            }
        }

        /// <summary>XMZADD 20260910 验证参考名称与使用模块的证据也会按公开快照规则移除本机路径和私密源码。</summary>
        [TestMethod]
        public async Task EmptyRemote_NewNameLayerEvidence_IsSanitizedInFullSnapshot()
        {
            var client = new StubGitHubDictionaryClient("100", new[] { "100" });
            client.Manifest = new SnapshotManifest
            {
                FormatVersion = 1,
                Revision = 0L,
                SnapshotPath = "snapshot/latest.json.gz",
                GeneratedAtUtc = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            };
            string sourceRoot = CreateSourceRoot();
            try
            {
                SnapshotData current = CreateFullSnapshot(1);
                TableMetadata table = current.Tables[0];
                FieldMetadata field = table.Fields[0];
                table.SuggestedChineseName = CreatePrivateNameValue("订单参考名");
                table.AlternativeChineseNames.Add(CreatePrivateNameValue("订单单据"));
                table.UsedByModules.Add(CreatePrivateNameValue("销售模块"));
                field.SuggestedChineseName = CreatePrivateNameValue("订单内码参考名");
                field.AlternativeChineseNames.Add(CreatePrivateNameValue("订单主键"));
                StructurePublishService service = CreateService(client, new SnapshotData(), current);

                StructurePublishResult preview = await service.PreviewAsync(
                    CreateRequest(sourceRoot, null), CancellationToken.None);

                Assert.AreEqual(StructurePublishOutcome.ReadyToPublish, preview.Outcome);
                SnapshotData decoded = new SnapshotCodec().DecodeAndValidate(
                    preview.FullSnapshotContent,
                    preview.FullSnapshotManifest.SnapshotSha256);
                TableMetadata decodedTable = decoded.Tables[0];
                FieldMetadata decodedField = decodedTable.Fields[0];
                MetadataValue[] publishedValues =
                {
                    decodedTable.SuggestedChineseName,
                    decodedTable.AlternativeChineseNames[0],
                    decodedTable.UsedByModules[0],
                    decodedField.SuggestedChineseName,
                    decodedField.AlternativeChineseNames[0]
                };
                for (int index = 0; index < publishedValues.Length; index++)
                {
                    Assert.IsNotNull(publishedValues[index]);
                    Assert.AreEqual(string.Empty, publishedValues[index].Evidence[0].SourcePath);
                    Assert.IsNull(publishedValues[index].Evidence[0].RawValue);
                    Assert.IsNull(publishedValues[index].Evidence[0].OriginalText);
                }
            }
            finally
            {
                Directory.Delete(sourceRoot, true);
            }
        }

        /// <summary>XMZADD 20260910 验证参考名称与使用模块证据中的凭据形态不能绕过完整快照公开校验。</summary>
        [TestMethod]
        public async Task EmptyRemote_NewNameLayerEvidenceCredential_ReturnsValidationFailure()
        {
            var client = new StubGitHubDictionaryClient("100", new[] { "100" });
            client.Manifest = new SnapshotManifest
            {
                FormatVersion = 1,
                Revision = 0L,
                SnapshotPath = "snapshot/latest.json.gz",
                GeneratedAtUtc = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            };
            string sourceRoot = CreateSourceRoot();
            try
            {
                for (int caseIndex = 0; caseIndex < 7; caseIndex++)
                {
                    SnapshotData current = CreateFullSnapshot(1);
                    MetadataValue unsafeValue = CreatePrivateNameValue("待审阅名称");
                    unsafeValue.Evidence[0].SourcePath = "Order/OrderEntity.vb";
                    unsafeValue.Evidence[0].RawValue = null;
                    unsafeValue.Evidence[0].OriginalText = null;
                    unsafeValue.Evidence[0].Explanation = "Pwd=fake-test-only";
                    if (caseIndex == 0)
                    {
                        current.Tables[0].SuggestedChineseName = unsafeValue;
                    }
                    else if (caseIndex == 1)
                    {
                        current.Tables[0].AlternativeChineseNames.Add(unsafeValue);
                    }
                    else if (caseIndex == 2)
                    {
                        current.Tables[0].UsedByModules.Add(unsafeValue);
                    }
                    else if (caseIndex == 3)
                    {
                        current.Tables[0].Fields[0].SuggestedChineseName = unsafeValue;
                    }
                    else if (caseIndex == 4)
                    {
                        current.Tables[0].Fields[0].AlternativeChineseNames.Add(unsafeValue);
                    }
                    else if (caseIndex == 5)
                    {
                        current.Tables[0].RejectedSuggestionFingerprints.Add("Pwd=fake-test-only");
                    }
                    else
                    {
                        current.Tables[0].Fields[0].RejectedSuggestionFingerprints.Add("Pwd=fake-test-only");
                    }
                    StructurePublishService service = CreateService(client, new SnapshotData(), current);

                    StructurePublishResult preview = await service.PreviewAsync(
                        CreateRequest(sourceRoot, null), CancellationToken.None);

                    Assert.AreEqual(StructurePublishOutcome.ValidationFailed, preview.Outcome,
                        "参考层位置未通过公开凭据校验，测试索引：" + caseIndex);
                    Assert.AreEqual(StructurePublishValidationReason.FullSnapshotInvalid, preview.ValidationReason);
                }
            }
            finally
            {
                Directory.Delete(sourceRoot, true);
            }
        }

        /// <summary>XMZADD 20260902 验证仓库初始化后仍严格执行普通 Issue 的操作上限，不把大型增量误作完整快照。</summary>
        [TestMethod]
        public async Task ExistingRemote_LargeIncrement_RemainsIssueLimited()
        {
            var client = new StubGitHubDictionaryClient("100", new[] { "100" });
            client.Manifest = new SnapshotManifest
            {
                FormatVersion = 1,
                Revision = 1L,
                SnapshotSha256 = new string('a', 64),
                SnapshotPath = "snapshot/revisions/000000001-" + new string('a', 64) + ".json.gz",
                GeneratedAtUtc = DateTime.UtcNow
            };
            string sourceRoot = CreateSourceRoot();
            try
            {
                StructurePublishService service = CreateService(
                    client,
                    CreateFullSnapshot(1),
                    CreateFullSnapshot(101));

                StructurePublishResult result = await service.PreviewAsync(
                    CreateRequest(sourceRoot, null), CancellationToken.None);

                Assert.AreEqual(StructurePublishOutcome.ValidationFailed, result.Outcome);
                Assert.AreEqual(StructurePublishValidationReason.OperationLimitExceeded, result.ValidationReason);
                Assert.AreEqual(0, client.FullSnapshotPublishCount);
                Assert.AreEqual(0, client.IssueCreateCount);
            }
            finally
            {
                Directory.Delete(sourceRoot, true);
            }
        }

        /// <summary>XMZADD 20260901 验证一次结构扫描只创建一个强类型批次，且源码绝对根目录不会进入公开证据。</summary>
        [TestMethod]
        public async Task ChangedStructure_CreatesOneIssueWithTypedOperationsAndRelativeEvidence()
        {
            var client = new StubGitHubDictionaryClient("100", new[] { "100" });
            string sourceRoot = CreateSourceRoot();
            try
            {
                SnapshotData current = CreateSnapshot(true);
                var sourceEvidence = new List<SourceEvidence>
                {
                    new SourceEvidence
                    {
                        ObjectName = "T_ORDER",
                        FieldName = "FNAME",
                        ChineseNameCandidate = "订单名称",
                        Evidence = new EvidenceItem
                        {
                            SourceType = "EOS源码",
                            SourcePath = Path.Combine(sourceRoot, "Order", "OrderEntity.vb"),
                            SourceLine = 12,
                            RuleName = "EntityProperty",
                            Explanation = "实体属性中文摘要"
                        }
                    }
                };
                StructurePublishService service = CreateService(client, CreateSnapshot(false), current, sourceEvidence);

                StructurePublishResult result = await service.PublishAsync(
                    CreateRequest(sourceRoot, null), CancellationToken.None);

                Assert.AreEqual(StructurePublishOutcome.Published, result.Outcome);
                Assert.AreEqual(1, client.IssueCreateCount);
                Assert.AreSame(client.LastBatch, result.Batch);
                Assert.IsTrue(ContainsOperation(client.LastBatch, "AddField"));
                Assert.IsTrue(ContainsOperation(client.LastBatch, "Set"));
                string json = DictionaryJsonSerializer.SerializeBatch(client.LastBatch);
                Assert.IsFalse(json.Contains(sourceRoot));
                Assert.IsFalse(json.Contains("fake-password"));
                Assert.AreEqual("Order/OrderEntity.vb", FindEvidencePath(client.LastBatch));
            }
            finally
            {
                Directory.Delete(sourceRoot, true);
            }
        }

        /// <summary>XMZADD 20260911 验证来源强度和业务用途经过发布安全净化后仍提供给正式推断链路。</summary>
        [TestMethod]
        public async Task Preview_SourceEvidenceClassification_SurvivesSanitization()
        {
            var client = new StubGitHubDictionaryClient("100", new[] { "100" });
            string sourceRoot = CreateSourceRoot();
            try
            {
                var inputEvidence = new List<SourceEvidence>
                {
                    new SourceEvidence
                    {
                        ObjectName = "T_ORDER",
                        FieldName = "FNAME",
                        Strength = SourceEvidenceStrength.DirectBusinessCode,
                        UsageKind = SourceUsageKind.Write,
                        Evidence = new EvidenceItem
                        {
                            SourceType = "EOS业务源码",
                            SourcePath = Path.Combine(sourceRoot, "Order", "OrderEntity.vb"),
                            SourceLine = 12,
                            RuleName = "EntityFieldAssignment",
                            Explanation = "业务写入订单名称"
                        }
                    }
                };
                var inference = new CapturingStructureNameInference();
                var service = new StructurePublishService(
                    client,
                    delegate(string scopeKey) { return CreateSnapshot(false); },
                    delegate(ConnectionProfile profile, CancellationToken cancellationToken) { return CreateSnapshot(true); },
                    delegate(string root, CancellationToken cancellationToken) { return inputEvidence; },
                    inference);

                await service.PreviewAsync(CreateRequest(sourceRoot, null), CancellationToken.None);

                Assert.IsNotNull(inference.SourceEvidence);
                Assert.AreEqual(1, inference.SourceEvidence.Count);
                Assert.AreEqual(SourceEvidenceStrength.DirectBusinessCode, inference.SourceEvidence[0].Strength);
                Assert.AreEqual(SourceUsageKind.Write, inference.SourceEvidence[0].UsageKind);
                Assert.AreEqual("Order/OrderEntity.vb", inference.SourceEvidence[0].Evidence.SourcePath);
            }
            finally
            {
                Directory.Delete(sourceRoot, true);
            }
        }

        /// <summary>XMZADD 20260901 验证扫描预览不会创建 Issue，只有确认发布预览后才提交唯一批次。</summary>
        [TestMethod]
        public async Task PreviewThenPublish_CreatesIssueOnlyAfterConfirmation()
        {
            var client = new StubGitHubDictionaryClient("100", new[] { "100" });
            string sourceRoot = CreateSourceRoot();
            try
            {
                StructurePublishService service = CreateService(client, CreateSnapshot(false), CreateSnapshot(true));

                StructurePublishResult preview = await service.PreviewAsync(
                    CreateRequest(sourceRoot, null), CancellationToken.None);

                Assert.AreEqual(StructurePublishOutcome.ReadyToPublish, preview.Outcome);
                Assert.AreEqual(0, client.IssueCreateCount);
                Assert.IsNotNull(preview.Diff);
                Assert.IsNotNull(preview.Batch);

                StructurePublishResult published = await service.PublishPreviewAsync(preview, CancellationToken.None);

                Assert.AreEqual(StructurePublishOutcome.Published, published.Outcome);
                Assert.AreEqual(1, client.IssueCreateCount);
            }
            finally
            {
                Directory.Delete(sourceRoot, true);
            }
        }

        /// <summary>XMZADD 20260901 验证远程授权等待期间外部修改预览对象不会改变用户已确认的实际发布批次。</summary>
        [TestMethod]
        public async Task PublishPreview_ConcurrentPreviewMutation_PublishesBoundPayload()
        {
            var client = new StubGitHubDictionaryClient("100", new[] { "100" });
            string sourceRoot = CreateSourceRoot();
            try
            {
                StructurePublishService service = CreateService(client, CreateSnapshot(false), CreateSnapshot(true));
                StructurePublishResult preview = await service.PreviewAsync(
                    CreateRequest(sourceRoot, null), CancellationToken.None);
                client.OnSecondIdentityRead = delegate { preview.Batch.Operations.Clear(); };

                StructurePublishResult published = await service.PublishPreviewAsync(preview, CancellationToken.None);

                Assert.AreEqual(StructurePublishOutcome.Published, published.Outcome);
                Assert.AreEqual(1, client.IssueCreateCount);
                Assert.IsTrue(client.LastBatch.Operations.Count > 0);
            }
            finally
            {
                Directory.Delete(sourceRoot, true);
            }
        }

        /// <summary>XMZADD 20260901 验证物理结构完全一致时不创建空 Issue。</summary>
        [TestMethod]
        public async Task NoChanges_DoesNotCreateIssue()
        {
            var client = new StubGitHubDictionaryClient("100", new[] { "100" });
            string sourceRoot = CreateSourceRoot();
            try
            {
                StructurePublishService service = CreateService(client, CreateSnapshot(true), CreateSnapshot(true));

                StructurePublishResult result = await service.PublishAsync(
                    CreateRequest(sourceRoot, null), CancellationToken.None);

                Assert.AreEqual(StructurePublishOutcome.NoChanges, result.Outcome);
                Assert.AreEqual(0, client.IssueCreateCount);
            }
            finally
            {
                Directory.Delete(sourceRoot, true);
            }
        }

        /// <summary>XMZADD 20260901 验证关系差异转换为带完整父子端点的强类型结构事件。</summary>
        [TestMethod]
        public async Task RelationAdded_CreatesTypedRelationOperation()
        {
            var client = new StubGitHubDictionaryClient("100", new[] { "100" });
            string sourceRoot = CreateSourceRoot();
            try
            {
                SnapshotData previous = CreateSnapshot(true);
                SnapshotData current = CreateSnapshot(true);
                AddCustomerTable(previous, false);
                AddCustomerTable(current, true);
                StructurePublishService service = CreateService(client, previous, current);

                StructurePublishResult result = await service.PublishAsync(
                    CreateRequest(sourceRoot, null), CancellationToken.None);

                Assert.AreEqual(StructurePublishOutcome.Published, result.Outcome);
                DictionaryChangeOperation relation = FindOperation(result.Batch, "AddRelation");
                Assert.IsNotNull(relation);
                Assert.AreEqual("FK_ORDER_CUSTOMER", relation.RelationPayload.ForeignKeyName);
                Assert.AreEqual("T_ORDER", relation.RelationPayload.ChildTableName);
            }
            finally
            {
                Directory.Delete(sourceRoot, true);
            }
        }

        /// <summary>XMZADD 20260901 验证知识库精确条目参与字段翻译，缺失知识库则标记降级但仍可发布物理变化。</summary>
        [TestMethod]
        public async Task KnowledgeBaseAvailable_TranslatesField_AndMissingRootDegrades()
        {
            var client = new StubGitHubDictionaryClient("100", new[] { "100" });
            string sourceRoot = CreateSourceRoot();
            string knowledgeRoot = CreateKnowledgeBase();
            try
            {
                StructurePublishService available = CreateService(client, CreateSnapshot(false), CreateSnapshot(true));
                StructurePublishResult translated = await available.PublishAsync(
                    CreateRequest(sourceRoot, knowledgeRoot), CancellationToken.None);

                Assert.AreEqual(StructurePublishOutcome.Published, translated.Outcome);
                Assert.IsFalse(translated.KnowledgeBaseDegraded);
                Assert.IsTrue(ContainsNewValue(translated.Batch, "订单名称"));

                var missingClient = new StubGitHubDictionaryClient("100", new[] { "100" });
                StructurePublishService missing = CreateService(missingClient, CreateSnapshot(false), CreateSnapshot(true));
                StructurePublishResult degraded = await missing.PublishAsync(
                    CreateRequest(sourceRoot, Path.Combine(sourceRoot, "missing-kb")), CancellationToken.None);

                Assert.AreEqual(StructurePublishOutcome.Published, degraded.Outcome);
                Assert.IsTrue(degraded.KnowledgeBaseDegraded);
                Assert.AreEqual(1, missingClient.IssueCreateCount);
            }
            finally
            {
                Directory.Delete(sourceRoot, true);
                Directory.Delete(knowledgeRoot, true);
            }
        }

        /// <summary>XMZADD 20260901 验证根目录外证据不能通过相似前缀或父目录跳转进入结构 Issue。</summary>
        [TestMethod]
        public async Task EvidenceOutsideSelectedRoot_IsRejectedBeforeIssueCreation()
        {
            var client = new StubGitHubDictionaryClient("100", new[] { "100" });
            string sourceRoot = CreateSourceRoot();
            string outside = sourceRoot + "-outside";
            Directory.CreateDirectory(outside);
            try
            {
                var evidence = new List<SourceEvidence>
                {
                    new SourceEvidence
                    {
                        ObjectName = "T_ORDER",
                        FieldName = "FNAME",
                        ChineseNameCandidate = "订单名称",
                        Evidence = new EvidenceItem
                        {
                            SourceType = "EOS源码",
                            SourcePath = Path.Combine(outside, "secret.vb"),
                            SourceLine = 1,
                            RuleName = "EntityProperty",
                            Explanation = "password=fake-password"
                        }
                    }
                };
                StructurePublishService service = CreateService(client, CreateSnapshot(false), CreateSnapshot(true), evidence);

                StructurePublishResult result = await service.PublishAsync(
                    CreateRequest(sourceRoot, null), CancellationToken.None);

                Assert.AreEqual(StructurePublishOutcome.ValidationFailed, result.Outcome);
                Assert.AreEqual(0, client.IssueCreateCount);
            }
            finally
            {
                Directory.Delete(sourceRoot, true);
                Directory.Delete(outside, true);
            }
        }

        /// <summary>XMZADD 20260902 验证公开增量保留空表和技术表，同时继续发布批准分类规则产生的分类结果。</summary>
        [TestMethod]
        public async Task UnclassifiedDatabaseScan_PublishesCompleteUserTableScopeAfterClassification()
        {
            var client = new StubGitHubDictionaryClient("100", new[] { "100" });
            string sourceRoot = CreateSourceRoot();
            try
            {
                SnapshotData previous = CreateSnapshot(false);
                SnapshotData current = new SnapshotData();
                current.Tables.Add(CreateScannedTable("T_ORDER", 5L, true));
                TableMetadata newOrder = CreateScannedTable("T_NEW_ORDER", 5L, true);
                newOrder.KeepWhenEmpty = true;
                current.Tables.Add(newOrder);
                current.Tables.Add(CreateScannedTable("T_BD_MATERIAL", 5L, true));
                current.Tables.Add(CreateScannedTable("T_EMPTY_ORDER", 0L, true));
                current.Tables.Add(CreateScannedTable("T_LOG", 100L, true));
                current.Tables.Add(CreateScannedTable("T_BD_MATERIAL_LOG", 100L, true));
                current.Tables.Add(CreateScannedTable("TMP_IMPORT", 100L, true));
                StructurePublishService service = CreateService(client, previous, current);

                StructurePublishResult result = await service.PublishAsync(
                    CreateRequest(sourceRoot, null), CancellationToken.None);

                Assert.AreEqual(StructurePublishOutcome.Published, result.Outcome);
                Assert.IsFalse(ContainsOperationForObject(result.Batch, "RemoveTable", "dbo.T_ORDER"));
                Assert.IsTrue(ContainsOperationForObject(result.Batch, "AddTable", "dbo.T_NEW_ORDER"));
                Assert.IsTrue(ContainsSet(result.Batch, "dbo.T_NEW_ORDER", "Category", "Business"));
                Assert.IsTrue(ContainsOperationForObject(result.Batch, "AddTable", "dbo.T_BD_MATERIAL"));
                Assert.IsTrue(ContainsSet(result.Batch, "dbo.T_BD_MATERIAL", "Category", "BaseData"));
                Assert.IsTrue(ContainsSet(result.Batch, "dbo.T_NEW_ORDER", "KeepWhenEmpty", "True"));
                Assert.IsTrue(ContainsOperationForObject(result.Batch, "AddTable", "dbo.T_EMPTY_ORDER"));
                Assert.IsTrue(ContainsOperationForObject(result.Batch, "AddTable", "dbo.T_LOG"));
                Assert.IsTrue(ContainsOperationForObject(result.Batch, "AddTable", "dbo.T_BD_MATERIAL_LOG"));
                Assert.IsTrue(ContainsOperationForObject(result.Batch, "AddTable", "dbo.TMP_IMPORT"));
            }
            finally
            {
                Directory.Delete(sourceRoot, true);
            }
        }

        /// <summary>XMZADD 20260901 验证数据库扩展说明发布时携带公开数据库证据，重放后不会变成人工锁定值。</summary>
        [TestMethod]
        public async Task DatabaseDescriptionSet_PublishesDatabaseEvidenceAndRemainsAutomaticAfterApply()
        {
            var client = new StubGitHubDictionaryClient("100", new[] { "100" });
            string sourceRoot = CreateSourceRoot();
            try
            {
                SnapshotData previous = CreateSnapshot(true);
                SnapshotData current = CreateSnapshot(true);
                current.Tables[0].Fields[1].ChineseName = new MetadataValue
                {
                    Value = "订单名称",
                    Status = ConfidenceStatus.DatabaseEvidence,
                    SourceSummary = "SQL Server 扩展说明",
                    Evidence = new List<EvidenceItem>()
                };
                StructurePublishService service = CreateService(client, previous, current);

                StructurePublishResult result = await service.PublishAsync(
                    CreateRequest(sourceRoot, null), CancellationToken.None);

                DictionaryChangeOperation set = FindSet(result.Batch, "dbo.T_ORDER", "FNAME", "ChineseName");
                Assert.IsNotNull(set);
                Assert.IsNotNull(set.Evidence);
                Assert.AreEqual(1, set.Evidence.Count);
                Assert.AreEqual("数据库说明", set.Evidence[0].SourceType);
                Assert.AreEqual("SqlExtendedDescription", set.Evidence[0].RuleName);

                var applyService = new DictionaryEventApplyService();
                applyService.Apply(previous, set, 1L, "100", new List<string> { "100" });
                MetadataValue applied = previous.Tables[0].Fields[1].ChineseName;
                Assert.AreEqual(ConfidenceStatus.DatabaseEvidence, applied.Status);
                Assert.IsFalse(applied.IsManualOverride);
                Assert.IsFalse(applied.IsLocked);
            }
            finally
            {
                Directory.Delete(sourceRoot, true);
            }
        }

        /// <summary>XMZADD 20260901 验证既有表估算行数变化通过物理属性白名单增量发布，新表则继续使用 AddTable 初始载荷。</summary>
        [TestMethod]
        public async Task ApproximateRowCountChange_PublishesWhitelistedSetForExistingTable()
        {
            var client = new StubGitHubDictionaryClient("100", new[] { "100" });
            string sourceRoot = CreateSourceRoot();
            try
            {
                SnapshotData previous = CreateSnapshot(false);
                SnapshotData current = CreateSnapshot(false);
                current.Tables[0].ApproximateRowCount = 25L;
                StructurePublishService service = CreateService(client, previous, current);

                StructurePublishResult result = await service.PublishAsync(
                    CreateRequest(sourceRoot, null), CancellationToken.None);

                Assert.AreEqual(StructurePublishOutcome.Published, result.Outcome);
                Assert.IsTrue(ContainsSet(result.Batch, "dbo.T_ORDER", "ApproximateRowCount", "25"));
            }
            finally
            {
                Directory.Delete(sourceRoot, true);
            }
        }

        /// <summary>XMZADD 20260902 验证既有未分类表继续保留在公开增量范围内，且不会被新表命名规则改分类。</summary>
        [TestMethod]
        public async Task ExistingUnclassifiedTable_RemainsPublishedWithoutReclassification()
        {
            var client = new StubGitHubDictionaryClient("100", new[] { "100" });
            string sourceRoot = CreateSourceRoot();
            try
            {
                SnapshotData previous = CreateSnapshot(false);
                SnapshotData current = CreateSnapshot(false);
                previous.Tables[0].Category = DictionaryTableCategory.Unclassified;
                current.Tables[0].Category = DictionaryTableCategory.Unclassified;
                StructurePublishService service = CreateService(client, previous, current);

                StructurePublishResult result = await service.PublishAsync(
                    CreateRequest(sourceRoot, null), CancellationToken.None);

                Assert.AreEqual(StructurePublishOutcome.NoChanges, result.Outcome);
                Assert.IsNull(result.Batch);
            }
            finally
            {
                Directory.Delete(sourceRoot, true);
            }
        }

        /// <summary>XMZADD 20260901 验证后续知识模块损坏时前面已找到的候选也不会部分发布。</summary>
        [TestMethod]
        public async Task KnowledgeBaseLaterModuleFailure_DiscardsAllPendingTranslations()
        {
            var client = new StubGitHubDictionaryClient("100", new[] { "100" });
            string sourceRoot = CreateSourceRoot();
            string knowledgeRoot = CreateMixedKnowledgeRoot();
            try
            {
                SnapshotData previous = CreateTwoTableSnapshot();
                SnapshotData current = CreateTwoTableSnapshot();
                StructurePublishService service = CreateService(client, previous, current);

                StructurePublishResult result = await service.PublishAsync(
                    CreateRequest(sourceRoot, knowledgeRoot), CancellationToken.None);

                Assert.AreEqual(StructurePublishOutcome.NoChanges, result.Outcome);
                Assert.IsTrue(result.KnowledgeBaseDegraded);
                Assert.AreEqual(0, client.IssueCreateCount);
            }
            finally
            {
                Directory.Delete(sourceRoot, true);
                Directory.Delete(knowledgeRoot, true);
            }
        }

        /// <summary>XMZADD 20260901 验证结构发布流水线在裁剪前实际执行共享缩写推理，并保持自动名称为待人工确认的推测状态。</summary>
        [TestMethod]
        public async Task PublishPipeline_ConfirmedAbbreviationEnrichment_RemainsGuessed()
        {
            var client = new StubGitHubDictionaryClient("100", new[] { "100" });
            string sourceRoot = CreateSourceRoot();
            try
            {
                SnapshotData previous = CreateSnapshot(false);
                SnapshotData current = CreateSnapshot(false);
                previous.Tables[0].Fields[0].FieldName = "FABC_STATUS";
                current.Tables[0].Fields[0].FieldName = "FABC_STATUS";
                current.Tables[0].Fields[0].ChineseName = new MetadataValue
                {
                    Value = "ABC状态",
                    Status = ConfidenceStatus.Guessed,
                    Evidence = new List<EvidenceItem>()
                };
                current.Abbreviations.Add(new AbbreviationEntry
                {
                    Abbreviation = "ABC",
                    ChineseMeaning = "业务中心",
                    TableScope = "T_ORDER",
                    Status = ConfidenceStatus.Confirmed,
                    ConfidenceScore = 100,
                    Evidence = new List<AbbreviationEvidence>()
                });
                StructurePublishService service = CreateService(client, previous, current);

                StructurePublishResult result = await service.PublishAsync(
                    CreateRequest(sourceRoot, null), CancellationToken.None);

                Assert.AreEqual(StructurePublishOutcome.NoChanges, result.Outcome);
                Assert.AreEqual("业务中心状态", current.Tables[0].Fields[0].ChineseName.Value);
                Assert.AreEqual(ConfidenceStatus.Guessed, current.Tables[0].Fields[0].ChineseName.Status);
                Assert.AreEqual(0, client.IssueCreateCount);
            }
            finally
            {
                Directory.Delete(sourceRoot, true);
            }
        }

        /// <summary>XMZADD 20260909 验证结构发布扫描在源码未返回实体时保留上一规范的可信 EOS 实体映射。</summary>
        [TestMethod]
        public async Task PreviewPipeline_MissingCurrentEntity_RetainsPreviousEosEntity()
        {
            var client = new StubGitHubDictionaryClient("100", new[] { "100" });
            string sourceRoot = CreateSourceRoot();
            try
            {
                SnapshotData previous = CreateSnapshot(false);
                SnapshotData current = CreateSnapshot(false);
                previous.Tables[0].EntityName = new MetadataValue
                {
                    Value = "t_Order",
                    Status = ConfidenceStatus.CodeEvidence,
                    SourceSummary = "EOS 源码实体类",
                    Evidence = new List<EvidenceItem>
                    {
                        new EvidenceItem
                        {
                            SourcePath = "ERP/表-类定义/t_Order.vb",
                            RuleName = "TableNameProperty"
                        }
                    }
                };
                StructurePublishService service = CreateService(client, previous, current);

                await service.PreviewAsync(CreateRequest(sourceRoot, null), CancellationToken.None);

                Assert.IsNotNull(current.Tables[0].EntityName);
                Assert.AreEqual("t_Order", current.Tables[0].EntityName.Value);
                Assert.AreEqual(ConfidenceStatus.CodeEvidence, current.Tables[0].EntityName.Status);
            }
            finally
            {
                Directory.Delete(sourceRoot, true);
            }
        }

        /// <summary>XMZADD 20260901 创建使用内存读取器的结构发布服务，确保自动测试不访问真实网络或数据库。</summary>
        private static StructurePublishService CreateService(
            StubGitHubDictionaryClient client,
            SnapshotData previous,
            SnapshotData current,
            IList<SourceEvidence> evidence = null)
        {
            return new StructurePublishService(
                client,
                delegate(string scopeKey) { return previous; },
                delegate(ConnectionProfile profile, CancellationToken cancellationToken) { return current; },
                delegate(string root, CancellationToken cancellationToken)
                {
                    return evidence ?? new List<SourceEvidence>();
                });
        }

        /// <summary>XMZADD 20260902 收集服务发出的阶段值，避免测试依赖 WPF 同步上下文。</summary>
        private sealed class RecordingScanProgress : IProgress<StructureScanProgress>
        {
            private readonly List<StructureScanStage> _stages = new List<StructureScanStage>();

            /// <summary>XMZADD 20260902 保存本次已进入的只读扫描阶段。</summary>
            public void Report(StructureScanProgress value)
            {
                if (value != null)
                {
                    _stages.Add(value.Stage);
                }
            }

            /// <summary>XMZADD 20260902 判断流程是否进入指定阶段。</summary>
            public bool Contains(StructureScanStage stage)
            {
                for (int index = 0; index < _stages.Count; index++)
                {
                    if (_stages[index] == stage)
                    {
                        return true;
                    }
                }
                return false;
            }
        }

        /// <summary>XMZADD 20260911 捕获发布净化后的源码证据以验证正式推断入口的数据契约。</summary>
        private sealed class CapturingStructureNameInference : IStructureNameInference
        {
            public IList<SourceEvidence> SourceEvidence { get; private set; }

            /// <summary>XMZADD 20260911 保存结构发布传入的安全证据，不执行额外名称推断。</summary>
            public void Enrich(SnapshotData snapshot, IList<SourceEvidence> sourceEvidence,
                EosKnowledgeBaseTranslationService knowledgeBase, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SourceEvidence = sourceEvidence;
            }
        }

        /// <summary>XMZADD 20260901 创建只含测试占位地址的只读请求，避免任何正式库信息进入测试。</summary>
        private static StructurePublishRequest CreateRequest(string sourceRoot, string knowledgeRoot)
        {
            return new StructurePublishRequest
            {
                ScopeKey = "test|EOS",
                Profile = new ConnectionProfile
                {
                    Id = "test",
                    Name = ConnectionProfileStore.DefaultEosProductionProfileName,
                    Server = "127.0.0.1",
                    Database = "EOS_TEST",
                    AuthenticationMode = AuthenticationMode.SqlServer,
                    UserName = "test-user",
                    Password = "fake-password",
                    IsReadOnly = true
                },
                SourceRoot = sourceRoot,
                KnowledgeBaseRoot = knowledgeRoot
            };
        }

        /// <summary>XMZADD 20260901 创建结构变化前后的最小业务表快照。</summary>
        private static SnapshotData CreateSnapshot(bool includeNameField)
        {
            var table = new TableMetadata
            {
                ScopeKey = "test|EOS",
                SchemaName = "dbo",
                ObjectName = "T_ORDER",
                ObjectType = "TABLE",
                ApproximateRowCount = 1L,
                Category = DictionaryTableCategory.Business,
                Fields = new List<FieldMetadata>
                {
                    new FieldMetadata
                    {
                        OwnerTableName = "T_ORDER",
                        FieldName = "FID",
                        DataType = "int",
                        LengthText = "—",
                        IsRequired = true,
                        IsPrimaryKey = true
                    }
                },
                Relations = new List<RelationMetadata>()
            };
            if (includeNameField)
            {
                table.Fields.Add(new FieldMetadata
                {
                    OwnerTableName = "T_ORDER",
                    FieldName = "FNAME",
                    DataType = "nvarchar",
                    LengthText = "50"
                });
            }
            return new SnapshotData { Tables = new List<TableMetadata> { table } };
        }

        /// <summary>XMZADD 20260902 创建指定数量的非空业务表，用于覆盖首次完整快照和规范数量上限。</summary>
        private static SnapshotData CreateFullSnapshot(int tableCount)
        {
            var snapshot = new SnapshotData();
            for (int index = 0; index < tableCount; index++)
            {
                string objectName = "T_INITIAL_" + index.ToString("00000", System.Globalization.CultureInfo.InvariantCulture);
                var table = new TableMetadata
                {
                    ScopeKey = "test|EOS",
                    SchemaName = "dbo",
                    ObjectName = objectName,
                    ObjectType = "TABLE",
                    ApproximateRowCount = 1L,
                    Category = DictionaryTableCategory.Business
                };
                table.Fields.Add(new FieldMetadata
                {
                    OwnerTableName = objectName,
                    FieldName = "FID",
                    DataType = "int",
                    LengthText = "—",
                    IsRequired = true,
                    IsPrimaryKey = true
                });
                snapshot.Tables.Add(table);
            }
            return snapshot;
        }

        /// <summary>XMZADD 20260910 创建包含测试凭据和长源码正文的名称证据以验证公开净化边界。</summary>
        private static MetadataValue CreatePrivateNameValue(string value)
        {
            return new MetadataValue
            {
                Value = value,
                Status = ConfidenceStatus.CodeEvidence,
                Evidence = new List<EvidenceItem>
                {
                    new EvidenceItem
                    {
                        SourceType = "EOS源码",
                        SourcePath = @"C:\Users\tester\private\OrderEntity.vb",
                        RawValue = "Pwd=fake-test-only",
                        OriginalText = new string('源', 300),
                        Explanation = "名称来源"
                    }
                }
            };
        }

        /// <summary>XMZADD 20260901 创建两个既有业务表以验证跨模块知识翻译的原子应用。</summary>
        private static SnapshotData CreateTwoTableSnapshot()
        {
            var snapshot = new SnapshotData();
            snapshot.Tables.Add(CreateScannedTable("T_FIRST", 1L, true));
            snapshot.Tables.Add(CreateScannedTable("T_SECOND", 1L, true));
            for (int index = 0; index < snapshot.Tables.Count; index++)
            {
                snapshot.Tables[index].Category = DictionaryTableCategory.Business;
                snapshot.Tables[index].Fields[0].FieldName = "FNAME";
                snapshot.Tables[index].Fields[0].OwnerTableName = snapshot.Tables[index].ObjectName;
            }
            return snapshot;
        }

        /// <summary>XMZADD 20260901 创建先有效后损坏的双模块知识库以验证事务性降级。</summary>
        private static string CreateMixedKnowledgeRoot()
        {
            string root = Path.Combine(Path.GetTempPath(), "shb-kb-mixed-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string header =
                "| 顶级分类 | 子系统 | 表单 | 表单标识 | 表名称 | 实体名称 | 实体入口名 | 主键字段 | 字段数 | 物理字段数 | 引用表字段数 | 详情 |\r\n" +
                "| --- | --- | --- | --- | --- | --- | --- | --- | ---: | ---: | ---: | --- |\r\n";
            File.WriteAllText(Path.Combine(root, "全量表索引.md"), header +
                "| 采购 | 采购 | 第一 | `FIRST` | `T_FIRST` | 单据头 | `FIRST` | `FID` | 1 | 1 | 0 | [第一.md](./第一.md#entity-1) |\r\n" +
                "| 采购 | 采购 | 第二 | `SECOND` | `T_SECOND` | 单据头 | `SECOND` | `FID` | 1 | 1 | 0 | [第二.md](./第二.md#entity-2) |\r\n",
                Encoding.UTF8);
            File.WriteAllText(Path.Combine(root, "第一.md"),
                "<a id=\"entity-1\"></a>\r\n\r\n## T_FIRST — 单据头\r\n\r\n### 字段\r\n\r\n" +
                "| 标题 | 标识 | 实体属性 | 数据库字段 | 字段类型 |\r\n" +
                "| --- | --- | --- | --- | --- |\r\n" +
                "| 第一名称 | FNAME | NAME | FNAME | 文本 |\r\n",
                Encoding.UTF8);
            File.WriteAllText(Path.Combine(root, "第二.md"), "损坏模块，没有实体锚点", Encoding.UTF8);
            return root;
        }

        /// <summary>XMZADD 20260901 创建模拟 SqlServerMetadataReader 输出的未分类真实表。</summary>
        private static TableMetadata CreateScannedTable(string objectName, long rowCount, bool includeField)
        {
            var table = new TableMetadata
            {
                ScopeKey = "test|EOS",
                SchemaName = "dbo",
                ObjectName = objectName,
                ObjectType = "TABLE",
                ApproximateRowCount = rowCount,
                Category = DictionaryTableCategory.Unclassified,
                Fields = new List<FieldMetadata>(),
                Relations = new List<RelationMetadata>()
            };
            if (includeField)
            {
                table.Fields.Add(new FieldMetadata
                {
                    OwnerTableName = objectName,
                    FieldName = "FID",
                    DataType = "int",
                    LengthText = "—",
                    IsPrimaryKey = true
                });
            }
            return table;
        }

        /// <summary>XMZADD 20260901 为结构发布夹具补充客户表和可选订单外键关系。</summary>
        private static void AddCustomerTable(SnapshotData snapshot, bool includeRelation)
        {
            var customer = new TableMetadata
            {
                ScopeKey = "test|EOS",
                SchemaName = "dbo",
                ObjectName = "T_CUSTOMER",
                ObjectType = "TABLE",
                ApproximateRowCount = 1L,
                Category = DictionaryTableCategory.BaseData,
                Fields = new List<FieldMetadata>
                {
                    new FieldMetadata { OwnerTableName = "T_CUSTOMER", FieldName = "FID", DataType = "int", LengthText = "—", IsPrimaryKey = true }
                },
                Relations = new List<RelationMetadata>()
            };
            snapshot.Tables.Add(customer);
            if (!includeRelation)
            {
                return;
            }
            var relation = new RelationMetadata
            {
                ForeignKeyName = "FK_ORDER_CUSTOMER",
                ParentSchemaName = "dbo",
                ParentTableName = "T_CUSTOMER",
                ParentFieldName = "FID",
                ChildSchemaName = "dbo",
                ChildTableName = "T_ORDER",
                ChildFieldName = "FID"
            };
            snapshot.Tables[0].Relations.Add(relation);
            customer.Relations.Add(relation);
        }

        /// <summary>XMZADD 20260901 创建包含证据目标文件的临时源码目录。</summary>
        private static string CreateSourceRoot()
        {
            string root = Path.Combine(Path.GetTempPath(), "SHB_STRUCTURE_SOURCE_" + Guid.NewGuid().ToString("N"));
            string directory = Path.Combine(root, "Order");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "OrderEntity.vb"), "Public Class OrderEntity\r\nEnd Class", Encoding.UTF8);
            return root;
        }

        /// <summary>XMZADD 20260901 创建可由现有知识库解析器精确匹配的最小字段条目。</summary>
        private static string CreateKnowledgeBase()
        {
            string root = Path.Combine(Path.GetTempPath(), "SHB_STRUCTURE_KB_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string header = "| 顶级分类 | 子系统 | 表单 | 表单标识 | 表名称 | 实体名称 | 实体入口名 | 主键字段 | 字段数 | 物理字段数 | 引用表字段数 | 详情 |\r\n" +
                "| --- | --- | --- | --- | --- | --- | --- | --- | ---: | ---: | ---: | --- |\r\n";
            File.WriteAllText(Path.Combine(root, "全量表索引.md"), header +
                "| 销售 | 订单 | 订单 | `ORDER` | `T_ORDER` | 单据头 | `ORDER` | `FID` | 2 | 2 | 0 | [订单.md](./订单.md#entity-1) |\r\n", Encoding.UTF8);
            File.WriteAllText(Path.Combine(root, "订单.md"),
                "<a id=\"entity-1\"></a>\r\n\r\n## T_ORDER — 订单\r\n\r\n### 字段\r\n\r\n" +
                "| 标题 | 标识 | 实体属性 | 数据库字段 | 字段类型 | 引用对象 | 引用表 | 枚举类型内码 | 枚举名称 | 枚举项目 | 分表 |\r\n" +
                "| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |\r\n" +
                "| 订单名称 | FNAME | NAME | FNAME | 文本 | | | | | | |\r\n", Encoding.UTF8);
            return root;
        }

        /// <summary>XMZADD 20260901 在传统循环中查找指定结构事件类型。</summary>
        private static bool ContainsOperation(DictionaryChangeBatch batch, string changeKind)
        {
            for (int index = 0; index < batch.Operations.Count; index++)
            {
                if (batch.Operations[index].ChangeKind == changeKind)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>XMZADD 20260901 按对象键查找指定结构事件，验证裁剪结果没有误删或误加表。</summary>
        private static bool ContainsOperationForObject(DictionaryChangeBatch batch, string changeKind, string objectKey)
        {
            for (int index = 0; index < batch.Operations.Count; index++)
            {
                DictionaryChangeOperation operation = batch.Operations[index];
                if (operation.ChangeKind == changeKind &&
                    string.Equals(operation.ObjectKey, objectKey, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>XMZADD 20260901 验证指定对象分类或保留标记已作为 Set 事件发布。</summary>
        private static bool ContainsSet(DictionaryChangeBatch batch, string objectKey, string propertyName, string newValue)
        {
            return FindSet(batch, objectKey, string.Empty, propertyName, newValue) != null;
        }

        /// <summary>XMZADD 20260901 按对象、字段和属性定位赋值事件。</summary>
        private static DictionaryChangeOperation FindSet(
            DictionaryChangeBatch batch,
            string objectKey,
            string fieldKey,
            string propertyName,
            string newValue = null)
        {
            for (int index = 0; index < batch.Operations.Count; index++)
            {
                DictionaryChangeOperation operation = batch.Operations[index];
                if (operation.ChangeKind == "Set" &&
                    string.Equals(operation.ObjectKey, objectKey, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(operation.FieldKey ?? string.Empty, fieldKey ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(operation.PropertyName, propertyName, StringComparison.Ordinal) &&
                    (newValue == null || string.Equals(operation.NewValue, newValue, StringComparison.Ordinal)))
                {
                    return operation;
                }
            }
            return null;
        }

        /// <summary>XMZADD 20260901 查找指定类型的结构事件以断言类型化载荷。</summary>
        private static DictionaryChangeOperation FindOperation(DictionaryChangeBatch batch, string changeKind)
        {
            for (int index = 0; index < batch.Operations.Count; index++)
            {
                if (batch.Operations[index].ChangeKind == changeKind)
                {
                    return batch.Operations[index];
                }
            }
            return null;
        }

        /// <summary>XMZADD 20260901 在批次中查找字段翻译值，证明知识库结果进入发布载荷。</summary>
        private static bool ContainsNewValue(DictionaryChangeBatch batch, string value)
        {
            for (int index = 0; index < batch.Operations.Count; index++)
            {
                if (batch.Operations[index].NewValue == value)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>XMZADD 20260901 读取批次首条公开证据相对路径，避免 JSON 斜线转义影响业务断言。</summary>
        private static string FindEvidencePath(DictionaryChangeBatch batch)
        {
            for (int operationIndex = 0; operationIndex < batch.Operations.Count; operationIndex++)
            {
                IList<EvidenceItem> evidence = batch.Operations[operationIndex].Evidence;
                if (evidence != null && evidence.Count > 0)
                {
                    return evidence[0].SourcePath;
                }
            }
            return null;
        }

        /// <summary>XMZADD 20260901 以内存身份、发布者名单和 Issue 捕获替代真实 GitHub。</summary>
        private sealed class StubGitHubDictionaryClient : IGitHubDictionaryClient, IGitHubFullSnapshotPublisher
        {
            private readonly string _userId;
            private readonly IList<string> _publisherIds;

            /// <summary>XMZADD 20260901 初始化结构发布权限测试所需的不可变身份和名单。</summary>
            public StubGitHubDictionaryClient(string userId, IEnumerable<string> publisherIds)
            {
                _userId = userId;
                _publisherIds = new List<string>(publisherIds);
            }

            public int IssueCreateCount { get; private set; }
            public int FullSnapshotPublishCount { get; private set; }
            public string HeadSha { get; set; } = new string('a', 40);
            public DictionaryChangeBatch LastBatch { get; private set; }
            public SnapshotManifest Manifest { get; set; }
            public Action OnSecondIdentityRead { get; set; }
            private int _identityReadCount;

            public Task<SnapshotManifest> GetManifestAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(Manifest);
            }
            public Task<byte[]> DownloadSnapshotAsync(string relativePath, CancellationToken cancellationToken) { throw new InvalidOperationException(); }
            public Task<DictionaryRevisionPackage> DownloadRevisionAsync(long revision, CancellationToken cancellationToken) { throw new InvalidOperationException(); }

            /// <summary>XMZADD 20260901 返回结构权限测试的固定 GitHub 数字身份。</summary>
            public Task<GitHubUserIdentity> GetCurrentUserAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _identityReadCount++;
                if (_identityReadCount == 2 && OnSecondIdentityRead != null)
                {
                    OnSecondIdentityRead();
                }
                return Task.FromResult(new GitHubUserIdentity { GitHubUserId = _userId, Login = "test-user" });
            }

            public Task<GitHubUserIdentity> GetCurrentUserAsync(string candidateToken, CancellationToken cancellationToken) { throw new InvalidOperationException(); }

            /// <summary>XMZADD 20260901 返回结构权限测试的固定发布者名单副本。</summary>
            public Task<IList<string>> GetPublisherIdsAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult<IList<string>>(new List<string>(_publisherIds));
            }

            /// <summary>XMZADD 20260901 捕获结构批次并返回固定 Issue 编号。</summary>
            public Task<int> CreateDictionaryIssueAsync(DictionaryChangeBatch batch, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IssueCreateCount++;
                LastBatch = batch;
                return Task.FromResult(77);
            }

            /// <summary>XMZADD 20260903 结构发布测试不参与远端工作流确认。</summary>
            public Task<bool> AreDictionaryOperationsAppliedAsync(
                DictionaryChangeBatch batch,
                int issueNumber,
                long maximumRevision,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(false);
            }

            /// <summary>XMZADD 20260903 返回待生效状态以满足结构发布测试的三态客户端契约。</summary>
            public Task<DictionaryIssueApplyStatus> GetDictionaryOperationsApplyStatusAsync(
                DictionaryChangeBatch batch,
                int issueNumber,
                long maximumRevision,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(DictionaryIssueApplyStatus.Pending);
            }

            /// <summary>XMZADD 20260903 返回原 Issue 编号以满足结构发布测试的恢复客户端契约。</summary>
            public Task<int> RecreateRejectedDictionaryIssueAsync(
                DictionaryChangeBatch batch,
                int rejectedIssueNumber,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(rejectedIssueNumber);
            }

            /// <summary>XMZADD 20260902 返回与当前 Revision 0 清单绑定的测试分支头。</summary>
            public Task<string> GetRevisionZeroHeadShaAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(Manifest != null && Manifest.Revision == 0L ? HeadSha : null);
            }

            /// <summary>XMZADD 20260902 捕获首次完整快照发布并返回固定 commit SHA。</summary>
            public Task<string> PublishFullSnapshotAsync(
                byte[] compressedSnapshot,
                SnapshotManifest manifest,
                string expectedHeadSha,
                string expectedPublisherGitHubUserId,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                FullSnapshotPublishCount++;
                return Task.FromResult(new string('f', 40));
            }
        }
    }
}
