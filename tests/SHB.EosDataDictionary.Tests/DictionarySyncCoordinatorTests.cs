using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHB.EosDataDictionary.Models;
using SHB.EosDataDictionary.Services;

namespace SHB.EosDataDictionary.Tests
{
    /// <summary>XMZADD 20260901 验证共享字典同步顺序、增量原子性、全量回退和离线 outbox 生命周期。</summary>
    [TestClass]
    public sealed class DictionarySyncCoordinatorTests
    {
        /// <summary>XMZADD 20260901 验证故障注入观察器及其构造入口不会扩散到正式公共 API。</summary>
        [TestMethod]
        public void CommitObserverTestingSurface_IsInternal()
        {
            Assert.IsFalse(typeof(IDictionarySyncCommitObserver).IsPublic);

            ConstructorInfo testingConstructor = null;
            ConstructorInfo[] constructors = typeof(DictionarySyncCoordinator).GetConstructors(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (int index = 0; index < constructors.Length; index++)
            {
                if (constructors[index].GetParameters().Length == 6)
                {
                    testingConstructor = constructors[index];
                    break;
                }
            }

            Assert.IsNotNull(testingConstructor);
            Assert.IsFalse(testingConstructor.IsPublic);
        }

        /// <summary>XMZADD 20260901 验证修订未变化时不下载快照或修订，只保存成功时间。</summary>
        [TestMethod]
        public async Task SyncAsync_UnchangedRevision_DoesNotDownloadSnapshotOrRevision()
        {
            TestContextData context = CreateContext(7L);
            try
            {
                context.Client.Manifest = CreateManifest(7L, context.LocalSnapshotSha256);

                await context.Coordinator.SyncAsync(CancellationToken.None);

                Assert.AreEqual(0, context.Client.SnapshotDownloadCount);
                Assert.AreEqual(0, context.Client.RevisionDownloadCount);
                Assert.AreEqual(context.Clock.UtcNow, context.LocalStore.LoadSyncState("test-state").LastSyncAtUtc);
            }
            finally
            {
                context.Dispose();
            }
        }

        /// <summary>XMZADD 20260903 验证种子修订也必须核对远端版本哈希，避免损坏或过期的 revision 0 缓存永久无法自愈。</summary>
        [TestMethod]
        public async Task SyncAsync_RevisionZeroManifestChanged_DownloadsFullSnapshot()
        {
            TestContextData context = CreateContext(0L);
            try
            {
                SnapshotData remoteSnapshot = CreateSnapshot(0L, "种子快照");
                var codec = new SnapshotCodec();
                byte[] compressed = codec.Encode(remoteSnapshot);
                context.Client.Manifest = CreateManifest(0L, codec.ComputeSha256(compressed));
                context.Client.SnapshotContent = compressed;

                await context.Coordinator.SyncAsync(CancellationToken.None);

                Assert.AreEqual(1, context.Client.SnapshotDownloadCount);
            }
            finally
            {
                context.Dispose();
            }
        }

        /// <summary>XMZADD 20260903 验证同步器按上传本地修改、获取远程字典的真实先后顺序报告阶段。</summary>
        [TestMethod]
        public async Task SyncAsync_ReportsUploadThenFetchStages()
        {
            TestContextData context = CreateContext(7L);
            try
            {
                context.Client.Manifest = CreateManifest(7L, context.LocalSnapshotSha256);
                var stages = new List<DictionarySyncStage>();

                await context.Coordinator.SyncAsync(CancellationToken.None, stages.Add);

                CollectionAssert.AreEqual(
                    new[] { DictionarySyncStage.UploadingChanges, DictionarySyncStage.FetchingRemoteDictionary },
                    stages);
            }
            finally
            {
                context.Dispose();
            }
        }

        /// <summary>XMZADD 20260901 验证有连续本地快照时只下载修订并把完整修订原子应用到本地快照。</summary>
        [TestMethod]
        public async Task SyncAsync_ContinuousRevision_DownloadsOnlyRevisionAndAppliesIt()
        {
            TestContextData context = CreateContext(7L);
            try
            {
                context.Client.Manifest = CreateManifest(8L, null);
                context.Client.Revisions[8L] = CreateRevision(8L, CreateSetOperation("remote-operation", "共享名称"));

                await context.Coordinator.SyncAsync(CancellationToken.None);

                Assert.AreEqual(1, context.Client.RevisionDownloadCount);
                Assert.AreEqual(0, context.Client.SnapshotDownloadCount);
                SnapshotData snapshot;
                Assert.IsTrue(context.SnapshotStore.TryLoadLatest("test-scope", out snapshot));
                Assert.AreEqual(8L, snapshot.Revision);
                Assert.AreEqual("共享名称", snapshot.Tables[0].Fields[0].ChineseName.Value);
            }
            finally
            {
                context.Dispose();
            }
        }

        /// <summary>XMZADD 20260903 验证增量落盘后的本地编码可独立校验且不会因远端压缩头不同回退全量。</summary>
        [TestMethod]
        public async Task SyncAsync_IncrementalThenSameManifest_DoesNotDownloadFullSnapshot()
        {
            TestContextData context = CreateContext(7L);
            try
            {
                SnapshotData remoteSnapshot = CreateSnapshot(8L, "共享名称");
                var codec = new SnapshotCodec();
                byte[] remoteCompressed = codec.Encode(remoteSnapshot);
                // 远端清单哈希只标识规范版本，本地载荷完整性必须用本地实际字节单独记录。
                remoteCompressed[4] = 3;
                remoteCompressed[5] = 4;
                context.Client.Manifest = CreateManifest(8L, codec.ComputeSha256(remoteCompressed));
                context.Client.Revisions[8L] = CreateRevision(8L, CreateSetOperation("remote-operation", "共享名称"));
                context.Client.SnapshotContent = remoteCompressed;

                await context.Coordinator.SyncAsync(CancellationToken.None);
                await context.Coordinator.SyncAsync(CancellationToken.None);

                Assert.AreEqual(1, context.Client.RevisionDownloadCount);
                Assert.AreEqual(0, context.Client.SnapshotDownloadCount);
            }
            finally
            {
                context.Dispose();
            }
        }

        /// <summary>XMZADD 20260903 验证本地载荷被替换但内嵌修订未变时禁止从错误基线增量，必须回退可信全量。</summary>
        [TestMethod]
        public async Task SyncAsync_LocalPayloadHashMismatch_SkipsIncrementalAndDownloadsFullSnapshot()
        {
            TestContextData context = CreateContext(7L);
            try
            {
                context.SnapshotStore.ReplaceScope("test-scope", CreateSnapshot(7L, "非可信本地基线"));
                SnapshotData remoteSnapshot = CreateSnapshot(8L, "远端全量名称");
                var codec = new SnapshotCodec();
                byte[] remoteContent = codec.Encode(remoteSnapshot);
                context.Client.Manifest = CreateManifest(8L, codec.ComputeSha256(remoteContent));
                context.Client.Revisions[8L] = CreateRevision(
                    8L,
                    CreateSetOperation("untrusted-incremental", "不应增量应用"));
                context.Client.SnapshotContent = remoteContent;

                await context.Coordinator.SyncAsync(CancellationToken.None);

                Assert.AreEqual(0, context.Client.RevisionDownloadCount);
                Assert.AreEqual(1, context.Client.SnapshotDownloadCount);
                SnapshotData restored;
                Assert.IsTrue(context.SnapshotStore.TryLoadLatest("test-scope", out restored));
                Assert.AreEqual("远端全量名称", restored.Tables[0].Fields[0].ChineseName.Value);
            }
            finally
            {
                context.Dispose();
            }
        }

        /// <summary>XMZADD 20260901 验证 Issue 创建成功只标记 Submitted，未见远程事件前不误判完成。</summary>
        [TestMethod]
        public async Task SyncAsync_UploadSucceeded_RemainsSubmittedUntilEventConfirmation()
        {
            TestContextData context = CreateContext(7L);
            try
            {
                DictionaryChangeBatch batch = CreatePendingBatch("local-operation");
                context.LocalStore.SaveOverridesWithPendingOperation(batch.Overrides, batch);
                context.Client.Manifest = CreateManifest(7L, context.LocalSnapshotSha256);

                await context.Coordinator.SyncAsync(CancellationToken.None);

                PendingDictionaryOperation pending = context.LocalStore.LoadPendingOperations()[0];
                Assert.AreEqual(PendingOperationStatus.Submitted, pending.Status);
                Assert.AreEqual(99, pending.GitHubIssueNumber);
            }
            finally
            {
                context.Dispose();
            }
        }

        /// <summary>XMZADD 20260901 验证远程单修订包含本地批次全部 OperationId 后才将批次标记完成。</summary>
        [TestMethod]
        public async Task SyncAsync_RemoteRevisionConfirmsAllOperations_MarksBatchCompleted()
        {
            TestContextData context = CreateContext(7L);
            try
            {
                DictionaryChangeBatch batch = CreatePendingBatch("confirmed-operation");
                context.LocalStore.SaveOverridesWithPendingOperation(batch.Overrides, batch);
                context.Client.Manifest = CreateManifest(8L, null);
                context.Client.Revisions[8L] = CreateRevision(
                    8L,
                    CreateSetOperation(batch.Operations[0].OperationId, "已确认"));

                await context.Coordinator.SyncAsync(CancellationToken.None);

                Assert.AreEqual(PendingOperationStatus.Completed, context.LocalStore.LoadPendingOperations()[0].Status);
            }
            finally
            {
                context.Dispose();
            }
        }

        /// <summary>XMZADD 20260901 验证只出现批次部分 OperationId 时仍保留 Submitted，防止拆分事件误确认整次编辑。</summary>
        [TestMethod]
        public async Task SyncAsync_RemoteRevisionConfirmsOnlyPartOfBatch_KeepsBatchSubmitted()
        {
            TestContextData context = CreateContext(7L);
            try
            {
                DictionaryChangeBatch batch = CreatePendingBatch("partial-confirmation");
                batch.Operations.Add(CreateSetOperation(Guid.NewGuid().ToString("N"), "第二项"));
                context.LocalStore.SaveOverridesWithPendingOperation(batch.Overrides, batch);
                context.Client.Manifest = CreateManifest(8L, null);
                context.Client.Revisions[8L] = CreateRevision(
                    8L,
                    CreateSetOperation(batch.Operations[0].OperationId, "只确认第一项"));

                await context.Coordinator.SyncAsync(CancellationToken.None);

                Assert.AreEqual(PendingOperationStatus.Submitted, context.LocalStore.LoadPendingOperations()[0].Status);
            }
            finally
            {
                context.Dispose();
            }
        }

        /// <summary>XMZADD 20260901 验证增量远程事实先确认 outbox，再替换快照，最后推进持久游标。</summary>
        [TestMethod]
        public async Task SyncAsync_IncrementalCommit_ConfirmsBeforeSnapshotAndState()
        {
            var observer = new RecordingSyncCommitObserver();
            TestContextData context = CreateContext(7L, observer);
            try
            {
                DictionaryChangeBatch batch = CreatePendingBatch("ordered-confirmation");
                context.LocalStore.SaveOverridesWithPendingOperation(batch.Overrides, batch);
                context.Client.Manifest = CreateManifest(8L, null);
                context.Client.Revisions[8L] = CreateRevision(
                    8L,
                    CreateSetOperation(batch.Operations[0].OperationId, "已确认"));

                await context.Coordinator.SyncAsync(CancellationToken.None);

                CollectionAssert.AreEqual(
                    new[] { "Confirm", "Replace", "State" },
                    new List<string>(observer.Stages));
                Assert.AreEqual(PendingOperationStatus.Completed, context.LocalStore.LoadPendingOperations()[0].Status);
            }
            finally
            {
                context.Dispose();
            }
        }

        /// <summary>XMZADD 20260901 验证确认 outbox 失败时不替换增量快照且不推进同步游标。</summary>
        [TestMethod]
        public async Task SyncAsync_ConfirmationPersistenceFails_DoesNotAdvanceSnapshotOrState()
        {
            var observer = new RecordingSyncCommitObserver { ThrowBeforeConfirmation = true };
            TestContextData context = CreateContext(7L, observer);
            try
            {
                DictionaryChangeBatch batch = CreatePendingBatch("failed-confirmation");
                context.LocalStore.SaveOverridesWithPendingOperation(batch.Overrides, batch);
                context.Client.Manifest = CreateManifest(8L, null);
                context.Client.Revisions[8L] = CreateRevision(
                    8L,
                    CreateSetOperation(batch.Operations[0].OperationId, "不应保存"));

                await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                    async delegate { await context.Coordinator.SyncAsync(CancellationToken.None); });

                SnapshotData actual;
                Assert.IsTrue(context.SnapshotStore.TryLoadLatest("test-scope", out actual));
                Assert.AreEqual(7L, actual.Revision);
                Assert.AreEqual("旧名称", actual.Tables[0].Fields[0].ChineseName.Value);
                Assert.AreEqual(7L, context.LocalStore.LoadSyncState("test-state").Revision);
                Assert.AreEqual(PendingOperationStatus.Submitted, context.LocalStore.LoadPendingOperations()[0].Status);
                CollectionAssert.AreEqual(new[] { "Confirm" }, new List<string>(observer.Stages));
            }
            finally
            {
                context.Dispose();
            }
        }

        /// <summary>XMZADD 20260901 验证瞬时上传失败保留 outbox 并按首次一分钟退避。</summary>
        [TestMethod]
        public async Task SyncAsync_TransientUploadFailure_RecordsRetryWithoutDeletingOutbox()
        {
            TestContextData context = CreateContext(7L);
            try
            {
                DictionaryChangeBatch batch = CreatePendingBatch("retry-operation");
                context.LocalStore.SaveOverridesWithPendingOperation(batch.Overrides, batch);
                context.Client.UploadException = new GitHubDictionaryClientException(
                    "暂时离线。", GitHubDictionaryErrorKind.Transient, true, null);
                context.Client.Manifest = CreateManifest(7L, context.LocalSnapshotSha256);

                await context.Coordinator.SyncAsync(CancellationToken.None);

                PendingDictionaryOperation pending = context.LocalStore.LoadPendingOperations()[0];
                Assert.AreEqual(PendingOperationStatus.Pending, pending.Status);
                Assert.AreEqual(1, pending.AttemptCount);
                Assert.AreEqual(context.Clock.UtcNow.AddMinutes(1), pending.NextAttemptAtUtc);
            }
            finally
            {
                context.Dispose();
            }
        }

        /// <summary>XMZADD 20260903 验证认证或协议错误仍保留待上传状态，使用户修复配置或升级程序后可以恢复。</summary>
        [TestMethod]
        public async Task SyncAsync_NonTransientUploadFailure_RemainsPendingForRecovery()
        {
            TestContextData context = CreateContext(7L);
            try
            {
                DictionaryChangeBatch batch = CreatePendingBatch("recoverable-permanent-error");
                context.LocalStore.SaveOverridesWithPendingOperation(batch.Overrides, batch);
                context.Client.UploadException = new GitHubDictionaryClientException(
                    "认证失败。", GitHubDictionaryErrorKind.Authentication, false, null);
                context.Client.Manifest = CreateManifest(7L, context.LocalSnapshotSha256);

                await context.Coordinator.SyncAsync(CancellationToken.None);

                PendingDictionaryOperation pending = context.LocalStore.LoadPendingOperations()[0];
                Assert.AreEqual(PendingOperationStatus.Pending, pending.Status);
                Assert.AreEqual(1, pending.AttemptCount);
                Assert.AreEqual(context.Clock.UtcNow.AddMinutes(1), pending.NextAttemptAtUtc);
            }
            finally
            {
                context.Dispose();
            }
        }

        /// <summary>XMZADD 20260903 验证旧版本遗留的 Failed 修改在升级后重新提交，避免人工结论永久滞留本机。</summary>
        [TestMethod]
        public async Task SyncNowAsync_LegacyFailedOperation_IsRetriedAfterUpgrade()
        {
            TestContextData context = CreateContext(7L);
            try
            {
                DictionaryChangeBatch batch = CreatePendingBatch("legacy-failed-retry");
                context.LocalStore.SaveOverridesWithPendingOperation(batch.Overrides, batch);
                context.LocalStore.MarkOperationFailed(batch.BatchId, "旧版本永久失败");
                context.Client.Manifest = CreateManifest(7L, context.LocalSnapshotSha256);

                await context.Coordinator.SyncNowAsync(CancellationToken.None);

                PendingDictionaryOperation pending = context.LocalStore.LoadPendingOperations()[0];
                Assert.AreEqual(PendingOperationStatus.Submitted, pending.Status);
                Assert.AreEqual(1, context.Client.IssueCreateCount);
            }
            finally
            {
                context.Dispose();
            }
        }

        /// <summary>XMZADD 20260901 验证连续瞬时失败严格采用一、二、五、十五、三十分钟并保持三十分钟上限。</summary>
        [TestMethod]
        public async Task SyncNowAsync_RepeatedTransientFailures_UsesBoundedRetrySchedule()
        {
            TestContextData context = CreateContext(7L);
            try
            {
                DictionaryChangeBatch batch = CreatePendingBatch("bounded-retry");
                context.LocalStore.SaveOverridesWithPendingOperation(batch.Overrides, batch);
                context.Client.UploadException = new GitHubDictionaryClientException(
                    "暂时离线。", GitHubDictionaryErrorKind.Transient, true, null);
                context.Client.Manifest = CreateManifest(7L, context.LocalSnapshotSha256);
                int[] expectedMinutes = { 1, 2, 5, 15, 30, 30 };

                for (int index = 0; index < expectedMinutes.Length; index++)
                {
                    await context.Coordinator.SyncNowAsync(CancellationToken.None);
                    PendingDictionaryOperation pending = context.LocalStore.LoadPendingOperations()[0];
                    Assert.AreEqual(context.Clock.UtcNow.AddMinutes(expectedMinutes[index]), pending.NextAttemptAtUtc);
                }
            }
            finally
            {
                context.Dispose();
            }
        }

        /// <summary>XMZADD 20260901 验证手工同步忽略未来重试时间立即尝试，而自动同步继续尊重退避。</summary>
        [TestMethod]
        public async Task SyncNowAsync_IgnoresNextAttemptTimeOnce()
        {
            TestContextData context = CreateContext(7L);
            try
            {
                DictionaryChangeBatch batch = CreatePendingBatch("manual-operation");
                context.LocalStore.SaveOverridesWithPendingOperation(batch.Overrides, batch);
                context.LocalStore.RecordOperationRetry(batch.BatchId, context.Clock.UtcNow.AddHours(1), "离线");
                context.Client.Manifest = CreateManifest(7L, context.LocalSnapshotSha256);

                await context.Coordinator.SyncAsync(CancellationToken.None);
                Assert.AreEqual(0, context.Client.IssueCreateCount);

                await context.Coordinator.SyncNowAsync(CancellationToken.None);
                Assert.AreEqual(1, context.Client.IssueCreateCount);
                Assert.AreEqual(PendingOperationStatus.Submitted, context.LocalStore.LoadPendingOperations()[0].Status);
            }
            finally
            {
                context.Dispose();
            }
        }

        /// <summary>XMZADD 20260901 验证修订缺口回退到哈希校验后的全量快照并只在成功后推进游标。</summary>
        [TestMethod]
        public async Task SyncAsync_RevisionNotFound_FallsBackToFullSnapshot()
        {
            TestContextData context = CreateContext(7L);
            try
            {
                DictionaryChangeBatch pendingBatch = CreatePendingBatch("full-fallback-pending");
                context.LocalStore.SaveOverridesWithPendingOperation(pendingBatch.Overrides, pendingBatch);
                context.LocalStore.MarkOperationSubmitted(pendingBatch.BatchId, 88);
                SnapshotData remoteSnapshot = CreateSnapshot(9L, "全量名称");
                byte[] compressed = new SnapshotCodec().Encode(remoteSnapshot);
                string hash = new SnapshotCodec().ComputeSha256(compressed);
                context.Client.Manifest = CreateManifest(9L, hash);
                context.Client.AppliedIssueNumbers.Add(88);
                context.Client.RevisionException = new GitHubDictionaryClientException(
                    "缺少修订。", GitHubDictionaryErrorKind.NotFound, false, null);
                context.Client.SnapshotContent = compressed;

                await context.Coordinator.SyncAsync(CancellationToken.None);

                Assert.AreEqual(1, context.Client.SnapshotDownloadCount);
                SnapshotData actual;
                Assert.IsTrue(context.SnapshotStore.TryLoadLatest("test-scope", out actual));
                Assert.AreEqual(9L, actual.Revision);
                Assert.AreEqual("全量名称", actual.Tables[0].Fields[0].ChineseName.Value);
                Assert.AreEqual(9L, context.LocalStore.LoadSyncState("test-state").Revision);
                Assert.AreEqual(PendingOperationStatus.Completed, context.LocalStore.LoadPendingOperations()[0].Status);
            }
            finally
            {
                context.Dispose();
            }
        }

        /// <summary>XMZADD 20260903 验证远端与本机使用不同 GZip 头时仍以独立本地完整性哈希识别已同步快照。</summary>
        [TestMethod]
        public async Task SyncAsync_FullSnapshotWithDifferentGzipHeader_DoesNotDownloadAgain()
        {
            TestContextData context = CreateContext(7L);
            try
            {
                SnapshotData remoteSnapshot = CreateSnapshot(9L, "远端名称");
                var codec = new SnapshotCodec();
                byte[] remoteCompressed = codec.Encode(remoteSnapshot);
                // GitHub 工作流和客户端压缩器可能写入不同头信息，业务内容相同不代表压缩字节能被客户端重建。
                remoteCompressed[4] = 1;
                remoteCompressed[5] = 2;
                context.Client.Manifest = CreateManifest(9L, codec.ComputeSha256(remoteCompressed));
                context.Client.RevisionException = new GitHubDictionaryClientException(
                    "缺少修订。", GitHubDictionaryErrorKind.NotFound, false, null);
                context.Client.SnapshotContent = remoteCompressed;

                await context.Coordinator.SyncAsync(CancellationToken.None);
                await context.Coordinator.SyncAsync(CancellationToken.None);

                Assert.AreEqual(1, context.Client.SnapshotDownloadCount);
            }
            finally
            {
                context.Dispose();
            }
        }

        /// <summary>XMZADD 20260903 验证全量同步把已解码对象图返回调用方，使界面无需从 SQLite 再次反序列化近百万字段。</summary>
        [TestMethod]
        public async Task SyncAsync_FullSnapshot_ReturnsDecodedSnapshotForUiReuse()
        {
            TestContextData context = CreateContext(7L);
            try
            {
                SnapshotData remoteSnapshot = CreateSnapshot(9L, "远端名称");
                var codec = new SnapshotCodec();
                byte[] compressed = codec.Encode(remoteSnapshot);
                context.Client.Manifest = CreateManifest(9L, codec.ComputeSha256(compressed));
                context.Client.RevisionException = new GitHubDictionaryClientException(
                    "缺少修订。", GitHubDictionaryErrorKind.NotFound, false, null);
                context.Client.SnapshotContent = compressed;

                object synchronizedSnapshot = await context.Coordinator.SyncAsync(CancellationToken.None);

                Assert.IsInstanceOfType(synchronizedSnapshot, typeof(SnapshotData));
                Assert.AreEqual(9L, ((SnapshotData)synchronizedSnapshot).Revision);
            }
            finally
            {
                context.Dispose();
            }
        }

        /// <summary>XMZADD 20260903 验证清单未变化时仍复查已提交批次，不可变事件稍后进入修订链后不会永久停留待生效。</summary>
        [TestMethod]
        public async Task SyncAsync_UnchangedRevision_RechecksSubmittedIssueCompletion()
        {
            TestContextData context = CreateContext(7L);
            try
            {
                DictionaryChangeBatch pendingBatch = CreatePendingBatch("same-revision-confirmation");
                context.LocalStore.SaveOverridesWithPendingOperation(pendingBatch.Overrides, pendingBatch);
                context.LocalStore.MarkOperationSubmitted(pendingBatch.BatchId, 88);
                context.Client.Manifest = CreateManifest(7L, context.LocalSnapshotSha256);
                context.Client.AppliedIssueNumbers.Add(88);

                await context.Coordinator.SyncAsync(CancellationToken.None);

                Assert.AreEqual(PendingOperationStatus.Completed, context.LocalStore.LoadPendingOperations()[0].Status);
                Assert.AreEqual(0, context.Client.SnapshotDownloadCount);
                Assert.AreEqual(0, context.Client.RevisionDownloadCount);
            }
            finally
            {
                context.Dispose();
            }
        }

        /// <summary>XMZADD 20260909 验证远端明确拒绝的待生效 Issue 转为终态，不再永久占用活动队列。</summary>
        [TestMethod]
        public async Task SyncNowAsync_RejectedSubmittedIssue_BecomesTerminal()
        {
            TestContextData context = CreateContext(7L);
            try
            {
                DictionaryChangeBatch pendingBatch = CreatePendingBatch("rejected-confirmation");
                context.LocalStore.SaveOverridesWithPendingOperation(pendingBatch.Overrides, pendingBatch);
                context.LocalStore.MarkOperationSubmitted(pendingBatch.BatchId, 88);
                context.Client.Manifest = CreateManifest(7L, context.LocalSnapshotSha256);
                context.Client.IssueStatuses[88] = DictionaryIssueApplyStatus.Rejected;

                await context.Coordinator.SyncNowAsync(CancellationToken.None);

                Assert.AreEqual(PendingOperationStatus.Rejected, context.LocalStore.LoadPendingOperations()[0].Status);
                Assert.AreEqual(0, context.LocalStore.LoadActivePendingOperations().Count);
            }
            finally
            {
                context.Dispose();
            }
        }

        /// <summary>XMZADD 20260903 验证待生效事件核验暂时失败时不否决已经校验并落盘的全量快照。</summary>
        [TestMethod]
        public async Task SyncAsync_FullSnapshotConfirmationRateLimited_AdvancesSnapshotAndKeepsSubmitted()
        {
            TestContextData context = CreateContext(7L);
            try
            {
                DictionaryChangeBatch pendingBatch = CreatePendingBatch("confirmation-rate-limited");
                context.LocalStore.SaveOverridesWithPendingOperation(pendingBatch.Overrides, pendingBatch);
                context.LocalStore.MarkOperationSubmitted(pendingBatch.BatchId, 88);
                SnapshotData remoteSnapshot = CreateSnapshot(9L, "全量名称");
                byte[] compressed = new SnapshotCodec().Encode(remoteSnapshot);
                context.Client.Manifest = CreateManifest(9L, new SnapshotCodec().ComputeSha256(compressed));
                context.Client.RevisionException = new GitHubDictionaryClientException(
                    "缺少修订。",
                    GitHubDictionaryErrorKind.NotFound,
                    false,
                    null);
                context.Client.SnapshotContent = compressed;
                context.Client.ConfirmationException = new GitHubDictionaryClientException(
                    "确认请求受限。",
                    GitHubDictionaryErrorKind.RateLimited,
                    true,
                    TimeSpan.FromMinutes(1D));

                await context.Coordinator.SyncAsync(CancellationToken.None);

                SnapshotData actual;
                Assert.IsTrue(context.SnapshotStore.TryLoadLatest("test-scope", out actual));
                Assert.AreEqual(9L, actual.Revision);
                Assert.AreEqual(9L, context.LocalStore.LoadSyncState("test-state").Revision);
                Assert.AreEqual(PendingOperationStatus.Submitted, context.LocalStore.LoadPendingOperations()[0].Status);
            }
            finally
            {
                context.Dispose();
            }
        }

        /// <summary>XMZADD 20260903 验证超大字典升级直接采用远端全量快照，避免增量事务克隆整棵字段对象图。</summary>
        [TestMethod]
        public async Task SyncAsync_LargeSnapshotRevisionAdvance_UsesFullSnapshotWithoutIncrementalClone()
        {
            TestContextData context = CreateContext(7L);
            try
            {
                var largeLocalSnapshot = new SnapshotData
                {
                    FormatVersion = 1,
                    Revision = 7L,
                    RefreshedAt = new DateTime(2026, 9, 1, 7, 0, 0, DateTimeKind.Utc)
                };
                for (int index = 0; index < 10001; index++)
                {
                    largeLocalSnapshot.Tables.Add(new TableMetadata
                    {
                        ScopeKey = "test-scope",
                        SchemaName = "dbo",
                        ObjectName = "T_LARGE_" + index.ToString("00000"),
                        ObjectType = "TABLE"
                    });
                }
                context.SnapshotStore.ReplaceScope("test-scope", largeLocalSnapshot, CancellationToken.None);
                SnapshotData remoteSnapshot = CreateSnapshot(9L, "全量更新");
                byte[] compressed = new SnapshotCodec().Encode(remoteSnapshot);
                context.Client.Manifest = CreateManifest(9L, new SnapshotCodec().ComputeSha256(compressed));
                context.Client.SnapshotContent = compressed;

                await context.Coordinator.SyncAsync(CancellationToken.None);

                Assert.AreEqual(1, context.Client.SnapshotDownloadCount);
                Assert.AreEqual(0, context.Client.RevisionDownloadCount);
                Assert.AreEqual(9L, context.LocalStore.LoadSyncState("test-state").Revision);
            }
            finally
            {
                context.Dispose();
            }
        }

        /// <summary>XMZADD 20260901 验证全量哈希失败时保留旧快照和旧游标，不提交半同步状态。</summary>
        [TestMethod]
        public async Task SyncAsync_FullSnapshotHashFailure_PreservesOldSnapshotAndState()
        {
            TestContextData context = CreateContext(7L);
            try
            {
                context.Client.Manifest = CreateManifest(9L, new string('0', 64));
                context.Client.RevisionException = new GitHubDictionaryClientException(
                    "缺少修订。", GitHubDictionaryErrorKind.NotFound, false, null);
                context.Client.SnapshotContent = new SnapshotCodec().Encode(CreateSnapshot(9L, "损坏候选"));

                await Assert.ThrowsExceptionAsync<InvalidDataException>(
                    async delegate { await context.Coordinator.SyncAsync(CancellationToken.None); });

                SnapshotData actual;
                Assert.IsTrue(context.SnapshotStore.TryLoadLatest("test-scope", out actual));
                Assert.AreEqual(7L, actual.Revision);
                Assert.AreEqual("旧名称", actual.Tables[0].Fields[0].ChineseName.Value);
                Assert.AreEqual(7L, context.LocalStore.LoadSyncState("test-state").Revision);
            }
            finally
            {
                context.Dispose();
            }
        }

        /// <summary>XMZADD 20260901 验证同一服务器修订任一事件失败时不保存前序事件的局部结果。</summary>
        [TestMethod]
        public async Task SyncAsync_OneRevisionContainsInvalidLaterEvent_PreservesWholeOldRevision()
        {
            TestContextData context = CreateContext(7L);
            try
            {
                DictionaryChangeOperation invalidOperation = CreateSetOperation("invalid-later-event", "无效");
                invalidOperation.FieldKey = "MISSING_FIELD";
                context.Client.Manifest = CreateManifest(8L, new string('0', 64));
                context.Client.Revisions[8L] = CreateRevision(
                    8L,
                    CreateSetOperation("valid-first-event", "不应保存"),
                    invalidOperation);
                context.Client.SnapshotContent = new byte[] { 1, 2, 3 };

                await Assert.ThrowsExceptionAsync<InvalidDataException>(
                    async delegate { await context.Coordinator.SyncAsync(CancellationToken.None); });

                SnapshotData actual;
                Assert.IsTrue(context.SnapshotStore.TryLoadLatest("test-scope", out actual));
                Assert.AreEqual(7L, actual.Revision);
                Assert.AreEqual("旧名称", actual.Tables[0].Fields[0].ChineseName.Value);
            }
            finally
            {
                context.Dispose();
            }
        }

        /// <summary>XMZADD 20260901 验证调用方取消直接传播且不把待上传批次标记失败或增加重试次数。</summary>
        [TestMethod]
        public async Task SyncAsync_Cancelled_PropagatesWithoutChangingOutboxFailureState()
        {
            TestContextData context = CreateContext(7L);
            try
            {
                DictionaryChangeBatch batch = CreatePendingBatch("cancel-operation");
                context.LocalStore.SaveOverridesWithPendingOperation(batch.Overrides, batch);
                context.Client.CancelUpload = true;
                var source = new CancellationTokenSource();
                source.Cancel();

                await Assert.ThrowsExceptionAsync<TaskCanceledException>(
                    async delegate { await context.Coordinator.SyncAsync(source.Token); });

                PendingDictionaryOperation pending = context.LocalStore.LoadPendingOperations()[0];
                Assert.AreEqual(PendingOperationStatus.Pending, pending.Status);
                Assert.AreEqual(0, pending.AttemptCount);
            }
            finally
            {
                context.Dispose();
            }
        }

        /// <summary>XMZADD 20260901 创建真实临时 SQLite 存储、固定时钟和完全内存化客户端的协调器测试上下文。</summary>
        private static TestContextData CreateContext(
            long localRevision,
            IDictionarySyncCommitObserver commitObserver = null)
        {
            string directory = Path.Combine(Path.GetTempPath(), "SHB.EosDataDictionary.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string databasePath = Path.Combine(directory, "dictionary.db");
            var localStore = new LocalDictionaryStore(databasePath);
            var snapshotStore = new SnapshotStore(databasePath);
            SnapshotData localSnapshot = CreateSnapshot(localRevision, "旧名称");
            snapshotStore.ReplaceScope("test-scope", localSnapshot);
            var snapshotCodec = new SnapshotCodec();
            string localSnapshotSha256 = snapshotCodec.ComputeSha256(snapshotCodec.Encode(localSnapshot));
            localStore.SaveSyncState(new DictionarySyncState
            {
                StateKey = "test-state",
                Revision = localRevision,
                ManifestHash = localSnapshotSha256,
                LocalPayloadHash = localSnapshotSha256,
                LastSyncAtUtc = new DateTime(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc)
            });
            var options = new DictionaryRepositoryOptions
            {
                Owner = "octo-owner",
                StateKey = "test-state",
                ScopeKey = "test-scope"
            };
            var client = new StubGitHubDictionaryClient();
            var clock = new FixedClock(new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc));
            return new TestContextData(
                directory,
                localStore,
                 snapshotStore,
                 client,
                 clock,
                 localSnapshotSha256,
                 commitObserver == null
                    ? new DictionarySyncCoordinator(localStore, snapshotStore, client, options, clock)
                    : new DictionarySyncCoordinator(localStore, snapshotStore, client, options, clock, commitObserver));
        }

        /// <summary>XMZADD 20260901 创建带单表单字段的规范快照以观察增量和全量替换结果。</summary>
        private static SnapshotData CreateSnapshot(long revision, string fieldChineseName)
        {
            var snapshot = new SnapshotData
            {
                FormatVersion = 1,
                Revision = revision,
                RefreshedAt = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc)
            };
            var table = new TableMetadata
            {
                SchemaName = "dbo",
                ObjectName = "T_TEST",
                ObjectType = "TABLE",
                Category = DictionaryTableCategory.Business
            };
            table.Fields.Add(new FieldMetadata
            {
                FieldName = "FNAME",
                OwnerTableName = "T_TEST",
                DataType = "nvarchar",
                LengthText = "100",
                ChineseName = new MetadataValue
                {
                    Value = fieldChineseName,
                    SourceType = "Manual",
                    Status = ConfidenceStatus.Confirmed
                }
            });
            snapshot.Tables.Add(table);
            return snapshot;
        }

        /// <summary>XMZADD 20260901 创建严格快照清单并按测试需要提供真实或占位哈希。</summary>
        private static SnapshotManifest CreateManifest(long revision, string hash)
        {
            return new SnapshotManifest
            {
                FormatVersion = 1,
                Revision = revision,
                SnapshotSha256 = hash ?? new string('a', 64),
                SnapshotPath = "snapshot/latest.json.gz",
                GeneratedAtUtc = new DateTime(2026, 9, 1, 7, 0, 0, DateTimeKind.Utc)
            };
        }

        /// <summary>XMZADD 20260901 创建本地 outbox 批次，批次号与单操作号保持一致便于远程确认。</summary>
        private static DictionaryChangeBatch CreatePendingBatch(string operationId)
        {
            string stableOperationId = Guid.NewGuid().ToString("N");
            var batch = new DictionaryChangeBatch
            {
                BatchId = stableOperationId,
                AuthorGitHubUserId = "123",
                CreatedAtUtc = new DateTime(2026, 9, 1, 7, 30, 0, DateTimeKind.Utc)
            };
            batch.Overrides.Add(new DictionaryOverride
            {
                ScopeKey = "test-scope",
                ObjectName = "T_TEST",
                FieldName = "FNAME",
                ObjectKey = "dbo.T_TEST",
                FieldKey = "FNAME",
                PropertyName = "ChineseName",
                ManualValue = "本地名称",
                IsLocked = true,
                UpdatedAt = batch.CreatedAtUtc
            });
            batch.Operations.Add(new DictionaryChangeOperation
            {
                OperationId = stableOperationId,
                AuthorGitHubUserId = "123",
                ObjectKey = "dbo.T_TEST",
                FieldKey = "FNAME",
                PropertyName = "ChineseName",
                NewValue = "本地名称",
                ChangeKind = "Set",
                CreatedAtUtc = batch.CreatedAtUtc
            });
            return batch;
        }

        /// <summary>XMZADD 20260901 创建可信仓库重放操作以模拟客户端下载后的合并事件。</summary>
        private static DictionaryChangeOperation CreateSetOperation(string operationId, string value)
        {
            return new DictionaryChangeOperation
            {
                OperationId = operationId,
                AuthorGitHubUserId = "1",
                ObjectKey = "dbo.T_TEST",
                FieldKey = "FNAME",
                PropertyName = "ChineseName",
                NewValue = value,
                ChangeKind = "Set",
                CreatedAtUtc = new DateTime(2026, 9, 1, 7, 45, 0, DateTimeKind.Utc)
            };
        }

        /// <summary>XMZADD 20260901 创建只含一个合并批次的服务器修订，固定单修订原子重放契约。</summary>
        private static DictionaryRevisionPackage CreateRevision(long revision, params DictionaryChangeOperation[] operations)
        {
            var package = new DictionaryRevisionPackage
            {
                FormatVersion = 1,
                Revision = revision,
                GeneratedAtUtc = new DateTime(2026, 9, 1, 7, 50, 0, DateTimeKind.Utc)
            };
            var batch = new DictionaryChangeBatch
            {
                BatchId = "repository-revision-" + revision,
                AuthorGitHubUserId = "1",
                CreatedAtUtc = package.GeneratedAtUtc
            };
            for (int index = 0; index < operations.Length; index++)
            {
                batch.Operations.Add(operations[index]);
            }
            package.Batches.Add(batch);
            return package;
        }

        /// <summary>XMZADD 20260901 保存协调器测试依赖并在完成后删除隔离 SQLite 目录。</summary>
        private sealed class TestContextData : IDisposable
        {
            private readonly string _directory;

            /// <summary>XMZADD 20260901 聚合单个测试的本地存储、客户端、时钟和协调器引用。</summary>
            public TestContextData(
                string directory,
                LocalDictionaryStore localStore,
                SnapshotStore snapshotStore,
                StubGitHubDictionaryClient client,
                FixedClock clock,
                string localSnapshotSha256,
                DictionarySyncCoordinator coordinator)
            {
                _directory = directory;
                LocalStore = localStore;
                SnapshotStore = snapshotStore;
                Client = client;
                Clock = clock;
                LocalSnapshotSha256 = localSnapshotSha256;
                Coordinator = coordinator;
            }

            public LocalDictionaryStore LocalStore { get; private set; }
            public SnapshotStore SnapshotStore { get; private set; }
            public StubGitHubDictionaryClient Client { get; private set; }
            public FixedClock Clock { get; private set; }
            public string LocalSnapshotSha256 { get; private set; }
            public DictionarySyncCoordinator Coordinator { get; private set; }

            /// <summary>XMZADD 20260901 删除仅属于当前测试的临时 SQLite 文件，避免状态跨测试泄漏。</summary>
            public void Dispose()
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Directory.Delete(_directory, true);
            }
        }

        /// <summary>XMZADD 20260901 提供可配置返回值和计数器的内存客户端，保证协调器测试不访问真实网络。</summary>
        private sealed class StubGitHubDictionaryClient : IGitHubDictionaryClient
        {
            /// <summary>XMZADD 20260901 初始化修订映射以便测试按修订号提供确定性事件包。</summary>
            public StubGitHubDictionaryClient()
            {
                Revisions = new Dictionary<long, DictionaryRevisionPackage>();
                AppliedIssueNumbers = new HashSet<int>();
                IssueStatuses = new Dictionary<int, DictionaryIssueApplyStatus>();
            }

            public SnapshotManifest Manifest { get; set; }
            public byte[] SnapshotContent { get; set; }
            public IDictionary<long, DictionaryRevisionPackage> Revisions { get; private set; }
            public ISet<int> AppliedIssueNumbers { get; private set; }
            public IDictionary<int, DictionaryIssueApplyStatus> IssueStatuses { get; private set; }
            public Exception UploadException { get; set; }
            public Exception RevisionException { get; set; }
            public Exception ConfirmationException { get; set; }
            public bool CancelUpload { get; set; }
            public int SnapshotDownloadCount { get; private set; }
            public int RevisionDownloadCount { get; private set; }
            public int IssueCreateCount { get; private set; }

            /// <summary>XMZADD 20260901 返回内存 manifest 并尊重调用方取消。</summary>
            public Task<SnapshotManifest> GetManifestAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(Manifest);
            }

            /// <summary>XMZADD 20260901 记录全量下载并返回内存压缩快照。</summary>
            public Task<byte[]> DownloadSnapshotAsync(string relativePath, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SnapshotDownloadCount++;
                return Task.FromResult(SnapshotContent);
            }

            /// <summary>XMZADD 20260901 记录增量下载并按测试分支返回修订或安全异常。</summary>
            public Task<DictionaryRevisionPackage> DownloadRevisionAsync(long revision, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RevisionDownloadCount++;
                if (RevisionException != null)
                {
                    throw RevisionException;
                }
                return Task.FromResult(Revisions[revision]);
            }

            /// <summary>XMZADD 20260901 返回固定数值身份以满足接口但不参与协调器测试。</summary>
            public Task<GitHubUserIdentity> GetCurrentUserAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(new GitHubUserIdentity { GitHubUserId = "123", Login = "test" });
            }

            /// <summary>XMZADD 20260901 返回固定候选身份以满足接口，但协调器流程不得调用此验证入口。</summary>
            public Task<GitHubUserIdentity> GetCurrentUserAsync(string candidateToken, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(new GitHubUserIdentity { GitHubUserId = "123", Login = "test" });
            }

            /// <summary>XMZADD 20260901 返回空发布者名单以满足同步协调器的离线客户端契约。</summary>
            public Task<IList<string>> GetPublisherIdsAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult<IList<string>>(new List<string>());
            }

            /// <summary>XMZADD 20260901 记录 Issue 创建并按测试需要模拟取消、瞬时失败或成功。</summary>
            public Task<int> CreateDictionaryIssueAsync(DictionaryChangeBatch batch, CancellationToken cancellationToken)
            {
                if (CancelUpload)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
                cancellationToken.ThrowIfCancellationRequested();
                IssueCreateCount++;
                if (UploadException != null)
                {
                    throw UploadException;
                }
                return Task.FromResult(99);
            }


            /// <summary>XMZADD 20260903 按测试配置返回 Issue 是否已由远端工作流成功处理。</summary>
            public Task<bool> AreDictionaryOperationsAppliedAsync(
                DictionaryChangeBatch batch,
                int issueNumber,
                long maximumRevision,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ConfirmationException != null)
                {
                    throw ConfirmationException;
                }
                return Task.FromResult(AppliedIssueNumbers.Contains(issueNumber));
            }

            /// <summary>XMZADD 20260903 按既有确认配置返回三态结果，供后续协调器恢复测试复用同一故障注入。</summary>
            public Task<DictionaryIssueApplyStatus> GetDictionaryOperationsApplyStatusAsync(
                DictionaryChangeBatch batch,
                int issueNumber,
                long maximumRevision,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ConfirmationException != null)
                {
                    throw ConfirmationException;
                }
                DictionaryIssueApplyStatus configuredStatus;
                if (IssueStatuses.TryGetValue(issueNumber, out configuredStatus))
                {
                    return Task.FromResult(configuredStatus);
                }
                return Task.FromResult(AppliedIssueNumbers.Contains(issueNumber)
                    ? DictionaryIssueApplyStatus.Applied
                    : DictionaryIssueApplyStatus.Pending);
            }

            /// <summary>XMZADD 20260903 返回固定替代 Issue 编号以满足协调器拒绝恢复契约。</summary>
            public Task<int> RecreateRejectedDictionaryIssueAsync(
                DictionaryChangeBatch batch,
                int rejectedIssueNumber,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(99);
            }
        }

        /// <summary>XMZADD 20260901 提供固定 UTC 时间以精确断言同步成功时间和退避边界。</summary>
        private sealed class FixedClock : ISystemClock
        {
            /// <summary>XMZADD 20260901 初始化不可变测试时间。</summary>
            public FixedClock(DateTime utcNow)
            {
                UtcNow = utcNow;
            }

            public DateTime UtcNow { get; private set; }
        }

        /// <summary>XMZADD 20260901 记录增量持久化边界并可模拟确认阶段故障以验证提交顺序。</summary>
        private sealed class RecordingSyncCommitObserver : IDictionarySyncCommitObserver
        {
            /// <summary>XMZADD 20260901 初始化可按调用顺序追加阶段名称的传统列表。</summary>
            public RecordingSyncCommitObserver()
            {
                Stages = new List<string>();
            }

            public IList<string> Stages { get; private set; }
            public bool ThrowBeforeConfirmation { get; set; }

            /// <summary>XMZADD 20260901 记录 outbox 确认边界并按测试需要模拟持久化失败。</summary>
            public void BeforeConfirmingBatches()
            {
                Stages.Add("Confirm");
                if (ThrowBeforeConfirmation)
                {
                    throw new InvalidOperationException("模拟确认持久化失败。");
                }
            }

            /// <summary>XMZADD 20260901 记录快照事务替换边界以核对它晚于远程事实确认。</summary>
            public void BeforeReplacingSnapshot()
            {
                Stages.Add("Replace");
            }

            /// <summary>XMZADD 20260901 记录游标保存边界以核对它是增量提交的最后阶段。</summary>
            public void BeforeSavingSyncState()
            {
                Stages.Add("State");
            }
        }
    }
}
