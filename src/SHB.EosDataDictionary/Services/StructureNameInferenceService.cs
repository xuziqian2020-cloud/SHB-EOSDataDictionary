using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using SHB.EosDataDictionary.Models;

namespace SHB.EosDataDictionary.Services
{
    /// <summary>XMZADD 20260901 定义结构发布中可替换的缩写与 AI 名称推理边界。</summary>
    public interface IStructureNameInference
    {
        /// <summary>XMZADD 20260901 在裁剪前使用受限源码、知识库、共享缩写和可选 AI 补充推测名称。</summary>
        void Enrich(SnapshotData snapshot, IList<SourceEvidence> sourceEvidence,
            EosKnowledgeBaseTranslationService knowledgeBase, CancellationToken cancellationToken);
    }

    /// <summary>XMZADD 20260901 将未知英文缩写交给既有证据优先级流水线，并确保结果只保持推测状态。</summary>
    public sealed class StructureNameInferenceService : IStructureNameInference
    {
        private const int MaximumInferenceCount = 50;
        private readonly IAbbreviationAiInference _aiInference;
        private readonly bool _allowExternalAi;

        /// <summary>XMZADD 20260901 注入可选 AI 缩写推理器，没有 AI 配置时仍使用知识库、源码和共享词典。</summary>
        public StructureNameInferenceService(IAbbreviationAiInference aiInference)
            : this(aiInference, true)
        {
        }

        /// <summary>XMZADD 20260902 允许结构扫描关闭外部 AI，使数据库快照不会被串行网络请求阻塞。</summary>
        public StructureNameInferenceService(IAbbreviationAiInference aiInference, bool allowExternalAi)
        {
            _aiInference = aiInference;
            _allowExternalAi = allowExternalAi;
        }

        /// <summary>XMZADD 20260901 逐表字段解析未知缩写并限制最多五十次推理，避免一次结构扫描产生无界外部请求。</summary>
        public void Enrich(SnapshotData snapshot, IList<SourceEvidence> sourceEvidence,
            EosKnowledgeBaseTranslationService knowledgeBase, CancellationToken cancellationToken)
        {
            if (snapshot == null || snapshot.Tables == null)
            {
                return;
            }
            var service = new AbbreviationInferenceService(knowledgeBase, _allowExternalAi ? _aiInference : null);
            int inferenceCount = 0;
            for (int tableIndex = 0; tableIndex < snapshot.Tables.Count && inferenceCount < MaximumInferenceCount; tableIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TableMetadata table = snapshot.Tables[tableIndex];
                if (table == null)
                {
                    continue;
                }
                ApplyIdentifierInference(snapshot, table, null, sourceEvidence, service, ref inferenceCount, cancellationToken);
                if (table.Fields == null)
                {
                    continue;
                }
                for (int fieldIndex = 0; fieldIndex < table.Fields.Count && inferenceCount < MaximumInferenceCount; fieldIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ApplyIdentifierInference(snapshot, table, table.Fields[fieldIndex], sourceEvidence, service,
                        ref inferenceCount, cancellationToken);
                }
            }
        }

        /// <summary>XMZADD 20260901 对单个表或字段应用未知缩写词义，并阻止推理结果伪装成可直接发布的高可信证据。</summary>
        private static void ApplyIdentifierInference(SnapshotData snapshot, TableMetadata table, FieldMetadata field,
            IList<SourceEvidence> sourceEvidence, AbbreviationInferenceService service, ref int inferenceCount,
            CancellationToken cancellationToken)
        {
            if (table == null || field == null && string.IsNullOrWhiteSpace(table.ObjectName) ||
                field != null && string.IsNullOrWhiteSpace(field.FieldName))
            {
                return;
            }
            MetadataValue current = field == null ? table.ChineseName : field.ChineseName;
            if (IsReliableExistingValue(current))
            {
                return;
            }
            string identifier = field == null ? table.ObjectName : field.FieldName;
            IdentifierTranslationResult translation = new IdentifierTranslationService().Translate(identifier);
            if (translation.UnknownTokens == null || translation.UnknownTokens.Count == 0)
            {
                return;
            }
            MetadataValue referenceName = current;
            if (referenceName == null || string.IsNullOrWhiteSpace(referenceName.Value))
            {
                // 正式名为空时沿用已有参考译名中的未知缩写，避免分层后丢失可供共享词典替换的业务文本。
                referenceName = field == null ? table.SuggestedChineseName : field.SuggestedChineseName;
            }
            string value = referenceName == null || string.IsNullOrWhiteSpace(referenceName.Value)
                ? translation.Value
                : referenceName.Value;
            bool changed = false;
            for (int tokenIndex = 0; tokenIndex < translation.UnknownTokens.Count && inferenceCount < MaximumInferenceCount; tokenIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string token = translation.UnknownTokens[tokenIndex];
                string abbreviation = NormalizeAbbreviationToken(token, field != null);
                var request = new AbbreviationInferenceRequest
                {
                    Abbreviation = abbreviation,
                    SchemaName = table.SchemaName,
                    TableName = table.ObjectName,
                    FieldName = field == null ? string.Empty : field.FieldName,
                    ConfirmedModule = GetValue(table.ModuleName),
                    DatabaseDescription = GetDatabaseDescription(current),
                    NamingRuleMeaning = abbreviation,
                    SourceEvidence = FilterSourceEvidence(sourceEvidence, table.ObjectName,
                        field == null ? string.Empty : field.FieldName),
                    ConfirmedGlossary = snapshot.Abbreviations ?? new List<AbbreviationEntry>()
                };
                AbbreviationInferenceResult result = service.Infer(request);
                inferenceCount++;
                if (result.Selected == null || result.Selected.Priority <= MetadataPriority.NamingRule ||
                    result.Selected.Entry == null ||
                    !IdentifierTranslationService.IsReliableChineseName(result.Selected.Entry.ChineseMeaning))
                {
                    continue;
                }
                string replaced = ReplaceIgnoreCase(value, token, result.Selected.Entry.ChineseMeaning);
                if (string.Equals(replaced, value, StringComparison.Ordinal) &&
                    !string.Equals(abbreviation, token, StringComparison.OrdinalIgnoreCase))
                {
                    replaced = ReplaceIgnoreCase(value, abbreviation, result.Selected.Entry.ChineseMeaning);
                }
                if (!string.Equals(replaced, value, StringComparison.Ordinal))
                {
                    value = replaced;
                    changed = true;
                }
            }
            if (!changed || !IdentifierTranslationService.IsReliableChineseName(value))
            {
                return;
            }
            var inferred = new MetadataValue
            {
                Value = value,
                Status = ConfidenceStatus.Guessed,
                ConfidenceScore = 60,
                SourceType = "缩写与AI推测",
                SourceSummary = "结构维护受限推理，等待人工确认",
                IsManualOverride = false,
                IsLocked = false,
                Evidence = new List<EvidenceItem>()
            };
            if (field == null)
            {
                // 缩写和 AI 仍属于参考译名，必须经过统一名称准入而不能直接占用正式名称。
                new BusinessNameLayerService().ApplyTableCandidate(table, inferred);
            }
            else
            {
                // 缩写和 AI 仍属于参考译名，必须经过统一名称准入而不能直接占用正式名称。
                new BusinessNameLayerService().ApplyFieldCandidate(field, inferred);
            }
        }

        /// <summary>XMZADD 20260901 判断现有名称是否已有人工、数据库、知识库或源码明确依据。</summary>
        private static bool IsReliableExistingValue(MetadataValue value)
        {
            return value != null && (value.IsManualOverride || value.IsLocked ||
                value.Status == ConfidenceStatus.Confirmed || value.Status == ConfidenceStatus.LocalOverride ||
                value.Status == ConfidenceStatus.DatabaseEvidence || value.Status == ConfidenceStatus.KnowledgeBaseEvidence ||
                value.Status == ConfidenceStatus.CodeEvidence);
        }

        /// <summary>XMZADD 20260901 对传统字段缩写移除技术 F 前缀，使 FABC 能命中共享词典中的 ABC。</summary>
        private static string NormalizeAbbreviationToken(string token, bool isField)
        {
            string value = (token ?? string.Empty).Trim();
            return isField && value.Length > 1 && (value[0] == 'F' || value[0] == 'f')
                ? value.Substring(1)
                : value;
        }

        /// <summary>XMZADD 20260901 仅把数据库证据值作为缩写数据库说明，普通命名推测不能提升优先级。</summary>
        private static string GetDatabaseDescription(MetadataValue value)
        {
            return value != null && value.Status == ConfidenceStatus.DatabaseEvidence ? value.Value : string.Empty;
        }

        /// <summary>XMZADD 20260901 为当前表字段筛选源码证据，防止其他对象的中文注释参与缩写推理。</summary>
        private static IList<SourceEvidence> FilterSourceEvidence(IList<SourceEvidence> source, string objectName,
            string fieldName)
        {
            var result = new List<SourceEvidence>();
            if (source == null)
            {
                return result;
            }
            for (int index = 0; index < source.Count; index++)
            {
                SourceEvidence item = source[index];
                if (item != null && string.Equals(item.ObjectName, objectName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(item.FieldName ?? string.Empty, fieldName ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(item);
                }
            }
            return result;
        }

        /// <summary>XMZADD 20260901 按不区分大小写的方式替换完整推测名称中的未知缩写。</summary>
        private static string ReplaceIgnoreCase(string value, string token, string replacement)
        {
            string source = value ?? string.Empty;
            if (string.IsNullOrEmpty(token))
            {
                return source;
            }
            var builder = new StringBuilder();
            int start = 0;
            while (start < source.Length)
            {
                int found = source.IndexOf(token, start, StringComparison.OrdinalIgnoreCase);
                if (found < 0)
                {
                    builder.Append(source.Substring(start));
                    break;
                }
                builder.Append(source.Substring(start, found - start));
                builder.Append(replacement);
                start = found + token.Length;
            }
            return builder.ToString();
        }

        /// <summary>XMZADD 20260901 安全读取元数据展示值供缩写业务范围使用。</summary>
        private static string GetValue(MetadataValue value)
        {
            return value == null ? string.Empty : value.Value;
        }
    }

    /// <summary>XMZADD 20260901 将本机默认 AI 配置适配为缩写推理器，并复用 AI 服务的统一提示安全边界。</summary>
    public sealed class AiBackedAbbreviationInference : IAbbreviationAiInference
    {
        private readonly AiProviderConfiguration _provider;
        private readonly IAiChatClient _chatClient;

        /// <summary>XMZADD 20260901 绑定本机 AI 配置与聊天客户端，配置不完整时调用会安全返回空候选。</summary>
        public AiBackedAbbreviationInference(AiProviderConfiguration provider, IAiChatClient chatClient)
        {
            _provider = provider;
            _chatClient = chatClient;
        }

        /// <summary>XMZADD 20260901 把缩写的受限知识和源码上下文转换为单字段 AI 请求并只返回中文名称。</summary>
        public string Infer(AbbreviationInferenceContext context)
        {
            if (_provider == null || !_provider.IsEnabled || string.IsNullOrWhiteSpace(_provider.ApiKey) ||
                _chatClient == null || context == null)
            {
                return string.Empty;
            }
            string schemaName;
            string objectName;
            SplitObjectScope(context.TableScope, out schemaName, out objectName);
            var table = new TableMetadata
            {
                SchemaName = schemaName,
                ObjectName = objectName,
                ObjectType = "TABLE",
                ModuleName = new MetadataValue { Value = context.ModuleScope, Status = ConfidenceStatus.Guessed }
            };
            var field = new FieldMetadata
            {
                OwnerTableName = objectName,
                FieldName = string.IsNullOrWhiteSpace(context.FieldName) ? context.Abbreviation : context.FieldName,
                BusinessMeaning = new MetadataValue
                {
                    Value = JoinContext(context.EvidenceContext),
                    Status = ConfidenceStatus.Guessed
                }
            };
            AiExplanationResult result = new AiInferenceService(_chatClient).Infer(
                _provider, "structure-abbreviation", table, field);
            return result == null ? string.Empty : result.ChineseName;
        }

        /// <summary>XMZADD 20260901 拆分可选架构前缀以构造 AI 表字段上下文。</summary>
        private static void SplitObjectScope(string scope, out string schemaName, out string objectName)
        {
            string value = (scope ?? string.Empty).Trim();
            int dot = value.LastIndexOf('.');
            schemaName = dot > 0 ? value.Substring(0, dot) : string.Empty;
            objectName = dot > 0 && dot < value.Length - 1 ? value.Substring(dot + 1) : value;
        }

        /// <summary>XMZADD 20260901 将最多八条缩写证据压缩为短业务上下文，具体脱敏由 AI 服务统一执行。</summary>
        private static string JoinContext(IList<string> context)
        {
            var builder = new StringBuilder();
            if (context == null)
            {
                return string.Empty;
            }
            for (int index = 0; index < context.Count && index < 8; index++)
            {
                string value = context[index] ?? string.Empty;
                if (value.Length > 240)
                {
                    value = value.Substring(0, 240);
                }
                if (builder.Length > 0)
                {
                    builder.Append("；");
                }
                builder.Append(value);
            }
            return builder.ToString();
        }
    }
}
