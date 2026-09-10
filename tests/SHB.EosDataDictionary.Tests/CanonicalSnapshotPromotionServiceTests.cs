using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHB.EosDataDictionary.Models;
using SHB.EosDataDictionary.Services;

namespace SHB.EosDataDictionary.Tests
{
    [TestClass]
    public sealed class CanonicalSnapshotPromotionServiceTests
    {
        /// <summary>XMZADD 20260909 验证提升服务用同一压缩字节生成 latest 与不可变修订，并保留最后真实事件路径。</summary>
        [TestMethod]
        public void Prepare_Revision10Candidate_WritesIdenticalRevision11Payloads()
        {
            string repositoryRoot = CreateRepositoryWithRevision10Manifest();
            try
            {
                SnapshotData candidate = CreateCandidateSnapshot(10L, false);
                string baseHash = ReadBaseHash(repositoryRoot);

                CanonicalSnapshotPromotionResult result = new CanonicalSnapshotPromotionService().Prepare(
                    repositoryRoot,
                    candidate,
                    10L,
                    baseHash,
                    new DateTime(2026, 9, 9, 0, 0, 0, DateTimeKind.Utc));

                CollectionAssert.AreEqual(
                    File.ReadAllBytes(result.LatestSnapshotPath),
                    File.ReadAllBytes(result.RevisionSnapshotPath));
                Assert.AreEqual(11L, result.Manifest.Revision);
                Assert.AreEqual(result.SnapshotSha256, result.Manifest.SnapshotSha256);
                Assert.AreEqual(result.SnapshotSha256,
                    new SnapshotCodec().ComputeSha256(File.ReadAllBytes(result.LatestSnapshotPath)));
                StringAssert.Contains(File.ReadAllText(Path.Combine(repositoryRoot, "snapshot", "manifest.json")),
                    "\"LastEventPath\":\"events/2026/09/08/241036946/last-event.json\"");
                StringAssert.Contains(File.ReadAllText(result.RevisionEventPath), "\"EventPaths\":[]");
                Assert.IsTrue(File.Exists(result.PromotionAuditPath));
            }
            finally
            {
                Directory.Delete(repositoryRoot, true);
            }
        }

        /// <summary>XMZADD 20260909 验证远程修订基线变化时提升立即拒绝且不产生 Revision 11 文件。</summary>
        [TestMethod]
        public void Prepare_BaseRevisionChanged_LeavesRepositoryUnchanged()
        {
            string repositoryRoot = CreateRepositoryWithRevision10Manifest();
            try
            {
                byte[] originalLatest = File.ReadAllBytes(Path.Combine(repositoryRoot, "snapshot", "latest.json.gz"));

                Assert.ThrowsException<InvalidOperationException>(delegate
                {
                    new CanonicalSnapshotPromotionService().Prepare(
                        repositoryRoot,
                        CreateCandidateSnapshot(10L, false),
                        9L,
                        ReadBaseHash(repositoryRoot),
                        DateTime.UtcNow);
                });

                CollectionAssert.AreEqual(originalLatest,
                    File.ReadAllBytes(Path.Combine(repositoryRoot, "snapshot", "latest.json.gz")));
                Assert.IsFalse(Directory.Exists(Path.Combine(repositoryRoot, "snapshot", "promotions")));
                Assert.IsFalse(File.Exists(Path.Combine(repositoryRoot, "events", "revisions", "000000011.json")));
            }
            finally
            {
                Directory.Delete(repositoryRoot, true);
            }
        }

        /// <summary>XMZADD 20260909 验证基线哈希参数或仓库载荷不一致时不允许生成新规范修订。</summary>
        [TestMethod]
        public void Prepare_BaseHashChanged_LeavesRepositoryUnchanged()
        {
            string repositoryRoot = CreateRepositoryWithRevision10Manifest();
            try
            {
                Assert.ThrowsException<InvalidOperationException>(delegate
                {
                    new CanonicalSnapshotPromotionService().Prepare(
                        repositoryRoot,
                        CreateCandidateSnapshot(10L, false),
                        10L,
                        new string('0', 64),
                        DateTime.UtcNow);
                });

                Assert.IsFalse(File.Exists(Path.Combine(repositoryRoot, "snapshot", "revisions",
                    "000000011-" + new string('0', 64) + ".json.gz")));
                Assert.IsFalse(Directory.Exists(Path.Combine(repositoryRoot, "snapshot", "promotions")));
            }
            finally
            {
                Directory.Delete(repositoryRoot, true);
            }
        }

        /// <summary>XMZADD 20260909 验证旧 latest 别名滞后时仍以 manifest 不可变修订为基线并在新修订中修复别名。</summary>
        [TestMethod]
        public void Prepare_LatestAliasIsStale_UsesManifestRevisionAndRepairsAlias()
        {
            string repositoryRoot = CreateRepositoryWithRevision10Manifest();
            try
            {
                string latestPath = Path.Combine(repositoryRoot, "snapshot", "latest.json.gz");
                SnapshotData staleSnapshot = CreateCandidateSnapshot(9L, false);
                File.WriteAllBytes(latestPath, new SnapshotCodec().Encode(staleSnapshot));
                string baseHash = ReadManifestRevisionHash(repositoryRoot);

                CanonicalSnapshotPromotionResult result = new CanonicalSnapshotPromotionService().Prepare(
                    repositoryRoot,
                    CreateCandidateSnapshot(10L, false),
                    10L,
                    baseHash,
                    DateTime.UtcNow);

                Assert.AreEqual(result.SnapshotSha256,
                    new SnapshotCodec().ComputeSha256(File.ReadAllBytes(latestPath)));
                CollectionAssert.AreEqual(
                    File.ReadAllBytes(latestPath),
                    File.ReadAllBytes(result.RevisionSnapshotPath));
            }
            finally
            {
                Directory.Delete(repositoryRoot, true);
            }
        }

        /// <summary>XMZADD 20260909 验证公开候选含本机绝对路径时原子拒绝，不覆盖当前 latest 或 manifest。</summary>
        [TestMethod]
        public void Prepare_PublicCandidateContainsAbsolutePath_LeavesRepositoryUnchanged()
        {
            string repositoryRoot = CreateRepositoryWithRevision10Manifest();
            try
            {
                string manifestPath = Path.Combine(repositoryRoot, "snapshot", "manifest.json");
                string originalManifest = File.ReadAllText(manifestPath);
                byte[] originalLatest = File.ReadAllBytes(Path.Combine(repositoryRoot, "snapshot", "latest.json.gz"));
                SnapshotData candidate = CreateCandidateSnapshot(10L, false);
                candidate.Tables[0].EntityName.Description = @"C:\private\dictionary-note.txt";

                Assert.ThrowsException<InvalidDataException>(delegate
                {
                    new CanonicalSnapshotPromotionService().Prepare(
                        repositoryRoot,
                        candidate,
                        10L,
                        ReadBaseHash(repositoryRoot),
                        DateTime.UtcNow);
                });

                Assert.AreEqual(originalManifest, File.ReadAllText(manifestPath));
                CollectionAssert.AreEqual(originalLatest,
                    File.ReadAllBytes(Path.Combine(repositoryRoot, "snapshot", "latest.json.gz")));
                Assert.IsFalse(Directory.Exists(Path.Combine(repositoryRoot, "snapshot", "promotions")));
                Assert.IsFalse(File.Exists(Path.Combine(repositoryRoot, "events", "revisions", "000000011.json")));
            }
            finally
            {
                Directory.Delete(repositoryRoot, true);
            }
        }

        /// <summary>XMZADD 20260909 验证本地审阅证据的原文和绝对路径在公开提升前被清理而不丢失规则摘要。</summary>
        [TestMethod]
        public void Prepare_LocalEvidenceDetails_SanitizesBeforePublicValidation()
        {
            string repositoryRoot = CreateRepositoryWithRevision10Manifest();
            try
            {
                SnapshotData candidate = CreateCandidateSnapshot(10L, true);

                CanonicalSnapshotPromotionResult result = new CanonicalSnapshotPromotionService().Prepare(
                    repositoryRoot,
                    candidate,
                    10L,
                    ReadBaseHash(repositoryRoot),
                    DateTime.UtcNow);

                SnapshotData published = new SnapshotCodec().DecodeAndValidate(
                    File.ReadAllBytes(result.RevisionSnapshotPath),
                    result.SnapshotSha256);
                EvidenceItem evidence = published.Tables[0].EntityName.Evidence[0];
                Assert.AreEqual(string.Empty, evidence.SourcePath);
                Assert.IsNull(evidence.RawValue);
                Assert.IsNull(evidence.OriginalText);
                Assert.AreEqual("TableNameProperty", evidence.RuleName);
                Assert.AreEqual("从 EOS 实体定义确认数据库对象。", evidence.Explanation);
            }
            finally
            {
                Directory.Delete(repositoryRoot, true);
            }
        }

        /// <summary>XMZADD 20260909 创建含真实 Revision 10 压缩载荷与 manifest 的隔离仓库。</summary>
        private static string CreateRepositoryWithRevision10Manifest()
        {
            string repositoryRoot = Path.Combine(Path.GetTempPath(), "shb-promotion-" + Guid.NewGuid().ToString("N"));
            string snapshotRoot = Path.Combine(repositoryRoot, "snapshot");
            string revisionRoot = Path.Combine(snapshotRoot, "revisions");
            Directory.CreateDirectory(revisionRoot);
            Directory.CreateDirectory(Path.Combine(repositoryRoot, "events", "revisions"));

            SnapshotData baseSnapshot = CreateCandidateSnapshot(10L, false);
            byte[] payload = new SnapshotCodec().Encode(baseSnapshot);
            string hash = new SnapshotCodec().ComputeSha256(payload);
            File.WriteAllBytes(Path.Combine(snapshotRoot, "latest.json.gz"), payload);
            File.WriteAllBytes(Path.Combine(revisionRoot, "000000010-" + hash + ".json.gz"), payload);
            string manifest = "{\"FormatVersion\":1," +
                "\"GeneratedAtUtc\":\"/Date(1788833318000)/\"," +
                "\"LastEventPath\":\"events/2026/09/08/241036946/last-event.json\"," +
                "\"Revision\":10," +
                "\"SnapshotPath\":\"snapshot/revisions/000000010-" + hash + ".json.gz\"," +
                "\"SnapshotSha256\":\"" + hash + "\"}";
            File.WriteAllText(Path.Combine(snapshotRoot, "manifest.json"), manifest);
            return repositoryRoot;
        }

        /// <summary>XMZADD 20260909 创建可通过正式快照与公开内容校验的最小候选。</summary>
        private static SnapshotData CreateCandidateSnapshot(long revision, bool includePrivateEvidence)
        {
            var entity = new MetadataValue
            {
                Value = "t_DA_Account",
                Status = ConfidenceStatus.CodeEvidence,
                SourceSummary = "EOS 源码实体类",
                Evidence = new List<EvidenceItem>
                {
                    new EvidenceItem
                    {
                        SourceType = "源码",
                        SourcePath = includePrivateEvidence
                            ? @"C:\private\ERP\表-类定义\t_DA_Account.vb"
                            : "ERP/表-类定义/t_DA_Account.vb",
                        SourceLine = 3,
                        RuleName = "TableNameProperty",
                        RawValue = includePrivateEvidence ? "Public Class t_DA_Account" : null,
                        OriginalText = includePrivateEvidence ? "Public Overrides ReadOnly Property TableName" : null,
                        Explanation = "从 EOS 实体定义确认数据库对象。"
                    }
                }
            };
            var table = new TableMetadata
            {
                ScopeKey = "github-shared-dictionary",
                SchemaName = "dbo",
                ObjectName = "DA_Account",
                ObjectType = "TABLE",
                Category = DictionaryTableCategory.Business,
                EntityName = entity
            };
            table.Fields.Add(new FieldMetadata
            {
                OwnerTableName = "DA_Account",
                FieldName = "Account_ID",
                DataType = "bigint",
                LengthText = "—",
                IsRequired = true,
                IsPrimaryKey = true
            });
            return new SnapshotData
            {
                FormatVersion = 1,
                Revision = revision,
                RefreshedAt = new DateTime(2026, 9, 8, 0, 0, 0, DateTimeKind.Utc),
                Tables = new List<TableMetadata> { table }
            };
        }

        /// <summary>XMZADD 20260909 从测试仓库 latest 计算真实基线哈希，避免测试依赖硬编码载荷。</summary>
        private static string ReadBaseHash(string repositoryRoot)
        {
            return new SnapshotCodec().ComputeSha256(
                File.ReadAllBytes(Path.Combine(repositoryRoot, "snapshot", "latest.json.gz")));
        }

        /// <summary>XMZADD 20260909 从 manifest 指向的不可变修订读取真实基线哈希，允许兼容 latest 别名暂时滞后。</summary>
        private static string ReadManifestRevisionHash(string repositoryRoot)
        {
            string manifest = File.ReadAllText(Path.Combine(repositoryRoot, "snapshot", "manifest.json"));
            int markerIndex = manifest.IndexOf("snapshot/revisions/", StringComparison.Ordinal);
            int endingIndex = manifest.IndexOf(".json.gz", markerIndex, StringComparison.Ordinal);
            string relativePath = manifest.Substring(markerIndex, endingIndex - markerIndex + ".json.gz".Length);
            return new SnapshotCodec().ComputeSha256(
                File.ReadAllBytes(Path.Combine(repositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar))));
        }
    }
}
