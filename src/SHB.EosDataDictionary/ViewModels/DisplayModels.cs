using System.Collections.ObjectModel;
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
        public string ChineseName { get { return Metadata.ChineseName == null || string.IsNullOrWhiteSpace(Metadata.ChineseName.Value) ? "暂无中文名称" : Metadata.ChineseName.Value; } }
        public string ObjectName { get { return Metadata.ObjectName; } }
        public string TreeTitle { get { return ChineseName; } }
        public string TreeSubtitle { get { return ObjectName ?? string.Empty; } }
        public string ObjectTypeText { get { return Metadata.ObjectType == "VIEW" ? "视图" : "物理表"; } }
        public string ModuleName { get { return Metadata.ModuleName == null ? "其他" : Metadata.ModuleName.Value; } }
        public string EntityName { get { return Metadata.EntityName == null ? string.Empty : Metadata.EntityName.Value; } }
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
            string entitySource = Metadata.EntityName == null ? string.Empty : Metadata.EntityName.SourceSummary;
            if (nameSource == "演示数据" && entitySource == "演示数据")
            {
                return "预览演示 · 非真实依据";
            }
            return "表名：" + nameSource + "；实体：" + entitySource;
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
        public string ChineseName { get { return Metadata.ChineseName == null || string.IsNullOrWhiteSpace(Metadata.ChineseName.Value) ? "暂无中文名称" : Metadata.ChineseName.Value; } }
        public string OwnerTableName { get { return Metadata.OwnerTableName; } }
        public string EntityPropertyName { get { return Metadata.EntityPropertyName == null ? "暂无实体属性" : Metadata.EntityPropertyName.Value; } }
        public string DataType { get { return Metadata.DataType; } }
        public string EnumText { get { return BuildEnumText(); } }
        public string LengthText { get { return Metadata.LengthText; } }
        public string RequiredText { get { return Metadata.IsRequired ? "是" : "否"; } }
        public string KeyText { get { return GetKeyText(); } }
        public string RelationText { get { return Metadata.RelationSummary == null ? "暂无关联说明" : Metadata.RelationSummary.Value; } }
        public string StatusText { get { return TableDisplayModel.GetStatusText(Metadata.ChineseName == null ? ConfidenceStatus.PendingConfirmation : Metadata.ChineseName.Status); } }
        public Brush StatusBrush { get { return TableDisplayModel.GetStatusBrush(Metadata.ChineseName == null ? ConfidenceStatus.PendingConfirmation : Metadata.ChineseName.Status); } }
        public string EvidenceSummary { get { return Metadata.ChineseName == null ? "没有明确字段含义" : Metadata.ChineseName.SourceSummary; } }

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
                result += Metadata.EnumItems[i].Value + "：" + Metadata.EnumItems[i].ChineseName.Value;
            }
            return result;
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
        public string SourcePath { get { return Evidence == null ? string.Empty : Evidence.SourcePath; } }
        public int SourceLine { get { return Evidence == null ? 0 : Evidence.SourceLine; } }
        public string RuleName { get { return Evidence == null ? string.Empty : Evidence.RuleName; } }
        public string Explanation { get { return Evidence == null ? string.Empty : Evidence.Explanation; } }
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
