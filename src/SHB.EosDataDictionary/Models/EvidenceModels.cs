using System.Collections.Generic;
using System.Runtime.Serialization;

namespace SHB.EosDataDictionary.Models
{
    /// <summary>XMZADD 20260828 描述字典值的可信度，避免把推测内容展示成确定内容。</summary>
    public enum ConfidenceStatus
    {
        Confirmed,
        DatabaseEvidence,
        CodeEvidence,
        Guessed,
        GuessedConflict,
        PendingConfirmation,
        AiGuessed,
        LocalOverride,
        KnowledgeBaseEvidence
    }

    /// <summary>XMZADD 20260831 定义自动结果与人工维护的业务优先级，确保刷新后仍以人工确认内容为准。</summary>
    public enum MetadataPriority
    {
        PendingConfirmation = 0,
        NamingRule = 10,
        AiGuessed = 20,
        ConfirmedGlossary = 30,
        CodeEvidence = 40,
        SqlUsageEvidence = 50,
        KnowledgeBaseEvidence = 60,
        DatabaseEvidence = 70,
        LocalOverride = 100
    }

    /// <summary>XMZADD 20260828 保存表字段中文解释的来源证据。</summary>
    public sealed class EvidenceItem : IExtensibleDataObject
    {
        public string SourceType { get; set; }
        public string SourcePath { get; set; }
        public int SourceLine { get; set; }
        public string RuleName { get; set; }
        public string RawValue { get; set; }
        public string OriginalText { get; set; }
        public string Explanation { get; set; }
        public ExtensionDataObject ExtensionData { get; set; }
    }

    /// <summary>XMZADD 20260828 统一保存展示值、可信度和自动结果。</summary>
    [DataContract]
    public sealed class MetadataValue
    {
        [DataMember(EmitDefaultValue = false, Order = 1)]
        public string Value { get; set; }
        [DataMember(EmitDefaultValue = false, Order = 2)]
        public string Description { get; set; }
        [DataMember(EmitDefaultValue = false, Order = 3)]
        public ConfidenceStatus Status { get; set; }
        [DataMember(EmitDefaultValue = false, Order = 4)]
        public int ConfidenceScore { get; set; }
        [DataMember(EmitDefaultValue = false, Order = 5)]
        public string SourceType { get; set; }
        [DataMember(EmitDefaultValue = false, Order = 6)]
        public string SourceSummary { get; set; }
        [DataMember(EmitDefaultValue = false, Order = 7)]
        public string OriginalAutomaticValue { get; set; }
        [DataMember(EmitDefaultValue = false, Order = 8)]
        public bool IsManualOverride { get; set; }
        [DataMember(EmitDefaultValue = false, Order = 9)]
        public bool IsLocked { get; set; }
        [DataMember(EmitDefaultValue = false, Order = 10)]
        public IList<EvidenceItem> Evidence { get; set; }
    }

    /// <summary>XMZADD 20260828 表示一个待合并的自动或人工候选值。</summary>
    public sealed class MetadataCandidate
    {
        public MetadataCandidate(string value, ConfidenceStatus status, string sourceSummary)
            : this(value, status, sourceSummary, null)
        {
        }

        /// <summary>XMZADD 20260901 创建带原始证据的名称候选，使等号清理不丢失审阅依据。</summary>
        public MetadataCandidate(string value, ConfidenceStatus status, string sourceSummary, EvidenceItem evidence)
            : this(value, status, sourceSummary, evidence, 0, null)
        {
        }

        /// <summary>XMZADD 20260915 创建保留源码分级的名称候选，使权威 XML 摘要不会在富化边界降级。</summary>
        public MetadataCandidate(string value, ConfidenceStatus status, string sourceSummary,
            EvidenceItem evidence, int confidenceScore, string sourceType)
        {
            Value = value;
            Status = status;
            SourceSummary = sourceSummary;
            Evidence = evidence;
            ConfidenceScore = confidenceScore;
            SourceType = sourceType;
        }

        public string Value { get; private set; }
        public ConfidenceStatus Status { get; private set; }
        public string SourceSummary { get; private set; }
        public EvidenceItem Evidence { get; private set; }
        public int ConfidenceScore { get; private set; }
        public string SourceType { get; private set; }
    }
}
