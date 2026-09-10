using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SHB.EosDataDictionary.Models;

namespace SHB.EosDataDictionary.Services
{
    /// <summary>XMZADD 20260901 暴露增量提交的三个持久化边界，便于验证远程确认、快照和游标的故障顺序。</summary>
    internal interface IDictionarySyncCommitObserver
    {
        /// <summary>XMZADD 20260901 在确认远程已接受的本地批次前触发持久化边界。</summary>
        void BeforeConfirmingBatches();

        /// <summary>XMZADD 20260901 在事务替换本地快照前触发持久化边界。</summary>
        void BeforeReplacingSnapshot();

        /// <summary>XMZADD 20260901 在推进共享字典持久游标前触发持久化边界。</summary>
        void BeforeSavingSyncState();
    }

    /// <summary>XMZADD 20260901 按先上传后拉取的固定顺序编排离线 outbox、增量修订和全量快照事务。</summary>
    public sealed class DictionarySyncCoordinator
    {
        private const int MaximumIncrementalTableCount = 10000;
        private static readonly int[] RetryMinutes = { 1, 2, 5, 15, 30 };
        private readonly LocalDictionaryStore _localStore;
        private readonly SnapshotStore _snapshotStore;
        private readonly IGitHubDictionaryClient _client;
        private readonly DictionaryRepositoryOptions _options;
        private readonly ISystemClock _clock;
        private readonly IDictionarySyncCommitObserver _commitObserver;
        private readonly SemaphoreSlim _syncLock;

        /// <summary>XMZADD 20260901 初始化同步依赖并验证仓库坐标，使所有网络和本地状态共享同一稳定作用域。</summary>
        public DictionarySyncCoordinator(
            LocalDictionaryStore localStore,
            SnapshotStore snapshotStore,
            IGitHubDictionaryClient client,
            DictionaryRepositoryOptions options,
            ISystemClock clock)
            : this(localStore, snapshotStore, client, options, clock, null)
        {
        }

        /// <summary>XMZADD 20260901 初始化带持久化边界观察器的同步器，使故障测试可核对远程确认优先顺序。</summary>
        internal DictionarySyncCoordinator(
            LocalDictionaryStore localStore,
            SnapshotStore snapshotStore,
            IGitHubDictionaryClient client,
            DictionaryRepositoryOptions options,
            ISystemClock clock,
            IDictionarySyncCommitObserver commitObserver)
        {
            if (localStore == null)
            {
                throw new ArgumentNullException("localStore");
            }
            if (snapshotStore == null)
            {
                throw new ArgumentNullException("snapshotStore");
            }
            if (client == null)
            {
                throw new ArgumentNullException("client");
            }
            if (options == null)
            {
                throw new ArgumentNullException("options");
            }
            if (clock == null)
            {
                throw new ArgumentNullException("clock");
            }
            options.Validate();
            _localStore = localStore;
            _snapshotStore = snapshotStore;
            _client = client;
            _options = options;
            _clock = clock;
            _commitObserver = commitObserver;
            _syncLock = new SemaphoreSlim(1, 1);
        }

        /// <summary>XMZADD 20260901 自动同步只提交已到重试时间的 Pending 批次，再拉取公开字典更新。</summary>
        public Task<SnapshotData> SyncAsync(CancellationToken cancellationToken)
        {
            return SyncCoreAsync(false, cancellationToken, null);
        }

        /// <summary>XMZADD 20260903 自动同步时报告上传与获取阶段，使主界面准确区分本机提交和远程刷新。</summary>
        public Task<SnapshotData> SyncAsync(CancellationToken cancellationToken, Action<DictionarySyncStage> reportStage)
        {
            return SyncCoreAsync(false, cancellationToken, reportStage);
        }

        /// <summary>XMZADD 20260901 手工同步忽略待上传和可恢复失败记录的重试时间，但不重复提交已提交或已完成批次。</summary>
        public Task<SnapshotData> SyncNowAsync(CancellationToken cancellationToken)
        {
            return SyncCoreAsync(true, cancellationToken, null);
        }

        /// <summary>XMZADD 20260903 手工同步时报告上传与获取阶段，使用户能看到当前网络动作。</summary>
        public Task<SnapshotData> SyncNowAsync(CancellationToken cancellationToken, Action<DictionarySyncStage> reportStage)
        {
            return SyncCoreAsync(true, cancellationToken, reportStage);
        }

        /// <summary>XMZADD 20260901 串行执行单次上传和拉取，避免自动任务与手工刷新同时推进同一本地游标。</summary>
        private async Task<SnapshotData> SyncCoreAsync(
            bool manual,
            CancellationToken cancellationToken,
            Action<DictionarySyncStage> reportStage)
        {
            await _syncLock.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reportStage != null)
                {
                    reportStage(DictionarySyncStage.UploadingChanges);
                }
                await UploadPendingOperationsAsync(manual, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (reportStage != null)
                {
                    reportStage(DictionarySyncStage.FetchingRemoteDictionary);
                }

                SnapshotManifest manifest = await _client.GetManifestAsync(cancellationToken);
                ValidateManifest(manifest);
                DictionarySyncState state = _localStore.LoadSyncState(_options.StateKey);
                int localTableCount;
                string localPayloadSha256;
                bool hasLocalSnapshot = _snapshotStore.TryGetScopeSummary(
                    _options.ScopeKey,
                    out localTableCount,
                    out localPayloadSha256);

                if (HasMatchingLocalSnapshot(state, manifest, hasLocalSnapshot, localPayloadSha256))
                {
                    await ConfirmSubmittedIssuesAsync(manifest.Revision, cancellationToken);
                    SaveSuccessfulState(manifest, localPayloadSha256);
                    return null;
                }

                SnapshotData localSnapshot = null;
                bool localSnapshotLoaded = false;
                if (hasLocalSnapshot && localTableCount <= MaximumIncrementalTableCount)
                {
                    localSnapshotLoaded = _snapshotStore.TryLoadLatest(_options.ScopeKey, out localSnapshot);
                }
                if (CanUseIncremental(
                    state,
                    localSnapshot,
                    localSnapshotLoaded,
                    localPayloadSha256,
                    manifest))
                {
                    bool incrementalApplied = await TryApplyIncrementalAsync(
                        state.Revision,
                        manifest,
                        localSnapshot,
                        cancellationToken);
                    if (incrementalApplied)
                    {
                        return localSnapshot;
                    }
                }

                return await ReplaceFromFullSnapshotAsync(manifest, cancellationToken);
            }
            finally
            {
                _syncLock.Release();
            }
        }

        /// <summary>XMZADD 20260903 用同步游标和本地压缩载荷哈希确认未变化快照，避免为状态检查反序列化近百万字段。</summary>
        private static bool HasMatchingLocalSnapshot(
            DictionarySyncState state,
            SnapshotManifest manifest,
            bool hasLocalSnapshot,
            string localPayloadSha256)
        {
            if (state == null || manifest == null || !hasLocalSnapshot || state.Revision != manifest.Revision)
            {
                return false;
            }
            return !string.IsNullOrWhiteSpace(manifest.SnapshotSha256) &&
                string.Equals(state.ManifestHash, manifest.SnapshotSha256, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(state.LocalPayloadHash) &&
                string.Equals(localPayloadSha256, state.LocalPayloadHash, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>XMZADD 20260903 依次提交可恢复的 outbox 批次，任何上传错误都保留人工修改供配置修复或升级后重试。</summary>
        private async Task UploadPendingOperationsAsync(bool manual, CancellationToken cancellationToken)
        {
            IList<PendingDictionaryOperation> pendingOperations = _localStore.LoadActivePendingOperations();
            for (int index = 0; index < pendingOperations.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PendingDictionaryOperation pending = pendingOperations[index];
                if (pending == null ||
                    (pending.Status != PendingOperationStatus.Pending && pending.Status != PendingOperationStatus.Failed))
                {
                    continue;
                }
                if (!manual && pending.NextAttemptAtUtc.HasValue && pending.NextAttemptAtUtc.Value > _clock.UtcNow)
                {
                    continue;
                }
                try
                {
                    int issueNumber = await _client.CreateDictionaryIssueAsync(pending.Batch, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    // Issue 只证明远端已受理，不代表事件已经进入规范修订。
                    _localStore.MarkOperationSubmitted(pending.OperationId, issueNumber);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (GitHubDictionaryClientException exception)
                {
                    if (exception.IsTransient)
                    {
                        DateTime nextAttemptAtUtc = CalculateNextAttemptAtUtc(pending, exception.RetryAfter);
                        _localStore.RecordOperationRetry(
                            pending.OperationId,
                            nextAttemptAtUtc,
                            "GitHub 暂时不可用，将自动重试。");
                    }
                    else
                    {
                        DateTime nextAttemptAtUtc = CalculateNextAttemptAtUtc(pending, null);
                        _localStore.RecordOperationRetry(
                            pending.OperationId,
                            nextAttemptAtUtc,
                            GetPermanentUploadMessage(exception.Kind));
                    }
                }
            }
        }

        /// <summary>XMZADD 20260901 计算固定 1、2、5、15、30 分钟退避，并在限流提示更长时仍封顶三十分钟。</summary>
        private DateTime CalculateNextAttemptAtUtc(
            PendingDictionaryOperation pending,
            TimeSpan? serverRetryAfter)
        {
            int retryIndex = pending.AttemptCount;
            if (retryIndex < 0)
            {
                retryIndex = 0;
            }
            if (retryIndex >= RetryMinutes.Length)
            {
                retryIndex = RetryMinutes.Length - 1;
            }
            double minutes = RetryMinutes[retryIndex];
            if (serverRetryAfter.HasValue && serverRetryAfter.Value.TotalMinutes > minutes)
            {
                minutes = serverRetryAfter.Value.TotalMinutes;
            }
            if (minutes > 30)
            {
                minutes = 30;
            }
            if (minutes < 1)
            {
                minutes = 1;
            }
            return _clock.UtcNow.AddMinutes(minutes);
        }

        /// <summary>XMZADD 20260901 将认证、协议或权限类永久上传失败转换为可直接展示的有限诊断。</summary>
        private static string GetPermanentUploadMessage(GitHubDictionaryErrorKind kind)
        {
            if (kind == GitHubDictionaryErrorKind.Authentication)
            {
                return "GitHub 认证失败，请重新配置个人 Token。";
            }
            if (kind == GitHubDictionaryErrorKind.NotFound)
            {
                return "GitHub 字典仓库或 Issue 接口不存在。";
            }
            return "字典批次未通过 GitHub 提交校验，请检查本地编辑。";
        }

        /// <summary>XMZADD 20260901 判断本地游标和持久快照严格一致且远端向前推进时才允许增量重放。</summary>
        private static bool CanUseIncremental(
            DictionarySyncState state,
            SnapshotData localSnapshot,
            bool hasLocalSnapshot,
            string localPayloadSha256,
            SnapshotManifest manifest)
        {
            return state != null && hasLocalSnapshot && localSnapshot != null &&
                !string.IsNullOrWhiteSpace(state.LocalPayloadHash) &&
                string.Equals(state.LocalPayloadHash, localPayloadSha256, StringComparison.OrdinalIgnoreCase) &&
                state.Revision >= 0 && localSnapshot.Revision == state.Revision &&
                manifest.Revision > state.Revision;
        }

        /// <summary>XMZADD 20260901 在内存连续应用全部服务器修订，缺口或事件链损坏时不持久化半成品并请求全量回退。</summary>
        private async Task<bool> TryApplyIncrementalAsync(
            long localRevision,
            SnapshotManifest manifest,
            SnapshotData workingSnapshot,
            CancellationToken cancellationToken)
        {
            var confirmedOperationIds = new HashSet<string>(StringComparer.Ordinal);
            var eventApplyService = new DictionaryEventApplyService();
            try
            {
                for (long revision = localRevision + 1; revision <= manifest.Revision; revision++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    DictionaryRevisionPackage package = await _client.DownloadRevisionAsync(revision, cancellationToken);
                    DictionaryChangeBatch batch = MergeAndValidateRevision(package, revision, confirmedOperationIds);
                    eventApplyService.ApplyTrustedRepositoryBatch(workingSnapshot, batch, revision);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (GitHubDictionaryClientException exception)
            {
                if (exception.Kind == GitHubDictionaryErrorKind.NotFound ||
                    exception.Kind == GitHubDictionaryErrorKind.Validation)
                {
                    return false;
                }
                throw;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            catch (ArgumentException)
            {
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            // 全部连续修订验证通过后才一次性替换持久快照，避免后续修订失败留下半条事件链。
            if (_commitObserver != null)
            {
                _commitObserver.BeforeConfirmingBatches();
            }
            MarkConfirmedBatchesCompleted(confirmedOperationIds);
            if (_commitObserver != null)
            {
                _commitObserver.BeforeReplacingSnapshot();
            }
            _snapshotStore.ReplaceScope(_options.ScopeKey, workingSnapshot, cancellationToken);
            SaveSuccessfulState(manifest, ReadLocalPayloadSha256());
            return true;
        }

        /// <summary>XMZADD 20260901 校验单个修订包并把多作者、多 Issue 批次合并为一个可信仓库事务。</summary>
        private static DictionaryChangeBatch MergeAndValidateRevision(
            DictionaryRevisionPackage package,
            long expectedRevision,
            HashSet<string> confirmedOperationIds)
        {
            if (package == null || package.FormatVersion != 1 || package.Revision != expectedRevision ||
                package.Batches == null || package.Batches.Count == 0)
            {
                throw new InvalidOperationException("远程修订包不兼容。");
            }
            var mergedBatch = new DictionaryChangeBatch
            {
                BatchId = "repository-revision-" + expectedRevision,
                AuthorGitHubUserId = string.Empty,
                CreatedAtUtc = package.GeneratedAtUtc
            };
            for (int batchIndex = 0; batchIndex < package.Batches.Count; batchIndex++)
            {
                DictionaryChangeBatch sourceBatch = package.Batches[batchIndex];
                if (sourceBatch == null || sourceBatch.Operations == null)
                {
                    throw new InvalidOperationException("远程修订批次无效。");
                }
                for (int operationIndex = 0; operationIndex < sourceBatch.Operations.Count; operationIndex++)
                {
                    DictionaryChangeOperation operation = sourceBatch.Operations[operationIndex];
                    if (operation == null || string.IsNullOrWhiteSpace(operation.OperationId) ||
                        !confirmedOperationIds.Add(operation.OperationId))
                    {
                        throw new InvalidOperationException("远程修订包含空或重复操作号。");
                    }
                    mergedBatch.Operations.Add(operation);
                }
            }
            if (mergedBatch.Operations.Count == 0)
            {
                throw new InvalidOperationException("远程修订不包含可重放事件。");
            }
            return mergedBatch;
        }

        /// <summary>XMZADD 20260901 在增量成功后仅确认其全部操作号都已出现在远程事件链中的 Submitted 批次。</summary>
        private void MarkConfirmedBatchesCompleted(HashSet<string> confirmedOperationIds)
        {
            IList<PendingDictionaryOperation> pendingOperations = _localStore.LoadActivePendingOperations();
            for (int index = 0; index < pendingOperations.Count; index++)
            {
                PendingDictionaryOperation pending = pendingOperations[index];
                if (pending == null || pending.Status != PendingOperationStatus.Submitted || pending.Batch == null ||
                    pending.Batch.Operations == null || pending.Batch.Operations.Count == 0)
                {
                    continue;
                }
                bool allConfirmed = true;
                for (int operationIndex = 0; operationIndex < pending.Batch.Operations.Count; operationIndex++)
                {
                    DictionaryChangeOperation operation = pending.Batch.Operations[operationIndex];
                    if (operation == null || !confirmedOperationIds.Contains(operation.OperationId))
                    {
                        allConfirmed = false;
                        break;
                    }
                }
                if (allConfirmed)
                {
                    _localStore.MarkOperationCompleted(pending.OperationId);
                }
            }
        }

        /// <summary>XMZADD 20260901 下载并完整校验全量快照，修订匹配后事务替换且最后保存同步游标。</summary>
        private async Task<SnapshotData> ReplaceFromFullSnapshotAsync(
            SnapshotManifest manifest,
            CancellationToken cancellationToken)
        {
            byte[] compressedContent = await _client.DownloadSnapshotAsync(manifest.SnapshotPath, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var codec = new SnapshotCodec();
            SnapshotData snapshot = codec.DecodeAndValidate(compressedContent, manifest.SnapshotSha256);
            if (snapshot.FormatVersion != manifest.FormatVersion || snapshot.Revision != manifest.Revision)
            {
                throw new InvalidDataException("远程规范快照与清单修订不一致。");
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (_commitObserver != null)
            {
                _commitObserver.BeforeReplacingSnapshot();
            }
            _snapshotStore.ReplaceValidatedScopeFromRemote(
                _options.ScopeKey,
                snapshot,
                compressedContent,
                manifest.SnapshotSha256,
                cancellationToken);
            await ConfirmSubmittedIssuesAsync(manifest.Revision, cancellationToken);
            SaveSuccessfulState(manifest, ReadLocalPayloadSha256());
            return snapshot;
        }

        /// <summary>XMZADD 20260903 通过主分支不可变事件链确认全量回退遗漏的本机提交，避免待生效永久卡住。</summary>
        private async Task ConfirmSubmittedIssuesAsync(long maximumRevision, CancellationToken cancellationToken)
        {
            IList<PendingDictionaryOperation> pendingOperations = _localStore.LoadActivePendingOperations();
            for (int index = 0; index < pendingOperations.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PendingDictionaryOperation pending = pendingOperations[index];
                if (pending == null || pending.Status != PendingOperationStatus.Submitted ||
                    !pending.GitHubIssueNumber.HasValue || pending.GitHubIssueNumber.Value <= 0)
                {
                    continue;
                }
                DictionaryIssueApplyStatus applyStatus;
                try
                {
                    applyStatus = await _client.GetDictionaryOperationsApplyStatusAsync(
                        pending.Batch,
                        pending.GitHubIssueNumber.Value,
                        maximumRevision,
                        cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (GitHubDictionaryClientException exception)
                {
                    if (exception.Kind == GitHubDictionaryErrorKind.Validation)
                    {
                        throw;
                    }
                    // 暂时无法核验只影响待生效状态，不能否决已经校验并落盘的规范快照。
                    continue;
                }
                if (applyStatus == DictionaryIssueApplyStatus.Applied)
                {
                    _localStore.MarkOperationCompleted(pending.OperationId);
                }
                else if (applyStatus == DictionaryIssueApplyStatus.Rejected)
                {
                    // 远端校验拒绝属于不可重试业务结论，保留审计但不能继续占用待生效队列。
                    _localStore.MarkOperationRejected(
                        pending.OperationId,
                        "GitHub Issue 校验失败，未进入规范快照。");
                }
            }
        }

        /// <summary>XMZADD 20260901 校验协调器收到的清单基本契约，避免测试替身或未来客户端绕过版本边界。</summary>
        private static void ValidateManifest(SnapshotManifest manifest)
        {
            if (manifest == null || manifest.FormatVersion != 1 || manifest.Revision < 0 ||
                string.IsNullOrWhiteSpace(manifest.SnapshotPath))
            {
                throw new InvalidDataException("远程快照清单不兼容。");
            }
        }

        /// <summary>XMZADD 20260901 仅在拉取或替换成功后保存远端修订、哈希和固定 UTC 成功时间。</summary>
        private void SaveSuccessfulState(SnapshotManifest manifest, string localPayloadSha256)
        {
            DictionarySyncState previous = _localStore.LoadSyncState(_options.StateKey);
            if (_commitObserver != null)
            {
                _commitObserver.BeforeSavingSyncState();
            }
            _localStore.SaveSyncState(new DictionarySyncState
            {
                StateKey = _options.StateKey,
                Revision = manifest.Revision,
                ManifestHash = manifest.SnapshotSha256,
                LocalPayloadHash = localPayloadSha256,
                LastSyncAtUtc = _clock.UtcNow,
                LastSuccessfulOperationId = previous == null ? null : previous.LastSuccessfulOperationId
            });
        }

        /// <summary>XMZADD 20260903 读取刚持久化载荷的实际哈希，使本地完整性校验与远端规范版本哈希各自独立。</summary>
        private string ReadLocalPayloadSha256()
        {
            int tableCount;
            string localPayloadSha256;
            if (!_snapshotStore.TryGetScopeSummary(_options.ScopeKey, out tableCount, out localPayloadSha256) ||
                string.IsNullOrWhiteSpace(localPayloadSha256))
            {
                throw new InvalidDataException("本地规范快照持久化后无法读取完整性摘要。");
            }
            return localPayloadSha256;
        }
    }
}
