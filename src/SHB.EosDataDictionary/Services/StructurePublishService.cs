using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SHB.EosDataDictionary.Models;

namespace SHB.EosDataDictionary.Services
{
    /// <summary>XMZADD 20260901 标识一次只读结构维护的安全终态，避免界面根据异常文本猜测是否已发布。</summary>
    public enum StructurePublishOutcome
    {
        Published = 0,
        NoChanges = 1,
        NotAuthorized = 2,
        NotConfigured = 3,
        ScanFailed = 4,
        ValidationFailed = 5,
        ReadyToPublish = 6
    }

    /// <summary>XMZADD 20260902 标识只读扫描的失败边界，使界面能给出不泄露配置的可执行提示。</summary>
    public enum StructureScanFailureStage
    {
        None = 0,
        PreviousSnapshot = 1,
        Database = 2,
        Source = 3
    }

    /// <summary>XMZADD 20260902 标识不含本机配置和远端正文的结构发布校验原因，供界面给出可执行提示。</summary>
    public enum StructurePublishValidationReason
    {
        None = 0,
        PublicContentInvalid = 1,
        OperationLimitExceeded = 2,
        FullSnapshotLimitExceeded = 3,
        FullSnapshotInvalid = 4,
        FullSnapshotPublisherUnavailable = 5,
        RemoteSnapshotChanged = 6,
        PreviewChanged = 7,
        FullSnapshotSizeExceeded = 8,
        FullSnapshotIncomplete = 9
    }

    /// <summary>XMZADD 20260902 标识结构预览的只读处理阶段，使界面能展示当前工作位置而非长期显示笼统扫描文字。</summary>
    public enum StructureScanStage
    {
        PreviousSnapshot = 0,
        DatabaseMetadata = 1,
        SourceEvidence = 2,
        MetadataEnrichment = 3,
        KnowledgeBase = 4,
        NameInference = 5,
        SnapshotPruning = 6,
        DifferenceAnalysis = 7
    }

    /// <summary>XMZADD 20260902 向界面传递不含连接配置、源码路径和凭据的结构扫描阶段提示。</summary>
    public sealed class StructureScanProgress
    {
        public StructureScanStage Stage { get; internal set; }
        public string Message { get; internal set; }
    }

    /// <summary>XMZADD 20260901 保存结构发布者本机只读连接、源码与可选知识库目录。</summary>
    public sealed class StructurePublishRequest
    {
        public string ScopeKey { get; set; }
        public ConnectionProfile Profile { get; set; }
        public string SourceRoot { get; set; }
        public string KnowledgeBaseRoot { get; set; }
    }

    /// <summary>XMZADD 20260901 返回结构发布结果、降级状态和已确认 Issue 批次，且不暴露本机配置。</summary>
    public sealed class StructurePublishResult
    {
        public StructurePublishOutcome Outcome { get; internal set; }
        public StructureScanFailureStage FailureStage { get; internal set; }
        public StructurePublishValidationReason ValidationReason { get; internal set; }
        public bool KnowledgeBaseDegraded { get; internal set; }
        public int? IssueNumber { get; internal set; }
        public string CommitSha { get; internal set; }
        public DictionaryChangeBatch Batch { get; internal set; }
        public SchemaDiffResult Diff { get; internal set; }
        public bool IsFullSnapshot { get; internal set; }
        public int FullSnapshotTableCount { get; internal set; }
        public int FullSnapshotFieldCount { get; internal set; }
        public long FullSnapshotCompressedBytes { get; internal set; }
        internal string PreviewJson { get; set; }
        internal byte[] FullSnapshotContent { get; set; }
        internal SnapshotManifest FullSnapshotManifest { get; set; }
        internal string BoundPublisherGitHubUserId { get; set; }
        internal string BoundRepositoryHeadSha { get; set; }
    }

    /// <summary>XMZADD 20260901 只读扫描本机 EOS 结构和源码并以单个 GitHub Issue 原子发布结构差异。</summary>
    public sealed class StructurePublishService
    {
        private const int MaximumPublishedEvidencePerOperation = 5;
        private const int MaximumEvidencePathLength = 512;
        private const int MaximumEvidenceTextLength = 240;
        private readonly IGitHubDictionaryClient _gitHubClient;
        private readonly Func<string, SnapshotData> _previousSnapshotReader;
        private readonly Func<ConnectionProfile, CancellationToken, SnapshotData> _metadataReader;
        private readonly Func<string, CancellationToken, IList<SourceEvidence>> _sourceAnalyzer;
        private readonly IStructureNameInference _nameInference;

        /// <summary>XMZADD 20260901 注入全部外部读取边界，使测试可证明流程不访问真实 GitHub 或 EOS 正式库。</summary>
        public StructurePublishService(
            IGitHubDictionaryClient gitHubClient,
            Func<string, SnapshotData> previousSnapshotReader,
            Func<ConnectionProfile, CancellationToken, SnapshotData> metadataReader,
            Func<string, CancellationToken, IList<SourceEvidence>> sourceAnalyzer)
            : this(gitHubClient, previousSnapshotReader, metadataReader, sourceAnalyzer,
                new StructureNameInferenceService(null))
        {
        }

        /// <summary>XMZADD 20260901 注入结构名称推理边界，使正式流程可组合共享缩写和本机可选 AI 且测试不访问网络。</summary>
        public StructurePublishService(
            IGitHubDictionaryClient gitHubClient,
            Func<string, SnapshotData> previousSnapshotReader,
            Func<ConnectionProfile, CancellationToken, SnapshotData> metadataReader,
            Func<string, CancellationToken, IList<SourceEvidence>> sourceAnalyzer,
            IStructureNameInference nameInference)
        {
            _gitHubClient = gitHubClient ?? throw new ArgumentNullException("gitHubClient");
            _previousSnapshotReader = previousSnapshotReader ?? throw new ArgumentNullException("previousSnapshotReader");
            _metadataReader = metadataReader ?? throw new ArgumentNullException("metadataReader");
            _sourceAnalyzer = sourceAnalyzer ?? throw new ArgumentNullException("sourceAnalyzer");
            _nameInference = nameInference ?? throw new ArgumentNullException("nameInference");
        }

        /// <summary>XMZADD 20260901 仅依据远程不可变 GitHub 数值 ID 判断能否进入首次配置窗口。</summary>
        public async Task<bool> CanOpenAsync(CancellationToken cancellationToken)
        {
            PublisherContext context = await ReadPublisherContextAsync(cancellationToken).ConfigureAwait(false);
            return context.IsPublisher;
        }

        /// <summary>XMZADD 20260901 在发布者身份之外校验本机只读连接和源码目录是否足以开始扫描。</summary>
        public async Task<bool> CanPublishAsync(
            StructurePublishRequest request,
            CancellationToken cancellationToken)
        {
            PublisherContext context = await ReadPublisherContextAsync(cancellationToken).ConfigureAwait(false);
            return context.IsPublisher && IsRequestConfigured(request);
        }

        /// <summary>XMZADD 20260901 执行只读扫描并在安全发布前返回可供用户确认的结构差异预览。</summary>
        public async Task<StructurePublishResult> PreviewAsync(
            StructurePublishRequest request,
            CancellationToken cancellationToken,
            IProgress<StructureScanProgress> progress = null)
        {
            return await PreparePreviewAsync(request, cancellationToken, progress).ConfigureAwait(false);
        }

        /// <summary>XMZADD 20260901 执行上一快照、只读扫描、证据翻译、清理、差异和单 Issue 的原子发布流水线。</summary>
        public async Task<StructurePublishResult> PublishAsync(
            StructurePublishRequest request,
            CancellationToken cancellationToken)
        {
            StructurePublishResult preview = await PreparePreviewAsync(request, cancellationToken, null).ConfigureAwait(false);
            if (preview.Outcome != StructurePublishOutcome.ReadyToPublish)
            {
                return preview;
            }
            return await PublishPreviewAsync(preview, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>XMZADD 20260902 生成绑定当前发布者身份且覆盖全部用户表的结构预览，但不创建远程 Issue。</summary>
        private async Task<StructurePublishResult> PreparePreviewAsync(
            StructurePublishRequest request,
            CancellationToken cancellationToken,
            IProgress<StructureScanProgress> progress)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PublisherContext initialContext = await ReadPublisherContextAsync(cancellationToken).ConfigureAwait(false);
            if (!initialContext.IsPublisher)
            {
                return CreateResult(StructurePublishOutcome.NotAuthorized, false, null, null, null);
            }
            if (!IsRequestConfigured(request))
            {
                return CreateResult(StructurePublishOutcome.NotConfigured, false, null, null, null);
            }

            SnapshotData previous;
            SnapshotData current;
            IList<SourceEvidence> sourceEvidence;
            try
            {
                ReportProgress(progress, StructureScanStage.PreviousSnapshot);
                previous = _previousSnapshotReader(request.ScopeKey) ?? new SnapshotData();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                if (exception is OutOfMemoryException || exception is StackOverflowException)
                {
                    throw;
                }
                return CreateResult(
                    StructurePublishOutcome.ScanFailed,
                    false,
                    null,
                    null,
                    null,
                    StructureScanFailureStage.PreviousSnapshot);
            }

            try
            {
                ReportProgress(progress, StructureScanStage.DatabaseMetadata);
                current = _metadataReader(request.Profile, cancellationToken);
                if (current == null)
                {
                    return CreateResult(
                        StructurePublishOutcome.ScanFailed,
                        false,
                        null,
                        null,
                        null,
                        StructureScanFailureStage.Database);
                }
                if (current.Tables == null)
                {
                    // 缺少表集合代表数据库结构读取结果不完整，禁止裁剪服务把它归一化为空快照。
                    return CreateResult(
                        StructurePublishOutcome.ValidationFailed,
                        false,
                        null,
                        null,
                        null,
                        StructureScanFailureStage.None,
                        StructurePublishValidationReason.FullSnapshotInvalid);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                if (exception is OutOfMemoryException || exception is StackOverflowException)
                {
                    throw;
                }
                return CreateResult(
                    StructurePublishOutcome.ScanFailed,
                    false,
                    null,
                    null,
                    null,
                    StructureScanFailureStage.Database);
            }

            try
            {
                ReportProgress(progress, StructureScanStage.SourceEvidence);
                sourceEvidence = _sourceAnalyzer(request.SourceRoot, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                if (exception is OutOfMemoryException || exception is StackOverflowException)
                {
                    throw;
                }
                return CreateResult(
                    StructurePublishOutcome.ScanFailed,
                    false,
                    null,
                    null,
                    null,
                    StructureScanFailureStage.Source);
            }

            IList<SourceEvidence> safeSourceEvidence;
            try
            {
                ReportProgress(progress, StructureScanStage.MetadataEnrichment);
                safeSourceEvidence = SanitizeSourceEvidence(sourceEvidence, request.SourceRoot);
            }
            catch (InvalidDataException)
            {
                return CreateResult(StructurePublishOutcome.ValidationFailed, false, null, null, null);
            }

            ApplyRetainedTableClassification(previous, current);
            // 数据库结构扫描无法独立恢复源码实体，必须先继承上一规范的可信映射再合并本次新证据。
            new EntityMappingRetentionService().Apply(previous, current);
            MetadataEnrichmentService.Enrich(current, safeSourceEvidence);
            bool knowledgeBaseDegraded = !IsKnowledgeBaseAvailable(request.KnowledgeBaseRoot);
            EosKnowledgeBaseTranslationService knowledgeBase = null;
            if (!knowledgeBaseDegraded)
            {
                ReportProgress(progress, StructureScanStage.KnowledgeBase);
                knowledgeBase = new EosKnowledgeBaseTranslationService(request.KnowledgeBaseRoot);
                knowledgeBaseDegraded = !ApplyKnowledgeBaseTranslations(
                    current,
                    knowledgeBase,
                    cancellationToken);
            }
            // 缩写和 AI 结果只作为 Guessed 补充；知识库事务降级时禁止推理器复用部分知识状态。
            ReportProgress(progress, StructureScanStage.NameInference);
            _nameInference.Enrich(current, safeSourceEvidence,
                knowledgeBaseDegraded ? null : knowledgeBase, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            ReportProgress(progress, StructureScanStage.SnapshotPruning);
            SnapshotPruningResult pruning;
            try
            {
                pruning = new SnapshotPruningService().PruneCompletePublicSnapshotForPublishing(current);
            }
            catch (IncompletePublicSnapshotException)
            {
                // 扫描缺失字段时禁止继续生成差异，统一返回安全校验结果避免界面因内部异常崩溃。
                return CreateResult(
                    StructurePublishOutcome.ValidationFailed,
                    knowledgeBaseDegraded,
                    null,
                    null,
                    null,
                    StructureScanFailureStage.None,
                    StructurePublishValidationReason.FullSnapshotIncomplete);
            }
            catch (InvalidDataException)
            {
                return CreateResult(
                    StructurePublishOutcome.ValidationFailed,
                    knowledgeBaseDegraded,
                    null,
                    null,
                    null,
                    StructureScanFailureStage.None,
                    StructurePublishValidationReason.PublicContentInvalid);
            }
            SnapshotData publishedSnapshot = pruning.Snapshot;
            ReportProgress(progress, StructureScanStage.DifferenceAnalysis);
            SchemaDiffResult diff = new SchemaDiffService().Compare(request.ScopeKey, previous, publishedSnapshot);
            if (IsEmptySnapshot(previous) && !IsEmptySnapshot(publishedSnapshot))
            {
                SnapshotManifest remoteManifest = await _gitHubClient.GetManifestAsync(cancellationToken).ConfigureAwait(false);
                if (remoteManifest != null && remoteManifest.Revision == 0L)
                {
                    IGitHubFullSnapshotPublisher fullSnapshotPublisher =
                        _gitHubClient as IGitHubFullSnapshotPublisher;
                    if (fullSnapshotPublisher == null)
                    {
                        return CreateResult(
                            StructurePublishOutcome.ValidationFailed,
                            knowledgeBaseDegraded,
                            null,
                            null,
                            diff,
                            StructureScanFailureStage.None,
                            StructurePublishValidationReason.FullSnapshotPublisherUnavailable);
                    }
                    string observedHeadSha = await fullSnapshotPublisher.GetRevisionZeroHeadShaAsync(
                        cancellationToken).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(observedHeadSha))
                    {
                        return CreateResult(
                            StructurePublishOutcome.ValidationFailed,
                            knowledgeBaseDegraded,
                            null,
                            null,
                            diff,
                            StructureScanFailureStage.None,
                            StructurePublishValidationReason.RemoteSnapshotChanged);
                    }
                    return CreateFullSnapshotPreview(
                        publishedSnapshot,
                        diff,
                        initialContext.Identity.GitHubUserId,
                        observedHeadSha,
                        knowledgeBaseDegraded);
                }
            }
            DictionaryChangeBatch batch;
            try
            {
                batch = BuildBatch(previous, publishedSnapshot, diff, initialContext.Identity.GitHubUserId, request);
            }
            catch (InvalidDataException exception)
            {
                StructurePublishValidationReason reason = string.Equals(
                    exception.Message,
                    "单次结构变化超过公开 Issue 协议上限。",
                    StringComparison.Ordinal)
                    ? StructurePublishValidationReason.OperationLimitExceeded
                    : StructurePublishValidationReason.PublicContentInvalid;
                return CreateResult(
                    StructurePublishOutcome.ValidationFailed,
                    knowledgeBaseDegraded,
                    null,
                    null,
                    diff,
                    StructureScanFailureStage.None,
                    reason);
            }
            if (batch.Operations.Count == 0)
            {
                return CreateResult(StructurePublishOutcome.NoChanges, knowledgeBaseDegraded, null, null, diff);
            }

            // 预览绑定扫描开始时的发布者身份，确认发布阶段会再次核验同一身份和名单。
            StructurePublishResult preview = CreateResult(
                StructurePublishOutcome.ReadyToPublish,
                knowledgeBaseDegraded,
                null,
                batch,
                diff);
            preview.PreviewJson = DictionaryJsonSerializer.SerializeBatch(batch);
            return preview;
        }

        /// <summary>XMZADD 20260901 重新核验预览完整性和远程发布资格后创建唯一结构 Issue。</summary>
        public async Task<StructurePublishResult> PublishPreviewAsync(
            StructurePublishResult preview,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (preview != null && preview.IsFullSnapshot)
            {
                return await PublishFullSnapshotPreviewAsync(preview, cancellationToken).ConfigureAwait(false);
            }
            if (preview == null || preview.Outcome != StructurePublishOutcome.ReadyToPublish ||
                preview.Batch == null || string.IsNullOrEmpty(preview.PreviewJson) ||
                !string.Equals(preview.PreviewJson, DictionaryJsonSerializer.SerializeBatch(preview.Batch), StringComparison.Ordinal))
            {
                return CreateResult(
                    StructurePublishOutcome.ValidationFailed,
                    false,
                    null,
                    null,
                    null,
                    StructureScanFailureStage.None,
                    StructurePublishValidationReason.PreviewChanged);
            }

            DictionaryChangeBatch boundBatch;
            try
            {
                // 后续异步授权只使用绑定 JSON 的私有副本，避免等待期间可变预览对象改变已确认内容。
                boundBatch = DictionaryJsonSerializer.DeserializeBatch(preview.PreviewJson);
                ClearLocalEvidenceExtensionData(boundBatch);
            }
            catch (Exception exception)
            {
                if (exception is OutOfMemoryException || exception is StackOverflowException)
                {
                    throw;
                }
                return CreateResult(StructurePublishOutcome.ValidationFailed, false, null, null, null);
            }

            // 确认发布前重新读取名单，避免预览期间发布资格被仓库撤销或切换了 GitHub 身份。
            PublisherContext finalContext = await ReadPublisherContextAsync(cancellationToken).ConfigureAwait(false);
            if (!finalContext.IsPublisher || finalContext.Identity == null || !string.Equals(
                boundBatch.AuthorGitHubUserId,
                finalContext.Identity.GitHubUserId,
                StringComparison.Ordinal))
            {
                return CreateResult(
                    StructurePublishOutcome.NotAuthorized,
                    preview.KnowledgeBaseDegraded,
                    null,
                    null,
                    preview.Diff);
            }

            DictionaryChangeValidationResult validation = new DictionaryChangeValidator().ValidateAndNormalize(
                boundBatch,
                finalContext.Identity.GitHubUserId,
                finalContext.PublisherIds);
            if (!validation.IsValid || !IsBatchSafeForPublicIssue(validation.NormalizedBatch))
            {
                return CreateResult(
                    StructurePublishOutcome.ValidationFailed,
                    preview.KnowledgeBaseDegraded,
                    null,
                    null,
                    preview.Diff);
            }

            int issueNumber = await _gitHubClient.CreateDictionaryIssueAsync(
                validation.NormalizedBatch,
                cancellationToken).ConfigureAwait(false);
            return CreateResult(
                StructurePublishOutcome.Published,
                preview.KnowledgeBaseDegraded,
                issueNumber,
                validation.NormalizedBatch,
                preview.Diff);
        }

        /// <summary>XMZADD 20260902 重新核验首次快照、发布者和远程 Revision 0 后调用单 Commit 发布边界。</summary>
        private async Task<StructurePublishResult> PublishFullSnapshotPreviewAsync(
            StructurePublishResult preview,
            CancellationToken cancellationToken)
        {
            IGitHubFullSnapshotPublisher publisher = _gitHubClient as IGitHubFullSnapshotPublisher;
            if (preview.Outcome != StructurePublishOutcome.ReadyToPublish ||
                preview.FullSnapshotContent == null || preview.FullSnapshotManifest == null ||
                string.IsNullOrWhiteSpace(preview.BoundPublisherGitHubUserId) ||
                string.IsNullOrWhiteSpace(preview.BoundRepositoryHeadSha))
            {
                return CreateResult(
                    StructurePublishOutcome.ValidationFailed,
                    preview.KnowledgeBaseDegraded,
                    null,
                    null,
                    preview.Diff,
                    StructureScanFailureStage.None,
                    StructurePublishValidationReason.PreviewChanged);
            }
            if (publisher == null)
            {
                return CreateResult(
                    StructurePublishOutcome.ValidationFailed,
                    preview.KnowledgeBaseDegraded,
                    null,
                    null,
                    preview.Diff,
                    StructureScanFailureStage.None,
                    StructurePublishValidationReason.FullSnapshotPublisherUnavailable);
            }

            byte[] contentCopy = new byte[preview.FullSnapshotContent.Length];
            Buffer.BlockCopy(preview.FullSnapshotContent, 0, contentCopy, 0, contentCopy.Length);
            try
            {
                var codec = new SnapshotCodec();
                string sha256 = codec.ComputeSha256(contentCopy);
                SnapshotData decoded = codec.DecodeAndValidate(contentCopy, sha256);
                if (decoded.Revision != 1L ||
                    !string.Equals(sha256, preview.FullSnapshotManifest.SnapshotSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException();
                }
            }
            catch (Exception exception)
            {
                if (exception is OutOfMemoryException || exception is StackOverflowException)
                {
                    throw;
                }
                return CreateResult(
                    StructurePublishOutcome.ValidationFailed,
                    preview.KnowledgeBaseDegraded,
                    null,
                    null,
                    preview.Diff,
                    StructureScanFailureStage.None,
                    StructurePublishValidationReason.PreviewChanged);
            }

            PublisherContext finalContext = await ReadPublisherContextAsync(cancellationToken).ConfigureAwait(false);
            if (!finalContext.IsPublisher || finalContext.Identity == null || !string.Equals(
                preview.BoundPublisherGitHubUserId,
                finalContext.Identity.GitHubUserId,
                StringComparison.Ordinal))
            {
                return CreateResult(
                    StructurePublishOutcome.NotAuthorized,
                    preview.KnowledgeBaseDegraded,
                    null,
                    null,
                    preview.Diff);
            }

            SnapshotManifest remoteManifest = await _gitHubClient.GetManifestAsync(cancellationToken).ConfigureAwait(false);
            if (remoteManifest == null || remoteManifest.Revision != 0L)
            {
                return CreateResult(
                    StructurePublishOutcome.ValidationFailed,
                    preview.KnowledgeBaseDegraded,
                    null,
                    null,
                    preview.Diff,
                    StructureScanFailureStage.None,
                    StructurePublishValidationReason.RemoteSnapshotChanged);
            }

            string currentHeadSha = await publisher.GetRevisionZeroHeadShaAsync(
                cancellationToken).ConfigureAwait(false);
            if (!string.Equals(
                currentHeadSha,
                preview.BoundRepositoryHeadSha,
                StringComparison.OrdinalIgnoreCase))
            {
                return CreateResult(
                    StructurePublishOutcome.ValidationFailed,
                    preview.KnowledgeBaseDegraded,
                    null,
                    null,
                    preview.Diff,
                    StructureScanFailureStage.None,
                    StructurePublishValidationReason.RemoteSnapshotChanged);
            }

            string commitSha = await publisher.PublishFullSnapshotAsync(
                contentCopy,
                preview.FullSnapshotManifest,
                preview.BoundRepositoryHeadSha,
                preview.BoundPublisherGitHubUserId,
                cancellationToken).ConfigureAwait(false);
            StructurePublishResult result = CreateResult(
                StructurePublishOutcome.Published,
                preview.KnowledgeBaseDegraded,
                null,
                null,
                preview.Diff);
            result.CommitSha = commitSha;
            result.IsFullSnapshot = true;
            result.FullSnapshotTableCount = preview.FullSnapshotTableCount;
            result.FullSnapshotFieldCount = preview.FullSnapshotFieldCount;
            result.FullSnapshotCompressedBytes = preview.FullSnapshotCompressedBytes;
            return result;
        }

        /// <summary>XMZADD 20260901 清除本地已绑定 JSON 往返产生的空扩展容器，避免把它误判为远程未知证据字段。</summary>
        private static void ClearLocalEvidenceExtensionData(DictionaryChangeBatch batch)
        {
            if (batch == null || batch.Operations == null)
            {
                return;
            }
            for (int operationIndex = 0; operationIndex < batch.Operations.Count; operationIndex++)
            {
                DictionaryChangeOperation operation = batch.Operations[operationIndex];
                if (operation == null || operation.Evidence == null)
                {
                    continue;
                }
                for (int evidenceIndex = 0; evidenceIndex < operation.Evidence.Count; evidenceIndex++)
                {
                    EvidenceItem evidence = operation.Evidence[evidenceIndex];
                    if (evidence != null)
                    {
                        evidence.ExtensionData = null;
                    }
                }
            }
        }

        /// <summary>XMZADD 20260901 同时读取当前身份和远程发布者名单，确保授权判断不依赖本机可伪造状态。</summary>
        private async Task<PublisherContext> ReadPublisherContextAsync(CancellationToken cancellationToken)
        {
            GitHubUserIdentity identity = await _gitHubClient.GetCurrentUserAsync(cancellationToken).ConfigureAwait(false);
            IList<string> publisherIds = await _gitHubClient.GetPublisherIdsAsync(cancellationToken).ConfigureAwait(false);
            bool isPublisher = false;
            if (identity != null && !string.IsNullOrEmpty(identity.GitHubUserId) && publisherIds != null)
            {
                for (int index = 0; index < publisherIds.Count; index++)
                {
                    if (string.Equals(identity.GitHubUserId, publisherIds[index], StringComparison.Ordinal))
                    {
                        isPublisher = true;
                        break;
                    }
                }
            }
            return new PublisherContext(identity, publisherIds, isPublisher);
        }

        /// <summary>XMZADD 20260901 要求本机连接明确为只读并拥有完整源码目录，防止发布入口退化成普通连接管理。</summary>
        private static bool IsRequestConfigured(StructurePublishRequest request)
        {
            if (request == null || request.Profile == null || !request.Profile.IsReadOnly ||
                string.IsNullOrWhiteSpace(request.ScopeKey) || string.IsNullOrWhiteSpace(request.SourceRoot) ||
                !Directory.Exists(request.SourceRoot) || string.IsNullOrWhiteSpace(request.Profile.Server) ||
                string.IsNullOrWhiteSpace(request.Profile.Database))
            {
                return false;
            }
            try
            {
                return request.Profile.BuildConnectionString().IndexOf(
                    "ApplicationIntent=ReadOnly",
                    StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        /// <summary>XMZADD 20260901 把源码证据限制在所选根目录内并复制为可公开的相对路径与短摘要。</summary>
        private static IList<SourceEvidence> SanitizeSourceEvidence(
            IList<SourceEvidence> source,
            string sourceRoot)
        {
            var result = new List<SourceEvidence>();
            if (source == null)
            {
                return result;
            }
            string normalizedRoot = NormalizeRoot(sourceRoot);
            int maximumCount = Math.Min(source.Count, 100000);
            for (int index = 0; index < maximumCount; index++)
            {
                SourceEvidence item = source[index];
                if (item == null || item.Evidence == null)
                {
                    continue;
                }
                string relativePath = GetSafeRelativePath(normalizedRoot, item.Evidence.SourcePath);
                string candidate = LimitPublicText(item.ChineseNameCandidate, MaximumEvidenceTextLength);
                EnsureNoSecretShape(candidate);
                var safeEvidence = new EvidenceItem
                {
                    SourceType = LimitPublicText(item.Evidence.SourceType, 32),
                    SourcePath = relativePath,
                    SourceLine = NormalizeSourceLine(item.Evidence.SourceLine),
                    RuleName = LimitPublicText(item.Evidence.RuleName, 64),
                    Explanation = LimitPublicText(item.Evidence.Explanation, MaximumEvidenceTextLength)
                };
                EnsureEvidenceSafe(safeEvidence);
                result.Add(new SourceEvidence
                {
                    ObjectName = LimitPublicText(item.ObjectName, 256),
                    FieldName = LimitPublicText(item.FieldName, 256),
                    EntityName = LimitPublicText(item.EntityName, 256),
                    ModulePath = LimitPublicText(item.ModulePath, 128),
                    ChineseNameCandidate = candidate,
                    EnumName = LimitPublicText(item.EnumName, 256),
                    EnumValue = LimitPublicText(item.EnumValue, 256),
                    PropertyTypeName = LimitPublicText(item.PropertyTypeName, 256),
                    Evidence = safeEvidence
                });
            }
            if (source.Count > maximumCount)
            {
                throw new InvalidDataException("源码证据数量超过发布边界。");
            }
            return result;
        }

        /// <summary>XMZADD 20260901 判断知识库索引可读且非空，缺失或明显损坏时只标记降级。</summary>
        private static bool IsKnowledgeBaseAvailable(string root)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                return false;
            }
            string indexPath = Path.Combine(root, "全量表索引.md");
            try
            {
                var info = new FileInfo(indexPath);
                return info.Exists && info.Length > 0 && info.Length <= 16L * 1024L * 1024L;
            }
            catch (Exception exception)
            {
                if (exception is OutOfMemoryException || exception is StackOverflowException)
                {
                    throw;
                }
                return false;
            }
        }

        /// <summary>XMZADD 20260901 以一次知识索引实例为全部字段补充精确高可信候选，避免字段循环重复扫描文档。</summary>
        private static bool ApplyKnowledgeBaseTranslations(
            SnapshotData snapshot,
            EosKnowledgeBaseTranslationService translator,
            CancellationToken cancellationToken)
        {
            var pendingUpdates = new List<KnowledgeTranslationUpdate>();
            for (int tableIndex = 0; tableIndex < snapshot.Tables.Count; tableIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TableMetadata table = snapshot.Tables[tableIndex];
                if (table == null || table.Fields == null)
                {
                    continue;
                }
                string module = GetMetadataValue(table.ModuleName);
                for (int fieldIndex = 0; fieldIndex < table.Fields.Count; fieldIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    FieldMetadata field = table.Fields[fieldIndex];
                    if (field == null)
                    {
                        continue;
                    }
                    KnowledgeBaseTranslationResult translation = translator.TranslateField(
                        table.SchemaName,
                        table.ObjectName,
                        field.FieldName,
                        module,
                        cancellationToken);
                    if (translation.Candidate != null &&
                        (field.ChineseName == null || GetPriority(field.ChineseName) < MetadataPriority.KnowledgeBaseEvidence))
                    {
                        pendingUpdates.Add(new KnowledgeTranslationUpdate(field, translation.Candidate));
                    }
                }
            }
            if (!translator.IsAvailable)
            {
                return false;
            }
            // 全部引用模块完整读取后才统一写入，避免后半段损坏时发布前半段知识候选。
            for (int index = 0; index < pendingUpdates.Count; index++)
            {
                pendingUpdates[index].Field.ChineseName = pendingUpdates[index].Value;
            }
            return true;
        }

        /// <summary>XMZADD 20260901 将结构差异和可靠自动名称转换为同一强类型批次，保证一次扫描只建一个 Issue。</summary>
        private static DictionaryChangeBatch BuildBatch(
            SnapshotData previous,
            SnapshotData current,
            SchemaDiffResult diff,
            string authorGitHubUserId,
            StructurePublishRequest request)
        {
            var batch = new DictionaryChangeBatch
            {
                BatchId = "structure-" + Guid.NewGuid().ToString("N"),
                AuthorGitHubUserId = authorGitHubUserId,
                CreatedAtUtc = DateTime.UtcNow
            };
            IDictionary<string, TableMetadata> previousTables = BuildTableLookup(previous);
            IDictionary<string, TableMetadata> currentTables = BuildTableLookup(current);
            for (int index = 0; index < diff.Changes.Count; index++)
            {
                SchemaChange change = diff.Changes[index];
                AppendStructureOperations(batch, change, previousTables, currentTables, authorGitHubUserId);
            }
            AppendTableMaintenanceOperations(batch, previousTables, currentTables, authorGitHubUserId);
            AppendTranslationOperations(batch, previousTables, currentTables, authorGitHubUserId, request);
            if (batch.Operations.Count > DictionaryChangeValidator.MaximumOperationCount)
            {
                throw new InvalidDataException("单次结构变化超过公开 Issue 协议上限。");
            }
            return batch;
        }

        /// <summary>XMZADD 20260902 将 Revision 0 的首次业务结构编码为不可变完整快照预览，绕开不适合全量数据的 Issue 上限。</summary>
        private StructurePublishResult CreateFullSnapshotPreview(
            SnapshotData snapshot,
            SchemaDiffResult diff,
            string authorGitHubUserId,
            string observedHeadSha,
            bool knowledgeBaseDegraded)
        {
            if (!(_gitHubClient is IGitHubFullSnapshotPublisher))
            {
                return CreateResult(
                    StructurePublishOutcome.ValidationFailed,
                    knowledgeBaseDegraded,
                    null,
                    null,
                    diff,
                    StructureScanFailureStage.None,
                    StructurePublishValidationReason.FullSnapshotPublisherUnavailable);
            }
            if (snapshot == null || snapshot.Tables == null)
            {
                return CreateResult(
                    StructurePublishOutcome.ValidationFailed,
                    knowledgeBaseDegraded,
                    null,
                    null,
                    diff,
                    StructureScanFailureStage.None,
                    StructurePublishValidationReason.FullSnapshotInvalid);
            }

            snapshot.FormatVersion = SnapshotValidator.SupportedFormatVersion;
            snapshot.Revision = 1L;
            snapshot.RefreshedAt = DateTime.UtcNow;
            byte[] compressedContent;
            SnapshotManifest manifest;
            try
            {
                new PublicSnapshotEvidenceSanitizer().Sanitize(snapshot);
                var validator = new SnapshotValidator();
                validator.Validate(snapshot);
                validator.ValidatePublicContent(snapshot);
                var codec = new SnapshotCodec();
                compressedContent = codec.Encode(snapshot);
                string sha256 = codec.ComputeSha256(compressedContent);
                codec.DecodeAndValidate(compressedContent, sha256);
                manifest = new SnapshotManifest
                {
                    FormatVersion = SnapshotValidator.SupportedFormatVersion,
                    Revision = 1L,
                    SnapshotSha256 = sha256,
                    SnapshotPath = "snapshot/revisions/000000001-" + sha256 + ".json.gz",
                    GeneratedAtUtc = snapshot.RefreshedAt
                };
            }
            catch (Exception exception)
            {
                if (exception is OutOfMemoryException || exception is StackOverflowException)
                {
                    throw;
                }
                return CreateResult(
                    StructurePublishOutcome.ValidationFailed,
                    knowledgeBaseDegraded,
                    null,
                    null,
                    diff,
                    StructureScanFailureStage.None,
                    GetFullSnapshotEncodingFailureReason(exception));
            }

            int fieldCount = CountFields(snapshot);
            StructurePublishResult preview = CreateResult(
                StructurePublishOutcome.ReadyToPublish,
                knowledgeBaseDegraded,
                null,
                null,
                diff);
            preview.IsFullSnapshot = true;
            preview.FullSnapshotTableCount = snapshot.Tables.Count;
            preview.FullSnapshotFieldCount = fieldCount;
            preview.FullSnapshotCompressedBytes = compressedContent.LongLength;
            preview.FullSnapshotContent = compressedContent;
            preview.FullSnapshotManifest = manifest;
            preview.BoundPublisherGitHubUserId = authorGitHubUserId;
            preview.BoundRepositoryHeadSha = observedHeadSha;
            return preview;
        }

        /// <summary>XMZADD 20260902 将完整快照固定压缩容量异常转换为独立原因，其余编码与公开安全失败保持原原因。</summary>
        private static StructurePublishValidationReason GetFullSnapshotEncodingFailureReason(Exception exception)
        {
            if (exception is SnapshotCompressedSizeExceededException)
            {
                return StructurePublishValidationReason.FullSnapshotSizeExceeded;
            }
            return StructurePublishValidationReason.FullSnapshotInvalid;
        }

        /// <summary>XMZADD 20260902 判断规范快照是否尚未包含任何可发布表，用于限定首次完整提交入口。</summary>
        private static bool IsEmptySnapshot(SnapshotData snapshot)
        {
            return snapshot == null || snapshot.Tables == null || snapshot.Tables.Count == 0;
        }

        /// <summary>XMZADD 20260902 统计完整快照字段数量供发布前人工核对，不枚举字段正文。</summary>
        private static int CountFields(SnapshotData snapshot)
        {
            long count = 0L;
            for (int tableIndex = 0; tableIndex < snapshot.Tables.Count; tableIndex++)
            {
                TableMetadata table = snapshot.Tables[tableIndex];
                if (table != null && table.Fields != null)
                {
                    count += table.Fields.Count;
                }
            }
            return count > int.MaxValue ? int.MaxValue : (int)count;
        }

        /// <summary>XMZADD 20260901 按差异类型创建新增、删除和物理属性赋值事件。</summary>
        private static void AppendStructureOperations(
            DictionaryChangeBatch batch,
            SchemaChange change,
            IDictionary<string, TableMetadata> previousTables,
            IDictionary<string, TableMetadata> currentTables,
            string author)
        {
            if (change.ChangeType == SchemaChangeType.RelationAdded ||
                change.ChangeType == SchemaChangeType.RelationRemoved)
            {
                RelationStructurePayload relation = change.RelationPayload;
                string relationObjectKey = BuildObjectKey(relation.ChildSchemaName, relation.ChildTableName);
                DictionaryChangeOperation relationOperation = CreateOperation(
                    author,
                    relationObjectKey,
                    string.Empty,
                    string.Empty,
                    change.ChangeType == SchemaChangeType.RelationAdded ? "AddRelation" : "RemoveRelation",
                    null);
                relationOperation.RelationPayload = DictionaryChangeValidator.CloneRelationPayload(relation);
                batch.Operations.Add(relationOperation);
                return;
            }
            string objectKey = BuildObjectKey(change.SchemaName, change.ObjectName);
            TableMetadata previousTable;
            TableMetadata currentTable;
            previousTables.TryGetValue(objectKey, out previousTable);
            currentTables.TryGetValue(objectKey, out currentTable);
            if (change.ChangeType == SchemaChangeType.ObjectAdded)
            {
                batch.Operations.Add(CreateAddTableOperation(currentTable, author));
                return;
            }
            if (change.ChangeType == SchemaChangeType.ObjectRemoved)
            {
                batch.Operations.Add(CreateOperation(author, objectKey, string.Empty, string.Empty, "RemoveTable", null));
                return;
            }
            if (change.ChangeType == SchemaChangeType.ObjectChanged)
            {
                AppendSetIfChanged(batch, author, objectKey, string.Empty, "ObjectType",
                    previousTable == null ? null : previousTable.ObjectType,
                    currentTable == null ? null : currentTable.ObjectType, null);
                return;
            }
            FieldMetadata previousField = FindField(previousTable, change.FieldName);
            FieldMetadata currentField = FindField(currentTable, change.FieldName);
            if (change.ChangeType == SchemaChangeType.FieldAdded)
            {
                batch.Operations.Add(CreateAddFieldOperation(objectKey, currentField, author));
                return;
            }
            if (change.ChangeType == SchemaChangeType.FieldRemoved)
            {
                batch.Operations.Add(CreateOperation(author, objectKey, change.FieldName, string.Empty, "RemoveField", null));
                return;
            }
            if (change.ChangeType == SchemaChangeType.FieldChanged)
            {
                AppendFieldSets(batch, author, objectKey, previousField, currentField);
            }
        }

        /// <summary>XMZADD 20260901 为字段类型、长度和主外键约束分别创建规范赋值事件。</summary>
        private static void AppendFieldSets(
            DictionaryChangeBatch batch,
            string author,
            string objectKey,
            FieldMetadata previous,
            FieldMetadata current)
        {
            AppendSetIfChanged(batch, author, objectKey, current.FieldName, "DataType", previous.DataType, current.DataType, null);
            AppendSetIfChanged(batch, author, objectKey, current.FieldName, "LengthText", previous.LengthText, current.LengthText, null);
            AppendSetIfChanged(batch, author, objectKey, current.FieldName, "IsRequired",
                previous.IsRequired.ToString(), current.IsRequired.ToString(), null);
            AppendSetIfChanged(batch, author, objectKey, current.FieldName, "IsPrimaryKey",
                previous.IsPrimaryKey.ToString(), current.IsPrimaryKey.ToString(), null);
            AppendSetIfChanged(batch, author, objectKey, current.FieldName, "IsForeignKey",
                previous.IsForeignKey.ToString(), current.IsForeignKey.ToString(), null);
        }

        /// <summary>XMZADD 20260901 继承已维护表分类并仅按批准的稳定命名规则识别新增业务表、基础资料表和技术表。</summary>
        private static void ApplyRetainedTableClassification(SnapshotData previous, SnapshotData current)
        {
            IDictionary<string, TableMetadata> previousTables = BuildTableLookup(previous);
            if (current == null || current.Tables == null)
            {
                return;
            }
            for (int index = 0; index < current.Tables.Count; index++)
            {
                TableMetadata table = current.Tables[index];
                if (table == null)
                {
                    continue;
                }
                TableMetadata previousTable;
                bool isExistingTable = previousTables.TryGetValue(
                    BuildObjectKey(table.SchemaName, table.ObjectName), out previousTable);
                if (isExistingTable)
                {
                    // 人工维护的分类与空表保留决定优先于每次数据库扫描的默认值。
                    table.Category = previousTable.Category;
                    if (previousTable.KeepWhenEmpty)
                    {
                        table.KeepWhenEmpty = true;
                    }
                }
                if (!isExistingTable && table.Category == DictionaryTableCategory.Unclassified)
                {
                    table.Category = InferNewTableCategory(table.ObjectName);
                }
            }
        }

        /// <summary>XMZADD 20260901 使用下划线分词后的完整标记判断新增表类别，避免短缩写误命中普通业务名称。</summary>
        private static DictionaryTableCategory InferNewTableCategory(string objectName)
        {
            string normalized = (objectName ?? string.Empty).Trim().ToUpperInvariant();
            if (ContainsNameToken(normalized, "LOG") || ContainsNameToken(normalized, "LOGS") ||
                ContainsNameToken(normalized, "AUDIT") || ContainsNameToken(normalized, "TRACE") ||
                ContainsNameToken(normalized, "TEMP") || ContainsNameToken(normalized, "TMP") ||
                ContainsNameToken(normalized, "BAK") || ContainsNameToken(normalized, "BACKUP") ||
                ContainsNameToken(normalized, "CACHE"))
            {
                return DictionaryTableCategory.Technical;
            }
            if (normalized.StartsWith("T_BD_", StringComparison.Ordinal) ||
                ContainsNameToken(normalized, "BASE") || ContainsNameToken(normalized, "BASIC") ||
                ContainsNameToken(normalized, "MASTER") || ContainsNameToken(normalized, "DICT") ||
                ContainsNameToken(normalized, "LOOKUP"))
            {
                return DictionaryTableCategory.BaseData;
            }
            return DictionaryTableCategory.Business;
        }

        /// <summary>XMZADD 20260901 只匹配由英文下划线分隔的完整命名片段，避免把业务单词的一部分当作技术表标记。</summary>
        private static bool ContainsNameToken(string normalizedName, string token)
        {
            string[] parts = (normalizedName ?? string.Empty).Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries);
            for (int index = 0; index < parts.Length; index++)
            {
                if (string.Equals(parts[index], token, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>XMZADD 20260901 在新增表物理事件之后发布分类与空表保留属性，使共享快照保留清理依据。</summary>
        private static void AppendTableMaintenanceOperations(
            DictionaryChangeBatch batch,
            IDictionary<string, TableMetadata> previousTables,
            IDictionary<string, TableMetadata> currentTables,
            string author)
        {
            foreach (KeyValuePair<string, TableMetadata> pair in currentTables)
            {
                TableMetadata previousTable;
                previousTables.TryGetValue(pair.Key, out previousTable);
                string previousCategory = previousTable == null ? null : previousTable.Category.ToString();
                string previousKeepWhenEmpty = previousTable == null ? null : previousTable.KeepWhenEmpty.ToString();
                AppendSetIfChanged(batch, author, pair.Key, string.Empty, "Category",
                    previousCategory, pair.Value.Category.ToString(), null);
                AppendSetIfChanged(batch, author, pair.Key, string.Empty, "KeepWhenEmpty",
                    previousKeepWhenEmpty, pair.Value.KeepWhenEmpty.ToString(), null);
                if (previousTable != null)
                {
                    AppendSetIfChanged(batch, author, pair.Key, string.Empty, "ApproximateRowCount",
                        previousTable.ApproximateRowCount.ToString(CultureInfo.InvariantCulture),
                        pair.Value.ApproximateRowCount.ToString(CultureInfo.InvariantCulture), null);
                }
            }
        }

        /// <summary>XMZADD 20260901 为源码或知识库确认度足够且前后变化的名称追加业务赋值事件。</summary>
        private static void AppendTranslationOperations(
            DictionaryChangeBatch batch,
            IDictionary<string, TableMetadata> previousTables,
            IDictionary<string, TableMetadata> currentTables,
            string author,
            StructurePublishRequest request)
        {
            foreach (KeyValuePair<string, TableMetadata> pair in currentTables)
            {
                TableMetadata previousTable;
                previousTables.TryGetValue(pair.Key, out previousTable);
                TableMetadata currentTable = pair.Value;
                AppendReliableNameSet(batch, author, pair.Key, string.Empty,
                    previousTable == null ? null : previousTable.ChineseName,
                    currentTable.ChineseName,
                    request);
                for (int fieldIndex = 0; fieldIndex < currentTable.Fields.Count; fieldIndex++)
                {
                    FieldMetadata currentField = currentTable.Fields[fieldIndex];
                    FieldMetadata previousField = FindField(previousTable, currentField.FieldName);
                    AppendReliableNameSet(batch, author, pair.Key, currentField.FieldName,
                        previousField == null ? null : previousField.ChineseName,
                        currentField.ChineseName,
                        request);
                }
            }
        }

        /// <summary>XMZADD 20260901 仅发布数据库、知识库或代码证据支持的名称，排除纯命名规则占位。</summary>
        private static void AppendReliableNameSet(
            DictionaryChangeBatch batch,
            string author,
            string objectKey,
            string fieldKey,
            MetadataValue previous,
            MetadataValue current,
            StructurePublishRequest request)
        {
            if (current == null || string.IsNullOrWhiteSpace(current.Value) ||
                current.Status != ConfidenceStatus.DatabaseEvidence &&
                current.Status != ConfidenceStatus.KnowledgeBaseEvidence &&
                current.Status != ConfidenceStatus.CodeEvidence)
            {
                return;
            }
            IList<EvidenceItem> evidence = SanitizePublishedEvidence(current.Evidence, current.Status, request);
            if (current.Status == ConfidenceStatus.DatabaseEvidence && evidence.Count == 0)
            {
                // 数据库扩展说明没有文件路径，仍需显式标记自动来源，避免服务端误作人工锁定值。
                evidence.Add(new EvidenceItem
                {
                    SourceType = "数据库说明",
                    SourcePath = string.Empty,
                    SourceLine = 0,
                    RuleName = "SqlExtendedDescription",
                    Explanation = "SQL Server 扩展说明"
                });
            }
            AppendSetIfChanged(batch, author, objectKey, fieldKey, "ChineseName",
                GetMetadataValue(previous), current.Value, evidence);
        }

        /// <summary>XMZADD 20260901 创建包含空子集合的新增表载荷，字段和关系由独立事件保持可审计顺序。</summary>
        private static DictionaryChangeOperation CreateAddTableOperation(TableMetadata table, string author)
        {
            var operation = CreateOperation(author, BuildObjectKey(table.SchemaName, table.ObjectName), string.Empty, string.Empty, "AddTable", null);
            operation.TablePayload = new TableStructurePayload
            {
                SchemaName = table.SchemaName,
                ObjectName = table.ObjectName,
                ObjectType = table.ObjectType,
                ApproximateRowCount = table.ApproximateRowCount
            };
            return operation;
        }

        /// <summary>XMZADD 20260901 创建完整字段物理事实载荷，使服务端无需解析自由文本。</summary>
        private static DictionaryChangeOperation CreateAddFieldOperation(string objectKey, FieldMetadata field, string author)
        {
            var operation = CreateOperation(author, objectKey, field.FieldName, string.Empty, "AddField", null);
            operation.FieldPayload = new FieldStructurePayload
            {
                FieldName = field.FieldName,
                OwnerTableName = field.OwnerTableName,
                DataType = field.DataType,
                LengthText = field.LengthText,
                IsRequired = field.IsRequired,
                IsPrimaryKey = field.IsPrimaryKey,
                IsForeignKey = field.IsForeignKey
            };
            return operation;
        }

        /// <summary>XMZADD 20260901 在值确有变化时追加一个物理或业务属性事件。</summary>
        private static void AppendSetIfChanged(
            DictionaryChangeBatch batch,
            string author,
            string objectKey,
            string fieldKey,
            string propertyName,
            string previousValue,
            string currentValue,
            IList<EvidenceItem> evidence)
        {
            if (currentValue == null || string.Equals(previousValue ?? string.Empty, currentValue, StringComparison.Ordinal))
            {
                return;
            }
            batch.Operations.Add(CreateOperation(author, objectKey, fieldKey, propertyName, "Set", currentValue, evidence));
        }

        /// <summary>XMZADD 20260901 创建具备全局幂等号和稳定键的结构发布操作。</summary>
        private static DictionaryChangeOperation CreateOperation(
            string author,
            string objectKey,
            string fieldKey,
            string propertyName,
            string changeKind,
            string newValue,
            IList<EvidenceItem> evidence = null)
        {
            return new DictionaryChangeOperation
            {
                OperationId = Guid.NewGuid().ToString("N"),
                AuthorGitHubUserId = author,
                ObjectKey = objectKey,
                FieldKey = fieldKey,
                PropertyName = propertyName,
                ChangeKind = changeKind,
                NewValue = newValue,
                CreatedAtUtc = DateTime.UtcNow,
                Evidence = evidence
            };
        }

        /// <summary>XMZADD 20260901 清洗最终名称证据，只保留相对路径、行号、规则和短摘要。</summary>
        private static IList<EvidenceItem> SanitizePublishedEvidence(
            IList<EvidenceItem> source,
            ConfidenceStatus status,
            StructurePublishRequest request)
        {
            var result = new List<EvidenceItem>();
            if (source == null)
            {
                return result;
            }
            string root = status == ConfidenceStatus.KnowledgeBaseEvidence
                ? NormalizeRoot(request.KnowledgeBaseRoot)
                : NormalizeRoot(request.SourceRoot);
            for (int index = 0; index < source.Count && result.Count < MaximumPublishedEvidencePerOperation; index++)
            {
                EvidenceItem item = source[index];
                if (item == null)
                {
                    continue;
                }
                string relativePath = GetSafeRelativePath(root, item.SourcePath);
                var safe = new EvidenceItem
                {
                    SourceType = LimitPublicText(item.SourceType, 32),
                    SourcePath = relativePath,
                    SourceLine = NormalizeSourceLine(item.SourceLine),
                    RuleName = LimitPublicText(item.RuleName, 64),
                    Explanation = LimitPublicText(item.Explanation, MaximumEvidenceTextLength)
                };
                EnsureEvidenceSafe(safe);
                result.Add(safe);
            }
            return result;
        }

        /// <summary>XMZADD 20260901 验证最终批次不含凭据形态、绝对路径或本机连接信息。</summary>
        private static bool IsBatchSafeForPublicIssue(DictionaryChangeBatch batch)
        {
            try
            {
                string json = DictionaryJsonSerializer.SerializeBatch(batch);
                EnsureNoSecretShape(json);
                return json.IndexOf("ApplicationIntent", StringComparison.OrdinalIgnoreCase) < 0 &&
                    json.IndexOf("Data Source", StringComparison.OrdinalIgnoreCase) < 0 &&
                    json.IndexOf("Initial Catalog", StringComparison.OrdinalIgnoreCase) < 0;
            }
            catch (Exception exception)
            {
                if (exception is OutOfMemoryException || exception is StackOverflowException)
                {
                    throw;
                }
                return false;
            }
        }

        /// <summary>XMZADD 20260901 验证单条证据的公开字段不包含绝对路径或凭据形态。</summary>
        private static void EnsureEvidenceSafe(EvidenceItem evidence)
        {
            if (evidence.SourcePath.Length > MaximumEvidencePathLength || Path.IsPathRooted(evidence.SourcePath) ||
                evidence.SourcePath.IndexOf("..", StringComparison.Ordinal) >= 0 ||
                evidence.SourcePath.IndexOf(':') >= 0)
            {
                throw new InvalidDataException("公开证据路径无效。");
            }
            EnsureNoSecretShape(evidence.SourceType);
            EnsureNoSecretShape(evidence.SourcePath);
            EnsureNoSecretShape(evidence.RuleName);
            EnsureNoSecretShape(evidence.Explanation);
        }

        /// <summary>XMZADD 20260901 拒绝常见密码、Token 和连接字符串形态，避免摘要误带本机凭据。</summary>
        private static void EnsureNoSecretShape(string value)
        {
            if (SensitiveTextGuard.ContainsCredentialShape(value))
            {
                throw new InvalidDataException("公开内容包含凭据形态。");
            }
        }

        /// <summary>XMZADD 20260901 将文件路径规范为根目录内的正斜线相对路径并防止前缀绕过。</summary>
        private static string GetSafeRelativePath(string normalizedRoot, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }
            string fullPath = Path.IsPathRooted(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(Path.Combine(normalizedRoot, path));
            string rootWithSeparator = normalizedRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("证据不属于所选根目录。");
            }
            string relativePath = fullPath.Substring(rootWithSeparator.Length).Replace('\\', '/');
            if (relativePath.Length == 0 || relativePath.Length > MaximumEvidencePathLength)
            {
                throw new InvalidDataException("证据相对路径无效。");
            }
            return relativePath;
        }

        /// <summary>XMZADD 20260901 规范根目录完整路径，供相对证据的边界比较复用。</summary>
        private static string NormalizeRoot(string root)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                throw new InvalidDataException("证据根目录无效。");
            }
            return Path.GetFullPath(root);
        }

        /// <summary>XMZADD 20260901 截断可公开短文本并移除换行控制字符，避免摘要承载源码正文。</summary>
        private static string LimitPublicText(string value, int maximumLength)
        {
            string text = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
            return text.Length <= maximumLength ? text : text.Substring(0, maximumLength);
        }

        /// <summary>XMZADD 20260901 限制公开证据行号范围，避免异常值破坏客户端展示。</summary>
        private static int NormalizeSourceLine(int line)
        {
            return line < 0 || line > 10000000 ? 0 : line;
        }

        /// <summary>XMZADD 20260901 为快照表建立稳定键索引，避免字段差异处理中反复全表搜索。</summary>
        private static IDictionary<string, TableMetadata> BuildTableLookup(SnapshotData snapshot)
        {
            var result = new Dictionary<string, TableMetadata>(StringComparer.OrdinalIgnoreCase);
            if (snapshot == null || snapshot.Tables == null)
            {
                return result;
            }
            for (int index = 0; index < snapshot.Tables.Count; index++)
            {
                TableMetadata table = snapshot.Tables[index];
                if (table != null)
                {
                    result[BuildObjectKey(table.SchemaName, table.ObjectName)] = table;
                }
            }
            return result;
        }

        /// <summary>XMZADD 20260901 在单表字段集合中按物理名定位字段。</summary>
        private static FieldMetadata FindField(TableMetadata table, string fieldName)
        {
            if (table == null || table.Fields == null)
            {
                return null;
            }
            for (int index = 0; index < table.Fields.Count; index++)
            {
                FieldMetadata field = table.Fields[index];
                if (field != null && string.Equals(field.FieldName, fieldName, StringComparison.OrdinalIgnoreCase))
                {
                    return field;
                }
            }
            return null;
        }

        /// <summary>XMZADD 20260901 生成共享事件使用的架构点对象稳定键。</summary>
        private static string BuildObjectKey(string schemaName, string objectName)
        {
            return (schemaName ?? string.Empty) + "." + (objectName ?? string.Empty);
        }

        /// <summary>XMZADD 20260901 安全读取元数据容器的展示值。</summary>
        private static string GetMetadataValue(MetadataValue value)
        {
            return value == null ? null : value.Value;
        }

        /// <summary>XMZADD 20260901 将可信状态映射到既有优先级，防止知识库覆盖数据库或人工结果。</summary>
        private static MetadataPriority GetPriority(MetadataValue value)
        {
            if (value == null)
            {
                return MetadataPriority.PendingConfirmation;
            }
            switch (value.Status)
            {
                case ConfidenceStatus.LocalOverride:
                    return MetadataPriority.LocalOverride;
                case ConfidenceStatus.DatabaseEvidence:
                    return MetadataPriority.DatabaseEvidence;
                case ConfidenceStatus.KnowledgeBaseEvidence:
                    return MetadataPriority.KnowledgeBaseEvidence;
                case ConfidenceStatus.CodeEvidence:
                    return MetadataPriority.CodeEvidence;
                case ConfidenceStatus.AiGuessed:
                    return MetadataPriority.AiGuessed;
                default:
                    return MetadataPriority.NamingRule;
            }
        }

        /// <summary>XMZADD 20260902 报告不含环境私有信息的扫描阶段，让长耗时只读操作对用户可见。</summary>
        private static void ReportProgress(IProgress<StructureScanProgress> progress, StructureScanStage stage)
        {
            if (progress == null)
            {
                return;
            }
            progress.Report(new StructureScanProgress
            {
                Stage = stage,
                Message = GetScanStageMessage(stage)
            });
        }

        /// <summary>XMZADD 20260902 将内部阶段转换为安全的中文界面文本，避免将数据库和目录细节写入状态栏。</summary>
        private static string GetScanStageMessage(StructureScanStage stage)
        {
            switch (stage)
            {
                case StructureScanStage.PreviousSnapshot:
                    return "正在读取本地快照…";
                case StructureScanStage.DatabaseMetadata:
                    return "正在读取 EOS 正式库结构…";
                case StructureScanStage.SourceEvidence:
                    return "正在扫描 EOS 源码证据…";
                case StructureScanStage.MetadataEnrichment:
                    return "正在合并数据库与源码证据…";
                case StructureScanStage.KnowledgeBase:
                    return "正在核验 EOS 知识库…";
                case StructureScanStage.NameInference:
                    return "正在补全本地缩写词义…";
                case StructureScanStage.SnapshotPruning:
                    return "正在清理非业务对象…";
                case StructureScanStage.DifferenceAnalysis:
                    return "正在计算结构差异…";
                default:
                    return "正在执行只读结构扫描…";
            }
        }

        /// <summary>XMZADD 20260901 创建不含异常详情和本机配置的结构发布结果。</summary>
        private static StructurePublishResult CreateResult(
            StructurePublishOutcome outcome,
            bool knowledgeBaseDegraded,
            int? issueNumber,
            DictionaryChangeBatch batch,
            SchemaDiffResult diff,
            StructureScanFailureStage failureStage = StructureScanFailureStage.None,
            StructurePublishValidationReason validationReason = StructurePublishValidationReason.None)
        {
            return new StructurePublishResult
            {
                Outcome = outcome,
                FailureStage = failureStage,
                ValidationReason = validationReason,
                KnowledgeBaseDegraded = knowledgeBaseDegraded,
                IssueNumber = issueNumber,
                Batch = batch,
                Diff = diff
            };
        }

        /// <summary>XMZADD 20260901 固化单次授权读取结果，供扫描前后身份一致性比较。</summary>
        private sealed class PublisherContext
        {
            /// <summary>XMZADD 20260901 保存身份、名单和匹配结果，避免同一授权阶段重复网络读取。</summary>
            public PublisherContext(GitHubUserIdentity identity, IList<string> publisherIds, bool isPublisher)
            {
                Identity = identity;
                PublisherIds = publisherIds ?? new List<string>();
                IsPublisher = isPublisher;
            }

            public GitHubUserIdentity Identity { get; private set; }
            public IList<string> PublisherIds { get; private set; }
            public bool IsPublisher { get; private set; }
        }

        /// <summary>XMZADD 20260901 暂存单字段知识库名称，确保整次知识读取成功后才提交到扫描快照。</summary>
        private sealed class KnowledgeTranslationUpdate
        {
            /// <summary>XMZADD 20260901 绑定待更新字段与已清洗知识候选。</summary>
            public KnowledgeTranslationUpdate(FieldMetadata field, MetadataValue value)
            {
                Field = field;
                Value = value;
            }

            public FieldMetadata Field { get; private set; }
            public MetadataValue Value { get; private set; }
        }
    }
}
