using System;
using System.Collections.Generic;

namespace SHB.EosDataDictionary.Models
{
    /// <summary>XMZADD 20260831 标识可接入的 AI 服务协议，便于 DeepSeek 和公司内部兼容服务共用配置方式。</summary>
    public enum AiProviderProtocol
    {
        OpenAiCompatible
    }

    /// <summary>XMZADD 20260831 标识 AI 推测任务的执行进度，避免加载数据库结构时等待外部接口完成。</summary>
    public enum AiInferenceTaskStatus
    {
        Pending,
        Running,
        Succeeded,
        Failed,
        Paused,
        Canceled
    }

    /// <summary>XMZADD 20260831 保存 AI 对表或字段的推测结果，为新手提供可追溯的业务解释参考。</summary>
    public sealed class AiExplanationResult
    {
        /// <summary>XMZADD 20260831 初始化候选项和证据集合，确保 AI 返回内容可逐项展示和保存。</summary>
        public AiExplanationResult()
        {
            Alternatives = new List<string>();
            Evidence = new List<EvidenceItem>();
        }

        public string ScopeKey { get; set; }
        public string ObjectName { get; set; }
        public string FieldName { get; set; }
        public string ChineseName { get; set; }
        public string BusinessMeaning { get; set; }
        public string Usage { get; set; }
        public int ConfidenceScore { get; set; }
        public IList<string> Alternatives { get; set; }
        public IList<EvidenceItem> Evidence { get; set; }
        public string ProviderId { get; set; }
        public string ProviderName { get; set; }
        public string Model { get; set; }
        public string ConfigurationVersion { get; set; }
        public DateTime GeneratedAt { get; set; }
    }

    /// <summary>XMZADD 20260831 保存一个 AI 服务商的调用配置，后续由本地配置服务负责加密持久化密钥。</summary>
    public sealed class AiProviderConfiguration
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public AiProviderProtocol Protocol { get; set; }
        public string Endpoint { get; set; }
        public string Model { get; set; }
        public string ApiKey { get; set; }
        public bool IsDefault { get; set; }
        public bool IsEnabled { get; set; }
        public int TimeoutSeconds { get; set; }
        public int MaxConcurrency { get; set; }
        public int BatchSize { get; set; }
        public string ConfigurationVersion { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    /// <summary>XMZADD 20260831 保存待解释对象的队列状态，以便新字段发现后自动安排 AI 推测。</summary>
    public sealed class AiInferenceTask
    {
        public string Id { get; set; }
        public string ScopeKey { get; set; }
        public string ObjectName { get; set; }
        public string FieldName { get; set; }
        public AiInferenceTaskStatus Status { get; set; }
        public string ContextFingerprint { get; set; }
        public string ProviderId { get; set; }
        public int RetryCount { get; set; }
        public string FailureMessage { get; set; }
        public DateTime EnqueuedAt { get; set; }
        public DateTime? StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
    }
}
