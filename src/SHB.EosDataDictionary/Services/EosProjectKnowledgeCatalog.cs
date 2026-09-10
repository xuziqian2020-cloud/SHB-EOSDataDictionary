using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SHB.EosDataDictionary.Services
{
    /// <summary>XMZADD 20260903 读取 EOS 项目现有表索引和核心字段字典，为第一版业务字典提供可追溯证据。</summary>
    public sealed class EosProjectKnowledgeCatalog
    {
        private const long MaximumFileBytes = 16L * 1024L * 1024L;
        private readonly string _rootPath;
        private readonly Dictionary<string, EosKnowledgeTableEntry> _tables =
            new Dictionary<string, EosKnowledgeTableEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<EosKnowledgeFieldEntry>> _fields =
            new Dictionary<string, List<EosKnowledgeFieldEntry>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<EosKnowledgeEnumItem>> _enumItems =
            new Dictionary<string, List<EosKnowledgeEnumItem>>(StringComparer.OrdinalIgnoreCase);
        private bool _loaded;

        /// <summary>XMZADD 20260903 绑定 EOS 知识库根目录，首次查询时再加载固定索引文件。</summary>
        public EosProjectKnowledgeCatalog(string rootPath)
        {
            _rootPath = rootPath;
        }

        public bool IsAvailable { get; private set; }

        /// <summary>XMZADD 20260903 按不带架构的物理表名返回知识库中的业务名称和模块。</summary>
        public EosKnowledgeTableEntry FindTable(string tableName)
        {
            EnsureLoaded();
            EosKnowledgeTableEntry entry;
            _tables.TryGetValue(NormalizeObjectName(tableName), out entry);
            return entry;
        }

        /// <summary>XMZADD 20260903 按物理表和字段返回适用于该表的核心字段及枚举证据。</summary>
        public EosKnowledgeFieldEntry FindField(string tableName, string fieldName)
        {
            EnsureLoaded();
            List<EosKnowledgeFieldEntry> candidates;
            if (!_fields.TryGetValue((fieldName ?? string.Empty).Trim(), out candidates))
            {
                return null;
            }

            string normalizedTable = NormalizeObjectName(tableName);
            // 当前表的明确条目优先于任何通用说明，避免通用名称覆盖表内精确业务语义。
            for (int index = 0; index < candidates.Count; index++)
            {
                EosKnowledgeFieldEntry candidate = candidates[index];
                if (MentionsTable(candidate.AppliesToTables, normalizedTable))
                {
                    return candidate;
                }
            }

            EosKnowledgeFieldEntry sharedCandidate = null;
            for (int index = 0; index < candidates.Count; index++)
            {
                EosKnowledgeFieldEntry candidate = candidates[index];
                if (!IsSharedField(candidate.AppliesToTables))
                {
                    continue;
                }
                if (sharedCandidate == null)
                {
                    sharedCandidate = candidate;
                    continue;
                }
                // 同名通用字段出现不同中文解释时没有足够上下文消歧，必须保留给后续业务证据复核。
                if (!string.Equals(sharedCandidate.ChineseName, candidate.ChineseName, StringComparison.Ordinal))
                {
                    return null;
                }
            }
            return sharedCandidate;
        }

        /// <summary>XMZADD 20260903 延迟加载两个固定知识库文件，缺失时保持可降级而不影响快照读取。</summary>
        private void EnsureLoaded()
        {
            if (_loaded)
            {
                return;
            }
            _loaded = true;
            if (string.IsNullOrWhiteSpace(_rootPath) || !Directory.Exists(_rootPath))
            {
                return;
            }

            string tablePath = Path.Combine(_rootPath, "01_数据库表结构", "_00_表索引.md");
            string fieldPath = Path.Combine(_rootPath, "02_字段字典", "_00_核心字段字典.md");
            if (File.Exists(tablePath))
            {
                ParseTableIndex(tablePath);
            }
            if (File.Exists(fieldPath))
            {
                ParseFieldDictionary(fieldPath);
                AttachEnumItems();
            }
            IsAvailable = _tables.Count > 0 || _fields.Count > 0;
        }

        /// <summary>XMZADD 20260903 按二级业务标题解析表名、中文含义和备注，并保留知识库行号。</summary>
        private void ParseTableIndex(string path)
        {
            string[] lines = ReadBoundedLines(path);
            string moduleName = "其他";
            for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                string trimmed = (lines[lineIndex] ?? string.Empty).Trim();
                if (trimmed.StartsWith("## ", StringComparison.Ordinal))
                {
                    moduleName = TranslateModule(trimmed.Substring(3));
                    continue;
                }
                string[] cells = SplitMarkdownRow(trimmed);
                if (cells == null || cells.Length < 5 || IsSeparator(cells[1]) ||
                    string.Equals(cells[1].Trim(), "表名", StringComparison.Ordinal))
                {
                    continue;
                }

                string tableName = NormalizeObjectName(cells[1]);
                string chineseName = CleanCell(cells[2]);
                if (string.IsNullOrWhiteSpace(tableName) || string.IsNullOrWhiteSpace(chineseName))
                {
                    continue;
                }
                EosKnowledgeTableEntry existing;
                if (_tables.TryGetValue(tableName, out existing) && !IsPendingName(existing.ChineseName))
                {
                    continue;
                }
                _tables[tableName] = new EosKnowledgeTableEntry
                {
                    TableName = tableName,
                    ChineseName = chineseName,
                    ModuleName = moduleName,
                    Remark = CleanCell(cells[3]),
                    RelativePath = MakeRelativePath(path),
                    LineNumber = lineIndex + 1
                };
            }
        }

        /// <summary>XMZADD 20260903 解析核心字段表及常用枚举表，使字段翻译和枚举共享同一知识来源。</summary>
        private void ParseFieldDictionary(string path)
        {
            string[] lines = ReadBoundedLines(path);
            bool inEnumSection = false;
            for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                string trimmed = (lines[lineIndex] ?? string.Empty).Trim();
                if (trimmed.StartsWith("## ", StringComparison.Ordinal))
                {
                    inEnumSection = trimmed.IndexOf("枚举", StringComparison.Ordinal) >= 0 ||
                                    trimmed.IndexOf("常量", StringComparison.Ordinal) >= 0;
                    continue;
                }
                string[] cells = SplitMarkdownRow(trimmed);
                if (cells == null || cells.Length < 5 || IsSeparator(cells[1]))
                {
                    continue;
                }
                if (inEnumSection)
                {
                    if (string.Equals(cells[1].Trim(), "字段", StringComparison.Ordinal))
                    {
                        continue;
                    }
                    AddEnumItem(CleanCell(cells[1]), CleanCell(cells[2]), CleanCell(cells[3]),
                        path, lineIndex + 1);
                    continue;
                }
                if (string.Equals(cells[1].Trim(), "字段名", StringComparison.Ordinal))
                {
                    continue;
                }
                AddFieldAliases(CleanCell(cells[1]), CleanFieldChineseName(cells[2]), CleanCell(cells[3]),
                    path, lineIndex + 1);
            }
        }

        /// <summary>XMZADD 20260903 将斜杠分隔的同义物理字段分别建索引，避免组合名称无法精确命中。</summary>
        private void AddFieldAliases(string fieldNames, string chineseName, string appliesToTables,
            string path, int lineNumber)
        {
            string[] aliases = (fieldNames ?? string.Empty).Split('/');
            for (int aliasIndex = 0; aliasIndex < aliases.Length; aliasIndex++)
            {
                string fieldName = aliases[aliasIndex].Trim().Trim('`');
                if (fieldName.Length == 0 || chineseName.Length == 0)
                {
                    continue;
                }
                List<EosKnowledgeFieldEntry> entries;
                if (!_fields.TryGetValue(fieldName, out entries))
                {
                    entries = new List<EosKnowledgeFieldEntry>();
                    _fields.Add(fieldName, entries);
                }
                entries.Add(new EosKnowledgeFieldEntry
                {
                    FieldName = fieldName,
                    ChineseName = chineseName,
                    AppliesToTables = appliesToTables,
                    RelativePath = MakeRelativePath(path),
                    LineNumber = lineNumber,
                    EnumItems = new List<EosKnowledgeEnumItem>()
                });
            }
        }

        /// <summary>XMZADD 20260903 累计同一业务字段的常量值，并保留每个值的知识库位置。</summary>
        private void AddEnumItem(string fieldName, string value, string chineseName, string path, int lineNumber)
        {
            if (fieldName.Length == 0 || value.Length == 0 || chineseName.Length == 0)
            {
                return;
            }
            List<EosKnowledgeEnumItem> items;
            if (!_enumItems.TryGetValue(fieldName, out items))
            {
                items = new List<EosKnowledgeEnumItem>();
                _enumItems.Add(fieldName, items);
            }
            items.Add(new EosKnowledgeEnumItem
            {
                Value = value,
                ChineseName = chineseName,
                RelativePath = MakeRelativePath(path),
                LineNumber = lineNumber
            });
        }

        /// <summary>XMZADD 20260903 把全局常量说明附着到同名字段条目，供表字段富化时一次读取。</summary>
        private void AttachEnumItems()
        {
            foreach (KeyValuePair<string, List<EosKnowledgeEnumItem>> pair in _enumItems)
            {
                List<EosKnowledgeFieldEntry> fields;
                if (!_fields.TryGetValue(pair.Key, out fields))
                {
                    fields = new List<EosKnowledgeFieldEntry>();
                    fields.Add(new EosKnowledgeFieldEntry
                    {
                        FieldName = pair.Key,
                        ChineseName = pair.Key,
                        AppliesToTables = "通用",
                        EnumItems = new List<EosKnowledgeEnumItem>()
                    });
                    _fields.Add(pair.Key, fields);
                }
                for (int fieldIndex = 0; fieldIndex < fields.Count; fieldIndex++)
                {
                    fields[fieldIndex].EnumItems = new List<EosKnowledgeEnumItem>(pair.Value);
                }
            }
        }

        /// <summary>XMZADD 20260903 受限读取知识库文件，避免异常 Markdown 消耗过量内存。</summary>
        private static string[] ReadBoundedLines(string path)
        {
            var fileInfo = new FileInfo(path);
            if (fileInfo.Length > MaximumFileBytes)
            {
                throw new InvalidDataException("EOS 知识库文件超过 16MB 安全上限。");
            }
            string content;
            using (var reader = new StreamReader(path, Encoding.UTF8, true))
            {
                content = reader.ReadToEnd();
            }
            return content.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        }

        /// <summary>XMZADD 20260903 将知识库章节名称归并为数据字典受控业务模块。</summary>
        private static string TranslateModule(string heading)
        {
            string value = (heading ?? string.Empty).Trim();
            if (value.IndexOf("仓库", StringComparison.Ordinal) >= 0 || value.IndexOf("库存", StringComparison.Ordinal) >= 0) return "仓库与库存";
            if (value.IndexOf("物料", StringComparison.Ordinal) >= 0 || value.IndexOf("BOM", StringComparison.OrdinalIgnoreCase) >= 0) return "物料与BOM";
            if (value.IndexOf("采购", StringComparison.Ordinal) >= 0) return "采购管理";
            if (value.IndexOf("计划", StringComparison.Ordinal) >= 0) return "计划管理";
            if (value.IndexOf("生产", StringComparison.Ordinal) >= 0) return "生产制造";
            if (value.IndexOf("销售", StringComparison.Ordinal) >= 0 || value.IndexOf("客户", StringComparison.Ordinal) >= 0) return "销售与客户";
            if (value.IndexOf("质量", StringComparison.Ordinal) >= 0) return "质量管理";
            if (value.IndexOf("财务", StringComparison.Ordinal) >= 0 || value.IndexOf("应付", StringComparison.Ordinal) >= 0) return "财务管理";
            if (value.IndexOf("人事", StringComparison.Ordinal) >= 0 || value.IndexOf("HR", StringComparison.OrdinalIgnoreCase) >= 0) return "人力资源";
            if (value.IndexOf("设备", StringComparison.Ordinal) >= 0 || value.IndexOf("工装", StringComparison.Ordinal) >= 0) return "设备与工装";
            if (value.IndexOf("文件", StringComparison.Ordinal) >= 0 || value.IndexOf("图纸", StringComparison.Ordinal) >= 0) return "文件与图纸";
            if (value.IndexOf("日志", StringComparison.Ordinal) >= 0 || value.IndexOf("审计", StringComparison.Ordinal) >= 0) return "日志与审计";
            if (value.IndexOf("系统", StringComparison.Ordinal) >= 0 || value.IndexOf("配置", StringComparison.Ordinal) >= 0) return "系统配置";
            return "其他";
        }

        /// <summary>XMZADD 20260903 判断字段适用范围是否明确包含当前物理表。</summary>
        private static bool MentionsTable(string appliesToTables, string tableName)
        {
            if (string.IsNullOrWhiteSpace(tableName))
            {
                return false;
            }
            string source = appliesToTables ?? string.Empty;
            int searchStart = 0;
            while (searchStart < source.Length)
            {
                int match = source.IndexOf(tableName, searchStart, StringComparison.OrdinalIgnoreCase);
                if (match < 0)
                {
                    return false;
                }
                int after = match + tableName.Length;
                bool validBefore = match == 0 || !IsIdentifierCharacter(source[match - 1]);
                bool validAfter = after >= source.Length || !IsIdentifierCharacter(source[after]);
                if (validBefore && validAfter)
                {
                    return true;
                }
                searchStart = match + 1;
            }
            return false;
        }

        /// <summary>XMZADD 20260903 识别物理表标识符字符，防止 Item 错配到 Item_Version 等相近表名。</summary>
        private static bool IsIdentifierCharacter(char value)
        {
            return char.IsLetterOrDigit(value) || value == '_';
        }

        /// <summary>XMZADD 20260903 识别知识库声明为通用或跨多表的共享字段。</summary>
        private static bool IsSharedField(string appliesToTables)
        {
            string value = appliesToTables ?? string.Empty;
            return value.IndexOf("通用", StringComparison.Ordinal) >= 0 ||
                   value.IndexOf("多表", StringComparison.Ordinal) >= 0 ||
                   value.IndexOf("几乎所有", StringComparison.Ordinal) >= 0;
        }

        /// <summary>XMZADD 20260903 去掉字段中文名中的枚举值说明，避免把取值范围显示成字段名称。</summary>
        private static string CleanFieldChineseName(string value)
        {
            string result = CleanCell(value);
            int bracket = result.IndexOf('（');
            if (bracket > 0 && result.IndexOf('=', bracket) >= 0)
            {
                result = result.Substring(0, bracket).Trim();
            }
            return result;
        }

        /// <summary>XMZADD 20260903 判断知识库中文名是否仍标记为待确认。</summary>
        private static bool IsPendingName(string value)
        {
            return (value ?? string.Empty).IndexOf("待确认", StringComparison.Ordinal) >= 0;
        }

        /// <summary>XMZADD 20260903 解析标准 Markdown 表格行并保留空列位置。</summary>
        private static string[] SplitMarkdownRow(string line)
        {
            string value = (line ?? string.Empty).Trim();
            return value.StartsWith("|", StringComparison.Ordinal) && value.EndsWith("|", StringComparison.Ordinal)
                ? value.Split('|')
                : null;
        }

        /// <summary>XMZADD 20260903 排除 Markdown 分隔行。</summary>
        private static bool IsSeparator(string value)
        {
            string text = CleanCell(value).Trim(':');
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

        /// <summary>XMZADD 20260903 清理 Markdown 单元格的空白和代码标记。</summary>
        private static string CleanCell(string value)
        {
            return (value ?? string.Empty).Trim().Trim('`').Trim();
        }

        /// <summary>XMZADD 20260903 去除架构和括号，形成知识库统一物理表键。</summary>
        private static string NormalizeObjectName(string value)
        {
            string result = CleanCell(value).Trim('[', ']');
            int dot = result.LastIndexOf('.');
            if (dot >= 0 && dot < result.Length - 1)
            {
                result = result.Substring(dot + 1).Trim('[', ']');
            }
            return result;
        }

        /// <summary>XMZADD 20260903 将证据路径限制为知识库根目录下的相对路径。</summary>
        private string MakeRelativePath(string path)
        {
            string root = Path.GetFullPath(_rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string fullPath = Path.GetFullPath(path);
            return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? fullPath.Substring(root.Length).Replace('\\', '/')
                : Path.GetFileName(path);
        }
    }

    /// <summary>XMZADD 20260903 保存 EOS 表索引中的精确业务名称、模块和来源位置。</summary>
    public sealed class EosKnowledgeTableEntry
    {
        public string TableName { get; set; }
        public string ChineseName { get; set; }
        public string ModuleName { get; set; }
        public string Remark { get; set; }
        public string RelativePath { get; set; }
        public int LineNumber { get; set; }
    }

    /// <summary>XMZADD 20260903 保存 EOS 核心字段字典中的名称、适用表和枚举集合。</summary>
    public sealed class EosKnowledgeFieldEntry
    {
        public string FieldName { get; set; }
        public string ChineseName { get; set; }
        public string AppliesToTables { get; set; }
        public string RelativePath { get; set; }
        public int LineNumber { get; set; }
        public IList<EosKnowledgeEnumItem> EnumItems { get; set; }
    }

    /// <summary>XMZADD 20260903 保存知识库明确列出的字段业务枚举值。</summary>
    public sealed class EosKnowledgeEnumItem
    {
        public string Value { get; set; }
        public string ChineseName { get; set; }
        public string RelativePath { get; set; }
        public int LineNumber { get; set; }
    }
}
