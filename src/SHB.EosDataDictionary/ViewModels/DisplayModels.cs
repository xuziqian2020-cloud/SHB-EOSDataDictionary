using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using SHB.EosDataDictionary.Models;

namespace SHB.EosDataDictionary.ViewModels
{
    /// <summary>XMZADD 20260901 将表元数据转换为支持稳定选中和编辑来源的中文显示行。</summary>
    public sealed class TableDisplayModel
    {
        /// <summary>XMZADD 20260901 保存数据表元数据引用，使列表选择与编辑窗口共享同一规范对象。</summary>
        public TableDisplayModel(TableMetadata metadata)
        {
            Metadata = metadata;
        }

        public TableMetadata Metadata { get; private set; }
        /// <summary>XMZADD 20260901 暴露当前显示行对应的数据表元数据，供编辑入口直接定位业务对象。</summary>
        public TableMetadata Source { get { return Metadata; } }
        public string OfficialChineseName { get { return GetValue(Metadata.ChineseName); } }
        public string ChineseName { get { return HasOfficialName ? OfficialChineseName : "待确认"; } }
        public string SuggestedChineseName { get { return GetValue(Metadata.SuggestedChineseName); } }
        public bool HasOfficialName { get { return !string.IsNullOrWhiteSpace(OfficialChineseName); } }
        public bool HasSuggestion { get { return !string.IsNullOrWhiteSpace(SuggestedChineseName); } }
        public int AlternativeChineseNameCount { get { return CountValues(Metadata.AlternativeChineseNames); } }
        public bool IsConflict
        {
            get
            {
                return AlternativeChineseNameCount > 0 ||
                       (Metadata.ChineseName != null &&
                        Metadata.ChineseName.Status == ConfidenceStatus.GuessedConflict);
            }
        }
        public string ConflictText { get { return IsConflict ? "有冲突" : "—"; } }
        public string ObjectName { get { return Metadata.ObjectName; } }
        public string TreeTitle { get { return ChineseName; } }
        public string TreeSubtitle { get { return ObjectName ?? string.Empty; } }
        public string ObjectTypeText { get { return Metadata.ObjectType == "VIEW" ? "视图" : "物理表"; } }
        public string ModuleName { get { return string.IsNullOrWhiteSpace(GetValue(Metadata.ModuleName)) ? "其他" : Metadata.ModuleName.Value; } }
        public string EntityName { get { return Metadata.EntityName == null ? string.Empty : Metadata.EntityName.Value; } }
        public bool HasEntity { get { return !string.IsNullOrWhiteSpace(EntityName); } }
        public string UsedByModulesText { get { return JoinMetadataValues(Metadata.UsedByModules); } }
        public string StatusText { get { return GetStatusText(GetMainStatus()); } }
        public Brush StatusBrush { get { return GetStatusBrush(GetMainStatus()); } }
        public string SourceSummary { get { return GetSourceSummary(); } }
        public bool IsExpanded { get; set; }
        public bool IsTreeSelected { get; set; }

        private ConfidenceStatus GetMainStatus()
        {
            if (Metadata.ChineseName != null)
            {
                return Metadata.ChineseName.Status;
            }
            return ConfidenceStatus.PendingConfirmation;
        }

        private string GetSourceSummary()
        {
            string nameSource = Metadata.ChineseName == null ? string.Empty : Metadata.ChineseName.SourceSummary;
            string suggestionSource = Metadata.SuggestedChineseName == null
                ? string.Empty
                : Metadata.SuggestedChineseName.SourceSummary;
            string entitySource = Metadata.EntityName == null ? string.Empty : Metadata.EntityName.SourceSummary;
            if (nameSource == "演示数据" && entitySource == "演示数据")
            {
                return "预览演示 · 非真实依据";
            }
            return "正式名：" + nameSource + "；参考名：" + suggestionSource + "；实体：" + entitySource;
        }

        /// <summary>XMZADD 20260917 安全读取名称层的文本值。</summary>
        private static string GetValue(MetadataValue value)
        {
            return value == null ? string.Empty : value.Value ?? string.Empty;
        }

        /// <summary>XMZADD 20260917 统计非空备选名，避免空占位触发冲突标识。</summary>
        private static int CountValues(IList<MetadataValue> values)
        {
            int count = 0;
            for (int index = 0; values != null && index < values.Count; index++)
            {
                if (!string.IsNullOrWhiteSpace(GetValue(values[index])))
                {
                    count++;
                }
            }
            return count;
        }

        /// <summary>XMZADD 20260917 对消费模块去重排序，保证列表显示和搜索结果稳定。</summary>
        private static string JoinMetadataValues(IList<MetadataValue> values)
        {
            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; values != null && index < values.Count; index++)
            {
                string name = GetValue(values[index]).Trim();
                if (name.Length > 0 && seen.Add(name))
                {
                    names.Add(name);
                }
            }
            names.Sort(StringComparer.Ordinal);
            return string.Join("、", names.ToArray());
        }

        internal static string GetStatusText(ConfidenceStatus status)
        {
            switch (status)
            {
                case ConfidenceStatus.Confirmed: return "已确认";
                case ConfidenceStatus.DatabaseEvidence: return "数据库依据";
                case ConfidenceStatus.CodeEvidence: return "代码依据";
                case ConfidenceStatus.AiGuessed: return "AI推测";
                case ConfidenceStatus.LocalOverride: return "人工修改";
                case ConfidenceStatus.GuessedConflict: return "推测（存在冲突）";
                case ConfidenceStatus.Guessed: return "推测";
                default: return "待确认";
            }
        }

        internal static Brush GetStatusBrush(ConfidenceStatus status)
        {
            switch (status)
            {
                case ConfidenceStatus.Confirmed: return new SolidColorBrush(Color.FromRgb(52, 211, 153));
                case ConfidenceStatus.DatabaseEvidence: return new SolidColorBrush(Color.FromRgb(96, 165, 250));
                case ConfidenceStatus.CodeEvidence: return new SolidColorBrush(Color.FromRgb(45, 212, 191));
                case ConfidenceStatus.AiGuessed: return new SolidColorBrush(Color.FromRgb(96, 165, 250));
                case ConfidenceStatus.LocalOverride: return new SolidColorBrush(Color.FromRgb(52, 211, 153));
                case ConfidenceStatus.GuessedConflict: return new SolidColorBrush(Color.FromRgb(248, 113, 113));
                case ConfidenceStatus.Guessed: return new SolidColorBrush(Color.FromRgb(251, 191, 36));
                default: return new SolidColorBrush(Color.FromRgb(148, 163, 184));
            }
        }
    }

    /// <summary>XMZADD 20260901 将字段元数据转换为支持稳定选中和编辑来源的字段显示行。</summary>
    public sealed class FieldDisplayModel
    {
        /// <summary>XMZADD 20260901 保存字段元数据引用，使字段选择与编辑窗口共享同一规范对象。</summary>
        public FieldDisplayModel(FieldMetadata metadata)
        {
            Metadata = metadata;
        }

        public FieldMetadata Metadata { get; private set; }
        /// <summary>XMZADD 20260901 暴露当前显示行对应的字段元数据，供编辑入口直接定位字段对象。</summary>
        public FieldMetadata Source { get { return Metadata; } }
        public string FieldName { get { return Metadata.FieldName; } }
        public string OfficialChineseName { get { return GetValue(Metadata.ChineseName); } }
        public string ChineseName { get { return HasOfficialName ? OfficialChineseName : "待确认"; } }
        public string SuggestedChineseName { get { return GetValue(Metadata.SuggestedChineseName); } }
        public bool HasOfficialName { get { return !string.IsNullOrWhiteSpace(OfficialChineseName); } }
        public bool HasSuggestion { get { return !string.IsNullOrWhiteSpace(SuggestedChineseName); } }
        public int AlternativeChineseNameCount { get { return CountValues(Metadata.AlternativeChineseNames); } }
        public bool IsConflict
        {
            get
            {
                return AlternativeChineseNameCount > 0 ||
                       (Metadata.ChineseName != null &&
                        Metadata.ChineseName.Status == ConfidenceStatus.GuessedConflict);
            }
        }
        public string ConflictText { get { return IsConflict ? "有冲突" : "—"; } }
        public string OwnerTableName { get { return Metadata.OwnerTableName; } }
        public string EntityPropertyName { get { return Metadata.EntityPropertyName == null ? "暂无实体属性" : Metadata.EntityPropertyName.Value; } }
        public string DataType { get { return Metadata.DataType; } }
        public string EnumText { get { return BuildEnumText(); } }
        public string LengthText { get { return Metadata.LengthText; } }
        public string RequiredText { get { return Metadata.IsRequired ? "是" : "否"; } }
        public string KeyText { get { return GetKeyText(); } }
        public string RelationText { get { return Metadata.RelationSummary == null ? "暂无关联说明" : Metadata.RelationSummary.Value; } }
        public bool IsActualUsed { get { return !string.IsNullOrWhiteSpace(GetValue(Metadata.Usage)); } }
        public string ActualUsageText { get { return IsActualUsed ? "业务已使用" : "未发现使用"; } }
        public string UsageSummary { get { return GetValue(Metadata.Usage); } }
        public string StatusText { get { return TableDisplayModel.GetStatusText(Metadata.ChineseName == null ? ConfidenceStatus.PendingConfirmation : Metadata.ChineseName.Status); } }
        public Brush StatusBrush { get { return TableDisplayModel.GetStatusBrush(Metadata.ChineseName == null ? ConfidenceStatus.PendingConfirmation : Metadata.ChineseName.Status); } }
        public string EvidenceSummary
        {
            get
            {
                if (Metadata.ChineseName != null && !string.IsNullOrWhiteSpace(Metadata.ChineseName.SourceSummary))
                {
                    return Metadata.ChineseName.SourceSummary;
                }
                return Metadata.SuggestedChineseName == null || string.IsNullOrWhiteSpace(Metadata.SuggestedChineseName.SourceSummary)
                    ? "没有明确字段含义"
                    : Metadata.SuggestedChineseName.SourceSummary;
            }
        }

        private string BuildEnumText()
        {
            if (Metadata.EnumItems == null || Metadata.EnumItems.Count == 0)
            {
                return Metadata.EnumName == null ? string.Empty : Metadata.EnumName.Value;
            }

            string result = Metadata.EnumName == null ? string.Empty : Metadata.EnumName.Value;
            for (int i = 0; i < Metadata.EnumItems.Count; i++)
            {
                if (result.Length > 0)
                {
                    result += "\r\n";
                }
                EnumItemMetadata item = Metadata.EnumItems[i];
                if (item != null)
                {
                    result += (item.Value ?? string.Empty) + "：" + GetValue(item.ChineseName);
                }
            }
            return result;
        }

        /// <summary>XMZADD 20260917 安全读取字段名称层和用途层文本。</summary>
        private static string GetValue(MetadataValue value)
        {
            return value == null ? string.Empty : value.Value ?? string.Empty;
        }

        /// <summary>XMZADD 20260917 统计有效字段备选名称以形成显式冲突状态。</summary>
        private static int CountValues(IList<MetadataValue> values)
        {
            int count = 0;
            for (int index = 0; values != null && index < values.Count; index++)
            {
                if (!string.IsNullOrWhiteSpace(GetValue(values[index])))
                {
                    count++;
                }
            }
            return count;
        }

        /// <summary>XMZADD 20260831 根据明确标记的逻辑关系区分推测外键和普通字段。</summary>
        private bool IsInferredForeignKey()
        {
            return Metadata.RelationSummary != null && Metadata.RelationSummary.Status == ConfidenceStatus.Guessed &&
                   (Metadata.RelationSummary.Value ?? string.Empty).StartsWith("推测关联：", System.StringComparison.Ordinal);
        }

        /// <summary>XMZADD 20260916 区分真实外键、命名推测和单一代码 JOIN 参考关系，避免新人误把参考当成约束。</summary>
        private string GetKeyText()
        {
            if (Metadata.IsPrimaryKey) return "主键";
            if (Metadata.IsForeignKey) return "外键";
            string relation = Metadata.RelationSummary == null ? string.Empty : Metadata.RelationSummary.Value ?? string.Empty;
            if (Metadata.RelationSummary != null && Metadata.RelationSummary.Status == ConfidenceStatus.Guessed &&
                relation.StartsWith("参考关联：", System.StringComparison.Ordinal)) return "参考外键";
            return IsInferredForeignKey() ? "推测外键" : "—";
        }
    }

    /// <summary>XMZADD 20260828 将关系元数据转换成关系详情显示行。</summary>
    public sealed class RelationDisplayModel
    {
        public RelationDisplayModel(RelationMetadata metadata)
        {
            Metadata = metadata;
        }

        public RelationMetadata Metadata { get; private set; }
        public string ParentTableName { get { return Metadata.ParentTableName; } }
        public string ParentFieldName { get { return Metadata.ParentFieldName; } }
        public string ChildTableName { get { return Metadata.ChildTableName; } }
        public string ChildFieldName { get { return Metadata.ChildFieldName; } }
        public string ForeignKeyName { get { return Metadata.ForeignKeyName; } }
        public string RelationType { get { return Metadata.RelationType == null ? "暂无关联类型" : Metadata.RelationType.Value; } }
        public string StatusText { get { return TableDisplayModel.GetStatusText(Metadata.RelationType == null ? ConfidenceStatus.PendingConfirmation : Metadata.RelationType.Status); } }
        public Brush StatusBrush { get { return TableDisplayModel.GetStatusBrush(Metadata.RelationType == null ? ConfidenceStatus.PendingConfirmation : Metadata.RelationType.Status); } }
    }

    /// <summary>XMZADD 20260831 将原始来源证据转换为可在明细表中定位和核验的展示行。</summary>
    public sealed class EvidenceDisplayModel
    {
        public EvidenceDisplayModel(EvidenceItem evidence)
        {
            Evidence = evidence;
        }

        public EvidenceItem Evidence { get; private set; }
        public string SourceType { get { return Evidence == null ? string.Empty : Evidence.SourceType; } }
        public string SourcePath { get { return Evidence == null ? string.Empty : SanitizeSourcePath(Evidence.SourcePath); } }
        public int SourceLine { get { return Evidence == null ? 0 : Evidence.SourceLine; } }
        public string RuleName { get { return Evidence == null ? string.Empty : Evidence.RuleName; } }
        public string Explanation { get { return Evidence == null ? string.Empty : SanitizeExplanation(Evidence.Explanation); } }

        /// <summary>XMZADD 20260917 将本机绝对路径压缩为文件名并统一相对路径分隔符，避免泄露环境目录。</summary>
        internal static string SanitizeSourcePath(string sourcePath)
        {
            string value = (sourcePath ?? string.Empty).Trim();
            if (value.Length == 0)
            {
                return string.Empty;
            }
            if (Path.IsPathRooted(value) || value.IndexOf("..", StringComparison.Ordinal) >= 0)
            {
                return Path.GetFileName(value) ?? string.Empty;
            }
            return value.Replace('\\', '/');
        }

        /// <summary>XMZADD 20260917 将证据说明限制为单行摘要，禁止来源页意外承载整段源码。</summary>
        internal static string SanitizeExplanation(string explanation)
        {
            string value = (explanation ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
            return value.Length <= 240 ? value : value.Substring(0, 240) + "…";
        }
    }

    /// <summary>XMZADD 20260831 表示资源树中的业务模块节点及其当前可见对象。</summary>
    public sealed class ModuleNode
    {
        public ModuleNode(string name, ConfidenceStatus status)
        {
            Name = name;
            StatusText = TableDisplayModel.GetStatusText(status);
            StatusBrush = TableDisplayModel.GetStatusBrush(status);
            Tables = new ObservableCollection<TableDisplayModel>();
        }

        public string Name { get; private set; }
        public string StatusText { get; private set; }
        public Brush StatusBrush { get; private set; }
        public ObservableCollection<TableDisplayModel> Tables { get; private set; }
        public bool IsExpanded { get; set; }
        public bool IsTreeSelected { get; set; }
    }
}
