using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using SHB.EosDataDictionary.Models;

namespace SHB.EosDataDictionary.Services
{
    /// <summary>XMZADD 20260901 定义可替换的缩写 AI 推理边界，使结构扫描可测试且不依赖真实网络。</summary>
    public interface IAbbreviationAiInference
    {
        /// <summary>XMZADD 20260901 根据受限业务上下文返回一个缩写中文候选。</summary>
        string Infer(AbbreviationInferenceContext context);
    }

    /// <summary>XMZADD 20260901 按固定来源优先级组合缩写候选，并确保自动结果仍显示为推测。</summary>
    public sealed class AbbreviationInferenceService
    {
        private const int MaximumCandidateCount = 16;
        private const int MaximumContextCount = 8;
        private const int MaximumEvidenceCount = 5;
        private const int MaximumTextLength = 240;
        private static readonly Regex ConnectionLabelRegex = new Regex(
            @"(?:连接字符串|connection\s*string)\s*[:=]\s*[^\r\n，。]*",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ConnectionValueRegex = new Regex(
            @"\b(?:server|data\s+source)\s*=\s*[^\r\n，。]*",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SensitiveValueRegex = new Regex(
            @"(?<key>\b(?:password|pwd|token|apikey|api_key|secret)\b)\s*[:=]\s*(?:\[[^\]\r\n]*\]|""[^""\r\n]*""|'[^'\r\n]*'|[^\s;，。\r\n]+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex WindowsAbsolutePathRegex = new Regex(
            @"\b[A-Z]:\\(?:[^\\\s""<>|]+\\)+(?<file>[^\\\s""<>|]+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex UncAbsolutePathRegex = new Regex(
            @"\\\\(?:[^\\\s""<>|]+\\)+(?<file>[^\\\s""<>|]+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private readonly EosKnowledgeBaseTranslationService _knowledgeBase;
        private readonly IAbbreviationAiInference _aiInference;

        /// <summary>XMZADD 20260901 注入可选知识库和 AI 推理器，任一来源不可用时仍可使用其余证据。</summary>
        public AbbreviationInferenceService(EosKnowledgeBaseTranslationService knowledgeBase, IAbbreviationAiInference aiInference)
        {
            _knowledgeBase = knowledgeBase;
            _aiInference = aiInference;
        }

        /// <summary>XMZADD 20260901 综合人工、数据库、知识库、源码、共享词典、AI 和命名规则选择缩写词义。</summary>
        public AbbreviationInferenceResult Infer(AbbreviationInferenceRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException("request");
            }
            var result = new AbbreviationInferenceResult();
            AddManualCandidate(result, request);
            AddDatabaseCandidate(result, request);
            KnowledgeBaseTranslationResult knowledgeResult = AddKnowledgeBaseCandidate(result, request);
            AddSourceCandidates(result, request);
            AddGlossaryCandidates(result, request);
            if (string.IsNullOrWhiteSpace(request.ManualMeaning))
            {
                BuildAiContext(result, request, knowledgeResult);
                AddAiCandidate(result, request);
            }
            AddNamingCandidate(result, request);
            SelectPreferredCandidate(result);
            return result;
        }

        /// <summary>XMZADD 20260901 添加人工确认候选，确保任何自动刷新都不能覆盖维护结果。</summary>
        private static void AddManualCandidate(AbbreviationInferenceResult result, AbbreviationInferenceRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.ManualMeaning))
            {
                return;
            }
            AddCandidate(result, CreateCandidate(request, request.ManualMeaning.Trim(), ConfidenceStatus.Confirmed,
                MetadataPriority.LocalOverride, 100, "人工维护", new List<AbbreviationEvidence>()));
        }

        /// <summary>XMZADD 20260901 将数据库明确说明标准化为最高级自动候选并保留完整原文。</summary>
        private static void AddDatabaseCandidate(AbbreviationInferenceResult result, AbbreviationInferenceRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.DatabaseDescription))
            {
                return;
            }
            string publishedDescription = SanitizePublishedText(request.DatabaseDescription);
            NameCandidate normalized = IdentifierTranslationService.NormalizeNameCandidate(publishedDescription);
            if (!IdentifierTranslationService.IsReliableChineseName(normalized.Value))
            {
                return;
            }
            var evidence = new List<AbbreviationEvidence>
            {
                new AbbreviationEvidence
                {
                    SourceType = "数据库说明",
                    RelativePath = string.Empty,
                    LineNumber = 0,
                    Summary = SanitizePublishedText(normalized.RawEvidence)
                }
            };
            AddCandidate(result, CreateCandidate(request, normalized.Value, ConfidenceStatus.Guessed,
                MetadataPriority.DatabaseEvidence, 90, "数据库说明", evidence));
        }

        /// <summary>XMZADD 20260901 添加知识库精确条目，并把冲突条目保留给 AI 上下文消歧。</summary>
        private KnowledgeBaseTranslationResult AddKnowledgeBaseCandidate(AbbreviationInferenceResult result, AbbreviationInferenceRequest request)
        {
            if (_knowledgeBase == null)
            {
                return new KnowledgeBaseTranslationResult();
            }
            KnowledgeBaseTranslationResult knowledge = _knowledgeBase.TranslateField(
                request.SchemaName, request.TableName, request.FieldName, request.ConfirmedModule);
            if (!_knowledgeBase.IsAvailable || knowledge.Candidate == null || string.IsNullOrWhiteSpace(knowledge.Candidate.Value))
            {
                // 损坏知识事务中的局部候选和消歧上下文都不能继续进入缩写或 AI 流水线。
                return _knowledgeBase.IsAvailable ? knowledge : new KnowledgeBaseTranslationResult();
            }
            var evidence = new List<AbbreviationEvidence>();
            if (knowledge.Candidate.Evidence != null)
            {
                for (int index = 0; index < knowledge.Candidate.Evidence.Count && evidence.Count < MaximumEvidenceCount; index++)
                {
                    EvidenceItem item = knowledge.Candidate.Evidence[index];
                    if (item != null)
                    {
                        evidence.Add(new AbbreviationEvidence
                        {
                            SourceType = SanitizePublishedText("EOS知识库"),
                            RelativePath = SanitizeRelativePath(item.SourcePath),
                            LineNumber = item.SourceLine,
                            Summary = SanitizePublishedText(item.Explanation)
                        });
                    }
                }
            }
            AddCandidate(result, CreateCandidate(request, knowledge.Candidate.Value, ConfidenceStatus.Guessed,
                MetadataPriority.KnowledgeBaseEvidence, 85, "EOS知识库", evidence));
            return knowledge;
        }

        /// <summary>XMZADD 20260901 添加可靠源码中文候选，并在公开证据中移除绝对目录。</summary>
        private static void AddSourceCandidates(AbbreviationInferenceResult result, AbbreviationInferenceRequest request)
        {
            if (request.SourceEvidence == null)
            {
                return;
            }
            for (int index = 0; index < request.SourceEvidence.Count && result.Candidates.Count < MaximumCandidateCount; index++)
            {
                SourceEvidence source = request.SourceEvidence[index];
                if (source == null || string.IsNullOrWhiteSpace(source.ChineseNameCandidate))
                {
                    continue;
                }
                string publishedCandidate = SanitizePublishedText(source.ChineseNameCandidate);
                NameCandidate normalized = IdentifierTranslationService.NormalizeNameCandidate(publishedCandidate);
                if (!IdentifierTranslationService.IsReliableChineseName(normalized.Value))
                {
                    continue;
                }
                var evidence = new List<AbbreviationEvidence>();
                if (source.Evidence != null)
                {
                    evidence.Add(new AbbreviationEvidence
                    {
                        SourceType = SanitizePublishedText("EOS源码"),
                        RelativePath = SanitizeRelativePath(source.Evidence.SourcePath),
                        LineNumber = source.Evidence.SourceLine,
                        Summary = SanitizePublishedText(normalized.RawEvidence)
                    });
                }
                AddCandidate(result, CreateCandidate(request, normalized.Value, ConfidenceStatus.Guessed,
                    MetadataPriority.CodeEvidence, 75, "可靠源码", evidence));
            }
        }

        /// <summary>XMZADD 20260901 添加当前模块和表范围内的已确认共享词典候选。</summary>
        private static void AddGlossaryCandidates(AbbreviationInferenceResult result, AbbreviationInferenceRequest request)
        {
            if (request.ConfirmedGlossary == null)
            {
                return;
            }
            for (int index = 0; index < request.ConfirmedGlossary.Count && result.Candidates.Count < MaximumCandidateCount; index++)
            {
                AbbreviationEntry item = request.ConfirmedGlossary[index];
                if (item == null || item.Status != ConfidenceStatus.Confirmed ||
                    !string.Equals(item.Abbreviation, request.Abbreviation, StringComparison.OrdinalIgnoreCase) ||
                    !ScopeMatches(item.ModuleScope, request.ConfirmedModule) ||
                    !ScopeMatches(item.TableScope, request.TableName) || string.IsNullOrWhiteSpace(item.ChineseMeaning))
                {
                    continue;
                }
                AddCandidate(result, CreateCandidate(request, item.ChineseMeaning.Trim(), ConfidenceStatus.Guessed,
                    MetadataPriority.ConfirmedGlossary, item.ConfidenceScore, "已确认共享词典",
                    CopyEvidence(item.Evidence)));
            }
        }

        /// <summary>XMZADD 20260901 将所有本地证据压缩到固定大小上下文，避免绝对路径和长正文交给 AI。</summary>
        private static void BuildAiContext(AbbreviationInferenceResult result, AbbreviationInferenceRequest request,
            KnowledgeBaseTranslationResult knowledge)
        {
            AddContext(result.AiContext, "缩写：" + request.Abbreviation);
            AddContext(result.AiContext, "模块：" + request.ConfirmedModule);
            AddContext(result.AiContext, "表字段：" + request.TableName + "." + request.FieldName);
            if (!string.IsNullOrWhiteSpace(request.DatabaseDescription))
            {
                AddContext(result.AiContext, "数据库说明：" + request.DatabaseDescription);
            }
            if (knowledge != null && knowledge.AiContext != null)
            {
                for (int index = 0; index < knowledge.AiContext.Count; index++)
                {
                    AddContext(result.AiContext, "知识库：" + knowledge.AiContext[index]);
                }
            }
            if (request.SourceEvidence != null)
            {
                for (int index = 0; index < request.SourceEvidence.Count; index++)
                {
                    SourceEvidence source = request.SourceEvidence[index];
                    if (source != null && !string.IsNullOrWhiteSpace(source.ChineseNameCandidate))
                    {
                        string path = source.Evidence == null ? string.Empty : SanitizeRelativePath(source.Evidence.SourcePath);
                        AddContext(result.AiContext, "源码：" + path + " " + source.ChineseNameCandidate);
                    }
                }
            }
        }

        /// <summary>XMZADD 20260901 调用注入式 AI 生成低于共享词典的推测候选，失败时继续保留规则结果。</summary>
        private void AddAiCandidate(AbbreviationInferenceResult result, AbbreviationInferenceRequest request)
        {
            if (_aiInference == null)
            {
                return;
            }
            try
            {
                string value = _aiInference.Infer(new AbbreviationInferenceContext
                {
                    Abbreviation = SanitizePublishedText(request.Abbreviation),
                    ModuleScope = SanitizePublishedText(request.ConfirmedModule),
                    TableScope = SanitizePublishedText(request.TableName),
                    FieldName = SanitizePublishedText(request.FieldName),
                    EvidenceContext = result.AiContext
                });
                NameCandidate normalized = IdentifierTranslationService.NormalizeNameCandidate(SanitizePublishedText(value));
                if (IdentifierTranslationService.IsReliableChineseName(normalized.Value))
                {
                    AddCandidate(result, CreateCandidate(request, normalized.Value, ConfidenceStatus.Guessed,
                        MetadataPriority.AiGuessed, 60, "AI推测", new List<AbbreviationEvidence>()));
                }
            }
            catch (Exception)
            {
                // AI 是可选推测来源，网络或模型失败不能丢失数据库、知识库、源码和规则候选。
            }
        }

        /// <summary>XMZADD 20260901 添加局部标识符翻译作为最低优先级兜底，不附加业务表或字段弱后缀。</summary>
        private static void AddNamingCandidate(AbbreviationInferenceResult result, AbbreviationInferenceRequest request)
        {
            if (!string.IsNullOrWhiteSpace(request.NamingRuleMeaning))
            {
                AddCandidate(result, CreateCandidate(request, request.NamingRuleMeaning, ConfidenceStatus.Guessed,
                    MetadataPriority.NamingRule, 30, "命名规则", new List<AbbreviationEvidence>()));
            }
        }

        /// <summary>XMZADD 20260901 按显式业务优先级选择单一缩写候选，避免依赖插入顺序。</summary>
        private static void SelectPreferredCandidate(AbbreviationInferenceResult result)
        {
            for (int index = 0; index < result.Candidates.Count; index++)
            {
                AbbreviationInferenceCandidate candidate = result.Candidates[index];
                if (result.Selected == null || candidate.Priority > result.Selected.Priority)
                {
                    result.Selected = candidate;
                }
            }
        }

        /// <summary>XMZADD 20260901 创建带模块和表范围的缩写条目，使同一缩写可在不同业务域表达不同含义。</summary>
        private static AbbreviationInferenceCandidate CreateCandidate(AbbreviationInferenceRequest request, string meaning,
            ConfidenceStatus status, MetadataPriority priority, int confidence, string sourceType,
            IList<AbbreviationEvidence> evidence)
        {
            return new AbbreviationInferenceCandidate
            {
                Priority = priority,
                SourceType = SanitizePublishedText(sourceType),
                Entry = new AbbreviationEntry
                {
                    Abbreviation = (request.Abbreviation ?? string.Empty).Trim(),
                    ChineseMeaning = SanitizePublishedText(meaning),
                    ModuleScope = request.ConfirmedModule,
                    TableScope = request.TableName,
                    ConfidenceScore = confidence,
                    Status = status,
                    Evidence = evidence ?? new List<AbbreviationEvidence>()
                }
            };
        }

        /// <summary>XMZADD 20260901 在固定数量内追加有效候选，防止异常证据制造无限内存增长。</summary>
        private static void AddCandidate(AbbreviationInferenceResult result, AbbreviationInferenceCandidate candidate)
        {
            if (candidate != null && result.Candidates.Count < MaximumCandidateCount)
            {
                result.Candidates.Add(candidate);
            }
        }

        /// <summary>XMZADD 20260901 判断共享词典空范围可通用，非空范围必须与当前上下文一致。</summary>
        private static bool ScopeMatches(string glossaryScope, string requestScope)
        {
            return string.IsNullOrWhiteSpace(glossaryScope) ||
                   string.Equals(glossaryScope.Trim(), (requestScope ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>XMZADD 20260901 复制共享词典证据并再次执行公开边界清洗。</summary>
        private static IList<AbbreviationEvidence> CopyEvidence(IList<AbbreviationEvidence> source)
        {
            var result = new List<AbbreviationEvidence>();
            if (source == null)
            {
                return result;
            }
            for (int index = 0; index < source.Count && result.Count < MaximumEvidenceCount; index++)
            {
                AbbreviationEvidence item = source[index];
                if (item != null)
                {
                    result.Add(new AbbreviationEvidence
                    {
                        SourceType = SanitizePublishedText(item.SourceType),
                        RelativePath = SanitizeRelativePath(item.RelativePath),
                        LineNumber = item.LineNumber,
                        Summary = SanitizePublishedText(item.Summary)
                    });
                }
            }
            return result;
        }

        /// <summary>XMZADD 20260901 将上下文限制为少量短文本，避免上传知识库或源码全文。</summary>
        private static void AddContext(IList<string> context, string value)
        {
            if (context.Count < MaximumContextCount && !string.IsNullOrWhiteSpace(value))
            {
                context.Add(SanitizePublishedText(value));
            }
        }

        /// <summary>XMZADD 20260901 将证据路径压缩为安全相对路径，绝对路径只保留文件名。</summary>
        private static string SanitizeRelativePath(string value)
        {
            string path = (value ?? string.Empty).Trim();
            if (path.Length == 0)
            {
                return string.Empty;
            }
            if (Path.IsPathRooted(path) || path.IndexOf("..", StringComparison.Ordinal) >= 0)
            {
                return Path.GetFileName(path);
            }
            return path.Replace('\\', '/');
        }

        /// <summary>XMZADD 20260901 统一清除公开缩写文本中的测试凭据形态、连接串和本机绝对路径。</summary>
        private static string SanitizePublishedText(string value)
        {
            string text = (value ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                return string.Empty;
            }
            // 连接字符串可能包含多个敏感键，整体替换比逐段保留更安全。
            text = ConnectionLabelRegex.Replace(text, "连接字符串=[REDACTED]");
            text = ConnectionValueRegex.Replace(text, "[CONNECTION_STRING_REDACTED]");
            text = SensitiveValueRegex.Replace(text, "${key}=[REDACTED]");
            text = WindowsAbsolutePathRegex.Replace(text, "${file}");
            text = UncAbsolutePathRegex.Replace(text, "${file}");
            return AiInferenceService.SanitizeAiText(text, MaximumTextLength);
        }

        /// <summary>XMZADD 20260901 截断候选和摘要，保证本地快照及 AI 请求边界稳定。</summary>
        private static string LimitText(string value)
        {
            string text = (value ?? string.Empty).Trim();
            return text.Length <= MaximumTextLength ? text : text.Substring(0, MaximumTextLength);
        }
    }

    /// <summary>XMZADD 20260901 保存一次缩写推理需要的业务范围和各类本地证据。</summary>
    public sealed class AbbreviationInferenceRequest
    {
        /// <summary>XMZADD 20260901 初始化可直接追加源码与共享词典证据的请求。</summary>
        public AbbreviationInferenceRequest()
        {
            SourceEvidence = new List<SourceEvidence>();
            ConfirmedGlossary = new List<AbbreviationEntry>();
        }

        public string Abbreviation { get; set; }
        public string SchemaName { get; set; }
        public string TableName { get; set; }
        public string FieldName { get; set; }
        public string ConfirmedModule { get; set; }
        public string ManualMeaning { get; set; }
        public string DatabaseDescription { get; set; }
        public string NamingRuleMeaning { get; set; }
        public IList<SourceEvidence> SourceEvidence { get; set; }
        public IList<AbbreviationEntry> ConfirmedGlossary { get; set; }
    }

    /// <summary>XMZADD 20260901 保存交给注入式 AI 的脱敏缩写业务上下文。</summary>
    public sealed class AbbreviationInferenceContext
    {
        public string Abbreviation { get; set; }
        public string ModuleScope { get; set; }
        public string TableScope { get; set; }
        public string FieldName { get; set; }
        public IList<string> EvidenceContext { get; set; }
    }

    /// <summary>XMZADD 20260901 保存一个带来源优先级的缩写候选。</summary>
    public sealed class AbbreviationInferenceCandidate
    {
        public AbbreviationEntry Entry { get; set; }
        public MetadataPriority Priority { get; set; }
        public string SourceType { get; set; }
    }

    /// <summary>XMZADD 20260901 保存缩写候选全集、最终选择和受限 AI 上下文。</summary>
    public sealed class AbbreviationInferenceResult
    {
        /// <summary>XMZADD 20260901 初始化缩写候选和 AI 上下文集合。</summary>
        public AbbreviationInferenceResult()
        {
            Candidates = new List<AbbreviationInferenceCandidate>();
            AiContext = new List<string>();
        }

        public AbbreviationInferenceCandidate Selected { get; set; }
        public IList<AbbreviationInferenceCandidate> Candidates { get; private set; }
        public IList<string> AiContext { get; private set; }
    }
}
