using System;
using System.IO;
using System.Security.Cryptography;
using SHB.EosDataDictionary.Models;

namespace SHB.EosDataDictionary.Services
{
    /// <summary>XMZADD 20260907 在独立 SQLite 副本上执行 V4 EOS 业务字典富化并验证源库不变。</summary>
    public sealed class OfflineDictionaryV1Generator
    {
        /// <summary>XMZADD 20260903 复制源字典、富化指定作用域、验证结构不变量并写出审计报告。</summary>
        public OfflineDictionaryV1GenerationResult Generate(OfflineDictionaryV1GenerationOptions options)
        {
            ValidateOptions(options);
            string sourcePath = Path.GetFullPath(options.SourceDatabasePath);
            string outputPath = Path.GetFullPath(options.OutputDatabasePath);
            string sourceHashBefore = ComputeSha256(sourcePath);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
            File.Copy(sourcePath, outputPath, true);

            var outputStore = new SnapshotStore(outputPath);
            SnapshotData snapshot;
            if (!outputStore.TryLoadLatest(options.ScopeKey, out snapshot))
            {
                throw new InvalidDataException("输出数据库中不存在指定快照作用域：" + options.ScopeKey);
            }
            int originalTableCount = CountTables(snapshot);
            int originalFieldCount = CountFields(snapshot);
            var sourceAnalyzer = new EosSourceAnalyzer();
            var sourceEvidence = sourceAnalyzer.Analyze(options.SourceRoot);
            var knowledgeCatalog = new EosProjectKnowledgeCatalog(options.KnowledgeBaseRoot);
            new BusinessDictionaryV1EnrichmentService().Enrich(snapshot, sourceEvidence, knowledgeCatalog);
            snapshot.RefreshedAt = DateTime.UtcNow;
            outputStore.ReplaceScope(options.ScopeKey, snapshot);

            SnapshotData verificationSnapshot;
            if (!outputStore.TryLoadLatest(options.ScopeKey, out verificationSnapshot) ||
                CountTables(verificationSnapshot) != originalTableCount ||
                CountFields(verificationSnapshot) != originalFieldCount)
            {
                throw new InvalidDataException("V4 输出快照未通过表数和字段数不变量校验。");
            }
            var outputLocalStore = new LocalDictionaryStore(outputPath);
            DictionarySyncState syncState = outputLocalStore.LoadSyncState(options.ScopeKey);
            if (syncState != null)
            {
                int synchronizedTableCount;
                string enrichedPayloadHash;
                if (!outputStore.TryGetScopeSummary(options.ScopeKey, out synchronizedTableCount, out enrichedPayloadHash) ||
                    synchronizedTableCount != originalTableCount || string.IsNullOrWhiteSpace(enrichedPayloadHash))
                {
                    throw new InvalidDataException("V4 输出快照未能生成有效的本地同步哈希。");
                }

                // 离线富化保留远端修订游标，但必须登记新的本地载荷，避免启动同步把补全结果误判为损坏并覆盖。
                syncState.LocalPayloadHash = enrichedPayloadHash;
                outputLocalStore.SaveSyncState(syncState);
            }
            string sourceHashAfter = ComputeSha256(sourcePath);
            if (!string.Equals(sourceHashBefore, sourceHashAfter, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("源 dictionary.db 在离线生成期间发生变化，已拒绝输出结论。");
            }

            var reportService = new BusinessDictionaryV1ReportService();
            BusinessDictionaryV1Report report = reportService.Create(verificationSnapshot, sourceEvidence);
            reportService.Write(options.ReportRoot, verificationSnapshot, report, sourceEvidence);
            return new OfflineDictionaryV1GenerationResult
            {
                SourceSha256 = sourceHashAfter,
                OutputSha256 = ComputeSha256(outputPath),
                SourceEvidenceCount = sourceEvidence.Count,
                Report = report
            };
        }

        /// <summary>XMZADD 20260903 校验离线生成路径边界，禁止把源数据库本身当作输出目标。</summary>
        private static void ValidateOptions(OfflineDictionaryV1GenerationOptions options)
        {
            if (options == null)
            {
                throw new ArgumentNullException("options");
            }
            if (string.IsNullOrWhiteSpace(options.SourceDatabasePath) || !File.Exists(options.SourceDatabasePath))
            {
                throw new FileNotFoundException("源 dictionary.db 不存在。", options.SourceDatabasePath);
            }
            if (string.IsNullOrWhiteSpace(options.OutputDatabasePath) ||
                string.Equals(Path.GetFullPath(options.SourceDatabasePath), Path.GetFullPath(options.OutputDatabasePath), StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("输出数据库必须是不同于源库的独立文件。", "OutputDatabasePath");
            }
            if (string.IsNullOrWhiteSpace(options.ScopeKey)) throw new ArgumentException("快照作用域不能为空。", "ScopeKey");
            if (string.IsNullOrWhiteSpace(options.SourceRoot) || !Directory.Exists(options.SourceRoot)) throw new DirectoryNotFoundException("EOS 源码目录不存在。");
            if (string.IsNullOrWhiteSpace(options.KnowledgeBaseRoot) || !Directory.Exists(options.KnowledgeBaseRoot)) throw new DirectoryNotFoundException("EOS 知识库目录不存在。");
            if (string.IsNullOrWhiteSpace(options.ReportRoot)) throw new ArgumentException("报告目录不能为空。", "ReportRoot");
        }

        /// <summary>XMZADD 20260903 统计快照中的有效表对象数量。</summary>
        private static int CountTables(SnapshotData snapshot)
        {
            if (snapshot == null || snapshot.Tables == null)
            {
                return 0;
            }
            int count = 0;
            for (int index = 0; index < snapshot.Tables.Count; index++)
            {
                if (snapshot.Tables[index] != null) count++;
            }
            return count;
        }

        /// <summary>XMZADD 20260903 统计快照中的有效字段对象数量，用于防止富化过程丢失物理结构。</summary>
        private static int CountFields(SnapshotData snapshot)
        {
            if (snapshot == null || snapshot.Tables == null)
            {
                return 0;
            }
            int count = 0;
            for (int tableIndex = 0; tableIndex < snapshot.Tables.Count; tableIndex++)
            {
                TableMetadata table = snapshot.Tables[tableIndex];
                if (table == null || table.Fields == null)
                {
                    continue;
                }
                for (int fieldIndex = 0; fieldIndex < table.Fields.Count; fieldIndex++)
                {
                    if (table.Fields[fieldIndex] != null) count++;
                }
            }
            return count;
        }

        /// <summary>XMZADD 20260903 计算文件 SHA-256，证明源库不变并标识输出版本。</summary>
        private static string ComputeSha256(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (SHA256 algorithm = SHA256.Create())
            {
                return BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", string.Empty);
            }
        }
    }

    /// <summary>XMZADD 20260907 保存离线 V4 生成器的输入路径和目标快照作用域。</summary>
    public sealed class OfflineDictionaryV1GenerationOptions
    {
        public string SourceDatabasePath { get; set; }
        public string OutputDatabasePath { get; set; }
        public string ScopeKey { get; set; }
        public string SourceRoot { get; set; }
        public string KnowledgeBaseRoot { get; set; }
        public string ReportRoot { get; set; }
    }

    /// <summary>XMZADD 20260907 返回 V4 输出哈希、源码证据量和覆盖率统计。</summary>
    public sealed class OfflineDictionaryV1GenerationResult
    {
        public string SourceSha256 { get; set; }
        public string OutputSha256 { get; set; }
        public int SourceEvidenceCount { get; set; }
        public BusinessDictionaryV1Report Report { get; set; }
    }
}
