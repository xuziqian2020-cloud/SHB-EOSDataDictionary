using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHB.EosDataDictionary.Models;
using SHB.EosDataDictionary.Services;

namespace SHB.EosDataDictionary.Tests
{
    /// <summary>XMZADD 20260831 验证 AI 推测使用默认服务商的结构上下文并生成可追溯结果。</summary>
    [TestClass]
    public sealed class AiInferenceServiceTests
    {
        /// <summary>XMZADD 20260831 验证兼容接口返回的 JSON 会被解析为字段中文名、业务含义和推测证据。</summary>
        [TestMethod]
        public void Infer_ParsesJsonResultAsAiGuess()
        {
            var provider = new AiProviderConfiguration
            {
                Id = "deepseek",
                Name = "DeepSeek",
                Protocol = AiProviderProtocol.OpenAiCompatible,
                Endpoint = "https://api.deepseek.com",
                Model = "deepseek-chat",
                IsDefault = true,
                IsEnabled = true
            };
            var table = new TableMetadata
            {
                ObjectName = "T_PURCHASE_ORDER",
                ChineseName = new MetadataValue { Value = "推测：采购订单", Status = ConfidenceStatus.Guessed },
                Fields = new List<FieldMetadata>()
            };
            var field = new FieldMetadata
            {
                FieldName = "FBILLNO",
                DataType = "nvarchar",
                LengthText = "50",
                OwnerTableName = "T_PURCHASE_ORDER"
            };
            var service = new AiInferenceService(new FixedAiChatClient(
                "{\"chineseName\":\"采购单号\",\"businessMeaning\":\"采购订单的业务单据编号\",\"usage\":\"用于查询和单据关联\",\"confidenceScore\":82,\"alternatives\":[\"订单编号\"],\"evidence\":\"字段名 BillNo、表名 Purchase Order\"}"));

            AiExplanationResult result = service.Infer(provider, "dev|SHB", table, field);

            Assert.AreEqual("采购单号", result.ChineseName);
            Assert.AreEqual("采购订单的业务单据编号", result.BusinessMeaning);
            Assert.AreEqual("用于查询和单据关联", result.Usage);
            Assert.AreEqual(82, result.ConfidenceScore);
            Assert.AreEqual("deepseek", result.ProviderId);
            Assert.AreEqual("AI推测", result.Evidence[0].SourceType);
            Assert.IsTrue(result.Evidence[0].Explanation.IndexOf("BillNo", StringComparison.Ordinal) >= 0);
        }

        /// <summary>XMZADD 20260831 验证未知表也可由 AI 根据物理表名和已有结构线索生成中文推测。</summary>
        [TestMethod]
        public void InferTable_ParsesTableChineseNameAndBusinessMeaning()
        {
            var provider = new AiProviderConfiguration { Id = "deepseek", Name = "DeepSeek", IsEnabled = true, Model = "deepseek-chat" };
            var table = new TableMetadata { ObjectName = "T_PURCHASE_ORDER", ObjectType = "TABLE", Fields = new List<FieldMetadata>() };
            var service = new AiInferenceService(new FixedAiChatClient(
                "{\"chineseName\":\"采购订单\",\"businessMeaning\":\"采购业务订单主数据\",\"usage\":\"承载采购订单头信息\",\"confidenceScore\":78,\"alternatives\":[],\"evidence\":\"表名 Purchase Order\"}"));

            AiExplanationResult result = service.InferTable(provider, "dev|SHB", table);

            Assert.AreEqual("采购订单", result.ChineseName);
            Assert.AreEqual("采购业务订单主数据", result.BusinessMeaning);
            Assert.AreEqual(string.Empty, result.FieldName);
        }

        /// <summary>XMZADD 20260831 验证批量推测仅调用一次模型，并以客户端分配的标识在乱序响应中回写原始表字段。</summary>
        [TestMethod]
        public void InferBatch_MapsUnorderedItemsByGeneratedIdAndCallsClientOnce()
        {
            var provider = new AiProviderConfiguration { Id = "deepseek", Name = "DeepSeek", IsEnabled = true, Model = "deepseek-chat" };
            var firstTable = new TableMetadata
            {
                ObjectName = "T_ORDER",
                ObjectType = "TABLE",
                Fields = new List<FieldMetadata> { new FieldMetadata { FieldName = "FID", DataType = "bigint" } }
            };
            var secondTable = new TableMetadata { ObjectName = "T_ORDER_ENTRY", ObjectType = "TABLE" };
            var secondField = new FieldMetadata
            {
                FieldName = "FQTY",
                OwnerTableName = secondTable.ObjectName,
                DataType = "decimal",
                ChineseName = new MetadataValue
                {
                    Evidence = new List<EvidenceItem>
                    {
                        new EvidenceItem { SourceType = "代码依据", Explanation = "数量聚合口径" }
                    }
                }
            };
            var client = new FixedAiChatClient(
                "{\"items\":[{\"id\":\"target-0002\",\"objectName\":\"T_FORGED\",\"fieldName\":\"F_FORGED\",\"chineseName\":\"数量\",\"businessMeaning\":\"单据明细数量\",\"usage\":\"数量统计\",\"confidenceScore\":88,\"alternatives\":[],\"evidence\":\"字段 Qty\"},{\"id\":\"target-0001\",\"objectName\":\"T_FORGED\",\"chineseName\":\"采购订单\",\"businessMeaning\":\"采购业务单据\",\"usage\":\"单据头\",\"confidenceScore\":80,\"alternatives\":[],\"evidence\":\"表名 Order\"}]}");
            var service = new AiInferenceService(client);
            var targets = new List<AiInferenceTarget>
            {
                new AiInferenceTarget(firstTable, null),
                new AiInferenceTarget(secondTable, secondField)
            };

            IList<AiExplanationResult> results = service.InferBatch(provider, "dev|SHB", targets);

            Assert.AreEqual(1, client.CallCount);
            StringAssert.Contains(client.UserPrompt, "target-0001");
            StringAssert.Contains(client.UserPrompt, "target-0002");
            StringAssert.Contains(client.UserPrompt, "FID bigint");
            StringAssert.Contains(client.UserPrompt, "数量聚合口径");
            Assert.AreEqual(2, results.Count);
            Assert.AreEqual("T_ORDER", results[0].ObjectName);
            Assert.AreEqual(string.Empty, results[0].FieldName);
            Assert.AreEqual("采购订单", results[0].ChineseName);
            Assert.AreEqual("T_ORDER_ENTRY", results[1].ObjectName);
            Assert.AreEqual("FQTY", results[1].FieldName);
            Assert.AreEqual("数量", results[1].ChineseName);
            Assert.AreEqual("deepseek", results[1].ProviderId);
            Assert.AreEqual("AI推测", results[1].Evidence[0].SourceType);
        }

        /// <summary>XMZADD 20260831 验证模型漏返回任一批内标识时整批结果被丢弃，避免部分推测进入本地字典。</summary>
        [TestMethod]
        public void InferBatch_MissingId_ReturnsNoPartialResults()
        {
            AssertInvalidBatchResponseReturnsNoResults(
                "{\"items\":[{\"id\":\"target-0001\",\"chineseName\":\"采购订单\",\"businessMeaning\":\"采购业务单据\",\"usage\":\"单据头\",\"confidenceScore\":80,\"alternatives\":[],\"evidence\":\"表名 Order\"}]}");
        }

        /// <summary>XMZADD 20260831 验证模型重复返回同一批内标识时整批结果被丢弃，防止一个目标覆盖多个结果位置。</summary>
        [TestMethod]
        public void InferBatch_DuplicateId_ReturnsNoPartialResults()
        {
            AssertInvalidBatchResponseReturnsNoResults(
                "{\"items\":[{\"id\":\"target-0001\",\"chineseName\":\"采购订单\",\"businessMeaning\":\"采购业务单据\",\"usage\":\"单据头\",\"confidenceScore\":80,\"alternatives\":[],\"evidence\":\"表名 Order\"},{\"id\":\"target-0001\",\"chineseName\":\"数量\",\"businessMeaning\":\"单据明细数量\",\"usage\":\"数量统计\",\"confidenceScore\":88,\"alternatives\":[],\"evidence\":\"字段 Qty\"}]}");
        }

        /// <summary>XMZADD 20260831 验证模型返回未知批内标识时整批结果被丢弃，禁止以对象名或字段名猜测落库身份。</summary>
        [TestMethod]
        public void InferBatch_UnknownId_ReturnsNoPartialResults()
        {
            AssertInvalidBatchResponseReturnsNoResults(
                "{\"items\":[{\"id\":\"target-0001\",\"chineseName\":\"采购订单\",\"businessMeaning\":\"采购业务单据\",\"usage\":\"单据头\",\"confidenceScore\":80,\"alternatives\":[],\"evidence\":\"表名 Order\"},{\"id\":\"target-9999\",\"chineseName\":\"数量\",\"businessMeaning\":\"单据明细数量\",\"usage\":\"数量统计\",\"confidenceScore\":88,\"alternatives\":[],\"evidence\":\"字段 Qty\"}]}");
        }

        /// <summary>XMZADD 20260901 验证知识库模糊字段条目只作为受限消歧上下文进入 AI，不直接冒充已确认名称。</summary>
        [TestMethod]
        public void Infer_KnowledgeBaseFuzzyContext_IsBoundedAndSentToAi()
        {
            string root = CreateKnowledgeFixture();
            try
            {
                var provider = new AiProviderConfiguration { Id = "deepseek", Name = "DeepSeek", IsEnabled = true, Model = "deepseek-chat" };
                var table = new TableMetadata
                {
                    SchemaName = "dbo",
                    ObjectName = "T_ORDER",
                    ObjectType = "TABLE",
                    Fields = new List<FieldMetadata>()
                };
                var field = new FieldMetadata { FieldName = "FABC", OwnerTableName = "T_ORDER", DataType = "nvarchar", LengthText = "40" };
                var client = new FixedAiChatClient(
                    "{\"chineseName\":\"业务中心\",\"businessMeaning\":\"订单业务中心\",\"usage\":\"业务识别\",\"confidenceScore\":70,\"alternatives\":[],\"evidence\":\"知识库模糊条目\"}");
                var service = new AiInferenceService(client, new EosKnowledgeBaseTranslationService(root));

                service.Infer(provider, "dev|SHB", table, field);

                StringAssert.Contains(client.UserPrompt, "EOS知识库受限上下文");
                StringAssert.Contains(client.UserPrompt, "消歧依据");
                StringAssert.Contains(client.UserPrompt, "知识库模糊业务中心");
                Assert.IsFalse(client.UserPrompt.Contains(root));
                Assert.IsFalse(client.UserPrompt.Contains(new string('长', 241)));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        /// <summary>XMZADD 20260901 验证单项推测会统一移除凭据、本机绝对路径和超量证据，防止本地环境信息进入模型提示词。</summary>
        [TestMethod]
        public void Infer_SensitiveAndManyEvidence_AreSanitizedAndBounded()
        {
            var provider = new AiProviderConfiguration { Id = "deepseek", Name = "DeepSeek", IsEnabled = true, Model = "deepseek-chat" };
            var evidence = new List<EvidenceItem>();
            evidence.Add(new EvidenceItem { SourceType = "代码依据", Explanation = "Authorization: Bearer private-token" });
            evidence.Add(new EvidenceItem { SourceType = "代码依据", Explanation = "C:\\Private\\customer.sql 中的字段说明" });
            for (int index = 0; index < 12; index++)
            {
                evidence.Add(new EvidenceItem { SourceType = "代码依据", Explanation = "安全依据" + index.ToString() });
            }
            var table = new TableMetadata
            {
                ObjectName = "T_ORDER",
                ModuleName = new MetadataValue { Value = "Server=private-server;Database=private-db" },
                BusinessMeaning = new MetadataValue { Value = "说明Api Key = private-api-key" },
                EntityName = new MetadataValue { Value = "Api-Key = private-hyphen-key" },
                ChineseName = new MetadataValue { Value = "采购订单", Evidence = evidence }
            };
            var field = new FieldMetadata
            {
                FieldName = "FBILLNO",
                DataType = "nvarchar",
                LengthText = "50",
                BusinessMeaning = new MetadataValue { Value = "订单\u2028编号" }
            };
            var client = new FixedAiChatClient(
                "{\"chineseName\":\"采购单号\",\"businessMeaning\":\"采购订单编号\",\"usage\":\"检索\",\"confidenceScore\":80,\"alternatives\":[],\"evidence\":\"字段结构\"}");
            var service = new AiInferenceService(client);

            service.Infer(provider, "dev|SHB|Password=private", table, field);

            Assert.AreEqual(1, client.CallCount);
            Assert.IsFalse(client.UserPrompt.Contains("private-token"));
            Assert.IsFalse(client.UserPrompt.Contains("private-server"));
            Assert.IsFalse(client.UserPrompt.Contains("private-api-key"));
            Assert.IsFalse(client.UserPrompt.Contains("private-hyphen-key"));
            Assert.IsFalse(client.UserPrompt.Contains("C:\\Private"));
            Assert.IsFalse(client.UserPrompt.Contains("\u2028"));
            Assert.IsTrue(CountOccurrences(client.UserPrompt, "代码依据 -") <= 4);
            Assert.IsTrue(Encoding.UTF8.GetByteCount(client.UserPrompt) <= 32 * 1024);
        }

        /// <summary>XMZADD 20260901 验证批量目标硬上限在网络调用前生效，避免被持久化的大批次设置绕过服务边界。</summary>
        [TestMethod]
        public void InferBatch_MoreThanFiftyTargets_ThrowsBeforeClientCall()
        {
            var provider = new AiProviderConfiguration { Id = "deepseek", Name = "DeepSeek", IsEnabled = true, Model = "deepseek-chat" };
            var targets = new List<AiInferenceTarget>();
            for (int index = 0; index < 51; index++)
            {
                targets.Add(new AiInferenceTarget(new TableMetadata { ObjectName = "T_ORDER_" + index.ToString() }, null));
            }
            var client = new FixedAiChatClient("{}");
            var service = new AiInferenceService(client);

            Assert.ThrowsException<ArgumentException>(() => service.InferBatch(provider, "dev|SHB", targets));

            Assert.AreEqual(0, client.CallCount);
        }

        /// <summary>XMZADD 20260901 验证批量 JSON 与单项提示使用相同脱敏规则，不会因序列化结构不同泄漏连接信息或绝对路径。</summary>
        [TestMethod]
        public void InferBatch_SensitiveContext_IsSanitized()
        {
            var provider = new AiProviderConfiguration { Id = "deepseek", Name = "DeepSeek", IsEnabled = true, Model = "deepseek-chat" };
            var table = new TableMetadata
            {
                ObjectName = "T_ORDER",
                ModuleName = new MetadataValue { Value = "Data Source=private-server" },
                BusinessMeaning = new MetadataValue { Value = "Secret : private-batch-secret" },
                EntityName = new MetadataValue { Value = "X-Api-Key: private-x-api-key" },
                ChineseName = new MetadataValue
                {
                    Value = "采购订单",
                    Evidence = new List<EvidenceItem>
                    {
                        new EvidenceItem { SourceType = "代码依据", Explanation = "C:\\Private\\order.vb 中的业务说明" },
                        new EvidenceItem { SourceType = "代码依据", Explanation = "token: private-token" }
                    }
                }
            };
            var client = new FixedAiChatClient(
                "{\"items\":[{\"id\":\"target-0001\",\"chineseName\":\"采购订单\",\"businessMeaning\":\"采购业务单据\",\"usage\":\"单据头\",\"confidenceScore\":80,\"alternatives\":[],\"evidence\":\"表结构\"}]}");
            var service = new AiInferenceService(client);

            service.InferBatch(provider, "dev|SHB|Authorization: Bearer private", new List<AiInferenceTarget>
            {
                new AiInferenceTarget(table, null)
            });

            Assert.AreEqual(1, client.CallCount);
            Assert.IsFalse(client.UserPrompt.Contains("private-server"));
            Assert.IsFalse(client.UserPrompt.Contains("private-token"));
            Assert.IsFalse(client.UserPrompt.Contains("private-batch-secret"));
            Assert.IsFalse(client.UserPrompt.Contains("private-x-api-key"));
            Assert.IsFalse(client.UserPrompt.Contains("C:\\\\Private"));
        }

        /// <summary>XMZADD 20260901 验证合法但异常庞大的批量上下文在发送前按 UTF-8 容量整体拒绝，避免外部调用成本失控。</summary>
        [TestMethod]
        public void InferBatch_ExcessiveSanitizedPrompt_ThrowsBeforeClientCall()
        {
            var provider = new AiProviderConfiguration { Id = "deepseek", Name = "DeepSeek", IsEnabled = true, Model = "deepseek-chat" };
            var targets = new List<AiInferenceTarget>();
            string longText = new string('业', 256);
            for (int index = 0; index < 50; index++)
            {
                var table = new TableMetadata
                {
                    ObjectName = "T_ORDER_" + index.ToString() + longText,
                    ObjectType = longText,
                    ChineseName = new MetadataValue { Value = longText },
                    ModuleName = new MetadataValue { Value = longText },
                    EntityName = new MetadataValue { Value = longText },
                    BusinessMeaning = new MetadataValue { Value = longText }
                };
                var field = new FieldMetadata
                {
                    FieldName = "FITEM_" + index.ToString() + longText,
                    DataType = longText,
                    LengthText = longText,
                    ChineseName = new MetadataValue { Value = longText },
                    EntityPropertyName = new MetadataValue { Value = longText },
                    BusinessMeaning = new MetadataValue { Value = longText },
                    Usage = new MetadataValue { Value = longText },
                    EnumName = new MetadataValue { Value = longText },
                    RelationSummary = new MetadataValue { Value = longText }
                };
                targets.Add(new AiInferenceTarget(table, field));
            }
            var client = new FixedAiChatClient("{}");
            var service = new AiInferenceService(client);

            Assert.ThrowsException<InvalidOperationException>(() => service.InferBatch(provider, "dev|SHB", targets));

            Assert.AreEqual(0, client.CallCount);
        }

        /// <summary>XMZADD 20260831 使用两项最小目标验证批量异常时不会返回任何已解析结果。</summary>
        private static void AssertInvalidBatchResponseReturnsNoResults(string response)
        {
            var provider = new AiProviderConfiguration { Id = "deepseek", Name = "DeepSeek", IsEnabled = true, Model = "deepseek-chat" };
            var firstTable = new TableMetadata { ObjectName = "T_ORDER", ObjectType = "TABLE" };
            var secondTable = new TableMetadata { ObjectName = "T_ORDER_ENTRY", ObjectType = "TABLE" };
            var secondField = new FieldMetadata { FieldName = "FQTY", OwnerTableName = secondTable.ObjectName, DataType = "decimal" };
            var service = new AiInferenceService(new FixedAiChatClient(response));
            var targets = new List<AiInferenceTarget>
            {
                new AiInferenceTarget(firstTable, null),
                new AiInferenceTarget(secondTable, secondField)
            };

            IList<AiExplanationResult> results = service.InferBatch(provider, "dev|SHB", targets);

            Assert.AreEqual(0, results.Count);
        }

        /// <summary>XMZADD 20260901 创建供 AI 推理验证的知识库模糊字段夹具。</summary>
        private static string CreateKnowledgeFixture()
        {
            string root = Path.Combine(Path.GetTempPath(), "SHB_AI_KB_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "全量表索引.md"),
                "| 顶级分类 | 子系统 | 表单 | 表单标识 | 表名称 | 实体名称 | 实体入口名 | 主键字段 | 字段数 | 物理字段数 | 引用表字段数 | 详情 |\r\n" +
                "| --- | --- | --- | --- | --- | --- | --- | --- | ---: | ---: | ---: | --- |\r\n" +
                "| 采购 | 采购管理 | 订单 | `ORDER` | `T_ORDER` | 单据头 | `ORDER` | `FID` | 1 | 1 | 0 | [采购.md](./采购.md#entity-1) |\r\n",
                Encoding.UTF8);
            File.WriteAllText(Path.Combine(root, "采购.md"),
                "<a id=\"entity-1\"></a>\r\n\r\n## T_ORDER — 单据头\r\n\r\n### 字段\r\n\r\n" +
                "| 标题 | 标识 | 实体属性 | 数据库字段 | 字段类型 | 引用对象 | 引用表 | 枚举类型内码 | 枚举名称 | 枚举项目 | 分表 |\r\n" +
                "| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |\r\n" +
                "| 知识库模糊业务中心" + new string('长', 300) + " | FABC_DETAIL | ABC | FABC_DETAIL | 文本 | | | | | | |\r\n",
                Encoding.UTF8);
            return root;
        }

        /// <summary>XMZADD 20260901 统计提示词中的证据标签数量，用于验证跨元数据字段共享统一证据预算。</summary>
        private static int CountOccurrences(string value, string searchValue)
        {
            int count = 0;
            int startIndex = 0;
            while (!string.IsNullOrEmpty(value) && !string.IsNullOrEmpty(searchValue))
            {
                int foundIndex = value.IndexOf(searchValue, startIndex, StringComparison.Ordinal);
                if (foundIndex < 0)
                {
                    break;
                }
                count++;
                startIndex = foundIndex + searchValue.Length;
            }
            return count;
        }

        /// <summary>XMZADD 20260831 提供固定 AI 文本，隔离外部网络以验证结果解析规则。</summary>
        private sealed class FixedAiChatClient : IAiChatClient
        {
            private readonly string _response;

            public int CallCount { get; private set; }
            public string UserPrompt { get; private set; }

            public FixedAiChatClient(string response)
            {
                _response = response;
            }

            public string Complete(AiProviderConfiguration provider, string systemPrompt, string userPrompt)
            {
                CallCount++;
                UserPrompt = userPrompt;
                return _response;
            }
        }
    }
}
