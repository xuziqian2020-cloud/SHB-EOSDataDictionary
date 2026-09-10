using System;
using System.Collections.Generic;

namespace SHB.EosDataDictionary.Models
{
    /// <summary>XMZADD 20260831 标识数据库结构的新增、删除和变更类型，为增量同步确定后续处理方式。</summary>
    public enum SchemaChangeType
    {
        ObjectAdded,
        ObjectRemoved,
        ObjectChanged,
        FieldAdded,
        FieldRemoved,
        FieldChanged,
        RelationAdded,
        RelationRemoved,
        RelationChanged
    }

    /// <summary>XMZADD 20260831 保存一次结构差异的定位与前后信息，供快照刷新和 AI 队列复用。</summary>
    public sealed class SchemaChange
    {
        public string ScopeKey { get; set; }
        public string SchemaName { get; set; }
        public string ObjectName { get; set; }
        public string FieldName { get; set; }
        public SchemaChangeType ChangeType { get; set; }
        public string PreviousValue { get; set; }
        public string CurrentValue { get; set; }
        public string Description { get; set; }
        public DateTime DetectedAt { get; set; }
        public RelationStructurePayload RelationPayload { get; set; }
    }

    /// <summary>XMZADD 20260831 汇总一次数据库结构同步发现的变化，避免后续流程重复比较完整快照。</summary>
    public sealed class SchemaDiffResult
    {
        /// <summary>XMZADD 20260831 初始化变化集合，确保未发现变化时仍可安全遍历结果。</summary>
        public SchemaDiffResult()
        {
            Changes = new List<SchemaChange>();
        }

        public IList<SchemaChange> Changes { get; set; }
    }
}
