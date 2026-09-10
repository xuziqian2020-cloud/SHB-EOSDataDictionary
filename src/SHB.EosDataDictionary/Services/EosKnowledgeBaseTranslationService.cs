using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using SHB.EosDataDictionary.Models;

namespace SHB.EosDataDictionary.Services
{
    /// <summary>XMZADD 20260901 从 EOS 项目知识库的全量索引和字段章节提取可核验的精确中文名称。</summary>
    public sealed class EosKnowledgeBaseTranslationService
    {
        private const int MaximumContextCount = 8;
        private const int MaximumEvidenceCount = 5;
        private const int MaximumSummaryLength = 240;
        private const long MaximumKnowledgeFileBytes = 16L * 1024L * 1024L;
        private const long MaximumTotalKnowledgeBytes = 128L * 1024L * 1024L;
        private const int MaximumIndexEntryCount = 100000;
        private const int MaximumModuleCount = 512;
        private const int MaximumFieldsPerModule = 100000;
        private const int MaximumTotalKnowledgeFields = 500000;
        private static readonly Regex DetailLinkRegex = new Regex(@"\[[^\]]+\]\(\./(?<path>[^#\)]+)#(?<anchor>[^\)]+)\)", RegexOptions.Compiled);
        private static readonly Regex AnchorRegex = new Regex("^<a\\s+id=\"(?<anchor>[^\"]+)\"></a>$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private readonly string _rootPath;
        private readonly Dictionary<string, List<KnowledgeIndexEntry>> _tableIndex = new Dictionary<string, List<KnowledgeIndexEntry>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IList<KnowledgeFieldEntry>> _sectionCache = new Dictionary<string, IList<KnowledgeFieldEntry>>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _parsedModulePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool _indexLoaded;
        private bool _knowledgeFailure;
        private long _totalKnowledgeBytes;
        private int _totalKnowledgeFieldCount;

        /// <summary>XMZADD 20260901 指示全量索引是否已完整解析出至少一个可核验业务对象。</summary>
        public bool IsAvailable { get; private set; }

        /// <summary>XMZADD 20260901 绑定结构发布者本机的 EOS 知识库目录，目录不可用时保持可降级。</summary>
        public EosKnowledgeBaseTranslationService(string rootPath)
        {
            _rootPath = rootPath;
        }

        /// <summary>XMZADD 20260901 按物理表和数据库字段精确查找唯一标题，冲突时仅返回受限 AI 上下文。</summary>
        public KnowledgeBaseTranslationResult TranslateField(string schemaName, string tableName, string fieldName, string confirmedModule)
        {
            return TranslateField(schemaName, tableName, fieldName, confirmedModule, CancellationToken.None);
        }

        /// <summary>XMZADD 20260901 按物理表字段读取受限知识库，并在结构扫描取消时立即停止后续解析。</summary>
        public KnowledgeBaseTranslationResult TranslateField(string schemaName, string tableName, string fieldName,
            string confirmedModule, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = new KnowledgeBaseTranslationResult();
            if (_knowledgeFailure)
            {
                return result;
            }
            try
            {
                EnsureIndexLoaded(cancellationToken);
                string normalizedTable = NormalizeObjectName(tableName);
                List<KnowledgeIndexEntry> tableEntries;
                if (string.IsNullOrWhiteSpace(normalizedTable) || string.IsNullOrWhiteSpace(fieldName) ||
                    !_tableIndex.TryGetValue(normalizedTable, out tableEntries))
                {
                    return result;
                }

                var exactMatches = new List<KnowledgeFieldMatch>();
                var fuzzyMatches = new List<KnowledgeFieldMatch>();
                for (int entryIndex = 0; entryIndex < tableEntries.Count; entryIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    KnowledgeIndexEntry indexEntry = tableEntries[entryIndex];
                    IList<KnowledgeFieldEntry> fields = LoadSectionFields(indexEntry, cancellationToken);
                    if (_knowledgeFailure)
                    {
                        // 任一关联模块损坏会使当前知识事务整体失效，禁止返回此前模块留下的局部命中。
                        return new KnowledgeBaseTranslationResult();
                    }
                    for (int fieldIndex = 0; fieldIndex < fields.Count; fieldIndex++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        KnowledgeFieldEntry field = fields[fieldIndex];
                        var match = new KnowledgeFieldMatch(indexEntry, field);
                        if (string.Equals(field.DatabaseField, fieldName.Trim(), StringComparison.OrdinalIgnoreCase))
                        {
                            exactMatches.Add(match);
                        }
                        else if (IsFuzzyFieldMatch(field.DatabaseField, fieldName))
                        {
                            fuzzyMatches.Add(match);
                        }
                    }
                }

                if (_knowledgeFailure)
                {
                    return new KnowledgeBaseTranslationResult();
                }

                IList<KnowledgeFieldMatch> selectedMatches = exactMatches;
                int distinctTitleCount = CountDistinctTitles(exactMatches);
                if (distinctTitleCount > 1 && !string.IsNullOrWhiteSpace(confirmedModule))
                {
                    List<KnowledgeFieldMatch> moduleMatches = FilterByConfirmedModule(exactMatches, confirmedModule);
                    if (moduleMatches.Count > 0)
                    {
                        selectedMatches = moduleMatches;
                        distinctTitleCount = CountDistinctTitles(moduleMatches);
                    }
                }

                if (selectedMatches.Count > 0 && distinctTitleCount == 1)
                {
                    if (IdentifierTranslationService.IsReliableChineseName(selectedMatches[0].Field.Title))
                    {
                        result.Candidate = CreateCandidate(selectedMatches);
                    }
                    else
                    {
                        AppendContexts(result.AiContext, selectedMatches);
                    }
                    return result;
                }

                if (exactMatches.Count > 0)
                {
                    AppendContexts(result.AiContext, exactMatches);
                }
                else
                {
                    AppendContexts(result.AiContext, fuzzyMatches);
                }
            }
            catch (OperationCanceledException)
            {
                ResetForRetry();
                throw;
            }
            catch (InvalidDataException)
            {
                MarkUnavailable();
                return new KnowledgeBaseTranslationResult();
            }
            catch (IOException)
            {
                MarkUnavailable();
                return new KnowledgeBaseTranslationResult();
            }
            catch (UnauthorizedAccessException)
            {
                MarkUnavailable();
                return new KnowledgeBaseTranslationResult();
            }
            catch (ArgumentException)
            {
                MarkUnavailable();
                return new KnowledgeBaseTranslationResult();
            }
            return result;
        }

        /// <summary>XMZADD 20260901 在读取 Markdown 前执行文件大小和取消校验，防止异常知识库占满内存。</summary>
        private string[] ReadBoundedLines(string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileInfo = new FileInfo(path);
            if (fileInfo.Length > MaximumKnowledgeFileBytes)
            {
                throw new InvalidDataException("知识库文件超过 16MB 安全上限。");
            }

            string content;
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var output = new MemoryStream())
            {
                var buffer = new byte[8192];
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int bytesRead = input.Read(buffer, 0, buffer.Length);
                    if (bytesRead == 0)
                    {
                        break;
                    }
                    if (output.Length + bytesRead > MaximumKnowledgeFileBytes)
                    {
                        throw new InvalidDataException("知识库文件在读取期间超过 16MB 安全上限。");
                    }
                    if (_totalKnowledgeBytes + bytesRead > MaximumTotalKnowledgeBytes)
                    {
                        throw new InvalidDataException("知识库本次读取总量超过 128MB 安全上限。");
                    }
                    _totalKnowledgeBytes += bytesRead;
                    output.Write(buffer, 0, bytesRead);
                }
                output.Position = 0L;
                using (var reader = new StreamReader(output, Encoding.UTF8, true))
                {
                    content = reader.ReadToEnd();
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            return content.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        }

        /// <summary>XMZADD 20260901 一次读取全量索引并按物理表建索引，避免字段循环重复扫描大型 Markdown。</summary>
        private void EnsureIndexLoaded(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_indexLoaded)
            {
                return;
            }
            if (string.IsNullOrWhiteSpace(_rootPath) || !Directory.Exists(_rootPath))
            {
                _indexLoaded = true;
                return;
            }
            string indexPath = Path.Combine(_rootPath, "全量表索引.md");
            if (!File.Exists(indexPath))
            {
                _indexLoaded = true;
                return;
            }

            string[] lines = ReadBoundedLines(indexPath, cancellationToken);
            int entryCount = 0;
            for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string[] cells = SplitMarkdownRow(lines[lineIndex]);
                if (cells == null || cells.Length < 13)
                {
                    continue;
                }
                string tableName = RemoveMarkdownCode(cells[5]);
                Match link = DetailLinkRegex.Match(cells[12]);
                if (string.IsNullOrWhiteSpace(tableName) || !link.Success)
                {
                    continue;
                }
                string relativePath = NormalizeRelativePath(link.Groups["path"].Value);
                if (relativePath == null)
                {
                    continue;
                }
                var entry = new KnowledgeIndexEntry
                {
                    TopCategory = cells[1].Trim(),
                    Subsystem = cells[2].Trim(),
                    FormName = cells[3].Trim(),
                    TableName = tableName,
                    RelativePath = relativePath,
                    Anchor = link.Groups["anchor"].Value.Trim()
                };
                List<KnowledgeIndexEntry> entries;
                if (!_tableIndex.TryGetValue(tableName, out entries))
                {
                    entries = new List<KnowledgeIndexEntry>();
                    _tableIndex.Add(tableName, entries);
                }
                entries.Add(entry);
                entryCount++;
                if (entryCount > MaximumIndexEntryCount)
                {
                    throw new InvalidDataException("知识库索引条目超过安全上限。");
                }
            }
            IsAvailable = entryCount > 0 && !_knowledgeFailure;
            _indexLoaded = true;
        }

        /// <summary>XMZADD 20260901 首次命中模块时建立全部锚点索引，后续字段查询直接按章节键读取。</summary>
        private IList<KnowledgeFieldEntry> LoadSectionFields(KnowledgeIndexEntry entry, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string cacheKey = entry.RelativePath + "#" + entry.Anchor;
            IList<KnowledgeFieldEntry> cached;
            if (_sectionCache.TryGetValue(cacheKey, out cached))
            {
                return cached;
            }
            if (!ParseModuleSections(entry.RelativePath, cancellationToken))
            {
                MarkUnavailable();
                return new List<KnowledgeFieldEntry>();
            }
            if (_sectionCache.TryGetValue(cacheKey, out cached))
            {
                return cached;
            }
            var missingSection = new List<KnowledgeFieldEntry>();
            _sectionCache.Add(cacheKey, missingSection);
            MarkUnavailable();
            return missingSection;
        }

        /// <summary>XMZADD 20260901 单次遍历模块文档并同时建立全部锚点字段索引，避免每个实体重复扫描全文。</summary>
        private bool ParseModuleSections(string relativePath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_parsedModulePaths.Contains(relativePath))
            {
                return true;
            }
            string modulePath = GetSafeModulePath(relativePath);
            if (modulePath == null || !File.Exists(modulePath))
            {
                MarkUnavailable();
                return false;
            }
            if (_parsedModulePaths.Count >= MaximumModuleCount)
            {
                throw new InvalidDataException("知识库模块数量超过安全上限。");
            }
            string[] lines = ReadBoundedLines(modulePath, cancellationToken);
            string currentAnchor = null;
            string currentSection = null;
            var currentFields = new List<KnowledgeFieldEntry>();
            bool inFields = false;
            for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string trimmed = (lines[lineIndex] ?? string.Empty).Trim();
                Match anchor = AnchorRegex.Match(trimmed);
                if (anchor.Success)
                {
                    StoreSection(relativePath, currentAnchor, currentFields);
                    currentAnchor = anchor.Groups["anchor"].Value.Trim();
                    currentSection = currentAnchor;
                    currentFields = new List<KnowledgeFieldEntry>();
                    inFields = false;
                    continue;
                }
                if (string.IsNullOrWhiteSpace(currentAnchor))
                {
                    continue;
                }
                if (trimmed.StartsWith("## ", StringComparison.Ordinal))
                {
                    currentSection = trimmed.Substring(3).Trim();
                    inFields = false;
                    continue;
                }
                if (trimmed.StartsWith("### ", StringComparison.Ordinal))
                {
                    inFields = string.Equals(trimmed, "### 字段", StringComparison.Ordinal);
                    continue;
                }
                if (!inFields)
                {
                    continue;
                }
                string[] cells = SplitMarkdownRow(trimmed);
                if (cells == null || cells.Length < 5 || IsMarkdownSeparator(cells[1]) ||
                    string.Equals(cells[1].Trim(), "标题", StringComparison.Ordinal))
                {
                    continue;
                }
                string title = RemoveMarkdownCode(cells[1]);
                string databaseField = RemoveMarkdownCode(cells[4]);
                if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(databaseField))
                {
                    if (currentFields.Count >= MaximumFieldsPerModule)
                    {
                        throw new InvalidDataException("知识库模块字段数量超过安全上限。");
                    }
                    _totalKnowledgeFieldCount++;
                    if (_totalKnowledgeFieldCount > MaximumTotalKnowledgeFields)
                    {
                        throw new InvalidDataException("知识库字段总量超过安全上限。");
                    }
                    currentFields.Add(new KnowledgeFieldEntry
                    {
                        Title = title,
                        DatabaseField = databaseField,
                        Section = currentSection,
                        LineNumber = lineIndex + 1
                    });
                }
            }
            StoreSection(relativePath, currentAnchor, currentFields);
            if (string.IsNullOrWhiteSpace(currentAnchor))
            {
                MarkUnavailable();
                return false;
            }
            _parsedModulePaths.Add(relativePath);
            return true;
        }

        /// <summary>XMZADD 20260901 将知识库读取或结构损坏记录为显式降级，避免后续成功查询掩盖本次不完整状态。</summary>
        private void MarkUnavailable()
        {
            _knowledgeFailure = true;
            IsAvailable = false;
            _indexLoaded = true;
            _tableIndex.Clear();
            _sectionCache.Clear();
            _parsedModulePaths.Clear();
            _totalKnowledgeBytes = 0L;
            _totalKnowledgeFieldCount = 0;
        }

        /// <summary>XMZADD 20260901 在调用方取消时清除未完成索引和预算，使同一实例下次不会复用半截知识状态。</summary>
        private void ResetForRetry()
        {
            _knowledgeFailure = false;
            IsAvailable = false;
            _indexLoaded = false;
            _totalKnowledgeBytes = 0L;
            _totalKnowledgeFieldCount = 0;
            _tableIndex.Clear();
            _sectionCache.Clear();
            _parsedModulePaths.Clear();
        }

        /// <summary>XMZADD 20260901 保存一个完整锚点的字段集合，重复锚点稳定保留首次出现的章节。</summary>
        private void StoreSection(string relativePath, string anchor, IList<KnowledgeFieldEntry> fields)
        {
            if (string.IsNullOrWhiteSpace(anchor))
            {
                return;
            }
            string cacheKey = relativePath + "#" + anchor;
            if (!_sectionCache.ContainsKey(cacheKey))
            {
                _sectionCache.Add(cacheKey, fields ?? new List<KnowledgeFieldEntry>());
            }
        }

        /// <summary>XMZADD 20260901 将唯一标题转换为高可信自动候选，并限制可公开证据数量。</summary>
        private static MetadataValue CreateCandidate(IList<KnowledgeFieldMatch> matches)
        {
            var candidate = new MetadataValue
            {
                Value = matches[0].Field.Title,
                Status = ConfidenceStatus.KnowledgeBaseEvidence,
                SourceType = "EOS知识库",
                SourceSummary = "EOS 项目知识库精确表字段条目",
                Evidence = new List<EvidenceItem>()
            };
            for (int index = 0; index < matches.Count && candidate.Evidence.Count < MaximumEvidenceCount; index++)
            {
                KnowledgeFieldMatch match = matches[index];
                candidate.Evidence.Add(new EvidenceItem
                {
                    SourceType = "EOS知识库",
                    SourcePath = match.Index.RelativePath,
                    SourceLine = match.Field.LineNumber,
                    RuleName = "ExactTableField",
                    RawValue = match.Field.DatabaseField + "=" + match.Field.Title,
                    OriginalText = match.Field.DatabaseField + "=" + match.Field.Title,
                    Explanation = LimitText(match.Field.Section + "；" + match.Field.DatabaseField + "：" + match.Field.Title)
                });
            }
            return candidate;
        }

        /// <summary>XMZADD 20260901 将冲突或模糊知识条目压缩成少量上下文，供 AI 消歧而不直接改名。</summary>
        private static void AppendContexts(IList<string> context, IList<KnowledgeFieldMatch> matches)
        {
            for (int index = 0; index < matches.Count && context.Count < MaximumContextCount; index++)
            {
                KnowledgeFieldMatch match = matches[index];
                string value = match.Index.RelativePath + "#" + match.Index.Anchor + ":" + match.Field.LineNumber + " " +
                               match.Index.TableName + "." + match.Field.DatabaseField + "：" + match.Field.Title +
                               "（" + match.Index.TopCategory + "/" + match.Index.Subsystem + "/" + match.Index.FormName + "）";
                context.Add(LimitText(value));
            }
        }

        /// <summary>XMZADD 20260901 按结构发布者已确认模块筛选冲突业务对象，禁止根据文件名隐式猜测。</summary>
        private static List<KnowledgeFieldMatch> FilterByConfirmedModule(IList<KnowledgeFieldMatch> matches, string confirmedModule)
        {
            var result = new List<KnowledgeFieldMatch>();
            string module = confirmedModule.Trim();
            for (int index = 0; index < matches.Count; index++)
            {
                KnowledgeIndexEntry entry = matches[index].Index;
                if (string.Equals(entry.TopCategory, module, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(entry.Subsystem, module, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(entry.FormName, module, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(matches[index]);
                }
            }
            return result;
        }

        /// <summary>XMZADD 20260901 统计精确条目中的中文标题种类，只有唯一标题才允许覆盖自动名称。</summary>
        private static int CountDistinctTitles(IList<KnowledgeFieldMatch> matches)
        {
            var titles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < matches.Count; index++)
            {
                if (!string.IsNullOrWhiteSpace(matches[index].Field.Title))
                {
                    titles.Add(matches[index].Field.Title.Trim());
                }
            }
            return titles.Count;
        }

        /// <summary>XMZADD 20260901 识别去除字段 F 前缀后的近似命中，仅供 AI 上下文使用。</summary>
        private static bool IsFuzzyFieldMatch(string indexedField, string requestedField)
        {
            string left = StripFieldPrefix(indexedField);
            string right = StripFieldPrefix(requestedField);
            return left.Length > 0 && right.Length > 0 &&
                   (string.Equals(left, right, StringComparison.OrdinalIgnoreCase) ||
                    left.IndexOf(right, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    right.IndexOf(left, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        /// <summary>XMZADD 20260901 仅为模糊比较移除传统 F 前缀，不改变精确物理字段键。</summary>
        private static string StripFieldPrefix(string value)
        {
            string result = (value ?? string.Empty).Trim();
            return result.Length > 1 && (result[0] == 'F' || result[0] == 'f') ? result.Substring(1) : result;
        }

        /// <summary>XMZADD 20260901 解析固定列数的 Markdown 表行，避免依赖外部 Markdown 组件。</summary>
        private static string[] SplitMarkdownRow(string line)
        {
            string value = (line ?? string.Empty).Trim();
            if (!value.StartsWith("|", StringComparison.Ordinal) || !value.EndsWith("|", StringComparison.Ordinal))
            {
                return null;
            }
            return value.Split('|');
        }

        /// <summary>XMZADD 20260901 识别 Markdown 表头分隔行，避免把格式符号当作业务字段。</summary>
        private static bool IsMarkdownSeparator(string value)
        {
            string text = (value ?? string.Empty).Trim().Trim(':');
            if (text.Length < 3)
            {
                return false;
            }
            for (int index = 0; index < text.Length; index++)
            {
                if (text[index] != '-')
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>XMZADD 20260901 去除知识库表格中的反引号包装并保持业务文本原样。</summary>
        private static string RemoveMarkdownCode(string value)
        {
            return (value ?? string.Empty).Trim().Trim('`').Trim();
        }

        /// <summary>XMZADD 20260901 将带架构的对象键归一到知识库使用的物理表名。</summary>
        private static string NormalizeObjectName(string value)
        {
            string result = RemoveMarkdownCode(value).Trim('[', ']');
            int dot = result.LastIndexOf('.');
            if (dot >= 0 && dot < result.Length - 1)
            {
                result = result.Substring(dot + 1).Trim('[', ']');
            }
            return result;
        }

        /// <summary>XMZADD 20260901 验证索引中的模块链接为知识库内部相对路径，防止越界读取。</summary>
        private string GetSafeModulePath(string relativePath)
        {
            string root = Path.GetFullPath(_rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
            return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? fullPath : null;
        }

        /// <summary>XMZADD 20260901 清理模块链接格式，公开证据只保留相对 Markdown 路径。</summary>
        private static string NormalizeRelativePath(string value)
        {
            string relative = (value ?? string.Empty).Trim().Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            if (relative.Length == 0 || Path.IsPathRooted(relative) || relative.IndexOf("..", StringComparison.Ordinal) >= 0)
            {
                return null;
            }
            return relative.Replace(Path.DirectorySeparatorChar, '/');
        }

        /// <summary>XMZADD 20260901 截断可公开摘要，避免知识库长文本进入快照或 AI 请求。</summary>
        private static string LimitText(string value)
        {
            string text = (value ?? string.Empty).Trim();
            return text.Length <= MaximumSummaryLength ? text : text.Substring(0, MaximumSummaryLength);
        }

        private sealed class KnowledgeIndexEntry
        {
            public string TopCategory { get; set; }
            public string Subsystem { get; set; }
            public string FormName { get; set; }
            public string TableName { get; set; }
            public string RelativePath { get; set; }
            public string Anchor { get; set; }
        }

        private sealed class KnowledgeFieldEntry
        {
            public string Title { get; set; }
            public string DatabaseField { get; set; }
            public string Section { get; set; }
            public int LineNumber { get; set; }
        }

        private sealed class KnowledgeFieldMatch
        {
            public KnowledgeFieldMatch(KnowledgeIndexEntry index, KnowledgeFieldEntry field)
            {
                Index = index;
                Field = field;
            }

            public KnowledgeIndexEntry Index { get; private set; }
            public KnowledgeFieldEntry Field { get; private set; }
        }
    }

    /// <summary>XMZADD 20260901 保存知识库精确名称候选或供 AI 消歧的受限短上下文。</summary>
    public sealed class KnowledgeBaseTranslationResult
    {
        /// <summary>XMZADD 20260901 初始化空结果，使知识库不可用时无需异常分支即可继续翻译。</summary>
        public KnowledgeBaseTranslationResult()
        {
            AiContext = new List<string>();
        }

        public MetadataValue Candidate { get; set; }
        public IList<string> AiContext { get; private set; }
    }
}
