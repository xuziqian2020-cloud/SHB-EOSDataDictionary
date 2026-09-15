using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHB.EosDataDictionary.Models;
using SHB.EosDataDictionary.Services;

namespace SHB.EosDataDictionary.Tests
{
    /// <summary>XMZADD 20260901 验证正式结构名称推理组合共享缩写、知识上下文和可选 AI 且只产生推测结果。</summary>
    [TestClass]
    public sealed class StructureNameInferenceServiceTests
    {
        /// <summary>XMZADD 20260901 验证已确认共享缩写可补全字段局部翻译，但不会冒充人工或高可信发布证据。</summary>
        [TestMethod]
        public void Enrich_ConfirmedAbbreviation_UpdatesNameAsGuessed()
        {
            SnapshotData snapshot = CreateSnapshot("FABC_STATUS", "ABC状态");
            snapshot.Abbreviations.Add(new AbbreviationEntry
            {
                Abbreviation = "ABC",
                ChineseMeaning = "业务中心",
                ModuleScope = "采购",
                TableScope = "T_ORDER",
                Status = ConfidenceStatus.Confirmed,
                ConfidenceScore = 100,
                Evidence = new List<AbbreviationEvidence>()
            });

            new StructureNameInferenceService(null).Enrich(
                snapshot, new List<SourceEvidence>(), null, CancellationToken.None);

            Assert.AreEqual(string.Empty, snapshot.Tables[0].Fields[0].ChineseName.Value);
            MetadataValue value = snapshot.Tables[0].Fields[0].SuggestedChineseName;
            Assert.AreEqual("业务中心状态", value.Value);
            Assert.AreEqual(ConfidenceStatus.Guessed, value.Status);
            Assert.IsFalse(value.IsManualOverride);
            Assert.IsFalse(value.IsLocked);
            Assert.IsNull(value.OriginalAutomaticValue);
        }

        /// <summary>XMZADD 20260901 验证知识库模糊条目经过缩写上下文交给可选 AI，最终仍标记为等待确认的推测。</summary>
        [TestMethod]
        public void Enrich_FuzzyKnowledgeContext_ReachesOptionalAiAndRemainsGuessed()
        {
            string root = CreateKnowledgeFixture();
            try
            {
                SnapshotData snapshot = CreateSnapshot("FABC_DETAIL", "ABC明细");
                var ai = new RecordingAbbreviationAi("业务中心");
                var knowledgeBase = new EosKnowledgeBaseTranslationService(root);

                new StructureNameInferenceService(ai).Enrich(
                    snapshot, new List<SourceEvidence>(), knowledgeBase, CancellationToken.None);

                Assert.AreEqual(1, ai.CallCount);
                Assert.IsTrue(ContextContains(ai.LastContext.EvidenceContext, "知识库："));
                Assert.AreEqual(string.Empty, snapshot.Tables[0].Fields[0].ChineseName.Value);
                Assert.AreEqual("业务中心明细", snapshot.Tables[0].Fields[0].SuggestedChineseName.Value);
                Assert.AreEqual(ConfidenceStatus.Guessed, snapshot.Tables[0].Fields[0].SuggestedChineseName.Status);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        /// <summary>XMZADD 20260902 验证结构扫描可关闭外部 AI 调用，避免单次快照被多个网络超时串行阻塞。</summary>
        [TestMethod]
        public void Enrich_ExternalAiDisabled_DoesNotInvokeAi()
        {
            SnapshotData snapshot = CreateSnapshot("FABC_STATUS", "ABC状态");
            var ai = new RecordingAbbreviationAi("业务中心");

            new StructureNameInferenceService(ai, false).Enrich(
                snapshot, new List<SourceEvidence>(), null, CancellationToken.None);

            Assert.AreEqual(0, ai.CallCount);
        }

        /// <summary>XMZADD 20260901 创建带采购模块和一个未知缩写字段的结构快照。</summary>
        private static SnapshotData CreateSnapshot(string fieldName, string currentName)
        {
            var table = new TableMetadata
            {
                SchemaName = "dbo",
                ObjectName = "T_ORDER",
                ObjectType = "TABLE",
                Category = DictionaryTableCategory.Business,
                ModuleName = new MetadataValue { Value = "采购", Status = ConfidenceStatus.Guessed },
                Fields = new List<FieldMetadata>
                {
                    new FieldMetadata
                    {
                        OwnerTableName = "T_ORDER",
                        FieldName = fieldName,
                        ChineseName = new MetadataValue
                        {
                            Value = currentName,
                            Status = ConfidenceStatus.Guessed,
                            Evidence = new List<EvidenceItem>()
                        }
                    }
                }
            };
            return new SnapshotData { Tables = new List<TableMetadata> { table } };
        }

        /// <summary>XMZADD 20260901 创建物理字段近似命中的最小知识库。</summary>
        private static string CreateKnowledgeFixture()
        {
            string root = Path.Combine(Path.GetTempPath(), "SHB_STRUCTURE_NAME_KB_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "全量表索引.md"),
                "| 顶级分类 | 子系统 | 表单 | 表单标识 | 表名称 | 实体名称 | 实体入口名 | 主键字段 | 字段数 | 物理字段数 | 引用表字段数 | 详情 |\r\n" +
                "| --- | --- | --- | --- | --- | --- | --- | --- | ---: | ---: | ---: | --- |\r\n" +
                "| 采购 | 采购 | 订单 | `ORDER` | `T_ORDER` | 单据头 | `ORDER` | `FID` | 1 | 1 | 0 | [采购.md](./采购.md#entity-1) |\r\n",
                Encoding.UTF8);
            File.WriteAllText(Path.Combine(root, "采购.md"),
                "<a id=\"entity-1\"></a>\r\n\r\n## T_ORDER — 单据头\r\n\r\n### 字段\r\n\r\n" +
                "| 标题 | 标识 | 实体属性 | 数据库字段 | 字段类型 |\r\n" +
                "| --- | --- | --- | --- | --- |\r\n" +
                "| 知识库业务中心 | FABC | ABC | FABC | 文本 |\r\n",
                Encoding.UTF8);
            return root;
        }

        /// <summary>XMZADD 20260901 判断缩写 AI 的受限证据上下文是否包含指定摘要。</summary>
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

        /// <summary>XMZADD 20260901 记录正式结构流水线传给 AI 的缩写上下文并返回固定中文词义。</summary>
        private sealed class RecordingAbbreviationAi : IAbbreviationAiInference
        {
            private readonly string _value;

            /// <summary>XMZADD 20260901 创建不联网的固定缩写 AI 替身。</summary>
            public RecordingAbbreviationAi(string value)
            {
                _value = value;
            }

            public int CallCount { get; private set; }
            public AbbreviationInferenceContext LastContext { get; private set; }

            /// <summary>XMZADD 20260901 保存本次受限上下文并返回固定候选。</summary>
            public string Infer(AbbreviationInferenceContext context)
            {
                CallCount++;
                LastContext = context;
                return _value;
            }
        }
    }
}
