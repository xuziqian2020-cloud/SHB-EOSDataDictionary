using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using SHB.EosDataDictionary.Models;

namespace SHB.EosDataDictionary.Services
{
    /// <summary>XMZADD 20260831 定义 AI 推测服务与外部大模型之间的可替换调用边界，便于本地测试时隔离网络请求。</summary>
    public interface IAiChatClient
    {
        /// <summary>XMZADD 20260831 向指定服务商提交系统提示和业务上下文，并返回模型原始文本结果。</summary>
        string Complete(AiProviderConfiguration provider, string systemPrompt, string userPrompt);
    }

    /// <summary>XMZADD 20260831 将 EOS 表字段的现有结构和证据交给 AI 推测，并生成带来源标识的中文业务解释。</summary>
    public sealed class AiInferenceService
    {
        public const int MaximumBatchTargetCount = 50;
        private const int MaximumAiScalarLength = 256;
        private const int MaximumAiEvidenceCount = 4;
        private const int MaximumAiEvidenceItemLength = 240;
        private const int MaximumAiEvidenceSummaryLength = 960;
        private const int MaximumSinglePromptBytes = 32 * 1024;
        private const int MaximumBatchPromptBytes = 128 * 1024;
        private const int MaximumKnowledgeContextCount = 4;
        private const int MaximumKnowledgeContextItemLength = 240;
        private const int MaximumKnowledgeContextLength = 960;
        private const string RedactedAiText = "[已移除敏感内容]";
        private readonly IAiChatClient _chatClient;
        private readonly EosKnowledgeBaseTranslationService _knowledgeBase;
        private static readonly Regex WindowsAbsolutePathRegex = new Regex(
            @"\b[A-Z]:\\(?:[^\\\s\""<>|]+\\)*(?<file>[^\\\s\""<>|]+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex UncAbsolutePathRegex = new Regex(
            @"\\\\(?:[^\\\s\""<>|]+\\)+(?<file>[^\\\s\""<>|]+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>XMZADD 20260831 使用 OpenAI Compatible 协议客户端创建可供 DeepSeek 与企业内部模型共用的推测服务。</summary>
        public AiInferenceService()
            : this(new OpenAiCompatibleChatClient(), null)
        {
        }

        /// <summary>XMZADD 20260831 使用指定聊天客户端创建推测服务，使自动解释的解析逻辑可以独立于外部网络进行验证。</summary>
        public AiInferenceService(IAiChatClient chatClient)
            : this(chatClient, null)
        {
        }

        /// <summary>XMZADD 20260901 注入聊天客户端和受限 EOS 知识库翻译器，使精确或模糊条目只以短上下文参与 AI 推理。</summary>
        public AiInferenceService(IAiChatClient chatClient, EosKnowledgeBaseTranslationService knowledgeBase)
        {
            if (chatClient == null)
            {
                throw new ArgumentNullException("chatClient");
            }

            _chatClient = chatClient;
            _knowledgeBase = knowledgeBase;
        }

        /// <summary>XMZADD 20260831 基于表、字段、物理约束与已有证据生成中文推测，并将结果明确标记为 AI 推测。</summary>
        public AiExplanationResult Infer(AiProviderConfiguration provider, string scopeKey,
            TableMetadata table, FieldMetadata field)
        {
            ValidateInput(provider, table, field);

            string knowledgeContext = BuildFieldKnowledgeContext(table, field);
            string userPrompt = BuildUserPrompt(scopeKey, table, field, knowledgeContext);
            EnsurePromptSize(userPrompt, MaximumSinglePromptBytes);
            string responseText = _chatClient.Complete(
                provider, BuildSystemPrompt(), userPrompt);
            AiResponseDocument response = ParseResponse(responseText);

            // AI 输出仅作为可追溯的推测证据，不能覆盖后续人工确认结果。
            var result = new AiExplanationResult
            {
                ScopeKey = scopeKey ?? string.Empty,
                ObjectName = table.ObjectName,
                FieldName = field.FieldName,
                ChineseName = response.ChineseName,
                BusinessMeaning = response.BusinessMeaning,
                Usage = response.Usage,
                ConfidenceScore = NormalizeConfidenceScore(response.ConfidenceScore),
                Alternatives = response.Alternatives ?? new List<string>(),
                ProviderId = provider.Id,
                ProviderName = provider.Name,
                Model = provider.Model,
                ConfigurationVersion = provider.ConfigurationVersion,
                GeneratedAt = DateTime.Now
            };

            result.Evidence.Add(new EvidenceItem
            {
                SourceType = "AI推测",
                SourcePath = provider.Endpoint,
                RuleName = "AiInference",
                RawValue = field.FieldName,
                Explanation = response.Evidence
            });
            return result;
        }

        /// <summary>XMZADD 20260831 基于物理表名、模块、实体和字段轮廓生成表级中文业务推测，使未知表也能直接显示参考解释。</summary>
        public AiExplanationResult InferTable(AiProviderConfiguration provider, string scopeKey, TableMetadata table)
        {
            if (provider == null)
            {
                throw new ArgumentNullException("provider");
            }
            if (!provider.IsEnabled)
            {
                throw new InvalidOperationException("当前 AI 服务商已禁用，不能执行自动推测。");
            }
            if (table == null || string.IsNullOrWhiteSpace(table.ObjectName))
            {
                throw new ArgumentException("必须提供具有物理表名的表结构。", "table");
            }

            string knowledgeContext = BuildTableKnowledgeContext(table);
            string userPrompt = BuildTableUserPrompt(scopeKey, table, knowledgeContext);
            EnsurePromptSize(userPrompt, MaximumSinglePromptBytes);
            AiResponseDocument response = ParseResponse(_chatClient.Complete(
                provider, BuildSystemPrompt(), userPrompt));
            var result = new AiExplanationResult
            {
                ScopeKey = scopeKey ?? string.Empty,
                ObjectName = table.ObjectName,
                FieldName = string.Empty,
                ChineseName = response.ChineseName,
                BusinessMeaning = response.BusinessMeaning,
                Usage = response.Usage,
                ConfidenceScore = NormalizeConfidenceScore(response.ConfidenceScore),
                Alternatives = response.Alternatives ?? new List<string>(),
                ProviderId = provider.Id,
                ProviderName = provider.Name,
                Model = provider.Model,
                ConfigurationVersion = provider.ConfigurationVersion,
                GeneratedAt = DateTime.Now
            };
            result.Evidence.Add(new EvidenceItem
            {
                SourceType = "AI推测",
                SourcePath = provider.Endpoint,
                RuleName = "AiTableInference",
                RawValue = table.ObjectName,
                Explanation = response.Evidence
            });
            return result;
        }

        /// <summary>XMZADD 20260831 使用一次模型调用批量推测表和字段，并以客户端生成的批内标识安全关联返回结果。</summary>
        public IList<AiExplanationResult> InferBatch(AiProviderConfiguration provider, string scopeKey, IList<AiInferenceTarget> targets)
        {
            var emptyResults = new List<AiExplanationResult>();
            if (targets == null || targets.Count == 0)
            {
                return emptyResults;
            }

            ValidateBatchInput(provider, targets);
            IList<AiBatchRequestItem> requestItems = CreateBatchRequestItems(targets);
            string userPrompt = BuildBatchUserPrompt(scopeKey, requestItems);
            EnsurePromptSize(userPrompt, MaximumBatchPromptBytes);
            string responseText = _chatClient.Complete(provider, BuildBatchSystemPrompt(), userPrompt);
            AiBatchResponseDocument response;
            try
            {
                response = ParseBatchResponse(responseText);
            }
            catch (InvalidOperationException)
            {
                // 任一返回契约错误都必须丢弃整批，避免部分推测进入本地字典。
                return emptyResults;
            }

            return CreateBatchResults(provider, scopeKey, targets, response);
        }

        /// <summary>XMZADD 20260831 预先校验整批目标，避免一项无效结构导致已发送请求的其他目标无法安全回写。</summary>
        private static void ValidateBatchInput(AiProviderConfiguration provider, IList<AiInferenceTarget> targets)
        {
            if (provider == null)
            {
                throw new ArgumentNullException("provider");
            }
            if (!provider.IsEnabled)
            {
                throw new InvalidOperationException("当前 AI 服务商已禁用，不能执行自动推测。");
            }
            if (targets.Count > MaximumBatchTargetCount)
            {
                throw new ArgumentException("单次 AI 批量目标不能超过 50 项。", "targets");
            }

            for (int index = 0; index < targets.Count; index++)
            {
                AiInferenceTarget target = targets[index];
                if (target == null || target.Table == null || string.IsNullOrWhiteSpace(target.Table.ObjectName))
                {
                    throw new ArgumentException("批量推测目标必须包含物理表名。", "targets");
                }
                if (target.Field != null && string.IsNullOrWhiteSpace(target.Field.FieldName))
                {
                    throw new ArgumentException("批量字段推测目标必须包含物理字段名。", "targets");
                }
            }
        }

        /// <summary>XMZADD 20260831 为每个目标生成稳定批内标识并整理模型所需结构上下文，不包含服务商密钥等配置机密。</summary>
        private IList<AiBatchRequestItem> CreateBatchRequestItems(IList<AiInferenceTarget> targets)
        {
            var result = new List<AiBatchRequestItem>();
            for (int index = 0; index < targets.Count; index++)
            {
                AiInferenceTarget target = targets[index];
                TableMetadata table = target.Table;
                FieldMetadata field = target.Field;
                result.Add(new AiBatchRequestItem
                {
                    Id = CreateBatchTargetId(index),
                    TargetType = field == null ? "table" : "field",
                    ObjectName = SanitizeAiText(table.ObjectName, MaximumAiScalarLength),
                    FieldName = field == null ? string.Empty : SanitizeAiText(field.FieldName, MaximumAiScalarLength),
                    ObjectType = SanitizeAiText(table.ObjectType, MaximumAiScalarLength),
                    DataType = field == null ? string.Empty : SanitizeAiText(field.DataType, MaximumAiScalarLength),
                    LengthText = field == null ? string.Empty : SanitizeAiText(field.LengthText, MaximumAiScalarLength),
                    IsRequired = field != null && field.IsRequired,
                    IsPrimaryKey = field != null && field.IsPrimaryKey,
                    IsForeignKey = field != null && field.IsForeignKey,
                    KnownChineseName = SanitizeAiText(GetMetadataValue(field == null ? table.ChineseName : field.ChineseName), MaximumAiScalarLength),
                    KnownModuleName = SanitizeAiText(GetMetadataValue(table.ModuleName), MaximumAiScalarLength),
                    KnownEntityName = SanitizeAiText(GetMetadataValue(field == null ? table.EntityName : field.EntityPropertyName), MaximumAiScalarLength),
                    KnownBusinessMeaning = SanitizeAiText(GetMetadataValue(field == null ? table.BusinessMeaning : field.BusinessMeaning), MaximumAiScalarLength),
                    KnownUsage = field == null ? string.Empty : SanitizeAiText(GetMetadataValue(field.Usage), MaximumAiScalarLength),
                    KnownEnumName = field == null ? string.Empty : SanitizeAiText(GetMetadataValue(field.EnumName), MaximumAiScalarLength),
                    KnownRelationSummary = field == null ? string.Empty : SanitizeAiText(GetMetadataValue(field.RelationSummary), MaximumAiScalarLength),
                    FieldOutline = field == null ? SanitizeAiText(BuildFieldOutline(table), MaximumAiEvidenceSummaryLength) : string.Empty,
                    EvidenceSummary = BuildBatchEvidenceSummary(table, field),
                    KnowledgeContext = field == null ? BuildTableKnowledgeContext(table) : BuildFieldKnowledgeContext(table, field)
                });
            }

            return result;
        }

        /// <summary>XMZADD 20260831 汇总表级目标的字段轮廓，使模型推测未知表名时仍可参考主键、编号等结构线索。</summary>
        private static string BuildFieldOutline(TableMetadata table)
        {
            if (table == null || table.Fields == null || table.Fields.Count == 0)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            for (int index = 0; index < table.Fields.Count && index < 12; index++)
            {
                FieldMetadata field = table.Fields[index];
                if (field == null || string.IsNullOrWhiteSpace(field.FieldName))
                {
                    continue;
                }
                if (builder.Length > 0)
                {
                    builder.Append("; ");
                }
                builder.Append(field.FieldName).Append(" ").Append(field.DataType ?? string.Empty).Append(" ").Append(field.LengthText ?? string.Empty);
            }
            return builder.ToString().Trim();
        }

        /// <summary>XMZADD 20260831 提取有限长度的代码和结构证据作为批量上下文，避免大快照把完整证据正文发送给外部模型。</summary>
        private static string BuildBatchEvidenceSummary(TableMetadata table, FieldMetadata field)
        {
            var builder = new StringBuilder();
            int evidenceCount = 0;
            AppendBatchEvidence(builder, table == null ? null : table.ChineseName, ref evidenceCount);
            AppendBatchEvidence(builder, table == null ? null : table.BusinessMeaning, ref evidenceCount);
            if (field != null)
            {
                AppendBatchEvidence(builder, field.ChineseName, ref evidenceCount);
                AppendBatchEvidence(builder, field.EntityPropertyName, ref evidenceCount);
                AppendBatchEvidence(builder, field.RelationSummary, ref evidenceCount);
            }
            return builder.ToString();
        }

        /// <summary>XMZADD 20260831 追加少量可追溯来源说明，保留模型判断依据同时控制单批提示词容量。</summary>
        private static void AppendBatchEvidence(StringBuilder builder, MetadataValue metadata, ref int evidenceCount)
        {
            if (metadata == null || metadata.Evidence == null || evidenceCount >= MaximumAiEvidenceCount ||
                builder.Length >= MaximumAiEvidenceSummaryLength)
            {
                return;
            }

            for (int index = 0; index < metadata.Evidence.Count && evidenceCount < MaximumAiEvidenceCount; index++)
            {
                EvidenceItem item = metadata.Evidence[index];
                if (item == null)
                {
                    continue;
                }

                string explanation = string.IsNullOrWhiteSpace(item.Explanation) ? item.RawValue : item.Explanation;
                explanation = SanitizeAiText(explanation, MaximumAiEvidenceItemLength);
                if (string.IsNullOrWhiteSpace(explanation) || string.Equals(explanation, RedactedAiText, StringComparison.Ordinal))
                {
                    continue;
                }
                string sourceType = SanitizeAiText(item.SourceType, 64);
                if (string.IsNullOrWhiteSpace(sourceType) || string.Equals(sourceType, RedactedAiText, StringComparison.Ordinal))
                {
                    sourceType = "未知来源";
                }
                string value = sourceType + "：" + explanation;
                int separatorLength = builder.Length > 0 ? 2 : 0;
                int remaining = MaximumAiEvidenceSummaryLength - builder.Length - separatorLength;
                if (remaining <= 0)
                {
                    return;
                }
                if (value.Length > remaining)
                {
                    value = value.Substring(0, remaining);
                }
                if (builder.Length > 0)
                {
                    builder.Append("; ");
                }
                builder.Append(value);
                evidenceCount++;
            }
        }

        /// <summary>XMZADD 20260831 使用固定序号生成批内标识，保证模型乱序返回时仍可精确关联而不依赖其复述对象名称。</summary>
        private static string CreateBatchTargetId(int index)
        {
            return "target-" + (index + 1).ToString("D4");
        }

        /// <summary>XMZADD 20260831 生成独立的批量 JSON 输出约束，避免破坏既有单项推测的 JSON 契约。</summary>
        private static string BuildBatchSystemPrompt()
        {
            return "你是企业数据库数据字典分析助手。只能依据输入结构推测，不得将推测表述为已确认事实。" +
                   "只返回一个 JSON 对象，且只包含 items 属性；items 是数组。" +
                   "数组每项只能包含 id、chineseName、businessMeaning、usage、confidenceScore、alternatives、evidence。" +
                   "id 必须原样返回输入 id；chineseName、businessMeaning、usage、evidence 必须为中文字符串；" +
                   "confidenceScore 是 0 到 100 的整数；alternatives 是字符串数组。" +
                   "不得返回对象名、字段名、Markdown、代码块或额外说明。";
        }

        /// <summary>XMZADD 20260831 将多项表字段上下文封装为批量 JSON 请求体，确保响应只能依据批内标识关联。</summary>
        private static string BuildBatchUserPrompt(string scopeKey, IList<AiBatchRequestItem> items)
        {
            var request = new AiBatchRequestDocument
            {
                ScopeKey = SanitizeAiText(scopeKey, MaximumAiScalarLength),
                Items = new List<AiBatchRequestItem>()
            };
            for (int index = 0; index < items.Count; index++)
            {
                request.Items.Add(items[index]);
            }

            using (var stream = new MemoryStream())
            {
                var serializer = new DataContractJsonSerializer(typeof(AiBatchRequestDocument));
                serializer.WriteObject(stream, request);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        /// <summary>XMZADD 20260831 解析批量响应信封并保留后续原子校验所需的每项标识与业务解释。</summary>
        private static AiBatchResponseDocument ParseBatchResponse(string responseText)
        {
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(ExtractJson(responseText));
                using (var stream = new MemoryStream(bytes))
                {
                    var serializer = new DataContractJsonSerializer(typeof(AiBatchResponseDocument));
                    return (AiBatchResponseDocument)serializer.ReadObject(stream);
                }
            }
            catch (SerializationException exception)
            {
                throw new InvalidOperationException("AI 返回内容不是约定的批量 JSON 数据字典格式。", exception);
            }
        }

        /// <summary>XMZADD 20260831 校验每个响应标识与请求一一对应后统一生成结果，保证任何缺失或异常项都不会产生部分落库数据。</summary>
        private static IList<AiExplanationResult> CreateBatchResults(AiProviderConfiguration provider, string scopeKey,
            IList<AiInferenceTarget> targets, AiBatchResponseDocument response)
        {
            var emptyResults = new List<AiExplanationResult>();
            if (response == null || response.Items == null || response.Items.Count != targets.Count)
            {
                return emptyResults;
            }

            var responseById = new Dictionary<string, AiBatchResponseItem>(StringComparer.Ordinal);
            for (int index = 0; index < response.Items.Count; index++)
            {
                AiBatchResponseItem item = response.Items[index];
                if (item == null || string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.ChineseName) ||
                    !IsKnownBatchTargetId(item.Id, targets.Count) || responseById.ContainsKey(item.Id))
                {
                    // 模型返回的对象名或字段名不能作为回退身份，异常标识必须使整批失效。
                    return emptyResults;
                }
                responseById.Add(item.Id, item);
            }

            var results = new List<AiExplanationResult>();
            for (int index = 0; index < targets.Count; index++)
            {
                AiBatchResponseItem item;
                if (!responseById.TryGetValue(CreateBatchTargetId(index), out item))
                {
                    return emptyResults;
                }

                AiInferenceTarget target = targets[index];
                results.Add(CreateBatchResult(provider, scopeKey, target, item));
            }

            return results;
        }

        /// <summary>XMZADD 20260831 仅接受当前请求生成的精确批内标识，阻止不同大小写或外来标识被误关联到业务对象。</summary>
        private static bool IsKnownBatchTargetId(string id, int targetCount)
        {
            const string prefix = "target-";
            if (string.IsNullOrWhiteSpace(id) || !id.StartsWith(prefix, StringComparison.Ordinal))
            {
                return false;
            }

            string sequenceText = id.Substring(prefix.Length);
            int sequence;
            if (sequenceText.Length != 4 ||
                !int.TryParse(sequenceText, NumberStyles.None, CultureInfo.InvariantCulture, out sequence) ||
                sequence < 1 || sequence > targetCount)
            {
                return false;
            }

            return string.Equals(id, CreateBatchTargetId(sequence - 1), StringComparison.Ordinal);
        }

        /// <summary>XMZADD 20260831 将已通过标识校验的批量项补全为可追溯 AI 结果，统一保留服务商信息与 AI 证据。</summary>
        private static AiExplanationResult CreateBatchResult(AiProviderConfiguration provider, string scopeKey,
            AiInferenceTarget target, AiBatchResponseItem response)
        {
            var result = new AiExplanationResult
            {
                ScopeKey = scopeKey ?? string.Empty,
                ObjectName = target.Table.ObjectName,
                FieldName = target.Field == null ? string.Empty : target.Field.FieldName,
                ChineseName = response.ChineseName,
                BusinessMeaning = response.BusinessMeaning ?? string.Empty,
                Usage = response.Usage ?? string.Empty,
                ConfidenceScore = NormalizeConfidenceScore(response.ConfidenceScore),
                Alternatives = response.Alternatives ?? new List<string>(),
                ProviderId = provider.Id,
                ProviderName = provider.Name,
                Model = provider.Model,
                ConfigurationVersion = provider.ConfigurationVersion,
                GeneratedAt = DateTime.Now
            };
            result.Evidence.Add(new EvidenceItem
            {
                SourceType = "AI推测",
                SourcePath = provider.Endpoint,
                RuleName = "AiBatchInference",
                RawValue = target.Field == null ? target.Table.ObjectName : target.Field.FieldName,
                Explanation = response.Evidence ?? string.Empty
            });
            return result;
        }

        /// <summary>XMZADD 20260831 校验推测所需的服务商、表和字段标识，避免将无法追溯的内容发送给外部模型。</summary>
        private static void ValidateInput(AiProviderConfiguration provider, TableMetadata table, FieldMetadata field)
        {
            if (provider == null)
            {
                throw new ArgumentNullException("provider");
            }
            if (!provider.IsEnabled)
            {
                throw new InvalidOperationException("当前 AI 服务商已禁用，不能执行自动推测。");
            }
            if (table == null || string.IsNullOrWhiteSpace(table.ObjectName))
            {
                throw new ArgumentException("必须提供具有物理表名的表结构。", "table");
            }
            if (field == null || string.IsNullOrWhiteSpace(field.FieldName))
            {
                throw new ArgumentException("必须提供具有物理字段名的字段结构。", "field");
            }
        }

        /// <summary>XMZADD 20260831 生成约束模型输出格式的系统提示，确保自动结果可保存为字段字典而不是自由文本。</summary>
        private static string BuildSystemPrompt()
        {
            return "你是企业数据库数据字典分析助手。只能依据输入的表结构、字段结构和证据进行推测，不能把推测说成已确认事实。" +
                   "请只返回一个 JSON 对象，且只能包含 chineseName、businessMeaning、usage、confidenceScore、alternatives、evidence 六个属性。" +
                   "chineseName、businessMeaning、usage、evidence 必须是中文字符串；confidenceScore 是 0 到 100 的整数；alternatives 是字符串数组。" +
                   "evidence 要简洁说明字段拆解、代码证据或结构证据的依据。不要使用 Markdown、代码块或任何额外说明。";
        }

        /// <summary>XMZADD 20260831 组合物理字段属性与已有中文证据，使模型推测能够向新手说明其业务判断依据。</summary>
        private static string BuildUserPrompt(string scopeKey, TableMetadata table, FieldMetadata field,
            string knowledgeContext)
        {
            var builder = new StringBuilder();
            builder.AppendLine("请推测以下 EOS 数据库字段的业务含义。结果仅供参考，状态会标记为 AI推测。");
            AppendLine(builder, "作用域", scopeKey);
            AppendLine(builder, "物理表名", table.ObjectName);
            AppendLine(builder, "已知表中文名", GetMetadataValue(table.ChineseName));
            AppendLine(builder, "已知模块", GetMetadataValue(table.ModuleName));
            AppendLine(builder, "已知实体", GetMetadataValue(table.EntityName));
            AppendLine(builder, "表业务说明", GetMetadataValue(table.BusinessMeaning));
            AppendLine(builder, "物理字段名", field.FieldName);
            AppendLine(builder, "已知字段中文名", GetMetadataValue(field.ChineseName));
            AppendLine(builder, "实体属性", GetMetadataValue(field.EntityPropertyName));
            AppendLine(builder, "物理类型", field.DataType);
            AppendLine(builder, "长度或精度", field.LengthText);
            AppendLine(builder, "是否必填", field.IsRequired ? "是" : "否");
            AppendLine(builder, "是否主键", field.IsPrimaryKey ? "是" : "否");
            AppendLine(builder, "是否外键", field.IsForeignKey ? "是" : "否");
            AppendLine(builder, "枚举名称", GetMetadataValue(field.EnumName));
            AppendLine(builder, "关联说明", GetMetadataValue(field.RelationSummary));
            AppendLine(builder, "已有字段业务说明", GetMetadataValue(field.BusinessMeaning));
            AppendLine(builder, "已有字段用途", GetMetadataValue(field.Usage));
            AppendLine(builder, "EOS知识库受限上下文", knowledgeContext, MaximumKnowledgeContextLength);
            int evidenceCount = 0;
            int evidenceLength = 0;
            AppendEvidence(builder, "表代码或结构证据", table.ChineseName, ref evidenceCount, ref evidenceLength);
            AppendEvidence(builder, "字段代码或结构证据", field.ChineseName, ref evidenceCount, ref evidenceLength);
            AppendEvidence(builder, "实体属性证据", field.EntityPropertyName, ref evidenceCount, ref evidenceLength);
            AppendEvidence(builder, "关联证据", field.RelationSummary, ref evidenceCount, ref evidenceLength);
            return builder.ToString();
        }

        /// <summary>XMZADD 20260831 组合表名、实体、模块和字段轮廓，让模型在没有人工字典时也能提供可追溯的表级推测。</summary>
        private static string BuildTableUserPrompt(string scopeKey, TableMetadata table, string knowledgeContext)
        {
            var builder = new StringBuilder();
            builder.AppendLine("请推测以下 EOS 数据库表或视图的业务中文名称和用途。结果仅供参考，状态会标记为 AI推测。");
            AppendLine(builder, "作用域", scopeKey);
            AppendLine(builder, "物理对象名", table.ObjectName);
            AppendLine(builder, "对象类型", table.ObjectType);
            AppendLine(builder, "已知中文名", GetMetadataValue(table.ChineseName));
            AppendLine(builder, "模块", GetMetadataValue(table.ModuleName));
            AppendLine(builder, "实体", GetMetadataValue(table.EntityName));
            if (table.Fields != null)
            {
                for (int i = 0; i < table.Fields.Count && i < 12; i++)
                {
                    FieldMetadata field = table.Fields[i];
                    if (field != null)
                    {
                        AppendLine(builder, "字段轮廓", field.FieldName + " " + field.DataType + " " + field.LengthText);
                    }
                }
            }
            AppendLine(builder, "EOS知识库受限上下文", knowledgeContext, MaximumKnowledgeContextLength);
            int evidenceCount = 0;
            int evidenceLength = 0;
            AppendEvidence(builder, "表代码或结构证据", table.ChineseName, ref evidenceCount, ref evidenceLength);
            return builder.ToString();
        }

        /// <summary>XMZADD 20260901 为单字段提取知识库精确候选或模糊消歧短上下文，禁止把模块全文发送给模型。</summary>
        private string BuildFieldKnowledgeContext(TableMetadata table, FieldMetadata field)
        {
            if (_knowledgeBase == null || table == null || field == null)
            {
                return string.Empty;
            }
            KnowledgeBaseTranslationResult translation = _knowledgeBase.TranslateField(
                table.SchemaName, table.ObjectName, field.FieldName, GetMetadataValue(table.ModuleName));
            var builder = new StringBuilder();
            int itemCount = 0;
            AppendKnowledgeTranslation(builder, translation, ref itemCount);
            return builder.ToString();
        }

        /// <summary>XMZADD 20260901 从有限字段轮廓收集表级知识上下文，并以统一总长度限制保护批量 AI 请求。</summary>
        private string BuildTableKnowledgeContext(TableMetadata table)
        {
            if (_knowledgeBase == null || table == null || table.Fields == null)
            {
                return string.Empty;
            }
            var builder = new StringBuilder();
            int itemCount = 0;
            for (int index = 0; index < table.Fields.Count && itemCount < MaximumKnowledgeContextCount; index++)
            {
                FieldMetadata field = table.Fields[index];
                if (field == null || string.IsNullOrWhiteSpace(field.FieldName))
                {
                    continue;
                }
                KnowledgeBaseTranslationResult translation = _knowledgeBase.TranslateField(
                    table.SchemaName, table.ObjectName, field.FieldName, GetMetadataValue(table.ModuleName));
                AppendKnowledgeTranslation(builder, translation, ref itemCount);
            }
            return builder.ToString();
        }

        /// <summary>XMZADD 20260901 将知识库候选与 AI 消歧上下文压缩到固定数量，保持精确条目和模糊条目的来源边界。</summary>
        private static void AppendKnowledgeTranslation(StringBuilder builder, KnowledgeBaseTranslationResult translation,
            ref int itemCount)
        {
            if (translation == null || itemCount >= MaximumKnowledgeContextCount)
            {
                return;
            }
            if (translation.Candidate != null && !string.IsNullOrWhiteSpace(translation.Candidate.Value))
            {
                AppendKnowledgeContextItem(builder, "精确候选：" + translation.Candidate.Value, ref itemCount);
            }
            if (translation.AiContext == null)
            {
                return;
            }
            for (int index = 0; index < translation.AiContext.Count && itemCount < MaximumKnowledgeContextCount; index++)
            {
                AppendKnowledgeContextItem(builder, "消歧依据：" + translation.AiContext[index], ref itemCount);
            }
        }

        /// <summary>XMZADD 20260901 追加单条脱敏短知识上下文并同时执行单项与总长度上限。</summary>
        private static void AppendKnowledgeContextItem(StringBuilder builder, string value, ref int itemCount)
        {
            string text = SanitizeKnowledgeContext(value);
            if (text.Length == 0 || builder.Length >= MaximumKnowledgeContextLength)
            {
                return;
            }
            int separatorLength = builder.Length > 0 ? 1 : 0;
            int remaining = MaximumKnowledgeContextLength - builder.Length - separatorLength;
            if (remaining <= 0)
            {
                return;
            }
            if (text.Length > remaining)
            {
                text = text.Substring(0, remaining);
            }
            if (builder.Length > 0)
            {
                builder.Append("；");
            }
            builder.Append(text);
            itemCount++;
        }

        /// <summary>XMZADD 20260901 移除控制字符和连接凭据形态并截断知识条目，避免本机知识正文越界进入外部模型。</summary>
        private static string SanitizeKnowledgeContext(string value)
        {
            string text = SanitizeAiText(value, MaximumKnowledgeContextItemLength);
            return string.Equals(text, RedactedAiText, StringComparison.Ordinal) ? string.Empty : text;
        }

        /// <summary>XMZADD 20260831 安全写入单项业务上下文，避免空值噪声干扰模型对真实字段含义的判断。</summary>
        private static void AppendLine(StringBuilder builder, string name, string value)
        {
            AppendLine(builder, name, value, MaximumAiScalarLength);
        }

        /// <summary>XMZADD 20260901 按指定上限写入脱敏业务上下文，使知识短文与普通结构属性共享安全规则但保留各自容量。</summary>
        private static void AppendLine(StringBuilder builder, string name, string value, int maximumLength)
        {
            string text = SanitizeAiText(value, maximumLength);
            if (!string.IsNullOrWhiteSpace(text))
            {
                builder.Append(name).Append("：").AppendLine(text);
            }
        }

        /// <summary>XMZADD 20260831 提取现有元数据的展示值，优先将人工或代码已知的中文信息作为 AI 推测上下文。</summary>
        private static string GetMetadataValue(MetadataValue value)
        {
            return value == null ? null : value.Value;
        }

        /// <summary>XMZADD 20260831 将已有来源证据简化为提示上下文，帮助模型区分代码事实与字段名称拆解推测。</summary>
        private static void AppendEvidence(StringBuilder builder, string name, MetadataValue metadata,
            ref int evidenceCount, ref int evidenceLength)
        {
            if (metadata == null || metadata.Evidence == null || evidenceCount >= MaximumAiEvidenceCount ||
                evidenceLength >= MaximumAiEvidenceSummaryLength)
            {
                return;
            }

            for (int index = 0; index < metadata.Evidence.Count && evidenceCount < MaximumAiEvidenceCount; index++)
            {
                EvidenceItem item = metadata.Evidence[index];
                if (item == null)
                {
                    continue;
                }

                string explanation = string.IsNullOrWhiteSpace(item.Explanation) ? item.RawValue : item.Explanation;
                explanation = SanitizeAiText(explanation, MaximumAiEvidenceItemLength);
                if (string.IsNullOrWhiteSpace(explanation) || string.Equals(explanation, RedactedAiText, StringComparison.Ordinal))
                {
                    continue;
                }
                string sourceType = SanitizeAiText(item.SourceType, 64);
                if (string.IsNullOrWhiteSpace(sourceType) || string.Equals(sourceType, RedactedAiText, StringComparison.Ordinal))
                {
                    sourceType = "未知来源";
                }
                string value = sourceType + " - " + explanation;
                int remaining = MaximumAiEvidenceSummaryLength - evidenceLength;
                if (value.Length > remaining)
                {
                    value = value.Substring(0, remaining);
                }
                if (value.Length == 0)
                {
                    return;
                }
                builder.Append(name).Append("：").AppendLine(value);
                evidenceLength += value.Length;
                evidenceCount++;
            }
        }

        /// <summary>XMZADD 20260901 统一清理 AI 单项与批量上下文中的控制字符、凭据形态和本机绝对路径，防止敏感环境信息离开本地。</summary>
        internal static string SanitizeAiText(string value, int maximumLength)
        {
            if (string.IsNullOrWhiteSpace(value) || maximumLength <= 0)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            bool previousWhitespace = false;
            for (int index = 0; index < value.Length; index++)
            {
                char current = value[index];
                UnicodeCategory category = char.GetUnicodeCategory(current);
                bool isUnsafeSeparator = category == UnicodeCategory.Control ||
                    category == UnicodeCategory.Format ||
                    category == UnicodeCategory.LineSeparator ||
                    category == UnicodeCategory.ParagraphSeparator;
                if (isUnsafeSeparator || char.IsWhiteSpace(current))
                {
                    if (!previousWhitespace && builder.Length > 0)
                    {
                        builder.Append(' ');
                    }
                    previousWhitespace = true;
                    continue;
                }
                builder.Append(current);
                previousWhitespace = false;
            }

            string text = builder.ToString().Trim();
            text = WindowsAbsolutePathRegex.Replace(text, match => match.Groups["file"].Value);
            text = UncAbsolutePathRegex.Replace(text, match => match.Groups["file"].Value);
            if (SensitiveTextGuard.ContainsCredentialShape(text))
            {
                return RedactedAiText;
            }

            return text.Length <= maximumLength ? text : text.Substring(0, maximumLength);
        }

        /// <summary>XMZADD 20260901 在外部调用前按 UTF-8 实际传输容量拒绝异常提示词，防止设置或快照污染造成超大请求。</summary>
        private static void EnsurePromptSize(string prompt, int maximumBytes)
        {
            if (Encoding.UTF8.GetByteCount(prompt ?? string.Empty) > maximumBytes)
            {
                throw new InvalidOperationException("AI 请求上下文超过安全容量，已取消本次推测。");
            }
        }

        /// <summary>XMZADD 20260831 清理模型偶尔附带的 Markdown 包装，确保后续仅对约定 JSON 契约进行解析。</summary>
        private static string ExtractJson(string responseText)
        {
            if (string.IsNullOrWhiteSpace(responseText))
            {
                throw new InvalidOperationException("AI 服务未返回字段推测结果。");
            }

            string value = responseText.Trim();
            if (!value.StartsWith("```", StringComparison.Ordinal))
            {
                return value;
            }

            int firstLineEnd = value.IndexOf('\n');
            int lastFence = value.LastIndexOf("```", StringComparison.Ordinal);
            if (firstLineEnd < 0 || lastFence <= firstLineEnd)
            {
                throw new InvalidOperationException("AI 返回的 JSON 代码块格式不完整。");
            }
            return value.Substring(firstLineEnd + 1, lastFence - firstLineEnd - 1).Trim();
        }

        /// <summary>XMZADD 20260831 按固定数据契约解析模型 JSON，避免自由文本进入本地数据字典并被误认为人工确认内容。</summary>
        private static AiResponseDocument ParseResponse(string responseText)
        {
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(ExtractJson(responseText));
                using (var stream = new MemoryStream(bytes))
                {
                    var serializer = new DataContractJsonSerializer(typeof(AiResponseDocument));
                    var response = (AiResponseDocument)serializer.ReadObject(stream);
                    if (response == null || string.IsNullOrWhiteSpace(response.ChineseName))
                    {
                        throw new InvalidOperationException("AI 返回的 JSON 缺少 chineseName。");
                    }
                    return response;
                }
            }
            catch (SerializationException exception)
            {
                throw new InvalidOperationException("AI 返回内容不是约定的 JSON 数据字典格式。", exception);
            }
        }

        /// <summary>XMZADD 20260831 将模型置信度限制在可展示范围内，避免异常服务返回值误导使用者判断推测可靠性。</summary>
        private static int NormalizeConfidenceScore(int confidenceScore)
        {
            if (confidenceScore < 0)
            {
                return 0;
            }
            if (confidenceScore > 100)
            {
                return 100;
            }
            return confidenceScore;
        }
    }

    /// <summary>XMZADD 20260831 通过 OpenAI Compatible Chat Completions 协议访问 DeepSeek 或企业内部模型服务。</summary>
    public sealed class OpenAiCompatibleChatClient : IAiChatClient
    {
        /// <summary>XMZADD 20260831 向兼容接口提交聊天请求，并在服务异常或格式异常时返回可诊断的中文错误。</summary>
        public string Complete(AiProviderConfiguration provider, string systemPrompt, string userPrompt)
        {
            ValidateProvider(provider);
            Uri endpoint = BuildChatCompletionsUri(provider.Endpoint);
            string requestText = SerializeRequest(provider.Model, systemPrompt, userPrompt);
            int timeoutSeconds = provider.TimeoutSeconds > 0 ? provider.TimeoutSeconds : 60;

            using (var client = new HttpClient())
            using (var request = new HttpRequestMessage(HttpMethod.Post, endpoint))
            {
                client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", provider.ApiKey);
                request.Content = new StringContent(requestText, Encoding.UTF8, "application/json");

                HttpResponseMessage response;
                try
                {
                    response = client.SendAsync(request).GetAwaiter().GetResult();
                }
                catch (Exception exception)
                {
                    throw new InvalidOperationException("调用 AI 服务失败：" + exception.Message, exception);
                }

                using (response)
                {
                    string responseText = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new InvalidOperationException("AI 服务返回 " + (int)response.StatusCode + "：" + LimitErrorText(responseText));
                    }
                    return ParseChatResponse(responseText);
                }
            }
        }

        /// <summary>XMZADD 20260831 验证兼容协议调用所需的服务端地址、模型和密钥，避免无效配置造成不可理解的网络错误。</summary>
        private static void ValidateProvider(AiProviderConfiguration provider)
        {
            if (provider == null)
            {
                throw new ArgumentNullException("provider");
            }
            if (provider.Protocol != AiProviderProtocol.OpenAiCompatible)
            {
                throw new NotSupportedException("当前仅支持 OpenAI Compatible 协议。");
            }
            if (string.IsNullOrWhiteSpace(provider.Endpoint))
            {
                throw new InvalidOperationException("AI 服务未配置 Endpoint。");
            }
            if (string.IsNullOrWhiteSpace(provider.Model))
            {
                throw new InvalidOperationException("AI 服务未配置模型名称。");
            }
            if (string.IsNullOrWhiteSpace(provider.ApiKey))
            {
                throw new InvalidOperationException("AI 服务未配置 API Key。");
            }
        }

        /// <summary>XMZADD 20260831 将服务基址或已包含 v1 的地址统一转换为 Chat Completions 端点，兼容 DeepSeek 与内部网关配置。</summary>
        private static Uri BuildChatCompletionsUri(string endpoint)
        {
            string baseEndpoint = endpoint.Trim().TrimEnd('/');
            if (baseEndpoint.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            {
                return CreateAbsoluteUri(baseEndpoint);
            }
            if (!baseEndpoint.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            {
                baseEndpoint += "/v1";
            }
            return CreateAbsoluteUri(baseEndpoint + "/chat/completions");
        }

        /// <summary>XMZADD 20260831 校验 AI 地址为合法绝对地址，防止本地配置错误时发出无法定位的调用请求。</summary>
        private static Uri CreateAbsoluteUri(string value)
        {
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new InvalidOperationException("AI 服务 Endpoint 必须是 HTTP 或 HTTPS 绝对地址。");
            }
            return uri;
        }

        /// <summary>XMZADD 20260831 将字段推测提示序列化为兼容协议请求体，固定低随机度以保持批量数据字典结果稳定。</summary>
        private static string SerializeRequest(string model, string systemPrompt, string userPrompt)
        {
            var request = new ChatCompletionRequest
            {
                Model = model,
                Temperature = 0.1D,
                Messages = new List<ChatMessage>
                {
                    new ChatMessage { Role = "system", Content = systemPrompt },
                    new ChatMessage { Role = "user", Content = userPrompt }
                }
            };

            using (var stream = new MemoryStream())
            {
                var serializer = new DataContractJsonSerializer(typeof(ChatCompletionRequest));
                serializer.WriteObject(stream, request);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        /// <summary>XMZADD 20260831 从兼容接口响应中读取首个助手内容，确保上层只处理字段字典约定的 JSON 文本。</summary>
        private static string ParseChatResponse(string responseText)
        {
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(responseText ?? string.Empty);
                using (var stream = new MemoryStream(bytes))
                {
                    var serializer = new DataContractJsonSerializer(typeof(ChatCompletionResponse));
                    var response = (ChatCompletionResponse)serializer.ReadObject(stream);
                    if (response == null || response.Choices == null || response.Choices.Count == 0 ||
                        response.Choices[0] == null || response.Choices[0].Message == null ||
                        string.IsNullOrWhiteSpace(response.Choices[0].Message.Content))
                    {
                        throw new InvalidOperationException("AI 服务未返回助手内容。");
                    }
                    return response.Choices[0].Message.Content;
                }
            }
            catch (SerializationException exception)
            {
                throw new InvalidOperationException("AI 服务响应不是 OpenAI Compatible JSON 格式。", exception);
            }
        }

        /// <summary>XMZADD 20260831 截断服务端错误正文，保留排查依据的同时避免状态栏与日志被长页面内容淹没。</summary>
        private static string LimitErrorText(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "无错误正文";
            }
            const int maximumLength = 500;
            return value.Length <= maximumLength ? value : value.Substring(0, maximumLength) + "...";
        }
    }

    /// <summary>XMZADD 20260831 定义 AI 字段推测结果的数据契约，确保模型输出只包含可写入本地字典的受控内容。</summary>
    [DataContract]
    internal sealed class AiResponseDocument
    {
        [DataMember(Name = "chineseName")]
        public string ChineseName { get; set; }

        [DataMember(Name = "businessMeaning")]
        public string BusinessMeaning { get; set; }

        [DataMember(Name = "usage")]
        public string Usage { get; set; }

        [DataMember(Name = "confidenceScore")]
        public int ConfidenceScore { get; set; }

        [DataMember(Name = "alternatives")]
        public List<string> Alternatives { get; set; }

        [DataMember(Name = "evidence")]
        public string Evidence { get; set; }
    }

    /// <summary>XMZADD 20260831 定义批量推测请求信封，隔离单项 JSON 契约并提供批内目标标识。</summary>
    [DataContract]
    internal sealed class AiBatchRequestDocument
    {
        [DataMember(Name = "scopeKey")]
        public string ScopeKey { get; set; }

        [DataMember(Name = "items")]
        public List<AiBatchRequestItem> Items { get; set; }
    }

    /// <summary>XMZADD 20260831 定义批量请求中的单个表字段上下文，确保不向模型传递服务商密钥等配置机密。</summary>
    [DataContract]
    internal sealed class AiBatchRequestItem
    {
        [DataMember(Name = "id")]
        public string Id { get; set; }

        [DataMember(Name = "targetType")]
        public string TargetType { get; set; }

        [DataMember(Name = "objectName")]
        public string ObjectName { get; set; }

        [DataMember(Name = "fieldName")]
        public string FieldName { get; set; }

        [DataMember(Name = "objectType")]
        public string ObjectType { get; set; }

        [DataMember(Name = "dataType")]
        public string DataType { get; set; }

        [DataMember(Name = "lengthText")]
        public string LengthText { get; set; }

        [DataMember(Name = "isRequired")]
        public bool IsRequired { get; set; }

        [DataMember(Name = "isPrimaryKey")]
        public bool IsPrimaryKey { get; set; }

        [DataMember(Name = "isForeignKey")]
        public bool IsForeignKey { get; set; }

        [DataMember(Name = "knownChineseName")]
        public string KnownChineseName { get; set; }

        [DataMember(Name = "knownModuleName")]
        public string KnownModuleName { get; set; }

        [DataMember(Name = "knownEntityName")]
        public string KnownEntityName { get; set; }

        [DataMember(Name = "knownBusinessMeaning")]
        public string KnownBusinessMeaning { get; set; }

        [DataMember(Name = "knownUsage")]
        public string KnownUsage { get; set; }

        [DataMember(Name = "knownEnumName")]
        public string KnownEnumName { get; set; }

        [DataMember(Name = "knownRelationSummary")]
        public string KnownRelationSummary { get; set; }

        [DataMember(Name = "fieldOutline")]
        public string FieldOutline { get; set; }

        [DataMember(Name = "evidenceSummary")]
        public string EvidenceSummary { get; set; }

        [DataMember(Name = "knowledgeContext")]
        public string KnowledgeContext { get; set; }
    }

    /// <summary>XMZADD 20260831 定义批量推测响应信封，使所有结果在写回前统一完成完整性校验。</summary>
    [DataContract]
    internal sealed class AiBatchResponseDocument
    {
        [DataMember(Name = "items")]
        public List<AiBatchResponseItem> Items { get; set; }
    }

    /// <summary>XMZADD 20260831 定义批量响应中的单项业务解释，身份仅由 id 字段参与关联。</summary>
    [DataContract]
    internal sealed class AiBatchResponseItem
    {
        [DataMember(Name = "id")]
        public string Id { get; set; }

        [DataMember(Name = "chineseName")]
        public string ChineseName { get; set; }

        [DataMember(Name = "businessMeaning")]
        public string BusinessMeaning { get; set; }

        [DataMember(Name = "usage")]
        public string Usage { get; set; }

        [DataMember(Name = "confidenceScore")]
        public int ConfidenceScore { get; set; }

        [DataMember(Name = "alternatives")]
        public List<string> Alternatives { get; set; }

        [DataMember(Name = "evidence")]
        public string Evidence { get; set; }
    }

    /// <summary>XMZADD 20260831 定义 OpenAI Compatible Chat Completions 请求结构，以便服务商配置只需切换地址、模型和密钥。</summary>
    [DataContract]
    internal sealed class ChatCompletionRequest
    {
        [DataMember(Name = "model")]
        public string Model { get; set; }

        [DataMember(Name = "messages")]
        public List<ChatMessage> Messages { get; set; }

        [DataMember(Name = "temperature")]
        public double Temperature { get; set; }
    }

    /// <summary>XMZADD 20260831 定义请求和响应共用的聊天消息结构，保留协议中角色与文本内容的最小集合。</summary>
    [DataContract]
    internal sealed class ChatMessage
    {
        [DataMember(Name = "role")]
        public string Role { get; set; }

        [DataMember(Name = "content")]
        public string Content { get; set; }
    }

    /// <summary>XMZADD 20260831 定义 OpenAI Compatible 响应的最小数据契约，只读取首个候选回答作为字段推测来源。</summary>
    [DataContract]
    internal sealed class ChatCompletionResponse
    {
        [DataMember(Name = "choices")]
        public List<ChatCompletionChoice> Choices { get; set; }
    }

    /// <summary>XMZADD 20260831 定义兼容接口返回的单个候选回答，便于统一抽取助手生成的 JSON 文本。</summary>
    [DataContract]
    internal sealed class ChatCompletionChoice
    {
        [DataMember(Name = "message")]
        public ChatMessage Message { get; set; }
    }
}
