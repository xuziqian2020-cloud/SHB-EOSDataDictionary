using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace SHB.EosDataDictionary.Models
{
    /// <summary>XMZADD 20260917 保存一次可独立追踪和重放的属性赋值、参考译名否决或结构修改。</summary>
    public sealed class DictionaryChangeOperation : IExtensibleDataObject
    {
        public string OperationId { get; set; }
        public string AuthorGitHubUserId { get; set; }
        public string ObjectKey { get; set; }
        public string FieldKey { get; set; }
        public string PropertyName { get; set; }
        public string OldValue { get; set; }
        public string NewValue { get; set; }
        public string ChangeKind { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public TableStructurePayload TablePayload { get; set; }
        public FieldStructurePayload FieldPayload { get; set; }
        public RelationStructurePayload RelationPayload { get; set; }
        public IList<EvidenceItem> Evidence { get; set; }
        public ExtensionDataObject ExtensionData { get; set; }

        /// <summary>XMZADD 20260901 使用稳定对象键创建可全局去重的字典赋值事件。</summary>
        public static DictionaryChangeOperation Create(
            string authorGitHubUserId,
            string objectKey,
            string fieldKey,
            string propertyName,
            string newValue)
        {
            return new DictionaryChangeOperation
            {
                OperationId = Guid.NewGuid().ToString("N"),
                AuthorGitHubUserId = authorGitHubUserId,
                ObjectKey = objectKey,
                FieldKey = fieldKey,
                PropertyName = propertyName,
                NewValue = newValue,
                ChangeKind = "Set",
                CreatedAtUtc = DateTime.UtcNow
            };
        }
    }

    /// <summary>XMZADD 20260901 保存新增表所需的类型化物理结构，避免从自由文本还原数据库事实。</summary>
    public sealed class TableStructurePayload : IExtensibleDataObject
    {
        /// <summary>XMZADD 20260901 初始化表结构载荷的子集合，便于校验新增表必须由后续独立事件补充字段和关系。</summary>
        public TableStructurePayload()
        {
            Fields = new List<FieldStructurePayload>();
            Relations = new List<RelationStructurePayload>();
        }

        public string SchemaName { get; set; }
        public string ObjectName { get; set; }
        public string ObjectType { get; set; }
        public long ApproximateRowCount { get; set; }
        public IList<FieldStructurePayload> Fields { get; set; }
        public IList<RelationStructurePayload> Relations { get; set; }
        public ExtensionDataObject ExtensionData { get; set; }
    }

    /// <summary>XMZADD 20260901 保存新增字段所需的类型化物理事实，确保布尔约束不会按任意文本解释。</summary>
    public sealed class FieldStructurePayload : IExtensibleDataObject
    {
        public string FieldName { get; set; }
        public string OwnerTableName { get; set; }
        public string DataType { get; set; }
        public string LengthText { get; set; }
        public bool IsRequired { get; set; }
        public bool IsPrimaryKey { get; set; }
        public bool IsForeignKey { get; set; }
        public ExtensionDataObject ExtensionData { get; set; }
    }

    /// <summary>XMZADD 20260901 保存关系两端和外键名组成的稳定结构身份，避免关系事件依赖显示文本定位。</summary>
    public sealed class RelationStructurePayload : IExtensibleDataObject
    {
        public string ForeignKeyName { get; set; }
        public string ParentSchemaName { get; set; }
        public string ParentTableName { get; set; }
        public string ParentFieldName { get; set; }
        public string ChildSchemaName { get; set; }
        public string ChildTableName { get; set; }
        public string ChildFieldName { get; set; }
        public ExtensionDataObject ExtensionData { get; set; }
    }

    /// <summary>XMZADD 20260901 聚合一次用户保存产生的本地覆盖和共享修改事件。</summary>
    public sealed class DictionaryChangeBatch : IExtensibleDataObject
    {
        /// <summary>XMZADD 20260901 初始化可供传统循环直接追加内容的字典修改批次。</summary>
        public DictionaryChangeBatch()
        {
            Overrides = new List<DictionaryOverride>();
            Operations = new List<DictionaryChangeOperation>();
        }

        public string BatchId { get; set; }
        public string AuthorGitHubUserId { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public IList<DictionaryOverride> Overrides { get; set; }
        public IList<DictionaryChangeOperation> Operations { get; set; }
        public ExtensionDataObject ExtensionData { get; set; }
    }

    /// <summary>XMZADD 20260901 保存一个共享修订内按批次组织的增量字典事件。</summary>
    public sealed class DictionaryRevisionPackage
    {
        /// <summary>XMZADD 20260901 初始化可供同步流程按顺序追加批次的修订包。</summary>
        public DictionaryRevisionPackage()
        {
            Batches = new List<DictionaryChangeBatch>();
        }

        public int FormatVersion { get; set; }
        public long Revision { get; set; }
        public DateTime GeneratedAtUtc { get; set; }
        public IList<DictionaryChangeBatch> Batches { get; set; }
    }

    /// <summary>XMZADD 20260903 标识一次共享字典同步正在上传本机修改或获取远程规范快照。</summary>
    public enum DictionarySyncStage
    {
        UploadingChanges = 0,
        FetchingRemoteDictionary = 1
    }

    /// <summary>XMZADD 20260901 标识离线字典修改从待提交到完成或失败的本地状态。</summary>
    public enum PendingOperationStatus
    {
        Pending = 0,
        Submitted = 1,
        Completed = 2,
        Failed = 3,
        Rejected = 4,
        Superseded = 5
    }

    /// <summary>XMZADD 20260901 保存一个本地待上传批次及其重试、提交和失败诊断状态。</summary>
    public sealed class PendingDictionaryOperation
    {
        public string OperationId { get; set; }
        public DictionaryChangeBatch Batch { get; set; }
        public PendingOperationStatus Status { get; set; }
        public int AttemptCount { get; set; }
        public DateTime? NextAttemptAtUtc { get; set; }
        public int? GitHubIssueNumber { get; set; }
        public string LastError { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
    }

    /// <summary>XMZADD 20260901 保存本机可重建缓存对应的共享修订游标。</summary>
    public sealed class DictionarySyncState
    {
        public string StateKey { get; set; }
        public long Revision { get; set; }
        public string ManifestHash { get; set; }
        public string LocalPayloadHash { get; set; }
        public DateTime? LastSyncAtUtc { get; set; }
        public string LastSuccessfulOperationId { get; set; }
    }

    /// <summary>XMZADD 20260901 记录对象从规范快照排除的稳定键和业务原因。</summary>
    public sealed class ExcludedObjectRecord
    {
        public string ObjectKey { get; set; }
        public string ReasonCode { get; set; }
        public string Reason { get; set; }
        public DictionaryTableCategory Category { get; set; }
        public long ApproximateRowCount { get; set; }
        public DateTime ExcludedAtUtc { get; set; }
    }

    /// <summary>XMZADD 20260901 保存 GitHub 返回的非凭据用户身份信息用于事件署名。</summary>
    public sealed class GitHubUserIdentity
    {
        public string GitHubUserId { get; set; }
        public string Login { get; set; }
        public string DisplayName { get; set; }
    }

    /// <summary>XMZADD 20260901 描述规范快照的位置、修订和完整性校验值。</summary>
    public sealed class SnapshotManifest
    {
        public int FormatVersion { get; set; }
        public long Revision { get; set; }
        public string SnapshotSha256 { get; set; }
        public string SnapshotPath { get; set; }
        public DateTime GeneratedAtUtc { get; set; }
    }
}
