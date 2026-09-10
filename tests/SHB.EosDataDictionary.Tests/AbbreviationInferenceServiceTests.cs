using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHB.EosDataDictionary.Models;
using SHB.EosDataDictionary.Services;

namespace SHB.EosDataDictionary.Tests
{
    /// <summary>XMZADD 20260901 验证缩写可综合数据库、知识库、源码、共享词典和 AI 证据生成可追溯推测。</summary>
    [TestClass]
    public sealed class AbbreviationInferenceServiceTests
    {
        /// <summary>XMZADD 20260901 验证数据库明确说明优先，并完整保留带等号的原始证据。</summary>
        [TestMethod]
        public void Infer_DatabaseDescriptionWinsAndKeepsRawEvidence()
        {
            var request = CreateRequest();
            request.DatabaseDescription = "ABC=业务中心";
            request.SourceEvidence.Add(CreateSourceEvidence("ABC=源码中心"));
            request.ConfirmedGlossary.Add(CreateGlossary("ABC", "词典中心"));
            var service = new AbbreviationInferenceService(null, new FakeAiInference("AI中心"));

            AbbreviationInferenceResult result = service.Infer(request);

            Assert.AreEqual("业务中心", result.Selected.Entry.ChineseMeaning);
            Assert.AreEqual(MetadataPriority.DatabaseEvidence, result.Selected.Priority);
            Assert.AreEqual(ConfidenceStatus.Guessed, result.Selected.Entry.Status);
            Assert.AreEqual("ABC", result.Selected.Entry.Abbreviation);
            Assert.AreEqual("采购", result.Selected.Entry.ModuleScope);
            Assert.AreEqual("dbo.T_ORDER", result.Selected.Entry.TableScope);
            Assert.AreEqual("ABC=业务中心", result.Selected.Entry.Evidence[0].Summary);
        }

        /// <summary>XMZADD 20260901 验证知识库精确条目优先于可靠源码，且证据不泄露本机根目录。</summary>
        [TestMethod]
        public void Infer_KnowledgeBaseExactWinsOverSourceEvidence()
        {
            string root = CreateKnowledgeFixture();
            try
            {
                var request = CreateRequest();
                request.TableName = "T_ORDER";
                request.FieldName = "FABC";
                request.SourceEvidence.Add(CreateSourceEvidence("FABC=源码业务中心"));
                var service = new AbbreviationInferenceService(
                    new EosKnowledgeBaseTranslationService(root), new FakeAiInference("AI中心"));

                AbbreviationInferenceResult result = service.Infer(request);

                Assert.AreEqual("知识库业务中心", result.Selected.Entry.ChineseMeaning);
                Assert.AreEqual(MetadataPriority.KnowledgeBaseEvidence, result.Selected.Priority);
                Assert.IsFalse(Path.IsPathRooted(result.Selected.Entry.Evidence[0].RelativePath));
                Assert.IsFalse(result.Selected.Entry.Evidence[0].RelativePath.Contains(root));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        /// <summary>XMZADD 20260901 验证知识库近似字段不会直接成为缩写词义，只以受限短上下文交给 AI 消歧。</summary>
        [TestMethod]
        public void Infer_KnowledgeBaseFuzzyMatch_IsPassedAsBoundedAiContext()
        {
            string root = CreateKnowledgeFixture();
            try
            {
                var request = CreateRequest();
                request.TableName = "T_ORDER";
                request.FieldName = "FABC_DETAIL";
                var ai = new FakeAiInference("AI业务中心");
                var service = new AbbreviationInferenceService(
                    new EosKnowledgeBaseTranslationService(root), ai);

                AbbreviationInferenceResult result = service.Infer(request);

                Assert.AreEqual("AI业务中心", result.Selected.Entry.ChineseMeaning);
                Assert.IsNotNull(ai.LastContext);
                Assert.IsTrue(ContextContains(ai.LastContext.EvidenceContext, "知识库："));
                Assert.IsTrue(ContextContains(ai.LastContext.EvidenceContext, "知识库业务中心"));
                Assert.IsTrue(result.AiContext.Count <= 8);
                for (int index = 0; index < result.AiContext.Count; index++)
                {
                    Assert.IsTrue(result.AiContext[index].Length <= 240);
                    Assert.IsFalse(result.AiContext[index].Contains(root));
                }
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        /// <summary>XMZADD 20260901 验证缩写流水线遇到被引用模块损坏时显式降级知识库且继续使用 AI 或命名规则。</summary>
        [TestMethod]
        public void Infer_DamagedKnowledgeModule_DegradesAndKeepsOtherCandidates()
        {
            string root = CreateKnowledgeFixture();
            try
            {
                File.WriteAllText(Path.Combine(root, "采购.md"), "损坏模块，没有实体锚点");
                var knowledgeBase = new EosKnowledgeBaseTranslationService(root);
                var request = CreateRequest();
                request.TableName = "T_ORDER";
                request.FieldName = "FABC";
                var service = new AbbreviationInferenceService(knowledgeBase, new FakeAiInference("AI业务中心"));

                AbbreviationInferenceResult result = service.Infer(request);

                Assert.AreEqual("AI业务中心", result.Selected.Entry.ChineseMeaning);
                Assert.IsFalse(knowledgeBase.IsAvailable);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        /// <summary>XMZADD 20260901 验证可靠源码高于已确认共享词典，公开证据只保留相对短路径和摘要。</summary>
        [TestMethod]
        public void Infer_ReliableSourceWinsOverConfirmedGlossaryAndSanitizesEvidence()
        {
            var request = CreateRequest();
            request.SourceEvidence.Add(CreateSourceEvidence("ABC=源码业务中心"));
            request.ConfirmedGlossary.Add(CreateGlossary("ABC", "词典业务中心"));
            var service = new AbbreviationInferenceService(null, new FakeAiInference("AI中心"));

            AbbreviationInferenceResult result = service.Infer(request);

            Assert.AreEqual("源码业务中心", result.Selected.Entry.ChineseMeaning);
            Assert.AreEqual(MetadataPriority.CodeEvidence, result.Selected.Priority);
            Assert.AreEqual("OrderEntity.vb", result.Selected.Entry.Evidence[0].RelativePath);
            Assert.IsTrue(result.Selected.Entry.Evidence[0].Summary.Length <= 240);
            Assert.IsFalse(result.Selected.Entry.Evidence[0].Summary.Contains("C:\\"));
        }

        /// <summary>XMZADD 20260901 验证已确认共享词典高于 AI，但自动套用到当前结果时仍保持推测状态。</summary>
        [TestMethod]
        public void Infer_ConfirmedGlossaryWinsOverAi()
        {
            var request = CreateRequest();
            request.ConfirmedGlossary.Add(CreateGlossary("ABC", "词典业务中心"));
            var service = new AbbreviationInferenceService(null, new FakeAiInference("AI中心"));

            AbbreviationInferenceResult result = service.Infer(request);

            Assert.AreEqual("词典业务中心", result.Selected.Entry.ChineseMeaning);
            Assert.AreEqual(MetadataPriority.ConfirmedGlossary, result.Selected.Priority);
            Assert.AreEqual(ConfidenceStatus.Guessed, result.Selected.Entry.Status);
        }

        /// <summary>XMZADD 20260901 验证 AI 失败不会丢失局部命名规则翻译。</summary>
        [TestMethod]
        public void Infer_AiFailureStillReturnsPartialNamingTranslation()
        {
            var request = CreateRequest();
            request.NamingRuleMeaning = "ABC订单状态";
            var service = new AbbreviationInferenceService(null, new FakeAiInference(new InvalidOperationException("offline")));

            AbbreviationInferenceResult result = service.Infer(request);

            Assert.AreEqual("ABC订单状态", result.Selected.Entry.ChineseMeaning);
            Assert.AreEqual(MetadataPriority.NamingRule, result.Selected.Priority);
            Assert.AreEqual(ConfidenceStatus.Guessed, result.Selected.Entry.Status);
        }

        /// <summary>XMZADD 20260901 验证人工确认始终最高且 AI 上下文数量和长度有严格上限。</summary>
        [TestMethod]
        public void Infer_ManualMeaningWinsAndBoundsAiContext()
        {
            var request = CreateRequest();
            request.ManualMeaning = "人工业务中心";
            for (int index = 0; index < 20; index++)
            {
                request.SourceEvidence.Add(CreateSourceEvidence("ABC=源码候选" + index + new string('长', 300)));
            }
            var ai = new FakeAiInference("AI中心");
            var service = new AbbreviationInferenceService(null, ai);

            AbbreviationInferenceResult result = service.Infer(request);

            Assert.AreEqual("人工业务中心", result.Selected.Entry.ChineseMeaning);
            Assert.AreEqual(MetadataPriority.LocalOverride, result.Selected.Priority);
            Assert.AreEqual(ConfidenceStatus.Confirmed, result.Selected.Entry.Status);
            Assert.AreEqual(0, ai.CallCount);
            Assert.IsTrue(result.AiContext.Count <= 8);
            for (int index = 0; index < result.AiContext.Count; index++)
            {
                Assert.IsTrue(result.AiContext[index].Length <= 240);
                Assert.IsFalse(result.AiContext[index].Contains("C:\\"));
            }
        }

        /// <summary>XMZADD 20260901 验证 AI 接口收到的四个定位标量已经移除测试凭据和绝对路径。</summary>
        [TestMethod]
        public void Infer_SanitizesAllAiContextScalarProperties()
        {
            var request = CreateRequest();
            request.Abbreviation = "ABC Token=demo-token";
            request.ConfirmedModule = "采购 C:\\Private\\Module.md";
            request.TableName = "dbo.T_ORDER Pwd: demo";
            request.FieldName = "FABC ApiKey=demo-key";
            var ai = new FakeAiInference("订单状态");
            var service = new AbbreviationInferenceService(null, ai);

            service.Infer(request);

            Assert.IsNotNull(ai.LastContext);
            AssertPublishedTextIsSanitized(ai.LastContext.Abbreviation);
            AssertPublishedTextIsSanitized(ai.LastContext.ModuleScope);
            AssertPublishedTextIsSanitized(ai.LastContext.TableScope);
            AssertPublishedTextIsSanitized(ai.LastContext.FieldName);
        }

        /// <summary>XMZADD 20260901 验证 AI 的英文或过程说明不能覆盖局部命名规则翻译。</summary>
        [DataTestMethod]
        [DataRow("DROP TABLE")]
        [DataRow("用于处理订单状态")]
        public void Infer_UnreliableAiName_FallsBackToNamingRule(string aiValue)
        {
            var request = CreateRequest();
            request.NamingRuleMeaning = "ABC订单状态";
            var service = new AbbreviationInferenceService(null, new FakeAiInference(aiValue));

            AbbreviationInferenceResult result = service.Infer(request);

            Assert.AreEqual("ABC订单状态", result.Selected.Entry.ChineseMeaning);
            Assert.AreEqual(MetadataPriority.NamingRule, result.Selected.Priority);
        }

        /// <summary>XMZADD 20260901 验证简短可靠中文 AI 名称仍可高于命名规则候选。</summary>
        [TestMethod]
        public void Infer_ReliableChineseAiName_RemainsAvailable()
        {
            var request = CreateRequest();
            request.NamingRuleMeaning = "ABC订单状态";
            var service = new AbbreviationInferenceService(null, new FakeAiInference("订单状态"));

            AbbreviationInferenceResult result = service.Infer(request);

            Assert.AreEqual("订单状态", result.Selected.Entry.ChineseMeaning);
            Assert.AreEqual(MetadataPriority.AiGuessed, result.Selected.Priority);
        }

        /// <summary>XMZADD 20260901 验证数据库、源码和 AI 文本中的测试凭据、连接串及绝对路径不会进入公开缩写结果。</summary>
        [TestMethod]
        public void Infer_SanitizesSensitivePublishedTextAcrossAllSources()
        {
            var request = CreateRequest();
            request.DatabaseDescription = "数据库中心 Pwd: demo";
            request.SourceEvidence.Add(CreateSourceEvidence("源码中心 Secret: demo C:\\Private\\Order.vb"));
            request.SourceEvidence.Add(CreateSourceEvidence("Token=demo-token 连接字符串=Server=test;Password=demo"));
            request.SourceEvidence.Add(CreateSourceEvidence("Secret=demo"));
            request.SourceEvidence.Add(CreateSourceEvidence("授权中心 Authorization: Bearer authorization-private"));
            request.SourceEvidence.Add(CreateSourceEvidence("账套中心 Initial Catalog=private-db;User ID=private-user"));
            var service = new AbbreviationInferenceService(null,
                new FakeAiInference("AI中心 ApiKey=demo-key Password=[test-secret] C:\\Private\\Order.vb"));

            AbbreviationInferenceResult result = service.Infer(request);

            for (int index = 0; index < result.AiContext.Count; index++)
            {
                AssertPublishedTextIsSanitized(result.AiContext[index]);
            }
            for (int candidateIndex = 0; candidateIndex < result.Candidates.Count; candidateIndex++)
            {
                AbbreviationInferenceCandidate candidate = result.Candidates[candidateIndex];
                AssertPublishedTextIsSanitized(candidate.SourceType);
                AssertPublishedTextIsSanitized(candidate.Entry.ChineseMeaning);
                for (int evidenceIndex = 0; evidenceIndex < candidate.Entry.Evidence.Count; evidenceIndex++)
                {
                    AssertPublishedTextIsSanitized(candidate.Entry.Evidence[evidenceIndex].Summary);
                }
            }
        }

        /// <summary>XMZADD 20260901 创建一次覆盖采购订单字段范围的缩写推理请求。</summary>
        private static AbbreviationInferenceRequest CreateRequest()
        {
            return new AbbreviationInferenceRequest
            {
                Abbreviation = "ABC",
                SchemaName = "dbo",
                TableName = "dbo.T_ORDER",
                FieldName = "FABC",
                ConfirmedModule = "采购",
                NamingRuleMeaning = "ABC",
                SourceEvidence = new List<SourceEvidence>(),
                ConfirmedGlossary = new List<AbbreviationEntry>()
            };
        }

        /// <summary>XMZADD 20260901 创建带绝对源码路径的证据，用于验证发布前脱敏。</summary>
        private static SourceEvidence CreateSourceEvidence(string candidate)
        {
            return new SourceEvidence
            {
                ObjectName = "T_ORDER",
                FieldName = "FABC",
                ChineseNameCandidate = candidate,
                Evidence = new EvidenceItem
                {
                    SourceType = "EOS源码",
                    SourcePath = "C:\\Private\\EOS\\OrderEntity.vb",
                    SourceLine = 20,
                    RuleName = "EntityProperty",
                    Explanation = "字段中文注释"
                }
            };
        }

        /// <summary>XMZADD 20260901 创建指定缩写的已确认共享词典条目。</summary>
        private static AbbreviationEntry CreateGlossary(string abbreviation, string meaning)
        {
            return new AbbreviationEntry
            {
                Abbreviation = abbreviation,
                ChineseMeaning = meaning,
                ModuleScope = "采购",
                TableScope = "dbo.T_ORDER",
                Status = ConfidenceStatus.Confirmed,
                ConfidenceScore = 100,
                Evidence = new List<AbbreviationEvidence>()
            };
        }

        /// <summary>XMZADD 20260901 创建知识库精确字段翻译的最小稳定夹具。</summary>
        private static string CreateKnowledgeFixture()
        {
            string root = Path.Combine(Path.GetTempPath(), "SHB_ABBR_KB_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "全量表索引.md"),
                "| 顶级分类 | 子系统 | 表单 | 表单标识 | 表名称 | 实体名称 | 实体入口名 | 主键字段 | 字段数 | 物理字段数 | 引用表字段数 | 详情 |\r\n" +
                "| --- | --- | --- | --- | --- | --- | --- | --- | ---: | ---: | ---: | --- |\r\n" +
                "| 采购 | 采购管理 | 订单 | `PUR_ORDER` | `T_ORDER` | 单据头 | `ORDER` | `FID` | 1 | 1 | 0 | [采购.md](./采购.md#entity-1) |\r\n");
            File.WriteAllText(Path.Combine(root, "采购.md"),
                "<a id=\"entity-1\"></a>\r\n\r\n## T_ORDER — 单据头\r\n\r\n### 字段\r\n\r\n" +
                "| 标题 | 标识 | 实体属性 | 数据库字段 | 字段类型 | 引用对象 | 引用表 | 枚举类型内码 | 枚举名称 | 枚举项目 | 分表 |\r\n" +
                "| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |\r\n" +
                "| 知识库业务中心 | FABC | ABC | FABC | 文本 | | | | | | |\r\n");
            return root;
        }

        /// <summary>XMZADD 20260901 断言公开文本不包含测试哨兵、完整连接串或本机绝对目录。</summary>
        private static void AssertPublishedTextIsSanitized(string value)
        {
            string text = value ?? string.Empty;
            Assert.IsFalse(text.Contains("test-secret"));
            Assert.IsFalse(text.Contains("Pwd: demo"));
            Assert.IsFalse(text.Contains("demo-token"));
            Assert.IsFalse(text.Contains("demo-key"));
            Assert.IsFalse(text.Contains("Secret=demo"));
            Assert.IsFalse(text.Contains("Server=test;Password=demo"));
            Assert.IsFalse(text.Contains("authorization-private"));
            Assert.IsFalse(text.Contains("private-db"));
            Assert.IsFalse(text.Contains("private-user"));
            Assert.IsFalse(text.Contains("C:\\Private"));
        }

        /// <summary>XMZADD 20260901 在不使用 LINQ 的前提下判断受限 AI 上下文是否包含指定知识摘要。</summary>
        private static bool ContextContains(IList<string> context, string value)
        {
            if (context == null)
            {
                return false;
            }
            for (int index = 0; index < context.Count; index++)
            {
                if ((context[index] ?? string.Empty).IndexOf(value, StringComparison.Ordinal) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>XMZADD 20260901 提供不联网的可注入 AI 推理替身。</summary>
        private sealed class FakeAiInference : IAbbreviationAiInference
        {
            private readonly string _value;
            private readonly Exception _exception;

            public FakeAiInference(string value)
            {
                _value = value;
            }

            public FakeAiInference(Exception exception)
            {
                _exception = exception;
            }

            public int CallCount { get; private set; }
            public AbbreviationInferenceContext LastContext { get; private set; }

            public string Infer(AbbreviationInferenceContext context)
            {
                CallCount++;
                LastContext = context;
                if (_exception != null)
                {
                    throw _exception;
                }
                return _value;
            }
        }
    }
}
