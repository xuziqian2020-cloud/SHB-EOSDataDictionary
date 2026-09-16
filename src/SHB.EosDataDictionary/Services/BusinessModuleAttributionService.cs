using System;
using System.Collections.Generic;
using System.IO;
using SHB.EosDataDictionary.Models;

namespace SHB.EosDataDictionary.Services
{
    /// <summary>XMZADD 20260916 按知识库、业务写入、入口流程和独立读取证据确定表的主模块及消费模块。</summary>
    public sealed class BusinessModuleAttributionService
    {
        private const int MaximumPublishedEvidencePerModule = 24;

        /// <summary>XMZADD 20260916 将同一物理表的源码证据归并为主模块和去重后的被使用模块。</summary>
        public void Apply(TableMetadata table, IList<SourceEvidence> evidence)
        {
            if (table == null)
            {
                return;
            }

            var summaries = new Dictionary<string, ModuleEvidenceSummary>(StringComparer.Ordinal);
            CollectEvidence(table.ObjectName, evidence, summaries);
            table.UsedByModules = CreateUsedByModules(table.UsedByModules, summaries);

            if (PreserveProtectedPrimaryModule(table))
            {
                return;
            }

            ModuleEvidenceSummary selected = SelectPrimaryModule(summaries);
            table.ModuleName = selected == null
                ? CreatePendingModule()
                : CreatePrimaryModule(selected);
        }

        /// <summary>XMZADD 20260916 将 EOS 目录名和旧模块名统一到数据字典受控业务模块。</summary>
        public static string NormalizeModuleName(string modulePath)
        {
            string value = (modulePath ?? string.Empty).Trim();
            if (value.Length == 0)
            {
                return "其他";
            }
            string key = value.Replace('\\', '/').ToUpperInvariant();

            if (key == "ITEM" || key.EndsWith("/ITEM", StringComparison.Ordinal) ||
                ContainsAny(key, "物料与BOM", "物料管理", "ITEM 物料", "/ITEM/", "MATERIAL", "BOM"))
            {
                return "物料与BOM";
            }
            if (ContainsAny(key, "仓储与库存", "仓库与库存", "仓库管理", "仓储", "仓库", "库存",
                    "WAREHOUSE", "STORAGE", "INVENTORY", "PALLET", "LOGISTICS", "W_MAIN", "WAREHOUSE_"))
            {
                return "仓储与库存";
            }
            if (ContainsAny(key, "采购管理", "采购", "PURCHASE", "PURCHASEx", "SUPPLIER"))
            {
                return "采购管理";
            }
            if (key.StartsWith("PLAN_", StringComparison.Ordinal) ||
                key.IndexOf("/PLAN_", StringComparison.Ordinal) >= 0 ||
                ContainsAny(key, "CUSTOMERDEMANDPLAN", "PLANSHARE"))
            {
                return "计划管理";
            }
            if (ContainsAny(key, "销售与客户", "销售管理", "销售", "客户", "SALE", "SALES", "CUSTOMER"))
            {
                return "销售与客户";
            }
            if (ContainsAny(key, "生产制造", "生产管理", "生产", "制造", "MANUFACTURE", "PRODUCTION",
                    "KANBAN", "WORKSTATION", "WORKSTATATION", "LINE_DATA", "ANDON", "PROCESS", "TECH"))
            {
                return "生产制造";
            }
            if (ContainsAny(key, "计划管理", "计划", "PLAN", "MRP", "MPS", "PROGRAM", "PROJECT_OPERATION"))
            {
                return "计划管理";
            }
            if (ContainsAny(key, "质量管理", "质量", "QUALITY", "QRQC", "TQC", "IQC"))
            {
                return "质量管理";
            }
            if (ContainsAny(key, "财务管理", "财务", "会计", "凭证", "应收", "应付", "FINANCE", "FINANCIAL",
                    "VOUCHER", "F_MIS", "RESEACHFEE"))
            {
                return "财务管理";
            }
            if (key == "HR" || key.EndsWith("/HR", StringComparison.Ordinal) ||
                ContainsAny(key, "人力资源", "人事", "/HR/", "HR_", "WELFARE", "WORKTIME", "ORG"))
            {
                return "人力资源";
            }
            if (ContainsAny(key, "设备与工装", "设备", "工装", "模具", "EQUIPMENT", "TOOLING", "FACILITY", "ASSET"))
            {
                return "设备与工装";
            }
            if (key == "LOG" || key.EndsWith("/LOG", StringComparison.Ordinal) ||
                ContainsAny(key, "日志与审计", "日志", "审计", "/LOG/", "LOGX", "AUDIT"))
            {
                return "日志与审计";
            }
            if (ContainsAny(key, "文件与图纸", "文档", "文件", "图纸", "DOCUMENT", "DRAWING", "FILE_X", "SHB_CATIA"))
            {
                return "文件与图纸";
            }
            if (ContainsAny(key, "系统配置", "系统", "配置", "权限", "SYSTEM", "CONFIG", "LOGIN",
                    "SOFTWARE_MANAGE", "PC_MANAGE", "DINGSERVICE", "WEAVER_OA", "SHB_COMMUNICATION"))
            {
                return "系统配置";
            }
            return "其他";
        }

        /// <summary>XMZADD 20260916 收集可解释业务模块且不来自生成实体或公共数据层的源码证据。</summary>
        private static void CollectEvidence(string objectName, IList<SourceEvidence> evidence,
            IDictionary<string, ModuleEvidenceSummary> summaries)
        {
            if (evidence == null)
            {
                return;
            }
            for (int index = 0; index < evidence.Count; index++)
            {
                SourceEvidence item = evidence[index];
                if (item == null || (!string.IsNullOrWhiteSpace(item.ObjectName) &&
                    !string.Equals(item.ObjectName, objectName, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                SourceEvidenceOrigin origin = GetOrigin(item);
                if (origin == SourceEvidenceOrigin.GeneratedEntity || IsCommonInfrastructure(item))
                {
                    continue;
                }
                string module = ResolveEvidenceModule(item);
                if (string.Equals(module, "其他", StringComparison.Ordinal) &&
                    origin != SourceEvidenceOrigin.BusinessCode)
                {
                    continue;
                }

                ModuleEvidenceSummary summary;
                if (!summaries.TryGetValue(module, out summary))
                {
                    summary = new ModuleEvidenceSummary(module);
                    summaries.Add(module, summary);
                }
                summary.Add(item, origin, IsProcessEntry(item));
            }
        }

        /// <summary>XMZADD 20260916 优先使用分析器模块目录，仅在缺失时回退源码目录且排除文件名关键词。</summary>
        private static string ResolveEvidenceModule(SourceEvidence evidence)
        {
            string module = NormalizeModuleName(evidence == null ? null : evidence.ModulePath);
            if (!string.Equals(module, "其他", StringComparison.Ordinal))
            {
                return module;
            }
            string sourcePath = evidence == null || evidence.Evidence == null
                ? string.Empty
                : evidence.Evidence.SourcePath ?? string.Empty;
            string normalizedPath = sourcePath.Replace('\\', '/');
            int separator = normalizedPath.LastIndexOf('/');
            string directory = separator < 0 ? string.Empty : normalizedPath.Substring(0, separator);
            return NormalizeModuleName(directory);
        }

        /// <summary>XMZADD 20260916 识别旧证据中缺失的来源角色，保证升级快照仍可按真实文件语境归属。</summary>
        private static SourceEvidenceOrigin GetOrigin(SourceEvidence evidence)
        {
            if (evidence == null)
            {
                return SourceEvidenceOrigin.Unknown;
            }
            if (evidence.Origin != SourceEvidenceOrigin.Unknown)
            {
                return evidence.Origin;
            }
            string sourceType = evidence.Evidence == null ? string.Empty : evidence.Evidence.SourceType ?? string.Empty;
            string sourcePath = evidence.Evidence == null ? string.Empty : evidence.Evidence.SourcePath ?? string.Empty;
            if (sourceType.IndexOf("生成实体", StringComparison.Ordinal) >= 0 ||
                sourcePath.IndexOf("表-类定义", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return SourceEvidenceOrigin.GeneratedEntity;
            }
            if (sourceType.IndexOf("设计器", StringComparison.Ordinal) >= 0 ||
                sourcePath.EndsWith(".Designer.vb", StringComparison.OrdinalIgnoreCase) ||
                sourcePath.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase))
            {
                return SourceEvidenceOrigin.Designer;
            }
            if (sourceType.IndexOf("自动生成", StringComparison.Ordinal) >= 0)
            {
                return SourceEvidenceOrigin.AutoGenerated;
            }
            if (sourceType.IndexOf("配置源码", StringComparison.Ordinal) >= 0)
            {
                return SourceEvidenceOrigin.Configuration;
            }
            return SourceEvidenceOrigin.BusinessCode;
        }

        /// <summary>XMZADD 20260916 排除数据库公共层、控件公共层和第三方目录，防止技术复用位置冒充业务所有者。</summary>
        private static bool IsCommonInfrastructure(SourceEvidence evidence)
        {
            string module = evidence == null ? string.Empty : evidence.ModulePath ?? string.Empty;
            string path = evidence == null || evidence.Evidence == null
                ? string.Empty
                : evidence.Evidence.SourcePath ?? string.Empty;
            string key = (module + "/" + path).Replace('\\', '/').ToUpperInvariant();
            return ContainsAny(key, "DATACONTROL", "C1_COMMON", "ADOPTED OPENSOURCE", "/PACKAGES/",
                       "DLL引用", "/SQLCLI/", "/GETDB/", "/SQL/", "/BASETABLE/") ||
                   key.StartsWith("G/", StringComparison.Ordinal) ||
                   key.IndexOf("/G/", StringComparison.Ordinal) >= 0;
        }

        /// <summary>XMZADD 20260916 识别菜单、Ribbon、主入口和流程文件，使入口证据高于普通读取位置。</summary>
        private static bool IsProcessEntry(SourceEvidence evidence)
        {
            string ruleName = evidence == null || evidence.Evidence == null
                ? string.Empty
                : evidence.Evidence.RuleName ?? string.Empty;
            string sourcePath = evidence == null || evidence.Evidence == null
                ? string.Empty
                : evidence.Evidence.SourcePath ?? string.Empty;
            string key = (ruleName + "/" + sourcePath).ToUpperInvariant();
            return ContainsAny(key, "MENU", "RIBBON", "NAVIGATION", "WORKFLOW", "PROCESSENTRY",
                "流程入口", "/MAIN/", "_MAIN/");
        }

        /// <summary>XMZADD 20260916 生成稳定排序的跨模块消费清单，并保留人工维护的消费模块。</summary>
        private static IList<MetadataValue> CreateUsedByModules(IList<MetadataValue> existing,
            IDictionary<string, ModuleEvidenceSummary> summaries)
        {
            // 发布或仅处理历史快照时可能没有本轮源码证据，此时旧快照中的可追溯消费模块不能被空扫描误删。
            if (summaries == null || summaries.Count == 0)
            {
                return existing ?? new List<MetadataValue>();
            }
            var result = new List<MetadataValue>();
            var ordered = new List<ModuleEvidenceSummary>();
            foreach (KeyValuePair<string, ModuleEvidenceSummary> pair in summaries)
            {
                if (pair.Value.HasUsageEvidence)
                {
                    ordered.Add(pair.Value);
                }
            }
            ordered.Sort(CompareModuleSummary);
            for (int index = 0; index < ordered.Count; index++)
            {
                result.Add(ordered[index].CreateUsedByValue());
            }
            if (existing != null)
            {
                for (int index = 0; index < existing.Count; index++)
                {
                    MetadataValue value = existing[index];
                    if (value != null && (value.IsManualOverride || value.IsLocked ||
                        value.Status == ConfidenceStatus.LocalOverride) && !ContainsModule(result, value.Value))
                    {
                        result.Add(value);
                    }
                }
            }
            return result;
        }

        /// <summary>XMZADD 20260916 保护人工模块、确认模块和知识库精确归属，并将系统结论规范到统一模块名。</summary>
        private static bool PreserveProtectedPrimaryModule(TableMetadata table)
        {
            MetadataValue existing = table.ModuleName;
            if (existing == null)
            {
                return false;
            }
            if (existing.IsManualOverride || existing.IsLocked ||
                existing.Status == ConfidenceStatus.LocalOverride)
            {
                return true;
            }
            if (existing.Status == ConfidenceStatus.Confirmed || IsExactKnowledgeModule(existing))
            {
                string normalized = NormalizeModuleName(existing.Value);
                if (!string.Equals(normalized, "其他", StringComparison.Ordinal))
                {
                    existing.Value = normalized;
                }
                return true;
            }
            return false;
        }

        /// <summary>XMZADD 20260916 判断模块是否来自项目表索引的精确业务章节。</summary>
        private static bool IsExactKnowledgeModule(MetadataValue value)
        {
            if (value == null || value.Status != ConfidenceStatus.KnowledgeBaseEvidence)
            {
                return false;
            }
            // 早期快照尚未保存模块证据明细，但知识库模块状态只由精确表索引产生，需要保持兼容。
            if (value.Evidence == null || value.Evidence.Count == 0)
            {
                return true;
            }
            for (int index = 0; index < value.Evidence.Count; index++)
            {
                EvidenceItem item = value.Evidence[index];
                if (item != null && string.Equals(item.RuleName, "ProjectModuleHeading", StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>XMZADD 20260916 按写入、业务入口、多处读取和目录上下文的固定优先级选择唯一主模块。</summary>
        private static ModuleEvidenceSummary SelectPrimaryModule(
            IDictionary<string, ModuleEvidenceSummary> summaries)
        {
            ModuleEvidenceSummary selected = null;
            bool hasTie = false;
            foreach (KeyValuePair<string, ModuleEvidenceSummary> pair in summaries)
            {
                ModuleEvidenceSummary current = pair.Value;
                if (current.PrimaryCategory == 0)
                {
                    continue;
                }
                if (selected == null || current.ComparePrimaryStrength(selected) > 0)
                {
                    selected = current;
                    hasTie = false;
                }
                else if (current.ComparePrimaryStrength(selected) == 0)
                {
                    hasTie = true;
                }
            }
            return hasTie ? null : selected;
        }

        /// <summary>XMZADD 20260916 根据获胜证据级别创建可追溯的主模块元数据。</summary>
        private static MetadataValue CreatePrimaryModule(ModuleEvidenceSummary summary)
        {
            bool isCodeEvidence = summary.PrimaryCategory >= 2;
            return new MetadataValue
            {
                Value = summary.ModuleName,
                Status = isCodeEvidence ? ConfidenceStatus.CodeEvidence : ConfidenceStatus.Guessed,
                ConfidenceScore = summary.PrimaryConfidenceScore,
                SourceType = isCodeEvidence ? "EOS业务源码" : "模块目录上下文",
                SourceSummary = summary.CreatePrimarySummary(),
                Evidence = summary.CopyEvidence()
            };
        }

        /// <summary>XMZADD 20260916 创建不误导新人的待确认模块值，用于只有技术结构证据或归属冲突的表。</summary>
        private static MetadataValue CreatePendingModule()
        {
            return new MetadataValue
            {
                Value = string.Empty,
                Status = ConfidenceStatus.PendingConfirmation,
                ConfidenceScore = 0,
                SourceType = "模块归属",
                SourceSummary = "未找到唯一可靠的业务模块证据",
                Evidence = new List<EvidenceItem>()
            };
        }

        /// <summary>XMZADD 20260916 按受控模块顺序稳定排列消费模块，避免刷新后界面顺序抖动。</summary>
        private static int CompareModuleSummary(ModuleEvidenceSummary left, ModuleEvidenceSummary right)
        {
            int order = GetModuleOrder(left.ModuleName).CompareTo(GetModuleOrder(right.ModuleName));
            return order != 0 ? order : string.Compare(left.ModuleName, right.ModuleName, StringComparison.Ordinal);
        }

        /// <summary>XMZADD 20260916 返回受控模块在界面和快照中的固定顺序。</summary>
        private static int GetModuleOrder(string module)
        {
            string[] modules =
            {
                "物料与BOM", "仓储与库存", "采购管理", "销售与客户", "生产制造", "计划管理",
                "质量管理", "财务管理", "人力资源", "设备与工装", "系统配置", "日志与审计", "文件与图纸"
            };
            for (int index = 0; index < modules.Length; index++)
            {
                if (string.Equals(modules[index], module, StringComparison.Ordinal))
                {
                    return index;
                }
            }
            return modules.Length;
        }

        /// <summary>XMZADD 20260916 判断元数据列表是否已包含同名模块。</summary>
        private static bool ContainsModule(IList<MetadataValue> modules, string module)
        {
            for (int index = 0; modules != null && index < modules.Count; index++)
            {
                if (modules[index] != null && string.Equals(modules[index].Value, module,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>XMZADD 20260916 判断文本是否包含任一模块关键词。</summary>
        private static bool ContainsAny(string value, params string[] candidates)
        {
            for (int index = 0; candidates != null && index < candidates.Length; index++)
            {
                if (value.IndexOf(candidates[index].ToUpperInvariant(), StringComparison.Ordinal) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>XMZADD 20260916 聚合同一规范模块的独立位置、用途方向和有限公开证据。</summary>
        private sealed class ModuleEvidenceSummary
        {
            private readonly HashSet<string> locations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            private readonly List<EvidenceItem> evidence = new List<EvidenceItem>();

            /// <summary>XMZADD 20260916 创建单个规范模块的证据聚合器。</summary>
            public ModuleEvidenceSummary(string moduleName)
            {
                ModuleName = moduleName;
            }

            public string ModuleName { get; private set; }
            public int WriteCount { get; private set; }
            public int ReadCount { get; private set; }
            public int DisplayCount { get; private set; }
            public int EntryCount { get; private set; }
            public int DirectoryCount { get; private set; }
            public bool HasUsageEvidence { get { return WriteCount + ReadCount + DisplayCount > 0; } }
            public int PrimaryCategory
            {
                get
                {
                    // “其他”只表示目录无法解释，不能因该文件执行写入就升级为不可覆盖的强业务归属。
                    if (string.Equals(ModuleName, "其他", StringComparison.Ordinal))
                    {
                        return DirectoryCount > 0 ? 1 : 0;
                    }
                    if (WriteCount > 0) return 4;
                    if (EntryCount > 0) return 3;
                    if (ReadCount >= 2) return 2;
                    if (DirectoryCount > 0) return 1;
                    return 0;
                }
            }
            public int PrimaryConfidenceScore
            {
                get
                {
                    if (PrimaryCategory == 4) return 96;
                    if (PrimaryCategory == 3) return 90;
                    if (PrimaryCategory == 2) return 84;
                    return 65;
                }
            }

            /// <summary>XMZADD 20260916 按独立源码位置累计当前模块的读写、展示和入口证据。</summary>
            public void Add(SourceEvidence source, SourceEvidenceOrigin origin, bool isProcessEntry)
            {
                string sourcePath = source == null || source.Evidence == null
                    ? string.Empty
                    : source.Evidence.SourcePath ?? string.Empty;
                int sourceLine = source == null || source.Evidence == null ? 0 : source.Evidence.SourceLine;
                string ruleName = source == null || source.Evidence == null
                    ? string.Empty
                    : source.Evidence.RuleName ?? string.Empty;
                string location = sourcePath + "|" + sourceLine.ToString() + "|" + ruleName + "|" + source.UsageKind.ToString();
                if (!locations.Add(location))
                {
                    return;
                }

                if (source.UsageKind == SourceUsageKind.Write) WriteCount++;
                if (source.UsageKind == SourceUsageKind.Read || source.UsageKind == SourceUsageKind.Relation) ReadCount++;
                if (source.UsageKind == SourceUsageKind.Display) DisplayCount++;
                if (origin == SourceEvidenceOrigin.BusinessCode && isProcessEntry) EntryCount++;
                if (origin == SourceEvidenceOrigin.BusinessCode) DirectoryCount++;

                if (evidence.Count < MaximumPublishedEvidencePerModule)
                {
                    evidence.Add(CloneEvidence(source == null ? null : source.Evidence));
                }
            }

            /// <summary>XMZADD 20260916 比较两个模块的主归属强度，不用目录字母顺序制造虚假确定性。</summary>
            public int ComparePrimaryStrength(ModuleEvidenceSummary other)
            {
                if (other == null) return 1;
                int category = PrimaryCategory.CompareTo(other.PrimaryCategory);
                if (category != 0) return category;
                int categoryCount = GetCategoryCount().CompareTo(other.GetCategoryCount());
                if (categoryCount != 0) return categoryCount;
                return locations.Count.CompareTo(other.locations.Count);
            }

            /// <summary>XMZADD 20260916 创建当前模块的跨模块使用元数据。</summary>
            public MetadataValue CreateUsedByValue()
            {
                int score = WriteCount > 0 ? 95 : ReadCount > 0 ? 85 : 75;
                return new MetadataValue
                {
                    Value = ModuleName,
                    Status = ConfidenceStatus.CodeEvidence,
                    ConfidenceScore = score,
                    SourceType = "EOS业务使用",
                    SourceSummary = "写入 " + WriteCount.ToString() + " 处，读取/关联 " + ReadCount.ToString() +
                                    " 处，展示 " + DisplayCount.ToString() + " 处",
                    Evidence = CopyEvidence()
                };
            }

            /// <summary>XMZADD 20260916 创建主模块选择原因摘要。</summary>
            public string CreatePrimarySummary()
            {
                if (PrimaryCategory == 4) return "EOS 核心业务代码写入该表";
                if (PrimaryCategory == 3) return "EOS 菜单或流程入口使用该表";
                if (PrimaryCategory == 2) return "EOS 多个独立业务位置读取该表";
                return "EOS 业务源码目录提供模块上下文";
            }

            /// <summary>XMZADD 20260916 深复制当前模块的有限来源证据供快照发布。</summary>
            public IList<EvidenceItem> CopyEvidence()
            {
                var result = new List<EvidenceItem>();
                for (int index = 0; index < evidence.Count; index++)
                {
                    result.Add(CloneEvidence(evidence[index]));
                }
                return result;
            }

            /// <summary>XMZADD 20260916 返回当前最高优先级实际参与比较的证据数量。</summary>
            private int GetCategoryCount()
            {
                if (PrimaryCategory == 4) return WriteCount;
                if (PrimaryCategory == 3) return EntryCount;
                if (PrimaryCategory == 2) return ReadCount;
                return DirectoryCount;
            }

            /// <summary>XMZADD 20260916 复制单条模块证据并移除本机绝对路径。</summary>
            private static EvidenceItem CloneEvidence(EvidenceItem source)
            {
                if (source == null)
                {
                    return new EvidenceItem { SourceType = "EOS源码", RuleName = "ModuleUsage" };
                }
                string path = source.SourcePath ?? string.Empty;
                if (Path.IsPathRooted(path) || path.IndexOf("..", StringComparison.Ordinal) >= 0)
                {
                    path = Path.GetFileName(path);
                }
                return new EvidenceItem
                {
                    SourceType = source.SourceType,
                    SourcePath = path.Replace('\\', '/'),
                    SourceLine = source.SourceLine,
                    RuleName = source.RuleName,
                    // 模块归属只需公开相对位置和规则，原 SQL 行可能包含连接信息或超长业务文本。
                    Explanation = LimitEvidenceText(source.Explanation)
                };
            }

            /// <summary>XMZADD 20260916 限制模块证据说明长度，避免源码正文意外进入公开快照。</summary>
            private static string LimitEvidenceText(string value)
            {
                string text = (value ?? string.Empty).Trim();
                return text.Length <= 240 ? text : text.Substring(0, 240);
            }
        }
    }
}
