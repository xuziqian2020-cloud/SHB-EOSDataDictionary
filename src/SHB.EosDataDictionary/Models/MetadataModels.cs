using System;
using System.Collections.Generic;

namespace SHB.EosDataDictionary.Models
{
    /// <summary>XMZADD 20260901 区分共享字典中的业务表、基础资料表、技术表和排除对象。</summary>
    public enum DictionaryTableCategory
    {
        Unclassified = 0,
        Business = 1,
        BaseData = 2,
        Technical = 3,
        Excluded = 4
    }

    /// <summary>XMZADD 20260828 保存一个 EOS 表或视图的结构和业务解释。</summary>
    public sealed class TableMetadata
    {
        /// <summary>XMZADD 20260911 初始化可持续维护的表级参考名称、模块和结构集合。</summary>
        public TableMetadata()
        {
            AlternativeChineseNames = new List<MetadataValue>();
            RejectedSuggestionFingerprints = new List<string>();
            UsedByModules = new List<MetadataValue>();
            Fields = new List<FieldMetadata>();
            Relations = new List<RelationMetadata>();
        }

        public string ScopeKey { get; set; }
        public string SchemaName { get; set; }
        public string ObjectName { get; set; }
        public string ObjectType { get; set; }
        public MetadataValue ChineseName { get; set; }
        /// <summary>XMZADD 20260910 保存尚未确认为正式名称的首选参考译名。</summary>
        public MetadataValue SuggestedChineseName { get; set; }
        /// <summary>XMZADD 20260910 保存可供人工审阅的其他参考译名。</summary>
        public IList<MetadataValue> AlternativeChineseNames { get; set; }
        /// <summary>XMZADD 20260910 保存已拒绝建议的稳定指纹以避免重复推荐。</summary>
        public IList<string> RejectedSuggestionFingerprints { get; set; }
        /// <summary>XMZADD 20260910 保存使用当前对象的业务模块证据。</summary>
        public IList<MetadataValue> UsedByModules { get; set; }
        public MetadataValue ModuleName { get; set; }
        public MetadataValue EntityName { get; set; }
        public MetadataValue BusinessMeaning { get; set; }
        public MetadataValue Remark { get; set; }
        public DictionaryTableCategory Category { get; set; }
        public long ApproximateRowCount { get; set; }
        public bool KeepWhenEmpty { get; set; }
        public IList<FieldMetadata> Fields { get; set; }
        public IList<RelationMetadata> Relations { get; set; }
    }

    /// <summary>XMZADD 20260828 保存一个 EOS 表字段的结构、枚举和关系说明。</summary>
    public sealed class FieldMetadata
    {
        /// <summary>XMZADD 20260911 初始化可持续维护的字段参考名称和枚举集合。</summary>
        public FieldMetadata()
        {
            AlternativeChineseNames = new List<MetadataValue>();
            RejectedSuggestionFingerprints = new List<string>();
            EnumItems = new List<EnumItemMetadata>();
        }

        public string FieldName { get; set; }
        public MetadataValue ChineseName { get; set; }
        /// <summary>XMZADD 20260910 保存尚未确认为正式名称的首选参考译名。</summary>
        public MetadataValue SuggestedChineseName { get; set; }
        /// <summary>XMZADD 20260910 保存可供人工审阅的其他参考译名。</summary>
        public IList<MetadataValue> AlternativeChineseNames { get; set; }
        /// <summary>XMZADD 20260910 保存已拒绝建议的稳定指纹以避免重复推荐。</summary>
        public IList<string> RejectedSuggestionFingerprints { get; set; }
        public string OwnerTableName { get; set; }
        public MetadataValue EntityPropertyName { get; set; }
        public MetadataValue BusinessMeaning { get; set; }
        public MetadataValue Usage { get; set; }
        public string DataType { get; set; }
        public string LengthText { get; set; }
        public bool IsRequired { get; set; }
        public bool IsPrimaryKey { get; set; }
        public bool IsForeignKey { get; set; }
        public MetadataValue EnumName { get; set; }
        public IList<EnumItemMetadata> EnumItems { get; set; }
        public MetadataValue RelationSummary { get; set; }
        public MetadataValue Remark { get; set; }
    }

    /// <summary>XMZADD 20260828 保存一个枚举值和中文含义的证据。</summary>
    public sealed class EnumItemMetadata
    {
        public string Value { get; set; }
        public MetadataValue ChineseName { get; set; }
    }

    /// <summary>XMZADD 20260828 保存父子表字段映射和关系类型。</summary>
    public sealed class RelationMetadata
    {
        public string ScopeKey { get; set; }
        public string ForeignKeyName { get; set; }
        public string ParentSchemaName { get; set; }
        public string ParentTableName { get; set; }
        public string ParentFieldName { get; set; }
        public string ChildSchemaName { get; set; }
        public string ChildTableName { get; set; }
        public string ChildFieldName { get; set; }
        public MetadataValue RelationType { get; set; }
        public MetadataValue BusinessMeaning { get; set; }
        public MetadataValue Remark { get; set; }
    }

    /// <summary>XMZADD 20260828 提供关系推导所需的结构约束输入。</summary>
    public sealed class RelationInput
    {
        public string ParentSchemaName { get; set; }
        public string ParentTableName { get; set; }
        public string ParentFieldName { get; set; }
        public string ChildSchemaName { get; set; }
        public string ChildTableName { get; set; }
        public string ChildFieldName { get; set; }
        public bool HasRealForeignKey { get; set; }
        public bool ChildForeignKeyIsUnique { get; set; }
        public bool IsBridgeTable { get; set; }
    }

    /// <summary>XMZADD 20260828 保存一个数据库作用域的一次结构快照。</summary>
    public sealed class SnapshotData
    {
        public SnapshotData()
        {
            Tables = new List<TableMetadata>();
            ExcludedObjects = new List<ExcludedObjectRecord>();
            Abbreviations = new List<AbbreviationEntry>();
        }

        public int FormatVersion { get; set; }
        public long Revision { get; set; }
        public DateTime RefreshedAt { get; set; }
        public IList<TableMetadata> Tables { get; set; }
        public IList<ExcludedObjectRecord> ExcludedObjects { get; set; }
        public IList<AbbreviationEntry> Abbreviations { get; set; }
    }
}
