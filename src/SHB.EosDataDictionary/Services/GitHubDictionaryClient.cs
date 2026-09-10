using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SHB.EosDataDictionary.Models;

namespace SHB.EosDataDictionary.Services
{
    /// <summary>XMZADD 20260901 标识 GitHub 字典访问失败的安全处理类别，供同步器区分重试、回退和重新认证。</summary>
    public enum GitHubDictionaryErrorKind
    {
        Authentication = 0,
        RateLimited = 1,
        NotFound = 2,
        Transient = 3,
        Validation = 4,
        Configuration = 5
    }

    /// <summary>XMZADD 20260903 标识字典事件 Issue 尚待处理、已进入可信修订或已被工作流明确拒绝。</summary>
    public enum DictionaryIssueApplyStatus
    {
        Pending = 0,
        Applied = 1,
        Rejected = 2
    }

    /// <summary>XMZADD 20260901 返回不含响应正文和 Token 的 GitHub 字典访问错误及重试提示。</summary>
    public sealed class GitHubDictionaryClientException : Exception
    {
        /// <summary>XMZADD 20260901 创建带固定安全消息和分类的客户端异常，避免透传远端敏感诊断。</summary>
        public GitHubDictionaryClientException(
            string message,
            GitHubDictionaryErrorKind kind,
            bool isTransient,
            TimeSpan? retryAfter)
            : base(message)
        {
            Kind = kind;
            IsTransient = isTransient;
            RetryAfter = retryAfter;
        }

        public GitHubDictionaryErrorKind Kind { get; private set; }
        public bool IsTransient { get; private set; }
        public TimeSpan? RetryAfter { get; private set; }
    }

    /// <summary>XMZADD 20260901 定义同步协调器依赖的 GitHub 字典协议操作，便于离线测试完全替代真实网络。</summary>
    public interface IGitHubDictionaryClient
    {
        /// <summary>XMZADD 20260901 匿名读取规范快照清单。</summary>
        Task<SnapshotManifest> GetManifestAsync(CancellationToken cancellationToken);

        /// <summary>XMZADD 20260901 匿名下载已校验路径的压缩规范快照。</summary>
        Task<byte[]> DownloadSnapshotAsync(string relativePath, CancellationToken cancellationToken);

        /// <summary>XMZADD 20260901 匿名下载并转换一个不可拆分的服务器修订事件包。</summary>
        Task<DictionaryRevisionPackage> DownloadRevisionAsync(long revision, CancellationToken cancellationToken);

        /// <summary>XMZADD 20260901 用个人 Token 读取不可变 GitHub 数值身份。</summary>
        Task<GitHubUserIdentity> GetCurrentUserAsync(CancellationToken cancellationToken);

        /// <summary>XMZADD 20260901 使用尚未保存的候选 Token 验证不可变 GitHub 身份，成功前不得替换本机凭据。</summary>
        Task<GitHubUserIdentity> GetCurrentUserAsync(string candidateToken, CancellationToken cancellationToken);

        /// <summary>XMZADD 20260901 匿名读取公开仓库中固定路径的结构发布者数字身份名单。</summary>
        Task<IList<string>> GetPublisherIdsAsync(CancellationToken cancellationToken);

        /// <summary>XMZADD 20260901 用个人 Token 创建严格协议格式的字典事件 Issue。</summary>
        Task<int> CreateDictionaryIssueAsync(DictionaryChangeBatch batch, CancellationToken cancellationToken);

        /// <summary>XMZADD 20260903 从主分支不可变事件和修订文档核验一个本机批次已真正进入共享快照。</summary>
        Task<bool> AreDictionaryOperationsAppliedAsync(
            DictionaryChangeBatch batch,
            int issueNumber,
            long maximumRevision,
            CancellationToken cancellationToken);

        /// <summary>XMZADD 20260903 从可信事件、修订文档和无效标签判定字典 Issue 的三态处理结果。</summary>
        Task<DictionaryIssueApplyStatus> GetDictionaryOperationsApplyStatusAsync(
            DictionaryChangeBatch batch,
            int issueNumber,
            long maximumRevision,
            CancellationToken cancellationToken);

        /// <summary>XMZADD 20260903 严格核对被拒绝的旧版私有覆盖 Issue，并创建不含本机覆盖的确定性替代 Issue。</summary>
        Task<int> RecreateRejectedDictionaryIssueAsync(
            DictionaryChangeBatch batch,
            int rejectedIssueNumber,
            CancellationToken cancellationToken);
    }

    /// <summary>XMZADD 20260902 定义首次完整快照的一次 Git Commit 发布边界，普通同步客户端不因此获得仓库写入职责。</summary>
    public interface IGitHubFullSnapshotPublisher
    {
        /// <summary>XMZADD 20260902 读取清单仍为 Revision 0 的精确分支头，供扫描预览绑定不可变发布基线。</summary>
        Task<string> GetRevisionZeroHeadShaAsync(CancellationToken cancellationToken);

        /// <summary>XMZADD 20260902 将完整压缩快照与清单放入同一个 Git tree，并以非强制方式原子推进目标分支。</summary>
        Task<string> PublishFullSnapshotAsync(
            byte[] compressedSnapshot,
            SnapshotManifest manifest,
            string expectedHeadSha,
            string expectedPublisherGitHubUserId,
            CancellationToken cancellationToken);
    }

    /// <summary>XMZADD 20260901 以匿名公开读取和最小 Bearer 鉴权访问 GitHub 共享字典协议。</summary>
    public sealed class GitHubDictionaryClient : IGitHubDictionaryClient, IGitHubFullSnapshotPublisher
    {
        public const int MaximumIssueBodyBytes = DictionaryChangeValidator.MaximumIssueBodyBytes;
        private const int ManifestMaximumBytes = 256 * 1024;
        private const int RevisionMaximumBytes = 512 * 1024;
        private const int EventMaximumBytes = 2 * 1024 * 1024;
        private const int RevisionEventsMaximumBytes = 8 * 1024 * 1024;
        private const int SnapshotMaximumBytes = 128 * 1024 * 1024;
        private const int ApiMaximumBytes = 512 * 1024;
        private const int IssueReconcileMaximumBytes = 2 * 1024 * 1024;
        private const int MaximumIssueSearchResults = 100;
        private const int MaximumRecentIssueResults = 20;
        private const int MaximumEventsPerRevision = 1000;
        private readonly HttpClient _httpClient;
        private readonly string _owner;
        private readonly string _repositoryName;
        private readonly string _branch;
        private readonly Uri _apiBaseUri;
        private readonly Uri _rawBaseUri;
        private readonly Func<string> _tokenProvider;

        /// <summary>XMZADD 20260901 初始化可注入 HTTP 客户端和延迟 Token 提供器，保证公开下载永不读取个人凭据。</summary>
        public GitHubDictionaryClient(
            HttpClient httpClient,
            DictionaryRepositoryOptions options,
            Func<string> tokenProvider)
        {
            if (httpClient == null)
            {
                throw new ArgumentNullException("httpClient");
            }
            if (options == null)
            {
                throw new ArgumentNullException("options");
            }
            var optionSnapshot = new DictionaryRepositoryOptions
            {
                Owner = options.Owner,
                RepositoryName = options.RepositoryName,
                Branch = options.Branch,
                ApiBaseUri = options.ApiBaseUri,
                RawBaseUri = options.RawBaseUri,
                StateKey = options.StateKey,
                ScopeKey = options.ScopeKey
            };
            optionSnapshot.Validate();
            if (httpClient.DefaultRequestHeaders.Contains("Authorization"))
            {
                throw new GitHubDictionaryClientException(
                    "GitHub 客户端不得预配置 Authorization。",
                    GitHubDictionaryErrorKind.Configuration,
                    false,
                    null);
            }
            _httpClient = httpClient;
            _owner = optionSnapshot.Owner;
            _repositoryName = optionSnapshot.RepositoryName;
            _branch = optionSnapshot.Branch;
            _apiBaseUri = new Uri(optionSnapshot.ApiBaseUri.AbsoluteUri, UriKind.Absolute);
            _rawBaseUri = new Uri(optionSnapshot.RawBaseUri.AbsoluteUri, UriKind.Absolute);
            _tokenProvider = tokenProvider;
        }

        /// <summary>XMZADD 20260901 使用凭据存储初始化客户端，使正式环境只在身份和 Issue 请求时解密 Token。</summary>
        public GitHubDictionaryClient(
            HttpClient httpClient,
            DictionaryRepositoryOptions options,
            IGitHubTokenProvider tokenProvider)
            : this(httpClient, options, tokenProvider == null ? null : new Func<string>(tokenProvider.GetToken))
        {
        }

        /// <summary>XMZADD 20260901 匿名读取并严格校验快照版本、哈希和仓库内相对路径。</summary>
        public async Task<SnapshotManifest> GetManifestAsync(CancellationToken cancellationToken)
        {
            Uri uri = BuildRawUri("snapshot/manifest.json");
            byte[] content = await SendAndReadAsync(HttpMethod.Get, uri, null, false, ManifestMaximumBytes, cancellationToken);
            return ParseManifest(content);
        }

        /// <summary>XMZADD 20260902 严格解析远程清单的版本、修订、哈希、日期和仓库内相对路径。</summary>
        private static SnapshotManifest ParseManifest(byte[] content)
        {
            RemoteManifestDto dto = DeserializeJson<RemoteManifestDto>(content, "远程快照清单格式无效。");
            if (dto.FormatVersion != 1 || dto.Revision < 0 || string.IsNullOrWhiteSpace(dto.SnapshotPath))
            {
                throw ValidationException("远程快照清单不兼容。");
            }
            string snapshotPath;
            try
            {
                snapshotPath = DictionaryRepositoryOptions.ValidateRelativePath(dto.SnapshotPath);
            }
            catch (ArgumentException)
            {
                throw ValidationException("远程快照清单路径无效。");
            }
            if (dto.Revision == 0)
            {
                if (!string.IsNullOrEmpty(dto.SnapshotSha256) && !IsSha256(dto.SnapshotSha256))
                {
                    throw ValidationException("远程快照清单哈希无效。");
                }
            }
            else if (!IsSha256(dto.SnapshotSha256))
            {
                throw ValidationException("远程快照清单哈希无效。");
            }
            return new SnapshotManifest
            {
                FormatVersion = dto.FormatVersion,
                Revision = dto.Revision,
                SnapshotSha256 = dto.SnapshotSha256,
                SnapshotPath = snapshotPath,
                GeneratedAtUtc = ParseRepositoryDate(dto.GeneratedAtUtc)
            };
        }

        /// <summary>XMZADD 20260901 匿名下载受大小限制的压缩快照，路径校验在发起网络请求前完成。</summary>
        public Task<byte[]> DownloadSnapshotAsync(string relativePath, CancellationToken cancellationToken)
        {
            string safePath = DictionaryRepositoryOptions.ValidateRelativePath(relativePath);
            return SendAndReadAsync(
                HttpMethod.Get,
                BuildRawUri(safePath),
                null,
                false,
                SnapshotMaximumBytes,
                cancellationToken);
        }

        /// <summary>XMZADD 20260901 匿名下载修订文档及全部事件并合并为单个服务器修订原子重放批次。</summary>
        public async Task<DictionaryRevisionPackage> DownloadRevisionAsync(
            long revision,
            CancellationToken cancellationToken)
        {
            if (revision <= 0 || revision > 999999999L)
            {
                throw new ArgumentOutOfRangeException("revision", "修订号格式无效。");
            }
            string revisionPath = "events/revisions/" + revision.ToString("000000000", CultureInfo.InvariantCulture) + ".json";
            byte[] revisionContent = await SendAndReadAsync(
                HttpMethod.Get,
                BuildRawUri(revisionPath),
                null,
                false,
                RevisionMaximumBytes,
                cancellationToken);
            RemoteRevisionDto dto = DeserializeJson<RemoteRevisionDto>(revisionContent, "远程修订文档格式无效。");
            ValidateRevisionDocument(dto, revision);

            var package = new DictionaryRevisionPackage
            {
                FormatVersion = dto.FormatVersion,
                Revision = dto.Revision,
                GeneratedAtUtc = ParseRepositoryDate(dto.GeneratedAtUtc)
            };
            var mergedBatch = new DictionaryChangeBatch
            {
                BatchId = "repository-revision-" + revision.ToString("000000000", CultureInfo.InvariantCulture),
                AuthorGitHubUserId = string.Empty,
                CreatedAtUtc = package.GeneratedAtUtc
            };
            long cumulativeEventBytes = 0;
            for (int index = 0; index < dto.EventPaths.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string eventPath = ValidateEventPath(dto.EventPaths[index]);
                int remainingEventBytes = (int)(RevisionEventsMaximumBytes - cumulativeEventBytes);
                if (remainingEventBytes <= 0)
                {
                    throw ValidationException("远程修订累计事件内容超过安全大小限制。");
                }
                byte[] eventContent = await SendAndReadAsync(
                    HttpMethod.Get,
                    BuildRawUri(eventPath),
                    null,
                    false,
                    Math.Min(EventMaximumBytes, remainingEventBytes),
                    cancellationToken);
                cumulativeEventBytes += eventContent.Length;
                AppliedDictionaryEvent appliedEvent = DeserializeJson<AppliedDictionaryEvent>(
                    eventContent,
                    "远程字典事件格式无效。");
                mergedBatch.Operations.Add(ConvertAppliedEvent(appliedEvent, revision));
            }
            package.Batches.Add(mergedBatch);
            return package;
        }

        /// <summary>XMZADD 20260901 仅在身份识别时附加 Bearer Token 并返回 GitHub 不可变数值用户 ID。</summary>
        public async Task<GitHubUserIdentity> GetCurrentUserAsync(CancellationToken cancellationToken)
        {
            byte[] content = await SendAndReadAsync(
                HttpMethod.Get,
                new Uri(_apiBaseUri, "user"),
                null,
                true,
                ApiMaximumBytes,
                cancellationToken);
            return DeserializeIdentity(content);
        }

        /// <summary>XMZADD 20260901 仅在单次官方用户请求中使用候选 Token，身份验证成功前不读取或覆盖已保存凭据。</summary>
        public async Task<GitHubUserIdentity> GetCurrentUserAsync(
            string candidateToken,
            CancellationToken cancellationToken)
        {
            ValidateAuthenticationToken(candidateToken);
            byte[] content = await SendAndReadAsync(
                HttpMethod.Get,
                new Uri(_apiBaseUri, "user"),
                null,
                true,
                ApiMaximumBytes,
                cancellationToken,
                candidateToken);
            return DeserializeIdentity(content);
        }

        /// <summary>XMZADD 20260901 匿名读取并严格校验发布者配置，避免授权判断依赖可变登录名或宽松 JSON。</summary>
        public async Task<IList<string>> GetPublisherIdsAsync(CancellationToken cancellationToken)
        {
            byte[] content = await SendAndReadAsync(
                HttpMethod.Get,
                BuildRawUri("config/publishers.json"),
                null,
                false,
                ManifestMaximumBytes,
                cancellationToken);
            if (!IsStrictPublisherConfigurationJson(content))
            {
                throw ValidationException("远程结构发布者配置格式无效。");
            }
            PublisherConfigurationDto configuration = DeserializeJson<PublisherConfigurationDto>(
                content,
                "远程结构发布者配置格式无效。");
            if (ContainsUnknownPublisherMembers(configuration) || configuration.GitHubUserIds == null ||
                configuration.GitHubUserIds.Count > 64)
            {
                throw ValidationException("远程结构发布者配置格式无效。");
            }

            var result = new List<string>();
            var uniqueIds = new HashSet<long>();
            for (int index = 0; index < configuration.GitHubUserIds.Count; index++)
            {
                long publisherId = configuration.GitHubUserIds[index];
                if (publisherId <= 0 || !uniqueIds.Add(publisherId))
                {
                    throw ValidationException("远程结构发布者配置格式无效。");
                }
                result.Add(publisherId.ToString(CultureInfo.InvariantCulture));
            }
            return result;
        }

        /// <summary>XMZADD 20260902 读取当前分支头并确认该提交仍是 Revision 0，供预览绑定精确远程基线。</summary>
        public async Task<string> GetRevisionZeroHeadShaAsync(CancellationToken cancellationToken)
        {
            string authenticationToken = ReadAuthenticationToken();
            string repositoryApiPath = "repos/" + _owner + "/" + _repositoryName + "/";
            string headSha = await ReadBranchHeadShaAsync(
                repositoryApiPath,
                authenticationToken,
                cancellationToken).ConfigureAwait(false);
            SnapshotManifest manifest = await ReadManifestAtCommitAsync(
                repositoryApiPath,
                headSha,
                authenticationToken,
                cancellationToken).ConfigureAwait(false);
            return manifest.Revision == 0L ? headSha : null;
        }

        /// <summary>XMZADD 20260902 以单个 Git commit 原子发布首次完整快照和清单，并禁止强制覆盖并发分支更新。</summary>
        public async Task<string> PublishFullSnapshotAsync(
            byte[] compressedSnapshot,
            SnapshotManifest manifest,
            string expectedHeadSha,
            string expectedPublisherGitHubUserId,
            CancellationToken cancellationToken)
        {
            SnapshotManifest manifestCopy = CopyFullSnapshotManifest(manifest);
            byte[] snapshotCopy = ValidateAndCopyFullSnapshot(compressedSnapshot, manifestCopy);
            string boundHeadSha = RequireGitSha(expectedHeadSha);
            string snapshotPath = manifestCopy.SnapshotPath;
            string manifestSha256 = manifestCopy.SnapshotSha256;
            DateTime generatedAtUtc = manifestCopy.GeneratedAtUtc.ToUniversalTime();
            string repositoryApiPath = "repos/" + _owner + "/" + _repositoryName + "/";
            string authenticationToken = ReadAuthenticationToken();

            // 整次完整提交固定使用一个 Token，并再次绑定预览时已核验的不可变 GitHub 数值身份。
            GitHubUserIdentity publishingIdentity = await GetCurrentUserAsync(
                authenticationToken,
                cancellationToken).ConfigureAwait(false);
            if (publishingIdentity == null || !string.Equals(
                publishingIdentity.GitHubUserId,
                expectedPublisherGitHubUserId,
                StringComparison.Ordinal))
            {
                throw new GitHubDictionaryClientException(
                    "当前 GitHub 身份已变化，请重新扫描后再发布。",
                    GitHubDictionaryErrorKind.Authentication,
                    false,
                    null);
            }

            string headSha = await ReadBranchHeadShaAsync(
                repositoryApiPath,
                authenticationToken,
                cancellationToken).ConfigureAwait(false);
            if (!string.Equals(headSha, boundHeadSha, StringComparison.OrdinalIgnoreCase))
            {
                throw ValidationException("扫描后远程分支已变化，请重新扫描后再发布。");
            }

            // 必须在同一个已观察分支头上确认仍是空清单，避免他人先发布后本次继续快进并覆盖其结果。
            SnapshotManifest headManifest = await ReadManifestAtCommitAsync(
                repositoryApiPath,
                headSha,
                authenticationToken,
                cancellationToken).ConfigureAwait(false);
            if (headManifest.Revision != 0L)
            {
                throw ValidationException("远程完整快照已由其他人发布，请重新扫描后再发布。");
            }

            byte[] commitContent = await SendAndReadAsync(
                HttpMethod.Get,
                new Uri(_apiBaseUri, repositoryApiPath + "git/commits/" + headSha),
                null,
                true,
                ApiMaximumBytes,
                cancellationToken,
                authenticationToken).ConfigureAwait(false);
            GitCommitResponseDto currentCommit = DeserializeJson<GitCommitResponseDto>(
                commitContent,
                "GitHub 当前提交格式无效。");
            string baseTreeSha = RequireGitSha(currentCommit.Tree == null ? null : currentCommit.Tree.Sha);

            string snapshotBlobSha = await CreateGitObjectAsync(
                repositoryApiPath + "git/blobs",
                CreateSnapshotBlobContent(snapshotCopy, cancellationToken),
                authenticationToken,
                cancellationToken).ConfigureAwait(false);

            var manifestDto = new FullSnapshotManifestDto
            {
                FormatVersion = 1,
                Revision = 1L,
                SnapshotSha256 = manifestSha256,
                SnapshotPath = snapshotPath,
                GeneratedAtUtc = FormatRepositoryDate(generatedAtUtc),
                LastEventPath = string.Empty
            };
            byte[] manifestContent = AppendLineFeed(SerializeJson(manifestDto, typeof(FullSnapshotManifestDto)));
            string manifestBlobSha = await CreateGitObjectAsync(
                repositoryApiPath + "git/blobs",
                new CreateGitBlobRequestDto
                {
                    Content = Convert.ToBase64String(manifestContent),
                    Encoding = "base64"
                },
                typeof(CreateGitBlobRequestDto),
                authenticationToken,
                cancellationToken).ConfigureAwait(false);

            var treeRequest = new CreateGitTreeRequestDto { BaseTree = baseTreeSha };
            treeRequest.Tree.Add(new GitTreeEntryDto
            {
                Path = snapshotPath,
                Mode = "100644",
                Type = "blob",
                Sha = snapshotBlobSha
            });
            treeRequest.Tree.Add(new GitTreeEntryDto
            {
                Path = "snapshot/manifest.json",
                Mode = "100644",
                Type = "blob",
                Sha = manifestBlobSha
            });
            string treeSha = await CreateGitObjectAsync(
                repositoryApiPath + "git/trees",
                treeRequest,
                typeof(CreateGitTreeRequestDto),
                authenticationToken,
                cancellationToken).ConfigureAwait(false);

            var commitRequest = new CreateGitCommitRequestDto
            {
                Message = "Initialize EOS data dictionary snapshot revision 1",
                Tree = treeSha
            };
            commitRequest.Parents.Add(headSha);
            string newCommitSha = await CreateGitObjectAsync(
                repositoryApiPath + "git/commits",
                commitRequest,
                typeof(CreateGitCommitRequestDto),
                authenticationToken,
                cancellationToken).ConfigureAwait(false);

            byte[] updateContent;
            try
            {
                updateContent = await SendAndReadAsync(
                    new HttpMethod("PATCH"),
                    new Uri(_apiBaseUri, repositoryApiPath + "git/refs/heads/" + _branch),
                    SerializeJson(
                        new UpdateGitReferenceRequestDto { Sha = newCommitSha, Force = false },
                        typeof(UpdateGitReferenceRequestDto)),
                    true,
                    ApiMaximumBytes,
                    cancellationToken,
                    authenticationToken).ConfigureAwait(false);
            }
            catch (GitHubDictionaryClientException exception)
            {
                if (exception.Kind != GitHubDictionaryErrorKind.Validation)
                {
                    throw;
                }
                throw ValidationException("远程分支已变化或拒绝原子更新，请重新扫描后再发布。");
            }
            GitReferenceResponseDto updatedReference = DeserializeJson<GitReferenceResponseDto>(
                updateContent,
                "GitHub 分支更新结果格式无效。");
            string updatedSha = RequireGitSha(updatedReference.Object == null ? null : updatedReference.Object.Sha);
            if (!string.Equals(updatedSha, newCommitSha, StringComparison.OrdinalIgnoreCase))
            {
                throw ValidationException("GitHub 分支更新结果与本次完整提交不一致。");
            }
            return newCommitSha;
        }

        /// <summary>XMZADD 20260902 在任何异步请求前复制完整清单，避免调用方并发修改已校验的发布路径、哈希或日期。</summary>
        private static SnapshotManifest CopyFullSnapshotManifest(SnapshotManifest manifest)
        {
            if (manifest == null)
            {
                throw ValidationException("首次完整快照发布参数无效。");
            }
            return new SnapshotManifest
            {
                FormatVersion = manifest.FormatVersion,
                Revision = manifest.Revision,
                SnapshotSha256 = manifest.SnapshotSha256,
                SnapshotPath = manifest.SnapshotPath,
                GeneratedAtUtc = manifest.GeneratedAtUtc
            };
        }

        /// <summary>XMZADD 20260902 从指定提交精确读取并校验清单，使首次完整提交与所观察分支头形成并发保护。</summary>
        private async Task<SnapshotManifest> ReadManifestAtCommitAsync(
            string repositoryApiPath,
            string commitSha,
            string authenticationToken,
            CancellationToken cancellationToken)
        {
            byte[] responseContent = await SendAndReadAsync(
                HttpMethod.Get,
                new Uri(
                    _apiBaseUri,
                    repositoryApiPath + "contents/snapshot/manifest.json?ref=" +
                    Uri.EscapeDataString(commitSha)),
                null,
                true,
                ApiMaximumBytes,
                cancellationToken,
                authenticationToken).ConfigureAwait(false);
            RepositoryContentResponseDto response = DeserializeJson<RepositoryContentResponseDto>(
                responseContent,
                "GitHub 当前清单返回格式无效。");
            if (!string.Equals(response.Encoding, "base64", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(response.Content))
            {
                throw ValidationException("GitHub 当前清单返回格式无效。");
            }

            byte[] manifestContent;
            try
            {
                manifestContent = Convert.FromBase64String(response.Content);
            }
            catch (FormatException)
            {
                throw ValidationException("GitHub 当前清单返回格式无效。");
            }
            if (manifestContent.Length == 0 || manifestContent.Length > ManifestMaximumBytes)
            {
                throw ValidationException("GitHub 当前清单返回格式无效。");
            }
            return ParseManifest(manifestContent);
        }

        /// <summary>XMZADD 20260902 使用固定 Token 读取目标分支当前 SHA，供预览绑定和确认发布共用相同并发规则。</summary>
        private async Task<string> ReadBranchHeadShaAsync(
            string repositoryApiPath,
            string authenticationToken,
            CancellationToken cancellationToken)
        {
            byte[] referenceContent = await SendAndReadAsync(
                HttpMethod.Get,
                new Uri(_apiBaseUri, repositoryApiPath + "git/ref/heads/" + _branch),
                null,
                true,
                ApiMaximumBytes,
                cancellationToken,
                authenticationToken).ConfigureAwait(false);
            GitReferenceResponseDto reference = DeserializeJson<GitReferenceResponseDto>(
                referenceContent,
                "GitHub 分支引用格式无效。");
            return RequireGitSha(reference.Object == null ? null : reference.Object.Sha);
        }

        /// <summary>XMZADD 20260902 统一完整快照与编解码器的压缩容量边界，并为 GitHub 单文件超限返回独立安全提示。</summary>
        private static void ValidateFullSnapshotLength(int compressedSnapshotLength)
        {
            if (compressedSnapshotLength <= 0)
            {
                throw ValidationException("首次完整快照发布参数无效。");
            }
            if (compressedSnapshotLength > SnapshotCodec.MaximumCompressedBytes)
            {
                throw ValidationException("完整快照超过 GitHub 单文件 100 MiB 限制。");
            }
        }

        /// <summary>XMZADD 20260902 在任何写仓库请求前复制并完整复核首次快照、修订号、哈希和内容寻址路径。</summary>
        private static byte[] ValidateAndCopyFullSnapshot(byte[] compressedSnapshot, SnapshotManifest manifest)
        {
            if (compressedSnapshot == null)
            {
                throw ValidationException("首次完整快照发布参数无效。");
            }
            // 统一使用编解码器上限，避免发布入口和快照格式接受范围不一致。
            ValidateFullSnapshotLength(compressedSnapshot.Length);
            if (manifest == null || manifest.FormatVersion != 1 || manifest.Revision != 1L ||
                manifest.GeneratedAtUtc == default(DateTime))
            {
                throw ValidationException("首次完整快照发布参数无效。");
            }
            var snapshotCopy = new byte[compressedSnapshot.Length];
            Buffer.BlockCopy(compressedSnapshot, 0, snapshotCopy, 0, compressedSnapshot.Length);
            var codec = new SnapshotCodec();
            string sha256 = codec.ComputeSha256(snapshotCopy);
            string expectedPath = "snapshot/revisions/000000001-" + sha256 + ".json.gz";
            if (!string.Equals(manifest.SnapshotSha256, sha256, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(manifest.SnapshotPath, expectedPath, StringComparison.Ordinal))
            {
                throw ValidationException("首次完整快照哈希或路径无效。");
            }
            try
            {
                SnapshotData decoded = codec.DecodeAndValidate(snapshotCopy, sha256);
                new SnapshotValidator().ValidatePublicContent(decoded);
                if (decoded.FormatVersion != 1 || decoded.Revision != 1L)
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
                throw ValidationException("首次完整快照未通过规范校验。");
            }
            return snapshotCopy;
        }

        /// <summary>XMZADD 20260902 创建 Git blob、tree 或 commit 并只接受标准 SHA 返回值。</summary>
        private async Task<string> CreateGitObjectAsync(
            string relativeApiPath,
            object request,
            Type requestType,
            string authenticationToken,
            CancellationToken cancellationToken)
        {
            byte[] content = await SendAndReadAsync(
                HttpMethod.Post,
                new Uri(_apiBaseUri, relativeApiPath),
                SerializeJson(request, requestType),
                true,
                ApiMaximumBytes,
                cancellationToken,
                authenticationToken).ConfigureAwait(false);
            GitShaResponseDto response = DeserializeJson<GitShaResponseDto>(
                content,
                "GitHub Git 对象返回格式无效。");
            return RequireGitSha(response.Sha);
        }

        /// <summary>XMZADD 20260902 流式创建大快照 Git blob，并复用统一请求头、认证、响应限制和安全错误映射。</summary>
        private async Task<string> CreateGitObjectAsync(
            string relativeApiPath,
            HttpContent requestContent,
            string authenticationToken,
            CancellationToken cancellationToken)
        {
            byte[] content = await SendAndReadContentAsync(
                HttpMethod.Post,
                new Uri(_apiBaseUri, relativeApiPath),
                requestContent,
                true,
                ApiMaximumBytes,
                cancellationToken,
                authenticationToken).ConfigureAwait(false);
            GitShaResponseDto response = DeserializeJson<GitShaResponseDto>(
                content,
                "GitHub Git 对象返回格式无效。");
            return RequireGitSha(response.Sha);
        }

        /// <summary>XMZADD 20260902 创建只持有不可变快照副本的流式 Blob 请求内容，避免构造全量 Base64 字符串和 JSON 字节数组。</summary>
        private static HttpContent CreateSnapshotBlobContent(
            byte[] snapshotCopy,
            CancellationToken cancellationToken)
        {
            return new StreamingGitBlobContent(snapshotCopy, cancellationToken);
        }

        /// <summary>XMZADD 20260902 严格接收 GitHub 返回的 40 位十六进制对象编号，避免不可信文本进入后续 API 路径。</summary>
        private static string RequireGitSha(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 40)
            {
                throw ValidationException("GitHub Git 对象编号格式无效。");
            }
            for (int index = 0; index < value.Length; index++)
            {
                char character = value[index];
                bool isDigit = character >= '0' && character <= '9';
                bool isLowerHex = character >= 'a' && character <= 'f';
                bool isUpperHex = character >= 'A' && character <= 'F';
                if (!isDigit && !isLowerHex && !isUpperHex)
                {
                    throw ValidationException("GitHub Git 对象编号格式无效。");
                }
            }
            return value;
        }

        /// <summary>XMZADD 20260902 生成仓库既有清单使用的 UTC DataContract 日期文本。</summary>
        private static string FormatRepositoryDate(DateTime value)
        {
            DateTime utcValue = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
            var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            long milliseconds;
            try
            {
                milliseconds = checked((long)(utcValue - epoch).TotalMilliseconds);
            }
            catch (OverflowException)
            {
                throw ValidationException("首次完整快照日期无效。");
            }
            return "/Date(" + milliseconds.ToString(CultureInfo.InvariantCulture) + ")/";
        }

        /// <summary>XMZADD 20260902 为公开清单追加仓库文本文件约定的单个换行符。</summary>
        private static byte[] AppendLineFeed(byte[] content)
        {
            var result = new byte[content.Length + 1];
            Buffer.BlockCopy(content, 0, result, 0, content.Length);
            result[result.Length - 1] = (byte)'\n';
            return result;
        }

        /// <summary>XMZADD 20260901 在数据契约转换前校验授权 JSON 原始类型，防止字符串数字被宽松转换为授权 ID。</summary>
        private static bool IsStrictPublisherConfigurationJson(byte[] content)
        {
            string json;
            try
            {
                json = new UTF8Encoding(false, true).GetString(content);
            }
            catch (DecoderFallbackException)
            {
                return false;
            }
            int index = 0;
            SkipJsonWhitespace(json, ref index);
            if (!ConsumeJsonText(json, ref index, "{\"githubUserIds\":"))
            {
                return false;
            }
            SkipJsonWhitespace(json, ref index);
            if (index >= json.Length || json[index++] != '[')
            {
                return false;
            }
            SkipJsonWhitespace(json, ref index);
            if (index < json.Length && json[index] != ']')
            {
                while (true)
                {
                    if (index >= json.Length || json[index] < '1' || json[index] > '9')
                    {
                        return false;
                    }
                    while (index < json.Length && json[index] >= '0' && json[index] <= '9')
                    {
                        index++;
                    }
                    SkipJsonWhitespace(json, ref index);
                    if (index >= json.Length || json[index] != ',')
                    {
                        break;
                    }
                    index++;
                    SkipJsonWhitespace(json, ref index);
                }
            }
            if (index >= json.Length || json[index++] != ']')
            {
                return false;
            }
            SkipJsonWhitespace(json, ref index);
            if (index >= json.Length || json[index++] != '}')
            {
                return false;
            }
            SkipJsonWhitespace(json, ref index);
            return index == json.Length;
        }

        /// <summary>XMZADD 20260901 跳过 JSON 协议允许的空白字符，使授权配置可由普通格式化工具安全生成。</summary>
        private static void SkipJsonWhitespace(string json, ref int index)
        {
            while (index < json.Length)
            {
                char character = json[index];
                if (character != ' ' && character != '\t' && character != '\r' && character != '\n')
                {
                    return;
                }
                index++;
            }
        }

        /// <summary>XMZADD 20260901 匹配固定 JSON 成员文本并允许成员标点之间存在标准空白。</summary>
        private static bool ConsumeJsonText(string json, ref int index, string expected)
        {
            for (int expectedIndex = 0; expectedIndex < expected.Length; expectedIndex++)
            {
                char character = expected[expectedIndex];
                if (character == ':' || character == '{')
                {
                    SkipJsonWhitespace(json, ref index);
                }
                if (index >= json.Length || json[index] != character)
                {
                    return false;
                }
                index++;
                if (character == ':' || character == '{')
                {
                    SkipJsonWhitespace(json, ref index);
                }
            }
            return true;
        }

        /// <summary>XMZADD 20260901 比较保留与忽略扩展成员的授权配置字节，严格拒绝任何未声明字段。</summary>
        private static bool ContainsUnknownPublisherMembers(PublisherConfigurationDto configuration)
        {
            byte[] preserving = SerializePublisherConfiguration(configuration, false);
            byte[] ignoring = SerializePublisherConfiguration(configuration, true);
            if (preserving.Length != ignoring.Length)
            {
                return true;
            }
            for (int index = 0; index < preserving.Length; index++)
            {
                if (preserving[index] != ignoring[index])
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>XMZADD 20260901 以一致数据契约序列化授权配置，使字节差异只来源于未知成员。</summary>
        private static byte[] SerializePublisherConfiguration(
            PublisherConfigurationDto configuration,
            bool ignoreExtensionData)
        {
            var serializer = new DataContractJsonSerializer(
                typeof(PublisherConfigurationDto),
                new DataContractJsonSerializerSettings
                {
                    IgnoreExtensionDataObject = ignoreExtensionData,
                    MaxItemsInObjectGraph = 128
                });
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, configuration);
                return stream.ToArray();
            }
        }

        /// <summary>XMZADD 20260901 将受限 GitHub 用户响应转换为不可变数值身份，供保存凭据前后共用同一验证规则。</summary>
        private static GitHubUserIdentity DeserializeIdentity(byte[] content)
        {
            GitHubUserDto dto = DeserializeJson<GitHubUserDto>(content, "GitHub 用户身份格式无效。");
            if (dto.Id <= 0 || string.IsNullOrWhiteSpace(dto.Login))
            {
                throw ValidationException("GitHub 用户身份格式无效。");
            }
            return new GitHubUserIdentity
            {
                GitHubUserId = dto.Id.ToString(CultureInfo.InvariantCulture),
                Login = dto.Login,
                DisplayName = dto.Name
            };
        }

        /// <summary>XMZADD 20260901 按 BatchId 复用已存在的精确事件，并在提交结果不确定时对账避免重复 Issue。</summary>
        public async Task<int> CreateDictionaryIssueAsync(
            DictionaryChangeBatch batch,
            CancellationToken cancellationToken)
        {
            if (batch == null)
            {
                throw new ArgumentNullException("batch");
            }
            if (string.IsNullOrWhiteSpace(batch.BatchId))
            {
                throw new ArgumentException("字典批次编号不能为空。", "batch");
            }
            string batchJson = DictionaryJsonSerializer.SerializePublicBatch(batch);
            string issueBody = "```json-v1\n" + batchJson + "\n```";
            if (Encoding.UTF8.GetByteCount(batchJson) > DictionaryChangeValidator.MaximumPayloadBytes ||
                Encoding.UTF8.GetByteCount(issueBody) > MaximumIssueBodyBytes)
            {
                throw ValidationException("GitHub Issue 正文超过安全大小限制。");
            }
            string issueTitle = "[EOS-DICTIONARY-EVENT] " + batch.BatchId;
            int? existingIssueNumber = await FindExistingDictionaryIssueAsync(
                issueTitle,
                issueBody,
                cancellationToken);
            if (existingIssueNumber.HasValue)
            {
                return existingIssueNumber.Value;
            }
            var requestDto = new CreateIssueRequestDto
            {
                Title = issueTitle,
                Body = issueBody
            };
            byte[] requestContent = SerializeJson(requestDto, typeof(CreateIssueRequestDto));
            Uri uri = new Uri(
                _apiBaseUri,
                "repos/" + _owner + "/" + _repositoryName + "/issues");
            byte[] responseContent;
            try
            {
                responseContent = await SendAndReadAsync(
                    HttpMethod.Post,
                    uri,
                    requestContent,
                    true,
                    ApiMaximumBytes,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                existingIssueNumber = await TryReconcileDictionaryIssueAsync(issueTitle, issueBody);
                if (existingIssueNumber.HasValue)
                {
                    return existingIssueNumber.Value;
                }
                throw;
            }
            catch (GitHubDictionaryClientException exception) when (exception.IsTransient)
            {
                existingIssueNumber = await TryReconcileDictionaryIssueAsync(issueTitle, issueBody);
                if (existingIssueNumber.HasValue)
                {
                    return existingIssueNumber.Value;
                }
                throw;
            }
            CreateIssueResponseDto response = DeserializeJson<CreateIssueResponseDto>(
                responseContent,
                "GitHub Issue 返回格式无效。");
            if (response.Number <= 0)
            {
                throw ValidationException("GitHub Issue 返回格式无效。");
            }
            return response.Number;
        }

        /// <summary>XMZADD 20260903 兼容旧协调器入口，仅在三态确认结果为已应用时返回成功。</summary>
        public async Task<bool> AreDictionaryOperationsAppliedAsync(
            DictionaryChangeBatch batch,
            int issueNumber,
            long maximumRevision,
            CancellationToken cancellationToken)
        {
            DictionaryIssueApplyStatus status = await GetDictionaryOperationsApplyStatusAsync(
                batch,
                issueNumber,
                maximumRevision,
                cancellationToken);
            return status == DictionaryIssueApplyStatus.Applied;
        }

        /// <summary>XMZADD 20260903 从 Issue 不可变作者和创建日期定位主分支事件，并以修订文档交叉核验全部 OperationId。</summary>
        public async Task<DictionaryIssueApplyStatus> GetDictionaryOperationsApplyStatusAsync(
            DictionaryChangeBatch batch,
            int issueNumber,
            long maximumRevision,
            CancellationToken cancellationToken)
        {
            if (batch == null)
            {
                throw new ArgumentNullException("batch");
            }
            if (issueNumber <= 0)
            {
                throw new ArgumentOutOfRangeException("issueNumber");
            }
            // 事件目录只采用 GitHub 不可修改的创建时间和数值用户 ID，避免可编辑标题、正文或登录名参与信任判断。
            Uri uri = new Uri(
                _apiBaseUri,
                "repos/" + _owner + "/" + _repositoryName + "/issues/" +
                issueNumber.ToString(CultureInfo.InvariantCulture));
            byte[] content = await SendAndReadAsync(
                HttpMethod.Get,
                uri,
                null,
                true,
                ApiMaximumBytes,
                cancellationToken);
            IssueEventLocationDto response = DeserializeJson<IssueEventLocationDto>(
                content,
                "GitHub Issue 定位信息返回格式无效。");
            if (response.Number != issueNumber || response.User == null ||
                !IsValidGitHubUserId(response.User.Id.ToString(CultureInfo.InvariantCulture)))
            {
                throw ValidationException("GitHub Issue 定位信息返回格式无效。");
            }
            if (maximumRevision <= 0 || batch.Operations == null || batch.Operations.Count == 0)
            {
                return DictionaryIssueApplyStatus.Pending;
            }
            DateTime issueCreatedAtUtc = ParseRepositoryDate(response.CreatedAt);
            string trustedAuthorId = response.User.Id.ToString(CultureInfo.InvariantCulture);
            string eventDirectory = "events/" +
                issueCreatedAtUtc.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture) + "/" +
                trustedAuthorId + "/";
            var revisionCache = new Dictionary<long, DictionaryRevisionPackage>();
            for (int index = 0; index < batch.Operations.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DictionaryChangeOperation expected = batch.Operations[index];
                if (expected == null || !IsSafeOperationId(expected.OperationId))
                {
                    throw ValidationException("本地待确认操作编号无效。");
                }
                string eventPath = eventDirectory + expected.OperationId + ".json";
                byte[] eventContent;
                try
                {
                    eventContent = await SendAndReadAsync(
                        HttpMethod.Get,
                        BuildRawUri(eventPath),
                        null,
                        false,
                        EventMaximumBytes,
                        cancellationToken);
                }
                catch (GitHubDictionaryClientException exception)
                {
                    if (exception.Kind == GitHubDictionaryErrorKind.NotFound)
                    {
                        // 只有事件确实缺失时才采信工作流无效标签，避免残留标签覆盖已经落库的可信事实。
                        return HasIssueLabel(response, "dictionary-event-invalid")
                            ? DictionaryIssueApplyStatus.Rejected
                            : DictionaryIssueApplyStatus.Pending;
                    }
                    throw;
                }
                AppliedDictionaryEvent appliedEvent = DeserializeJson<AppliedDictionaryEvent>(
                    eventContent,
                    "远程字典事件格式无效。");
                if (appliedEvent != null && appliedEvent.Revision > maximumRevision)
                {
                    // 原始事件比当前清单更早刷新到 CDN 时继续等待，不能提前把本机提交标记为已生效。
                    return DictionaryIssueApplyStatus.Pending;
                }
                if (!MatchesAppliedEvent(appliedEvent, expected, trustedAuthorId, maximumRevision))
                {
                    throw ValidationException("远程字典事件与本地待确认操作不一致。");
                }
                DictionaryRevisionPackage revisionPackage;
                if (!revisionCache.TryGetValue(appliedEvent.Revision, out revisionPackage))
                {
                    // 孤立事件文件不能证明快照已应用，必须再由同一主分支修订文档登记后才完成本机 outbox。
                    try
                    {
                        revisionPackage = await DownloadRevisionAsync(appliedEvent.Revision, cancellationToken);
                    }
                    catch (GitHubDictionaryClientException exception)
                    {
                        if (exception.Kind == GitHubDictionaryErrorKind.NotFound)
                        {
                            return DictionaryIssueApplyStatus.Pending;
                        }
                        throw;
                    }
                    revisionCache.Add(appliedEvent.Revision, revisionPackage);
                }
                if (!RevisionContainsOperation(revisionPackage, expected.OperationId))
                {
                    throw ValidationException("远程修订文档未登记待确认操作。");
                }
            }
            return DictionaryIssueApplyStatus.Applied;
        }

        /// <summary>XMZADD 20260903 严格核对旧版私有覆盖 Issue 后关闭原单，并以确定性公开批次复用现有幂等创建流程。</summary>
        public async Task<int> RecreateRejectedDictionaryIssueAsync(
            DictionaryChangeBatch batch,
            int rejectedIssueNumber,
            CancellationToken cancellationToken)
        {
            if (batch == null || rejectedIssueNumber <= 0 ||
                batch.Overrides == null || batch.Overrides.Count == 0)
            {
                throw ValidationException("被拒绝的字典 Issue 恢复参数无效。");
            }

            string expectedTitle = "[EOS-DICTIONARY-EVENT] " + batch.BatchId;
            string expectedLegacyBody = "```json-v1\n" +
                DictionaryJsonSerializer.SerializeBatch(batch) + "\n```";
            string expectedPublicBody = "```json-v1\n" +
                DictionaryJsonSerializer.SerializePublicBatch(batch) + "\n```";
            Uri issueUri = new Uri(
                _apiBaseUri,
                "repos/" + _owner + "/" + _repositoryName + "/issues/" +
                rejectedIssueNumber.ToString(CultureInfo.InvariantCulture));
            byte[] issueContent = await SendAndReadAsync(
                HttpMethod.Get,
                issueUri,
                null,
                true,
                ApiMaximumBytes,
                cancellationToken);
            IssueEventLocationDto issue = DeserializeJson<IssueEventLocationDto>(
                issueContent,
                "GitHub 被拒绝 Issue 返回格式无效。");
            string issueAuthorId = issue.User == null
                ? string.Empty
                : issue.User.Id.ToString(CultureInfo.InvariantCulture);

            // 关闭和重建会修改远端状态，必须先把不可变身份及可编辑协议内容全部与本机原批次精确对齐。
            if (issue.Number != rejectedIssueNumber ||
                !IsValidGitHubUserId(issueAuthorId) ||
                !string.Equals(issueAuthorId, batch.AuthorGitHubUserId, StringComparison.Ordinal) ||
                !string.Equals(issue.Title, expectedTitle, StringComparison.Ordinal) ||
                !string.Equals(issue.Body, expectedLegacyBody, StringComparison.Ordinal) ||
                string.Equals(issue.Body, expectedPublicBody, StringComparison.Ordinal) ||
                !HasIssueLabel(issue, "dictionary-event-invalid") ||
                (!string.Equals(issue.State, "open", StringComparison.Ordinal) &&
                 !string.Equals(issue.State, "closed", StringComparison.Ordinal)))
            {
                throw ValidationException("GitHub 被拒绝 Issue 与本机旧批次不一致，已拒绝恢复。");
            }

            var recoveryBatch = new DictionaryChangeBatch
            {
                BatchId = CreateRecoveryBatchId(batch.BatchId, rejectedIssueNumber),
                AuthorGitHubUserId = batch.AuthorGitHubUserId,
                CreatedAtUtc = batch.CreatedAtUtc,
                Operations = new List<DictionaryChangeOperation>()
            };
            // 恢复批次只复制可公开操作引用，绝不复制或修改原批次中的本机人工覆盖。
            if (batch.Operations != null)
            {
                for (int index = 0; index < batch.Operations.Count; index++)
                {
                    recoveryBatch.Operations.Add(batch.Operations[index]);
                }
            }

            if (string.Equals(issue.State, "open", StringComparison.Ordinal))
            {
                var updateRequest = new UpdateIssueRequestDto { State = "closed" };
                byte[] updateContent = SerializeJson(updateRequest, typeof(UpdateIssueRequestDto));
                await SendAndReadAsync(
                    new HttpMethod("PATCH"),
                    issueUri,
                    updateContent,
                    true,
                    ApiMaximumBytes,
                    cancellationToken);
            }

            return await CreateDictionaryIssueAsync(recoveryBatch, cancellationToken);
        }

        /// <summary>XMZADD 20260903 以传统循环精确识别 GitHub 标签，避免相似名称误触发拒绝恢复。</summary>
        private static bool HasIssueLabel(IssueEventLocationDto issue, string expectedLabel)
        {
            if (issue == null || issue.Labels == null)
            {
                return false;
            }
            for (int index = 0; index < issue.Labels.Count; index++)
            {
                GitHubIssueLabelDto label = issue.Labels[index];
                if (label != null && string.Equals(
                    label.Name,
                    expectedLabel,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>XMZADD 20260903 由原批次号、旧 Issue 编号和固定恢复协议生成可重复对账的 32 位批次 GUID。</summary>
        private static string CreateRecoveryBatchId(string batchId, int rejectedIssueNumber)
        {
            const string recoveryProtocolVersion = "dictionary-issue-recovery-v1";
            string source = (batchId ?? string.Empty) + "\n" +
                rejectedIssueNumber.ToString(CultureInfo.InvariantCulture) + "\n" +
                recoveryProtocolVersion;
            byte[] hash;
            using (SHA256 sha256 = SHA256.Create())
            {
                hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(source));
            }
            var guidBytes = new byte[16];
            Array.Copy(hash, 0, guidBytes, 0, guidBytes.Length);
            return new Guid(guidBytes).ToString("N");
        }

        /// <summary>XMZADD 20260903 校验不可变事件的身份、修订和目标属性与本机待确认操作完全对应。</summary>
        private static bool MatchesAppliedEvent(
            AppliedDictionaryEvent appliedEvent,
            DictionaryChangeOperation expected,
            string trustedAuthorId,
            long maximumRevision)
        {
            return appliedEvent != null && appliedEvent.Revision > 0 &&
                appliedEvent.Revision <= maximumRevision &&
                string.Equals(appliedEvent.OperationId, expected.OperationId, StringComparison.Ordinal) &&
                string.Equals(appliedEvent.AuthorGitHubUserId, trustedAuthorId, StringComparison.Ordinal) &&
                string.Equals(appliedEvent.ObjectKey ?? string.Empty, expected.ObjectKey ?? string.Empty, StringComparison.Ordinal) &&
                string.Equals(appliedEvent.FieldKey ?? string.Empty, expected.FieldKey ?? string.Empty, StringComparison.Ordinal) &&
                string.Equals(appliedEvent.PropertyName ?? string.Empty, expected.PropertyName ?? string.Empty, StringComparison.Ordinal) &&
                string.Equals(appliedEvent.ChangeKind, expected.ChangeKind, StringComparison.Ordinal) &&
                (!string.Equals(expected.ChangeKind, "Set", StringComparison.Ordinal) ||
                 string.Equals(appliedEvent.NewValue ?? string.Empty, expected.NewValue ?? string.Empty, StringComparison.Ordinal));
        }

        /// <summary>XMZADD 20260903 确认事件还被同一可信修订包登记，避免孤立文件被误认为已进入规范快照。</summary>
        private static bool RevisionContainsOperation(DictionaryRevisionPackage package, string operationId)
        {
            if (package == null || package.Batches == null)
            {
                return false;
            }
            for (int batchIndex = 0; batchIndex < package.Batches.Count; batchIndex++)
            {
                DictionaryChangeBatch batch = package.Batches[batchIndex];
                if (batch == null || batch.Operations == null)
                {
                    continue;
                }
                for (int operationIndex = 0; operationIndex < batch.Operations.Count; operationIndex++)
                {
                    DictionaryChangeOperation operation = batch.Operations[operationIndex];
                    if (operation != null && string.Equals(
                        operation.OperationId,
                        operationId,
                        StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>XMZADD 20260901 精确核对候选 Issue 的标题和正文，使相同 BatchId 只能表示同一批业务变更。</summary>
        private async Task<int?> FindExistingDictionaryIssueAsync(
            string expectedTitle,
            string expectedBody,
            CancellationToken cancellationToken)
        {
            string query = "repo:" + _owner + "/" + _repositoryName +
                " is:issue in:title \"" + expectedTitle + "\"";
            Uri uri = new Uri(
                _apiBaseUri,
                "search/issues?q=" + Uri.EscapeDataString(query) +
                "&per_page=" + MaximumIssueSearchResults.ToString(CultureInfo.InvariantCulture));
            byte[] content = await SendAndReadAsync(
                HttpMethod.Get,
                uri,
                null,
                true,
                ApiMaximumBytes,
                cancellationToken);
            SearchIssuesResponseDto response = DeserializeJson<SearchIssuesResponseDto>(
                content,
                "GitHub Issue 查重返回格式无效。");
            if (response.IncompleteResults || response.TotalCount < 0 ||
                response.TotalCount > MaximumIssueSearchResults ||
                response.Items == null || response.Items.Count > MaximumIssueSearchResults ||
                response.TotalCount != response.Items.Count)
            {
                throw ValidationException("GitHub Issue 查重返回格式无效。");
            }

            return FindMatchingDictionaryIssue(response.Items, expectedTitle, expectedBody);
        }

        /// <summary>XMZADD 20260901 直接读取仓库最新 Issue 对账刚提交的批次，避免依赖搜索索引刷新。</summary>
        private async Task<int?> FindRecentDictionaryIssueAsync(
            string expectedTitle,
            string expectedBody,
            CancellationToken cancellationToken)
        {
            Uri uri = new Uri(
                _apiBaseUri,
                "repos/" + _owner + "/" + _repositoryName +
                "/issues?state=all&sort=created&direction=desc&per_page=" +
                MaximumRecentIssueResults.ToString(CultureInfo.InvariantCulture));
            byte[] content = await SendAndReadAsync(
                HttpMethod.Get,
                uri,
                null,
                true,
                IssueReconcileMaximumBytes,
                cancellationToken);
            IList<SearchIssueItemDto> items = DeserializeJson<List<SearchIssueItemDto>>(
                content,
                "GitHub Issue 对账返回格式无效。");
            if (items == null || items.Count > MaximumRecentIssueResults)
            {
                throw ValidationException("GitHub Issue 对账返回格式无效。");
            }
            return FindMatchingDictionaryIssue(items, expectedTitle, expectedBody);
        }

        /// <summary>XMZADD 20260901 在受限候选集合中精确匹配标题和正文，并拒绝同 BatchId 的内容冲突。</summary>
        private static int? FindMatchingDictionaryIssue(
            IList<SearchIssueItemDto> items,
            string expectedTitle,
            string expectedBody)
        {
            int? matchingIssueNumber = null;
            bool batchIdCollision = false;
            for (int index = 0; index < items.Count; index++)
            {
                SearchIssueItemDto item = items[index];
                if (item == null)
                {
                    throw ValidationException("GitHub Issue 查重返回格式无效。");
                }
                if (item.PullRequest != null)
                {
                    continue;
                }
                if (!string.Equals(item.Title, expectedTitle, StringComparison.Ordinal))
                {
                    continue;
                }
                if (item.Number <= 0)
                {
                    throw ValidationException("GitHub Issue 查重返回格式无效。");
                }
                if (!string.Equals(item.Body, expectedBody, StringComparison.Ordinal))
                {
                    batchIdCollision = true;
                    continue;
                }
                if (!matchingIssueNumber.HasValue || item.Number < matchingIssueNumber.Value)
                {
                    matchingIssueNumber = item.Number;
                }
            }
            if (batchIdCollision)
            {
                throw ValidationException("同一 BatchId 已存在不同内容，已拒绝重复发布。");
            }
            return matchingIssueNumber;
        }

        /// <summary>XMZADD 20260901 在提交结果未知后以独立令牌直接读取最新 Issue，避免搜索索引延迟掩盖已成功创建的事件。</summary>
        private async Task<int?> TryReconcileDictionaryIssueAsync(string expectedTitle, string expectedBody)
        {
            try
            {
                return await FindRecentDictionaryIssueAsync(
                    expectedTitle,
                    expectedBody,
                    CancellationToken.None);
            }
            catch (GitHubDictionaryClientException exception)
            {
                if (exception.Kind == GitHubDictionaryErrorKind.Validation)
                {
                    throw;
                }
                return null;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        /// <summary>XMZADD 20260901 构造只包含已校验仓库坐标和相对路径的公开原始文件地址。</summary>
        private Uri BuildRawUri(string relativePath)
        {
            string safePath = DictionaryRepositoryOptions.ValidateRelativePath(relativePath);
            string repositoryPath = _owner + "/" + _repositoryName + "/" + _branch + "/" + safePath;
            return new Uri(_rawBaseUri, repositoryPath);
        }

        /// <summary>XMZADD 20260902 将既有 JSON 字节请求转换为可统一释放的 HTTP 内容并复用安全发送边界。</summary>
        private async Task<byte[]> SendAndReadAsync(
            HttpMethod method,
            Uri uri,
            byte[] requestContent,
            bool authenticated,
            int maximumBytes,
            CancellationToken cancellationToken,
            string explicitAuthenticationToken = null)
        {
            HttpContent httpContent = null;
            if (requestContent != null)
            {
                httpContent = new ByteArrayContent(requestContent);
                httpContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            }
            return await SendAndReadContentAsync(
                method,
                uri,
                httpContent,
                authenticated,
                maximumBytes,
                cancellationToken,
                explicitAuthenticationToken).ConfigureAwait(false);
        }

        /// <summary>XMZADD 20260902 统一发送字节数组或流式 GitHub 内容，保持既有认证、请求头、响应限制和安全错误分类。</summary>
        private async Task<byte[]> SendAndReadContentAsync(
            HttpMethod method,
            Uri uri,
            HttpContent requestContent,
            bool authenticated,
            int maximumBytes,
            CancellationToken cancellationToken,
            string explicitAuthenticationToken = null)
        {
            using (var request = new HttpRequestMessage(method, uri))
            {
                // 由请求统一释放普通或流式内容，避免各调用路径产生不同资源生命周期。
                request.Content = requestContent;
                cancellationToken.ThrowIfCancellationRequested();
                if (!authenticated && _httpClient.DefaultRequestHeaders.Contains("Authorization"))
                {
                    throw new GitHubDictionaryClientException(
                        "匿名 GitHub 下载客户端不得预配置 Authorization。",
                        GitHubDictionaryErrorKind.Configuration,
                        false,
                        null);
                }
                request.Headers.UserAgent.ParseAdd("SHB-EOSDataDictionary/1.0");
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
                request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2026-03-10");
                if (authenticated)
                {
                    string token = explicitAuthenticationToken;
                    if (token == null)
                    {
                        token = ReadAuthenticationToken();
                    }
                    ValidateAuthenticationToken(token);
                    try
                    {
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    }
                    catch (FormatException)
                    {
                        throw new GitHubDictionaryClientException(
                            "GitHub 个人 Token 格式无效，请重新配置。",
                            GitHubDictionaryErrorKind.Authentication,
                            false,
                            null);
                    }
                }
                HttpResponseMessage response;
                try
                {
                    response = await _httpClient.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    throw new GitHubDictionaryClientException(
                        "连接 GitHub 超时，请稍后重试。",
                        GitHubDictionaryErrorKind.Transient,
                        true,
                        null);
                }
                catch (HttpRequestException)
                {
                    throw new GitHubDictionaryClientException(
                        "暂时无法连接 GitHub，请稍后重试。",
                        GitHubDictionaryErrorKind.Transient,
                        true,
                        null);
                }

                using (response)
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        throw CreateHttpException(response);
                    }
                    if (response.Content == null)
                    {
                        throw ValidationException("GitHub 返回内容为空。");
                    }
                    if (response.Content.Headers.ContentLength.HasValue &&
                        response.Content.Headers.ContentLength.Value > maximumBytes)
                    {
                        throw ValidationException("GitHub 返回内容超过安全大小限制。");
                    }
                    return await ReadLimitedContentAsync(response.Content, maximumBytes, cancellationToken);
                }
            }
        }

        /// <summary>XMZADD 20260902 一次读取并校验当前个人 Token，供多请求原子发布固定同一认证上下文。</summary>
        private string ReadAuthenticationToken()
        {
            string token;
            try
            {
                token = _tokenProvider == null ? null : _tokenProvider();
            }
            catch (Exception exception)
            {
                if (exception is OutOfMemoryException || exception is StackOverflowException)
                {
                    throw;
                }
                throw new GitHubDictionaryClientException(
                    "GitHub 个人 Token 无法读取，请重新配置。",
                    GitHubDictionaryErrorKind.Authentication,
                    false,
                    null);
            }
            ValidateAuthenticationToken(token);
            return token;
        }

        /// <summary>XMZADD 20260901 限制认证 Token 长度、空白和控制字符，避免异常凭据进入请求头或造成无界头部分配。</summary>
        private static void ValidateAuthenticationToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token) || token.Length > 4096 || token != token.Trim())
            {
                throw new GitHubDictionaryClientException(
                    "GitHub 个人 Token 格式无效，请重新配置。",
                    GitHubDictionaryErrorKind.Authentication,
                    false,
                    null);
            }
            for (int index = 0; index < token.Length; index++)
            {
                if (char.IsControl(token[index]))
                {
                    throw new GitHubDictionaryClientException(
                        "GitHub 个人 Token 格式无效，请重新配置。",
                        GitHubDictionaryErrorKind.Authentication,
                        false,
                        null);
                }
            }
        }

        /// <summary>XMZADD 20260901 按状态码和限流头生成不含远端正文的认证、缺失或瞬时错误。</summary>
        private static GitHubDictionaryClientException CreateHttpException(HttpResponseMessage response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return new GitHubDictionaryClientException(
                    "GitHub 认证失败，请重新配置个人 Token。",
                    GitHubDictionaryErrorKind.Authentication,
                    false,
                    null);
            }
            TimeSpan? retryAfter = ReadRetryAfter(response);
            bool exhausted = HeaderEquals(response, "X-RateLimit-Remaining", "0");
            if ((int)response.StatusCode == 429)
            {
                return new GitHubDictionaryClientException(
                    "GitHub 请求受到限流，请稍后重试。",
                    GitHubDictionaryErrorKind.RateLimited,
                    true,
                    retryAfter);
            }
            if (response.StatusCode == HttpStatusCode.Forbidden && (retryAfter.HasValue || exhausted))
            {
                return new GitHubDictionaryClientException(
                    "GitHub 请求受到限流，请稍后重试。",
                    GitHubDictionaryErrorKind.RateLimited,
                    true,
                    retryAfter);
            }
            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                return new GitHubDictionaryClientException(
                    "GitHub 拒绝当前凭据访问仓库。",
                    GitHubDictionaryErrorKind.Authentication,
                    false,
                    null);
            }
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new GitHubDictionaryClientException(
                    "GitHub 字典资源不存在。",
                    GitHubDictionaryErrorKind.NotFound,
                    false,
                    null);
            }
            if (response.StatusCode == HttpStatusCode.RequestTimeout)
            {
                return new GitHubDictionaryClientException(
                    "GitHub 请求超时，请稍后重试。",
                    GitHubDictionaryErrorKind.Transient,
                    true,
                    retryAfter);
            }
            int numericStatus = (int)response.StatusCode;
            if (numericStatus >= 500 && numericStatus <= 599)
            {
                return new GitHubDictionaryClientException(
                    "GitHub 服务暂时不可用，请稍后重试。",
                    GitHubDictionaryErrorKind.Transient,
                    true,
                    retryAfter);
            }
            return new GitHubDictionaryClientException(
                "GitHub 请求未成功完成。",
                GitHubDictionaryErrorKind.Validation,
                false,
                null);
        }

        /// <summary>XMZADD 20260901 读取标准 Retry-After 或限流重置时间，为协调器提供安全等待时长。</summary>
        private static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
        {
            if (response.Headers.RetryAfter != null)
            {
                if (response.Headers.RetryAfter.Delta.HasValue)
                {
                    return response.Headers.RetryAfter.Delta.Value;
                }
                if (response.Headers.RetryAfter.Date.HasValue)
                {
                    TimeSpan difference = response.Headers.RetryAfter.Date.Value.UtcDateTime - DateTime.UtcNow;
                    return difference < TimeSpan.Zero ? TimeSpan.Zero : difference;
                }
            }
            IEnumerable<string> resetValues;
            if (response.Headers.TryGetValues("X-RateLimit-Reset", out resetValues))
            {
                foreach (string value in resetValues)
                {
                    long unixSeconds;
                    if (long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out unixSeconds))
                    {
                        try
                        {
                            DateTime resetUtc = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(unixSeconds);
                            TimeSpan difference = resetUtc - DateTime.UtcNow;
                            return difference < TimeSpan.Zero ? TimeSpan.Zero : difference;
                        }
                        catch (ArgumentOutOfRangeException)
                        {
                            return null;
                        }
                    }
                }
            }
            return null;
        }

        /// <summary>XMZADD 20260901 比较单值响应头以识别 GitHub 官方限流耗尽信号。</summary>
        private static bool HeaderEquals(HttpResponseMessage response, string name, string expected)
        {
            IEnumerable<string> values;
            if (!response.Headers.TryGetValues(name, out values))
            {
                return false;
            }
            foreach (string value in values)
            {
                if (string.Equals(value, expected, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>XMZADD 20260901 以固定缓冲逐段读取响应并在越界或取消时立即终止，防止恶意大文件耗尽内存。</summary>
        private static async Task<byte[]> ReadLimitedContentAsync(
            HttpContent content,
            int maximumBytes,
            CancellationToken cancellationToken)
        {
            using (Stream input = await content.ReadAsStreamAsync())
            using (var output = new MemoryStream())
            {
                var buffer = new byte[81920];
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int read = await input.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                    if (read == 0)
                    {
                        break;
                    }
                    if (output.Length + read > maximumBytes)
                    {
                        throw ValidationException("GitHub 返回内容超过安全大小限制。");
                    }
                    output.Write(buffer, 0, read);
                }
                return output.ToArray();
            }
        }

        /// <summary>XMZADD 20260901 严格校验修订格式、目标修订和有限安全事件路径集合。</summary>
        private static void ValidateRevisionDocument(RemoteRevisionDto dto, long expectedRevision)
        {
            if (dto == null || dto.FormatVersion != 1 || dto.Revision != expectedRevision ||
                dto.EventPaths == null || dto.EventPaths.Count == 0 ||
                dto.EventPaths.Count > MaximumEventsPerRevision)
            {
                throw ValidationException("远程修订文档不兼容或事件数量无效。");
            }
            var paths = new HashSet<string>(StringComparer.Ordinal);
            for (int index = 0; index < dto.EventPaths.Count; index++)
            {
                string path = ValidateEventPath(dto.EventPaths[index]);
                if (!paths.Add(path))
                {
                    throw ValidationException("远程修订文档包含重复事件路径。");
                }
            }
        }

        /// <summary>XMZADD 20260901 限制修订引用只指向不可变事件目录，禁止把修订文档或其他文件伪装成事件。</summary>
        private static string ValidateEventPath(string relativePath)
        {
            string path;
            try
            {
                path = DictionaryRepositoryOptions.ValidateRelativePath(relativePath);
            }
            catch (ArgumentException)
            {
                throw ValidationException("远程修订文档包含不安全事件路径。");
            }
            if (!path.StartsWith("events/", StringComparison.Ordinal) ||
                path.StartsWith("events/revisions/", StringComparison.Ordinal) ||
                !path.EndsWith(".json", StringComparison.Ordinal))
            {
                throw ValidationException("远程修订文档包含无效事件路径。");
            }
            return path;
        }

        /// <summary>XMZADD 20260901 将不可变审计事件映射为本地重放操作，并仅采用服务端确认的新值或结构载荷。</summary>
        private static DictionaryChangeOperation ConvertAppliedEvent(AppliedDictionaryEvent source, long revision)
        {
            if (source == null || source.Revision != revision || !IsSafeOperationId(source.OperationId) ||
                !IsValidGitHubUserId(source.AuthorGitHubUserId) ||
                string.IsNullOrWhiteSpace(source.ChangeKind))
            {
                throw ValidationException("远程字典事件身份或修订无效。");
            }
            var operation = new DictionaryChangeOperation
            {
                OperationId = source.OperationId,
                AuthorGitHubUserId = source.AuthorGitHubUserId,
                ObjectKey = source.ObjectKey,
                FieldKey = source.FieldKey,
                PropertyName = source.PropertyName,
                ChangeKind = source.ChangeKind,
                CreatedAtUtc = source.AppliedAtUtc,
                Evidence = CloneEvidence(source.Evidence)
            };
            switch (source.ChangeKind)
            {
                case "Set":
                    operation.NewValue = source.NewValue;
                    break;
                case "AddTable":
                    if (source.NewTablePayload == null)
                    {
                        throw ValidationException("远程新增表事件缺少结构载荷。");
                    }
                    operation.TablePayload = source.NewTablePayload;
                    break;
                case "AddField":
                    if (source.NewFieldPayload == null)
                    {
                        throw ValidationException("远程新增字段事件缺少结构载荷。");
                    }
                    operation.FieldPayload = source.NewFieldPayload;
                    break;
                case "AddRelation":
                    if (source.NewRelationPayload == null)
                    {
                        throw ValidationException("远程新增关系事件缺少结构载荷。");
                    }
                    operation.RelationPayload = source.NewRelationPayload;
                    break;
                case "RemoveTable":
                case "RemoveField":
                    break;
                case "RemoveRelation":
                    if (source.OldRelationPayload == null)
                    {
                        throw ValidationException("远程删除关系事件缺少历史结构载荷。");
                    }
                    operation.RelationPayload = source.OldRelationPayload;
                    break;
                default:
                    throw ValidationException("远程字典事件类型不兼容。");
            }
            return operation;
        }

        /// <summary>XMZADD 20260901 深拷贝服务端已校验的公开证据，避免增量包与反序列化对象共享可变集合。</summary>
        private static IList<EvidenceItem> CloneEvidence(IList<EvidenceItem> source)
        {
            var result = new List<EvidenceItem>();
            if (source == null)
            {
                return result;
            }
            for (int index = 0; index < source.Count; index++)
            {
                EvidenceItem item = source[index];
                if (item == null)
                {
                    continue;
                }
                result.Add(new EvidenceItem
                {
                    SourceType = item.SourceType,
                    SourcePath = item.SourcePath,
                    SourceLine = item.SourceLine,
                    RuleName = item.RuleName,
                    Explanation = item.Explanation
                });
            }
            return result;
        }

        /// <summary>XMZADD 20260901 校验远程全局操作号字符集和长度，防止路径式编号进入本地幂等状态。</summary>
        private static bool IsSafeOperationId(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 128)
            {
                return false;
            }
            for (int index = 0; index < value.Length; index++)
            {
                char character = value[index];
                bool safe = (character >= 'a' && character <= 'z') ||
                    (character >= 'A' && character <= 'Z') ||
                    (character >= '0' && character <= '9') ||
                    character == '-' || character == '_';
                if (!safe)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>XMZADD 20260901 只接受非零 ASCII 数字 GitHub 用户 ID，避免仓库事件作者被替换或模糊解释。</summary>
        private static bool IsValidGitHubUserId(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 64)
            {
                return false;
            }
            bool hasNonZeroDigit = false;
            for (int index = 0; index < value.Length; index++)
            {
                char character = value[index];
                if (character < '0' || character > '9')
                {
                    return false;
                }
                if (character != '0')
                {
                    hasNonZeroDigit = true;
                }
            }
            return hasNonZeroDigit;
        }

        /// <summary>XMZADD 20260901 校验 manifest 中只接受完整小写或大写十六进制 SHA-256。</summary>
        private static bool IsSha256(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 64)
            {
                return false;
            }
            for (int index = 0; index < value.Length; index++)
            {
                char character = value[index];
                bool valid = (character >= '0' && character <= '9') ||
                    (character >= 'a' && character <= 'f') ||
                    (character >= 'A' && character <= 'F');
                if (!valid)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>XMZADD 20260901 解析仓库协议使用的 ISO UTC 或旧初始清单日期格式并统一为 UTC。</summary>
        private static DateTime ParseRepositoryDate(string value)
        {
            DateTime parsed;
            if (!string.IsNullOrEmpty(value) && DateTime.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out parsed))
            {
                return parsed.ToUniversalTime();
            }
            if (!string.IsNullOrEmpty(value) && value.StartsWith("/Date(", StringComparison.Ordinal) &&
                value.EndsWith(")/", StringComparison.Ordinal))
            {
                string millisecondsText = value.Substring(6, value.Length - 8);
                long milliseconds;
                if (long.TryParse(millisecondsText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out milliseconds))
                {
                    try
                    {
                        return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(milliseconds);
                    }
                    catch (ArgumentOutOfRangeException)
                    {
                        throw ValidationException("远程仓库日期格式无效。");
                    }
                }
            }
            throw ValidationException("远程仓库日期格式无效。");
        }

        /// <summary>XMZADD 20260901 使用固定数据契约安全序列化 GitHub API 请求，禁止拼接用户文本形成 JSON。</summary>
        private static byte[] SerializeJson(object value, Type type)
        {
            try
            {
                var serializer = new DataContractJsonSerializer(
                    type,
                    new DataContractJsonSerializerSettings
                    {
                        DateTimeFormat = new DateTimeFormat("o", CultureInfo.InvariantCulture)
                    });
                using (var stream = new MemoryStream())
                {
                    serializer.WriteObject(stream, value);
                    return stream.ToArray();
                }
            }
            catch (SerializationException)
            {
                throw ValidationException("GitHub 请求内容无法安全序列化。");
            }
        }

        /// <summary>XMZADD 20260901 从受限 UTF-8 内容反序列化公开仓库或 API DTO，并隐藏损坏载荷详情。</summary>
        private static T DeserializeJson<T>(byte[] content, string safeMessage) where T : class
        {
            if (content == null || content.Length == 0)
            {
                throw ValidationException(safeMessage);
            }
            try
            {
                var serializer = new DataContractJsonSerializer(typeof(T));
                using (var stream = new MemoryStream(content, false))
                {
                    T value = serializer.ReadObject(stream) as T;
                    if (value == null || stream.Position != stream.Length)
                    {
                        throw new SerializationException();
                    }
                    return value;
                }
            }
            catch (Exception exception)
            {
                if (exception is OutOfMemoryException || exception is StackOverflowException)
                {
                    throw;
                }
                throw ValidationException(safeMessage);
            }
        }

        /// <summary>XMZADD 20260901 创建统一的永久协议校验异常，阻止损坏远程内容进入本地状态。</summary>
        private static GitHubDictionaryClientException ValidationException(string message)
        {
            return new GitHubDictionaryClientException(
                message,
                GitHubDictionaryErrorKind.Validation,
                false,
                null);
        }

        /// <summary>XMZADD 20260902 将不可变快照副本分块编码为 GitHub Blob JSON，限制上传阶段临时内存与快照大小无关。</summary>
        private sealed class StreamingGitBlobContent : HttpContent
        {
            private const int SourceBlockBytes = 12 * 1024;
            private static readonly byte[] Base64Alphabet = Encoding.ASCII.GetBytes(
                "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/");
            private static readonly byte[] JsonPrefix = Encoding.ASCII.GetBytes("{\"content\":\"");
            private static readonly byte[] JsonSuffix = Encoding.ASCII.GetBytes("\",\"encoding\":\"base64\"}");
            private readonly byte[] _source;
            private readonly CancellationToken _cancellationToken;

            /// <summary>XMZADD 20260902 捕获已校验快照副本和本次发布取消标记，并声明固定 JSON 媒体类型。</summary>
            public StreamingGitBlobContent(byte[] source, CancellationToken cancellationToken)
            {
                if (source == null)
                {
                    throw new ArgumentNullException("source");
                }
                _source = source;
                _cancellationToken = cancellationToken;
                Headers.ContentType = new MediaTypeHeaderValue("application/json");
            }

            /// <summary>XMZADD 20260902 按 Base64 四字符分组精确计算请求长度，使 HTTP 层无需预缓冲正文。</summary>
            protected override bool TryComputeLength(out long length)
            {
                length = JsonPrefix.Length +
                    (4L * ((_source.Length + 2L) / 3L)) +
                    JsonSuffix.Length;
                return true;
            }

            /// <summary>XMZADD 20260902 按三字节边界分块生成连续 Base64 并写入网络流，避免产生全量字符串或 JSON 副本。</summary>
            protected override async Task SerializeToStreamAsync(Stream stream, TransportContext context)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                await stream.WriteAsync(
                    JsonPrefix,
                    0,
                    JsonPrefix.Length,
                    _cancellationToken).ConfigureAwait(false);

                // 源块取三字节倍数，保证跨块编码连续且只有最终块产生 Base64 填充。
                var outputBuffer = new byte[(SourceBlockBytes / 3) * 4];
                int sourceOffset = 0;
                while (_source.Length - sourceOffset >= 3)
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    int sourceCount = Math.Min(SourceBlockBytes, _source.Length - sourceOffset);
                    sourceCount -= sourceCount % 3;
                    int sourceLimit = sourceOffset + sourceCount;
                    int outputOffset = 0;
                    while (sourceOffset < sourceLimit)
                    {
                        int first = _source[sourceOffset];
                        int second = _source[sourceOffset + 1];
                        int third = _source[sourceOffset + 2];
                        outputBuffer[outputOffset] = Base64Alphabet[first >> 2];
                        outputBuffer[outputOffset + 1] = Base64Alphabet[((first & 3) << 4) | (second >> 4)];
                        outputBuffer[outputOffset + 2] = Base64Alphabet[((second & 15) << 2) | (third >> 6)];
                        outputBuffer[outputOffset + 3] = Base64Alphabet[third & 63];
                        sourceOffset += 3;
                        outputOffset += 4;
                    }
                    await stream.WriteAsync(
                        outputBuffer,
                        0,
                        outputOffset,
                        _cancellationToken).ConfigureAwait(false);
                }

                int remainingBytes = _source.Length - sourceOffset;
                if (remainingBytes > 0)
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    int first = _source[sourceOffset];
                    int second = remainingBytes == 2 ? _source[sourceOffset + 1] : 0;
                    outputBuffer[0] = Base64Alphabet[first >> 2];
                    outputBuffer[1] = Base64Alphabet[((first & 3) << 4) | (second >> 4)];
                    if (remainingBytes == 2)
                    {
                        outputBuffer[2] = Base64Alphabet[(second & 15) << 2];
                    }
                    else
                    {
                        outputBuffer[2] = (byte)'=';
                    }
                    outputBuffer[3] = (byte)'=';
                    await stream.WriteAsync(
                        outputBuffer,
                        0,
                        4,
                        _cancellationToken).ConfigureAwait(false);
                }

                _cancellationToken.ThrowIfCancellationRequested();
                await stream.WriteAsync(
                    JsonSuffix,
                    0,
                    JsonSuffix.Length,
                    _cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>XMZADD 20260902 定义首次完整发布写入仓库的规范清单字段。</summary>
        [DataContract]
        private sealed class FullSnapshotManifestDto
        {
            [DataMember]
            public int FormatVersion { get; set; }

            [DataMember]
            public long Revision { get; set; }

            [DataMember]
            public string SnapshotSha256 { get; set; }

            [DataMember]
            public string SnapshotPath { get; set; }

            [DataMember]
            public string GeneratedAtUtc { get; set; }

            [DataMember]
            public string LastEventPath { get; set; }
        }

        /// <summary>XMZADD 20260902 映射 GitHub Contents API 返回的受限 base64 文件正文。</summary>
        [DataContract]
        private sealed class RepositoryContentResponseDto
        {
            [DataMember(Name = "encoding")]
            public string Encoding { get; set; }

            [DataMember(Name = "content")]
            public string Content { get; set; }
        }

        /// <summary>XMZADD 20260902 定义 GitHub 创建 base64 blob 的固定请求字段。</summary>
        [DataContract]
        private sealed class CreateGitBlobRequestDto
        {
            [DataMember(Name = "content")]
            public string Content { get; set; }

            [DataMember(Name = "encoding")]
            public string Encoding { get; set; }
        }

        /// <summary>XMZADD 20260902 定义一次完整提交中同时替换快照和清单的 tree 请求。</summary>
        [DataContract]
        private sealed class CreateGitTreeRequestDto
        {
            /// <summary>XMZADD 20260902 初始化 tree 条目集合，避免序列化缺失成员。</summary>
            public CreateGitTreeRequestDto()
            {
                Tree = new List<GitTreeEntryDto>();
            }

            [DataMember(Name = "base_tree")]
            public string BaseTree { get; set; }

            [DataMember(Name = "tree")]
            public IList<GitTreeEntryDto> Tree { get; set; }
        }

        /// <summary>XMZADD 20260902 定义完整提交 tree 中单个不可执行普通文件条目。</summary>
        [DataContract]
        private sealed class GitTreeEntryDto
        {
            [DataMember(Name = "path")]
            public string Path { get; set; }

            [DataMember(Name = "mode")]
            public string Mode { get; set; }

            [DataMember(Name = "type")]
            public string Type { get; set; }

            [DataMember(Name = "sha")]
            public string Sha { get; set; }
        }

        /// <summary>XMZADD 20260902 定义只包含固定消息、新 tree 和单个父提交的 Git commit 请求。</summary>
        [DataContract]
        private sealed class CreateGitCommitRequestDto
        {
            /// <summary>XMZADD 20260902 初始化父提交集合以保证完整快照只形成一条快进提交链。</summary>
            public CreateGitCommitRequestDto()
            {
                Parents = new List<string>();
            }

            [DataMember(Name = "message")]
            public string Message { get; set; }

            [DataMember(Name = "tree")]
            public string Tree { get; set; }

            [DataMember(Name = "parents")]
            public IList<string> Parents { get; set; }
        }

        /// <summary>XMZADD 20260902 定义非强制更新 main 引用的固定请求字段。</summary>
        [DataContract]
        private sealed class UpdateGitReferenceRequestDto
        {
            [DataMember(Name = "sha")]
            public string Sha { get; set; }

            [DataMember(Name = "force")]
            public bool Force { get; set; }
        }

        /// <summary>XMZADD 20260902 映射 GitHub blob、tree 和 commit 创建结果的对象编号。</summary>
        [DataContract]
        private sealed class GitShaResponseDto
        {
            [DataMember(Name = "sha")]
            public string Sha { get; set; }
        }

        /// <summary>XMZADD 20260902 映射 GitHub 分支引用返回的目标对象编号。</summary>
        [DataContract]
        private sealed class GitReferenceResponseDto
        {
            [DataMember(Name = "object")]
            public GitShaResponseDto Object { get; set; }
        }

        /// <summary>XMZADD 20260902 映射 GitHub 当前提交的基础 tree，确保新提交只替换目标快照文件。</summary>
        [DataContract]
        private sealed class GitCommitResponseDto
        {
            [DataMember(Name = "sha")]
            public string Sha { get; set; }

            [DataMember(Name = "tree")]
            public GitShaResponseDto Tree { get; set; }
        }

        /// <summary>XMZADD 20260901 映射公开快照清单字段而不接受任意运行时类型。</summary>
        [DataContract]
        private sealed class RemoteManifestDto
        {
            [DataMember]
            public int FormatVersion { get; set; }

            [DataMember]
            public long Revision { get; set; }

            [DataMember]
            public string SnapshotSha256 { get; set; }

            [DataMember]
            public string SnapshotPath { get; set; }

            [DataMember]
            public string GeneratedAtUtc { get; set; }
        }

        /// <summary>XMZADD 20260901 映射修订文档的固定版本、修订和事件路径集合。</summary>
        [DataContract]
        private sealed class RemoteRevisionDto
        {
            /// <summary>XMZADD 20260901 初始化可枚举的事件路径集合以便缺失字段仍可被显式拒绝。</summary>
            public RemoteRevisionDto()
            {
                EventPaths = new List<string>();
            }

            [DataMember]
            public int FormatVersion { get; set; }

            [DataMember]
            public long Revision { get; set; }

            [DataMember]
            public string GeneratedAtUtc { get; set; }

            [DataMember]
            public IList<string> EventPaths { get; set; }
        }

        /// <summary>XMZADD 20260901 映射 GitHub 用户 API 的不可变数值 ID 和显示信息。</summary>
        [DataContract]
        private sealed class GitHubUserDto
        {
            [DataMember(Name = "id")]
            public long Id { get; set; }

            [DataMember(Name = "login")]
            public string Login { get; set; }

            [DataMember(Name = "name")]
            public string Name { get; set; }
        }

        /// <summary>XMZADD 20260901 映射公开发布者配置并保留未知成员，使授权名单只能采用固定数字 ID 契约。</summary>
        [DataContract]
        private sealed class PublisherConfigurationDto : IExtensibleDataObject
        {
            /// <summary>XMZADD 20260901 初始化发布者集合，便于明确区分空白名单与缺失字段。</summary>
            public PublisherConfigurationDto()
            {
                GitHubUserIds = new List<long>();
            }

            [DataMember(Name = "githubUserIds", IsRequired = true)]
            public IList<long> GitHubUserIds { get; set; }

            public ExtensionDataObject ExtensionData { get; set; }
        }

        /// <summary>XMZADD 20260901 定义 GitHub 创建 Issue 请求字段，由数据契约负责全部用户文本转义。</summary>
        [DataContract]
        private sealed class CreateIssueRequestDto
        {
            [DataMember(Name = "title")]
            public string Title { get; set; }

            [DataMember(Name = "body")]
            public string Body { get; set; }
        }

        /// <summary>XMZADD 20260903 定义关闭旧 Issue 的最小请求字段，避免恢复流程改写标题、正文或标签。</summary>
        [DataContract]
        private sealed class UpdateIssueRequestDto
        {
            [DataMember(Name = "state")]
            public string State { get; set; }
        }

        /// <summary>XMZADD 20260901 映射 GitHub 创建 Issue 后返回的稳定问题编号。</summary>
        [DataContract]
        private sealed class CreateIssueResponseDto
        {
            [DataMember(Name = "number")]
            public int Number { get; set; }
        }

        /// <summary>XMZADD 20260903 映射 Issue 的定位、协议内容、状态和标签，供三态确认及旧版恢复共用。</summary>
        [DataContract]
        private sealed class IssueEventLocationDto
        {
            /// <summary>XMZADD 20260903 初始化标签集合，使未返回标签的确认响应仍可按待生效处理。</summary>
            public IssueEventLocationDto()
            {
                Labels = new List<GitHubIssueLabelDto>();
            }

            [DataMember(Name = "number", IsRequired = true)]
            public int Number { get; set; }

            [DataMember(Name = "created_at", IsRequired = true)]
            public string CreatedAt { get; set; }

            [DataMember(Name = "user", IsRequired = true)]
            public GitHubIssueUserDto User { get; set; }

            [DataMember(Name = "title")]
            public string Title { get; set; }

            [DataMember(Name = "body")]
            public string Body { get; set; }

            [DataMember(Name = "state")]
            public string State { get; set; }

            [DataMember(Name = "labels")]
            public IList<GitHubIssueLabelDto> Labels { get; set; }
        }

        /// <summary>XMZADD 20260903 映射 GitHub Issue 标签名称，以精确识别工作流明确拒绝标记。</summary>
        [DataContract]
        private sealed class GitHubIssueLabelDto
        {
            [DataMember(Name = "name")]
            public string Name { get; set; }
        }

        /// <summary>XMZADD 20260903 映射 Issue 作者的不可变 GitHub 数值身份，禁止以可改登录名定位事件。</summary>
        [DataContract]
        private sealed class GitHubIssueUserDto
        {
            [DataMember(Name = "id", IsRequired = true)]
            public long Id { get; set; }
        }

        /// <summary>XMZADD 20260901 映射 GitHub Issue 查重响应并限制候选集合，发布前只接受受控搜索结果。</summary>
        [DataContract]
        private sealed class SearchIssuesResponseDto
        {
            /// <summary>XMZADD 20260901 初始化候选集合，使缺失或空结果可以按固定协议处理。</summary>
            public SearchIssuesResponseDto()
            {
                Items = new List<SearchIssueItemDto>();
            }

            [DataMember(Name = "total_count", IsRequired = true)]
            public int TotalCount { get; set; }

            [DataMember(Name = "incomplete_results", IsRequired = true)]
            public bool IncompleteResults { get; set; }

            [DataMember(Name = "items", IsRequired = true)]
            public IList<SearchIssueItemDto> Items { get; set; }
        }

        /// <summary>XMZADD 20260901 映射候选 Issue 的稳定编号、标题和完整正文，供 BatchId 精确对账。</summary>
        [DataContract]
        private sealed class SearchIssueItemDto
        {
            [DataMember(Name = "number", IsRequired = true)]
            public int Number { get; set; }

            [DataMember(Name = "title", IsRequired = true)]
            public string Title { get; set; }

            [DataMember(Name = "body", IsRequired = true)]
            public string Body { get; set; }

            [DataMember(Name = "pull_request", EmitDefaultValue = false)]
            public PullRequestMarkerDto PullRequest { get; set; }
        }

        /// <summary>XMZADD 20260901 标记仓库 Issue 列表中的拉取请求，使对账只复用真正的字典 Issue。</summary>
        [DataContract]
        private sealed class PullRequestMarkerDto
        {
        }
    }
}
