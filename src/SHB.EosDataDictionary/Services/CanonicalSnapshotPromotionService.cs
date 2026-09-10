using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using SHB.EosDataDictionary.Models;

namespace SHB.EosDataDictionary.Services
{
    /// <summary>XMZADD 20260909 返回管理员全量规范提升生成的文件、哈希和业务数量。</summary>
    public sealed class CanonicalSnapshotPromotionResult
    {
        public SnapshotManifest Manifest { get; internal set; }
        public string SnapshotSha256 { get; internal set; }
        public string LatestSnapshotPath { get; internal set; }
        public string RevisionSnapshotPath { get; internal set; }
        public string ManifestPath { get; internal set; }
        public string RevisionEventPath { get; internal set; }
        public string PromotionAuditPath { get; internal set; }
        public int TableCount { get; internal set; }
        public int FieldCount { get; internal set; }
        public int EntityCount { get; internal set; }
        public int EnumItemCount { get; internal set; }
        public int RelationCount { get; internal set; }
    }

    /// <summary>XMZADD 20260909 将已验证候选提升为下一规范修订，并生成可由 Git 单次提交的公开文件。</summary>
    public sealed class CanonicalSnapshotPromotionService
    {
        private readonly SnapshotCodec _codec = new SnapshotCodec();
        private readonly SnapshotValidator _validator = new SnapshotValidator();

        /// <summary>XMZADD 20260909 核对仓库基线后生成同字节快照、空事件修订、提升审计和最终 manifest。</summary>
        public CanonicalSnapshotPromotionResult Prepare(
            string repositoryRoot,
            SnapshotData candidate,
            long expectedBaseRevision,
            string expectedBaseSha256,
            DateTime generatedAtUtc)
        {
            string normalizedRepositoryRoot = ValidateArguments(
                repositoryRoot, candidate, expectedBaseRevision, expectedBaseSha256);
            string snapshotRoot = Path.Combine(normalizedRepositoryRoot, "snapshot");
            string manifestPath = Path.Combine(snapshotRoot, "manifest.json");
            byte[] originalManifest = File.ReadAllBytes(manifestPath);
            PromotionManifestDocument baseManifest = DeserializeDocument<PromotionManifestDocument>(originalManifest);
            ValidateBaseManifest(baseManifest, expectedBaseRevision, expectedBaseSha256);

            string baseSnapshotPath = ResolveRepositoryPath(normalizedRepositoryRoot, baseManifest.SnapshotPath);
            byte[] basePayload = File.ReadAllBytes(baseSnapshotPath);
            ValidateBasePayload(basePayload, baseManifest, expectedBaseRevision, expectedBaseSha256);
            string latestSnapshotPath = Path.Combine(snapshotRoot, "latest.json.gz");
            byte[] originalLatest = File.ReadAllBytes(latestSnapshotPath);

            DateTime stableGeneratedAtUtc = generatedAtUtc.Kind == DateTimeKind.Utc
                ? generatedAtUtc
                : generatedAtUtc.ToUniversalTime();
            long revision = expectedBaseRevision + 1L;
            candidate.Revision = revision;
            candidate.RefreshedAt = stableGeneratedAtUtc;
            _validator.Validate(candidate);
            new PublicSnapshotEvidenceSanitizer().Sanitize(candidate);
            _validator.ValidatePublicContent(candidate);

            // 同一字节数组同时进入 latest、本次不可变修订和本机规范库，避免确定性压缩实现差异。
            byte[] promotedPayload = _codec.Encode(candidate);
            string promotedSha256 = _codec.ComputeSha256(promotedPayload);
            string revisionText = revision.ToString("000000000", CultureInfo.InvariantCulture);
            string revisionRelativePath = "snapshot/revisions/" + revisionText + "-" + promotedSha256 + ".json.gz";
            string revisionSnapshotPath = ResolveRepositoryPath(normalizedRepositoryRoot, revisionRelativePath);
            string revisionEventPath = Path.Combine(normalizedRepositoryRoot, "events", "revisions", revisionText + ".json");
            string promotionAuditPath = Path.Combine(snapshotRoot, "promotions", revisionText + ".json");
            EnsureNewTargetsDoNotExist(revisionSnapshotPath, revisionEventPath, promotionAuditPath);

            PromotionCounts counts = CountCandidate(candidate);
            var manifestDocument = new PromotionManifestDocument
            {
                FormatVersion = SnapshotValidator.SupportedFormatVersion,
                GeneratedAtUtc = stableGeneratedAtUtc,
                LastEventPath = NormalizeLastEventPath(baseManifest.LastEventPath),
                Revision = revision,
                SnapshotPath = revisionRelativePath,
                SnapshotSha256 = promotedSha256
            };
            byte[] revisionEvent = SerializeDocument(new PromotionRevisionDocument
            {
                EventPaths = new List<string>(),
                FormatVersion = SnapshotValidator.SupportedFormatVersion,
                GeneratedAtUtc = stableGeneratedAtUtc,
                Revision = revision
            });
            byte[] promotionAudit = SerializeDocument(new PromotionAuditDocument
            {
                BaseRevision = expectedBaseRevision,
                BaseSnapshotSha256 = expectedBaseSha256.ToLowerInvariant(),
                EntityCount = counts.EntityCount,
                EnumItemCount = counts.EnumItemCount,
                FieldCount = counts.FieldCount,
                FormatVersion = SnapshotValidator.SupportedFormatVersion,
                GeneratedAtUtc = stableGeneratedAtUtc,
                NewSnapshotSha256 = promotedSha256,
                RelationCount = counts.RelationCount,
                Revision = revision,
                TableCount = counts.TableCount
            });
            byte[] promotedManifest = SerializeDocument(manifestDocument);

            CommitFiles(
                promotedPayload,
                revisionEvent,
                promotionAudit,
                promotedManifest,
                originalLatest,
                originalManifest,
                latestSnapshotPath,
                revisionSnapshotPath,
                revisionEventPath,
                promotionAuditPath,
                manifestPath);

            return new CanonicalSnapshotPromotionResult
            {
                Manifest = new SnapshotManifest
                {
                    FormatVersion = manifestDocument.FormatVersion,
                    GeneratedAtUtc = manifestDocument.GeneratedAtUtc,
                    Revision = manifestDocument.Revision,
                    SnapshotPath = manifestDocument.SnapshotPath,
                    SnapshotSha256 = manifestDocument.SnapshotSha256
                },
                SnapshotSha256 = promotedSha256,
                LatestSnapshotPath = latestSnapshotPath,
                RevisionSnapshotPath = revisionSnapshotPath,
                ManifestPath = manifestPath,
                RevisionEventPath = revisionEventPath,
                PromotionAuditPath = promotionAuditPath,
                TableCount = counts.TableCount,
                FieldCount = counts.FieldCount,
                EntityCount = counts.EntityCount,
                EnumItemCount = counts.EnumItemCount,
                RelationCount = counts.RelationCount
            };
        }

        /// <summary>XMZADD 20260909 拒绝缺失仓库、无效候选和不可信基线参数，避免在错误目录创建公开文件。</summary>
        private static string ValidateArguments(
            string repositoryRoot,
            SnapshotData candidate,
            long expectedBaseRevision,
            string expectedBaseSha256)
        {
            if (string.IsNullOrWhiteSpace(repositoryRoot))
            {
                throw new ArgumentException("规范仓库目录不能为空。", "repositoryRoot");
            }
            string normalizedRepositoryRoot = Path.GetFullPath(repositoryRoot);
            if (!Directory.Exists(normalizedRepositoryRoot) ||
                !File.Exists(Path.Combine(normalizedRepositoryRoot, "snapshot", "manifest.json")))
            {
                throw new DirectoryNotFoundException("规范仓库目录缺少 snapshot/manifest.json。");
            }
            if (candidate == null)
            {
                throw new ArgumentNullException("candidate");
            }
            if (expectedBaseRevision < 0 || expectedBaseRevision == long.MaxValue)
            {
                throw new ArgumentOutOfRangeException("expectedBaseRevision");
            }
            if (!IsSha256(expectedBaseSha256))
            {
                throw new ArgumentException("预期基线 SHA-256 格式无效。", "expectedBaseSha256");
            }
            if (candidate.Revision != expectedBaseRevision)
            {
                throw new InvalidOperationException("候选快照修订号与预期基线不一致。");
            }
            return normalizedRepositoryRoot;
        }

        /// <summary>XMZADD 20260909 核对 manifest 的格式、修订和哈希，阻止覆盖并发产生的新规范。</summary>
        private static void ValidateBaseManifest(
            PromotionManifestDocument manifest,
            long expectedBaseRevision,
            string expectedBaseSha256)
        {
            if (manifest == null || manifest.FormatVersion != SnapshotValidator.SupportedFormatVersion ||
                manifest.Revision != expectedBaseRevision ||
                !string.Equals(manifest.SnapshotSha256, expectedBaseSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("仓库 manifest 已偏离预期基线。");
            }
            if (string.IsNullOrWhiteSpace(manifest.SnapshotPath))
            {
                throw new InvalidDataException("仓库 manifest 缺少快照路径。");
            }
        }

        /// <summary>XMZADD 20260909 回读 manifest 指向的快照并核对内容修订，避免只信任文本清单。</summary>
        private void ValidateBasePayload(
            byte[] basePayload,
            PromotionManifestDocument manifest,
            long expectedBaseRevision,
            string expectedBaseSha256)
        {
            SnapshotData baseSnapshot = _codec.DecodeAndValidate(basePayload, expectedBaseSha256);
            if (baseSnapshot.Revision != expectedBaseRevision ||
                baseSnapshot.FormatVersion != manifest.FormatVersion)
            {
                throw new InvalidOperationException("仓库基线快照内容与 manifest 不一致。");
            }
        }

        /// <summary>XMZADD 20260909 将 manifest 相对路径限制在仓库根目录内，防止异常清单读取外部文件。</summary>
        private static string ResolveRepositoryPath(string repositoryRoot, string relativePath)
        {
            string safeRelativePath;
            try
            {
                safeRelativePath = DictionaryRepositoryOptions.ValidateRelativePath(relativePath);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException("仓库 manifest 快照路径无效。", exception);
            }
            string combinedPath = Path.GetFullPath(Path.Combine(
                repositoryRoot,
                safeRelativePath.Replace('/', Path.DirectorySeparatorChar)));
            string rootWithSeparator = repositoryRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            if (!combinedPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("仓库 manifest 快照路径越出仓库根目录。");
            }
            return combinedPath;
        }

        /// <summary>XMZADD 20260909 验证最后业务事件仍是安全仓库相对路径，全量提升不得伪造字段事件。</summary>
        private static string NormalizeLastEventPath(string lastEventPath)
        {
            if (string.IsNullOrWhiteSpace(lastEventPath))
            {
                return null;
            }
            try
            {
                return DictionaryRepositoryOptions.ValidateRelativePath(lastEventPath);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException("仓库最后事件路径无效。", exception);
            }
        }

        /// <summary>XMZADD 20260909 拒绝覆盖既有不可变修订或审计文件，确保管理员提升可重复审查但不可改写。</summary>
        private static void EnsureNewTargetsDoNotExist(params string[] paths)
        {
            for (int index = 0; index < paths.Length; index++)
            {
                if (File.Exists(paths[index]))
                {
                    throw new InvalidOperationException("目标修订文件已经存在，禁止覆盖。");
                }
            }
        }

        /// <summary>XMZADD 20260909 统计提升审计和命令行验收所需的对象、字段、实体、枚举和关系数量。</summary>
        private static PromotionCounts CountCandidate(SnapshotData candidate)
        {
            var result = new PromotionCounts();
            result.TableCount = candidate.Tables.Count;
            for (int tableIndex = 0; tableIndex < candidate.Tables.Count; tableIndex++)
            {
                TableMetadata table = candidate.Tables[tableIndex];
                if (table.EntityName != null && !string.IsNullOrWhiteSpace(table.EntityName.Value) &&
                    table.EntityName.Status == ConfidenceStatus.CodeEvidence)
                {
                    result.EntityCount++;
                }
                result.FieldCount += table.Fields.Count;
                result.RelationCount += table.Relations.Count;
                for (int fieldIndex = 0; fieldIndex < table.Fields.Count; fieldIndex++)
                {
                    result.EnumItemCount += table.Fields[fieldIndex].EnumItems.Count;
                }
            }
            return result;
        }

        /// <summary>XMZADD 20260909 先暂存全部新文件再以 manifest 为最后提交点，失败时恢复旧 latest 并删除未发布修订。</summary>
        private static void CommitFiles(
            byte[] promotedPayload,
            byte[] revisionEvent,
            byte[] promotionAudit,
            byte[] promotedManifest,
            byte[] originalLatest,
            byte[] originalManifest,
            string latestSnapshotPath,
            string revisionSnapshotPath,
            string revisionEventPath,
            string promotionAuditPath,
            string manifestPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(revisionSnapshotPath));
            Directory.CreateDirectory(Path.GetDirectoryName(revisionEventPath));
            Directory.CreateDirectory(Path.GetDirectoryName(promotionAuditPath));
            var stagedFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { latestSnapshotPath, WriteTemporaryFile(latestSnapshotPath, promotedPayload) },
                { revisionSnapshotPath, WriteTemporaryFile(revisionSnapshotPath, promotedPayload) },
                { revisionEventPath, WriteTemporaryFile(revisionEventPath, revisionEvent) },
                { promotionAuditPath, WriteTemporaryFile(promotionAuditPath, promotionAudit) },
                { manifestPath, WriteTemporaryFile(manifestPath, promotedManifest) }
            };
            bool revisionCreated = false;
            bool eventCreated = false;
            bool auditCreated = false;
            bool latestReplaced = false;
            bool manifestReplaced = false;
            try
            {
                File.Move(stagedFiles[revisionSnapshotPath], revisionSnapshotPath);
                revisionCreated = true;
                File.Move(stagedFiles[revisionEventPath], revisionEventPath);
                eventCreated = true;
                File.Move(stagedFiles[promotionAuditPath], promotionAuditPath);
                auditCreated = true;
                ReplaceExistingFile(stagedFiles[latestSnapshotPath], latestSnapshotPath);
                latestReplaced = true;
                ReplaceExistingFile(stagedFiles[manifestPath], manifestPath);
                manifestReplaced = true;
            }
            catch
            {
                if (!manifestReplaced)
                {
                    if (latestReplaced)
                    {
                        TryRestoreFile(latestSnapshotPath, originalLatest);
                    }
                    if (revisionCreated)
                    {
                        TryDeleteFile(revisionSnapshotPath);
                    }
                    if (eventCreated)
                    {
                        TryDeleteFile(revisionEventPath);
                    }
                    if (auditCreated)
                    {
                        TryDeleteFile(promotionAuditPath);
                    }
                    if (!File.Exists(manifestPath))
                    {
                        TryRestoreFile(manifestPath, originalManifest);
                    }
                }
                throw;
            }
            finally
            {
                foreach (KeyValuePair<string, string> pair in stagedFiles)
                {
                    TryDeleteFile(pair.Value);
                }
            }
        }

        /// <summary>XMZADD 20260909 在目标目录写入唯一临时文件，使最终替换不跨磁盘卷。</summary>
        private static string WriteTemporaryFile(string targetPath, byte[] content)
        {
            string temporaryPath = targetPath + ".promotion-" + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temporaryPath, content);
            return temporaryPath;
        }

        /// <summary>XMZADD 20260909 原子替换已存在的 latest 或 manifest，禁止删除后再写产生短暂空窗。</summary>
        private static void ReplaceExistingFile(string temporaryPath, string targetPath)
        {
            if (!File.Exists(targetPath))
            {
                throw new FileNotFoundException("规范基线文件在提交前消失。", targetPath);
            }
            File.Replace(temporaryPath, targetPath, null, true);
        }

        /// <summary>XMZADD 20260909 尽力恢复提交点之前的基线文件，恢复异常不得覆盖原始失败原因。</summary>
        private static void TryRestoreFile(string targetPath, byte[] content)
        {
            try
            {
                string temporaryPath = WriteTemporaryFile(targetPath, content);
                ReplaceExistingFile(temporaryPath, targetPath);
            }
            catch
            {
            }
        }

        /// <summary>XMZADD 20260909 尽力清除未被 manifest 引用的新文件，清理异常不得覆盖原始失败原因。</summary>
        private static void TryDeleteFile(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }

        /// <summary>XMZADD 20260909 使用稳定 DataContract JSON 生成公开审计文档和 manifest。</summary>
        private static byte[] SerializeDocument<T>(T value)
        {
            var serializer = new DataContractJsonSerializer(typeof(T));
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, value);
                // 仓库现有 JSON 使用普通斜杠，统一格式可减少人工审阅时的无意义转义差异。
                string json = Encoding.UTF8.GetString(stream.ToArray()).Replace("\\/", "/");
                return Encoding.UTF8.GetBytes(json);
            }
        }

        /// <summary>XMZADD 20260909 严格反序列化仓库 manifest，格式损坏时不进入任何写入阶段。</summary>
        private static T DeserializeDocument<T>(byte[] content)
        {
            try
            {
                var serializer = new DataContractJsonSerializer(typeof(T));
                using (var stream = new MemoryStream(content, false))
                {
                    return (T)serializer.ReadObject(stream);
                }
            }
            catch (Exception exception)
            {
                if (exception is OutOfMemoryException || exception is StackOverflowException)
                {
                    throw;
                }
                throw new InvalidDataException("仓库 manifest 格式无效。", exception);
            }
        }

        /// <summary>XMZADD 20260909 验证并发保护使用完整十六进制 SHA-256，拒绝截断或非哈希文本。</summary>
        private static bool IsSha256(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length != 64)
            {
                return false;
            }
            for (int index = 0; index < value.Length; index++)
            {
                char character = value[index];
                if (!((character >= '0' && character <= '9') ||
                      (character >= 'a' && character <= 'f') ||
                      (character >= 'A' && character <= 'F')))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>XMZADD 20260909 聚合管理员提升审计需要的业务对象数量。</summary>
        private sealed class PromotionCounts
        {
            public int TableCount { get; set; }
            public int FieldCount { get; set; }
            public int EntityCount { get; set; }
            public int EnumItemCount { get; set; }
            public int RelationCount { get; set; }
        }

        /// <summary>XMZADD 20260909 映射公开 manifest 并保留最后真实事件路径。</summary>
        [DataContract]
        private sealed class PromotionManifestDocument
        {
            [DataMember(Order = 0)]
            public int FormatVersion { get; set; }

            [DataMember(Order = 1)]
            public DateTime GeneratedAtUtc { get; set; }

            [DataMember(Order = 2)]
            public string LastEventPath { get; set; }

            [DataMember(Order = 3)]
            public long Revision { get; set; }

            [DataMember(Order = 4)]
            public string SnapshotPath { get; set; }

            [DataMember(Order = 5)]
            public string SnapshotSha256 { get; set; }
        }

        /// <summary>XMZADD 20260909 表示不伪造字段变更的管理员空事件修订。</summary>
        [DataContract]
        private sealed class PromotionRevisionDocument
        {
            [DataMember(Order = 0)]
            public IList<string> EventPaths { get; set; }

            [DataMember(Order = 1)]
            public int FormatVersion { get; set; }

            [DataMember(Order = 2)]
            public DateTime GeneratedAtUtc { get; set; }

            [DataMember(Order = 3)]
            public long Revision { get; set; }
        }

        /// <summary>XMZADD 20260909 记录不含本机路径和凭据的全量提升数量审计。</summary>
        [DataContract]
        private sealed class PromotionAuditDocument
        {
            [DataMember(Order = 0)]
            public long BaseRevision { get; set; }

            [DataMember(Order = 1)]
            public string BaseSnapshotSha256 { get; set; }

            [DataMember(Order = 2)]
            public int EntityCount { get; set; }

            [DataMember(Order = 3)]
            public int EnumItemCount { get; set; }

            [DataMember(Order = 4)]
            public int FieldCount { get; set; }

            [DataMember(Order = 5)]
            public int FormatVersion { get; set; }

            [DataMember(Order = 6)]
            public DateTime GeneratedAtUtc { get; set; }

            [DataMember(Order = 7)]
            public string NewSnapshotSha256 { get; set; }

            [DataMember(Order = 8)]
            public int RelationCount { get; set; }

            [DataMember(Order = 9)]
            public long Revision { get; set; }

            [DataMember(Order = 10)]
            public int TableCount { get; set; }
        }
    }
}
