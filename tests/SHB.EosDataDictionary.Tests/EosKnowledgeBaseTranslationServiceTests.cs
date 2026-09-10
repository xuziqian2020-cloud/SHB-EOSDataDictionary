using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHB.EosDataDictionary.Models;
using SHB.EosDataDictionary.Services;

namespace SHB.EosDataDictionary.Tests
{
    /// <summary>XMZADD 20260901 验证 EOS 项目知识库只以精确物理表字段条目生成可公开的翻译证据。</summary>
    [TestClass]
    public sealed class EosKnowledgeBaseTranslationServiceTests
    {
        /// <summary>XMZADD 20260901 验证多个业务对象给出相同标题时仍可形成唯一高可信候选。</summary>
        [TestMethod]
        public void TranslateField_ExactEntriesWithSameTitle_ReturnsKnowledgeBaseCandidate()
        {
            string root = CreateFixture();
            try
            {
                var service = new EosKnowledgeBaseTranslationService(root);

                KnowledgeBaseTranslationResult result = service.TranslateField("dbo", "T_BD_MATERIAL", "FNAME", null);

                Assert.IsNotNull(result.Candidate);
                Assert.AreEqual("名称", result.Candidate.Value);
                Assert.AreEqual(ConfidenceStatus.KnowledgeBaseEvidence, result.Candidate.Status);
                Assert.IsTrue(result.Candidate.Evidence.Count > 0);
                Assert.IsFalse(Path.IsPathRooted(result.Candidate.Evidence[0].SourcePath));
                Assert.IsFalse(result.Candidate.Evidence[0].SourcePath.Contains(root));
                Assert.IsTrue(result.Candidate.Evidence[0].SourceLine > 0);
                Assert.IsTrue(result.Candidate.Evidence[0].Explanation.Length <= 240);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        /// <summary>XMZADD 20260901 验证冲突标题只能由已确认模块消歧，未消歧时仅进入受限 AI 上下文。</summary>
        [TestMethod]
        public void TranslateField_ConflictingTitles_RequiresConfirmedModule()
        {
            string root = CreateFixture();
            try
            {
                var service = new EosKnowledgeBaseTranslationService(root);

                KnowledgeBaseTranslationResult ambiguous = service.TranslateField(null, "T_BD_MATERIAL", "FSTATUS", null);
                KnowledgeBaseTranslationResult resolved = service.TranslateField(null, "T_BD_MATERIAL", "FSTATUS", "基础管理");

                Assert.IsNull(ambiguous.Candidate);
                Assert.IsTrue(ambiguous.AiContext.Count > 0);
                Assert.IsTrue(ambiguous.AiContext.Count <= 8);
                Assert.AreEqual("启用状态", resolved.Candidate.Value);
                Assert.AreEqual(ConfidenceStatus.KnowledgeBaseEvidence, resolved.Candidate.Status);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        /// <summary>XMZADD 20260901 验证近似字段命中只作为 AI 短上下文，不能直接覆盖字段名称。</summary>
        [TestMethod]
        public void TranslateField_FuzzyFieldMatch_OnlyReturnsAiContext()
        {
            string root = CreateFixture();
            try
            {
                var service = new EosKnowledgeBaseTranslationService(root);

                KnowledgeBaseTranslationResult result = service.TranslateField(null, "T_BD_MATERIAL", "NAME", null);

                Assert.IsNull(result.Candidate);
                Assert.IsTrue(result.AiContext.Count > 0);
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

        /// <summary>XMZADD 20260901 验证知识库缺失或索引损坏时安静降级，不阻断其他翻译来源。</summary>
        [TestMethod]
        public void TranslateField_MissingOrDamagedKnowledgeBase_ReturnsEmptyResult()
        {
            string missingRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            var missingService = new EosKnowledgeBaseTranslationService(missingRoot);

            KnowledgeBaseTranslationResult missing = missingService.TranslateField(null, "T_TEST", "FNAME", null);

            Assert.IsNull(missing.Candidate);
            Assert.AreEqual(0, missing.AiContext.Count);

            string damagedRoot = Path.Combine(Path.GetTempPath(), "SHB_KB_DAMAGED_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(damagedRoot);
            try
            {
                File.WriteAllText(Path.Combine(damagedRoot, "全量表索引.md"), "这不是实体索引", Encoding.UTF8);
                var damagedService = new EosKnowledgeBaseTranslationService(damagedRoot);

                KnowledgeBaseTranslationResult damaged = damagedService.TranslateField(null, "T_TEST", "FNAME", null);

                Assert.IsNull(damaged.Candidate);
                Assert.AreEqual(0, damaged.AiContext.Count);
            }
            finally
            {
                Directory.Delete(damagedRoot, true);
            }
        }

        /// <summary>XMZADD 20260901 验证索引引用的模块内容损坏会显式标记知识库降级。</summary>
        [TestMethod]
        public void TranslateField_ReferencedModuleDamaged_MarksUnavailable()
        {
            string root = CreateRecoverableModuleFixture();
            try
            {
                File.WriteAllText(Path.Combine(root, "恢复模块.md"), "损坏模块，没有实体锚点", Encoding.UTF8);
                var service = new EosKnowledgeBaseTranslationService(root);

                KnowledgeBaseTranslationResult result = service.TranslateField(null, "T_RECOVER", "FNAME", null);

                Assert.IsNull(result.Candidate);
                Assert.AreEqual(0, result.AiContext.Count);
                Assert.IsFalse(service.IsAvailable);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        /// <summary>XMZADD 20260901 验证同一表先命中正常模块再遇到损坏模块时整次查询返回空，不泄露失败前的局部候选。</summary>
        [TestMethod]
        public void TranslateField_ValidThenDamagedModule_ReturnsNoPartialCandidate()
        {
            string root = CreatePartiallyDamagedTableFixture();
            try
            {
                var service = new EosKnowledgeBaseTranslationService(root);

                KnowledgeBaseTranslationResult result = service.TranslateField(null, "T_PARTIAL", "FNAME", null);

                Assert.IsNull(result.Candidate);
                Assert.AreEqual(0, result.AiContext.Count);
                Assert.IsFalse(service.IsAvailable);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        /// <summary>XMZADD 20260901 验证知识库所有文件的实际读取字节受全局预算约束。</summary>
        [TestMethod]
        public void TranslateField_TotalByteBudgetExceeded_MarksUnavailable()
        {
            string root = CreateFixture();
            try
            {
                var service = new EosKnowledgeBaseTranslationService(root);
                SetPrivateField(service, "_totalKnowledgeBytes", (128L * 1024L * 1024L) - 1L);

                KnowledgeBaseTranslationResult result = service.TranslateField(null, "T_BD_MATERIAL", "FNAME", null);

                Assert.IsNull(result.Candidate);
                Assert.IsFalse(service.IsAvailable);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        /// <summary>XMZADD 20260901 验证跨模块累计字段数量超过全局上限时知识库显式降级。</summary>
        [TestMethod]
        public void TranslateField_TotalFieldBudgetExceeded_MarksUnavailable()
        {
            string root = CreateFixture();
            try
            {
                var service = new EosKnowledgeBaseTranslationService(root);
                service.TranslateField(null, "T_UNKNOWN", "FNAME", null);
                SetPrivateField(service, "_totalKnowledgeFieldCount", 500000);

                KnowledgeBaseTranslationResult result = service.TranslateField(null, "T_BD_MATERIAL", "FNAME", null);

                Assert.IsNull(result.Candidate);
                Assert.IsFalse(service.IsAvailable);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        /// <summary>XMZADD 20260901 验证知识库翻译会传播调用方取消，不把取消误判成可降级的知识库缺失。</summary>
        [TestMethod]
        public void TranslateField_PreCancelled_ThrowsOperationCanceledException()
        {
            var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var service = new EosKnowledgeBaseTranslationService(Path.GetTempPath());

            Assert.ThrowsException<OperationCanceledException>(() =>
                service.TranslateField(null, "T_TEST", "FNAME", null, cancellation.Token));
        }

        /// <summary>XMZADD 20260901 验证超大知识库索引在读取前安全降级，避免本机损坏文件占满内存。</summary>
        [TestMethod]
        public void TranslateField_OversizedIndex_ReturnsEmptyAndUnavailable()
        {
            string root = Path.Combine(Path.GetTempPath(), "SHB_KB_OVERSIZED_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string indexPath = Path.Combine(root, "全量表索引.md");
                using (FileStream stream = File.Create(indexPath))
                {
                    stream.SetLength((16L * 1024L * 1024L) + 1L);
                }
                var service = new EosKnowledgeBaseTranslationService(root);

                KnowledgeBaseTranslationResult result = service.TranslateField(null, "T_TEST", "FNAME", null);

                Assert.IsNull(result.Candidate);
                Assert.AreEqual(0, result.AiContext.Count);
                Assert.IsFalse(service.IsAvailable);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        /// <summary>XMZADD 20260901 验证首次读取模块即建立全部锚点索引，后续查询不再逐锚点扫描全文。</summary>
        [TestMethod]
        public void TranslateField_MultipleAnchorsInSameModule_AreIndexedInOneParse()
        {
            string root = CreateSharedModuleFixture();
            try
            {
                var service = new EosKnowledgeBaseTranslationService(root);

                KnowledgeBaseTranslationResult first = service.TranslateField(null, "T_FIRST", "FNAME", null);
                IDictionary sectionCache = GetSectionCache(service);

                Assert.AreEqual("第一名称", first.Candidate.Value);
                Assert.AreEqual(3, sectionCache.Count);
                Assert.IsTrue(sectionCache.Contains("共享模块.md#entity-1"));
                Assert.IsTrue(sectionCache.Contains("共享模块.md#entity-2"));
                Assert.IsTrue(sectionCache.Contains("共享模块.md#entity-3"));

                File.Delete(Path.Combine(root, "共享模块.md"));
                KnowledgeBaseTranslationResult second = service.TranslateField(null, "T_SECOND", "FNAME", null);
                KnowledgeBaseTranslationResult empty = service.TranslateField(null, "T_EMPTY", "FNAME", null);

                Assert.AreEqual("第二名称", second.Candidate.Value);
                Assert.IsNull(empty.Candidate);
                Assert.AreEqual(3, sectionCache.Count);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        /// <summary>XMZADD 20260901 验证单实例遇到模块缺失后保持事务降级，新实例可在文件修复后完整恢复。</summary>
        [TestMethod]
        public void TranslateField_ModuleAppearsAfterFirstMiss_RequiresFreshTransaction()
        {
            string root = CreateRecoverableModuleFixture();
            try
            {
                var service = new EosKnowledgeBaseTranslationService(root);

                KnowledgeBaseTranslationResult missing = service.TranslateField(null, "T_RECOVER", "FNAME", null);
                WriteRecoverableModule(root);
                KnowledgeBaseTranslationResult sameTransaction = service.TranslateField(null, "T_RECOVER", "FNAME", null);
                var freshService = new EosKnowledgeBaseTranslationService(root);
                KnowledgeBaseTranslationResult recovered = freshService.TranslateField(null, "T_RECOVER", "FNAME", null);

                Assert.IsNull(missing.Candidate);
                Assert.IsNull(sameTransaction.Candidate);
                Assert.IsFalse(service.IsAvailable);
                Assert.AreEqual("恢复名称", recovered.Candidate.Value);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        /// <summary>XMZADD 20260901 验证英文、过程说明和带等号格式的知识库标题只能进入 AI 上下文。</summary>
        [DataTestMethod]
        [DataRow("FENGLISH")]
        [DataRow("FPROCEDURE")]
        [DataRow("FFORMAT")]
        public void TranslateField_UnreliableExactTitle_OnlyReturnsAiContext(string fieldName)
        {
            string root = CreateUnreliableTitleFixture();
            try
            {
                var service = new EosKnowledgeBaseTranslationService(root);

                KnowledgeBaseTranslationResult result = service.TranslateField(null, "T_BAD_TITLE", fieldName, null);

                Assert.IsNull(result.Candidate);
                Assert.IsTrue(result.AiContext.Count > 0);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        /// <summary>XMZADD 20260901 验证可靠中文知识库标题仍可作为精确高可信名称。</summary>
        [TestMethod]
        public void TranslateField_ReliableExactTitle_ReturnsCandidate()
        {
            string root = CreateUnreliableTitleFixture();
            try
            {
                var service = new EosKnowledgeBaseTranslationService(root);

                KnowledgeBaseTranslationResult result = service.TranslateField(null, "T_BAD_TITLE", "FSTATUS", null);

                Assert.AreEqual("订单状态", result.Candidate.Value);
                Assert.AreEqual(ConfidenceStatus.KnowledgeBaseEvidence, result.Candidate.Status);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        /// <summary>XMZADD 20260901 创建包含同名标题、冲突标题与模块链接的最小知识库。</summary>
        private static string CreateFixture()
        {
            string root = Path.Combine(Path.GetTempPath(), "SHB_KB_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string index =
                "# 全量实体索引\r\n\r\n" +
                "| 顶级分类 | 子系统 | 表单 | 表单标识 | 表名称 | 实体名称 | 实体入口名 | 主键字段 | 字段数 | 物理字段数 | 引用表字段数 | 详情 |\r\n" +
                "| --- | --- | --- | --- | --- | --- | --- | --- | ---: | ---: | ---: | --- |\r\n" +
                "| PLM | 系统建模 | 物料同步 | `PLM_SYNC` | `T_BD_MATERIAL` | 单据头 | `MATERIAL` | `FID` | 2 | 2 | 0 | [03-PLM.md](./03-PLM.md#entity-1) |\r\n" +
                "| 基础管理 | 基础资料 | 物料 | `BD_MATERIAL` | `T_BD_MATERIAL` | 物料 | `MATERIAL` | `FID` | 2 | 2 | 0 | [08-基础管理.md](./08-基础管理.md#entity-2) |\r\n";
            string plm =
                "<a id=\"entity-1\"></a>\r\n\r\n" +
                "## T_BD_MATERIAL — 单据头\r\n\r\n" +
                "### 字段\r\n\r\n" +
                "| 标题 | 标识 | 实体属性 | 数据库字段 | 字段类型 | 引用对象 | 引用表 | 枚举类型内码 | 枚举名称 | 枚举项目 | 分表 |\r\n" +
                "| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |\r\n" +
                "| 名称 | FNAME | NAME | FNAME | 文本 | | | | | | |\r\n" +
                "| 审核状态 | FSTATUS | STATUS | FSTATUS | 文本 | | | | | | |\r\n";
            string baseData =
                "<a id=\"entity-2\"></a>\r\n\r\n" +
                "## T_BD_MATERIAL — 物料\r\n\r\n" +
                "### 字段\r\n\r\n" +
                "| 标题 | 标识 | 实体属性 | 数据库字段 | 字段类型 | 引用对象 | 引用表 | 枚举类型内码 | 枚举名称 | 枚举项目 | 分表 |\r\n" +
                "| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |\r\n" +
                "| 名称 | FNAME | NAME | FNAME | 文本 | | | | | | |\r\n" +
                "| 启用状态 | FSTATUS | STATUS | FSTATUS | 文本 | | | | | | |\r\n";
            File.WriteAllText(Path.Combine(root, "全量表索引.md"), index, Encoding.UTF8);
            File.WriteAllText(Path.Combine(root, "03-PLM.md"), plm, Encoding.UTF8);
            File.WriteAllText(Path.Combine(root, "08-基础管理.md"), baseData, Encoding.UTF8);
            return root;
        }

        /// <summary>XMZADD 20260901 创建两个物理表锚点共用一个模块文档的缓存夹具。</summary>
        private static string CreateSharedModuleFixture()
        {
            string root = Path.Combine(Path.GetTempPath(), "SHB_KB_SHARED_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "全量表索引.md"),
                CreateIndexHeader() +
                "| 采购 | 采购 | 第一 | `FIRST` | `T_FIRST` | 单据头 | `FIRST` | `FID` | 1 | 1 | 0 | [共享模块.md](./共享模块.md#entity-1) |\r\n" +
                "| 采购 | 采购 | 第二 | `SECOND` | `T_SECOND` | 单据头 | `SECOND` | `FID` | 1 | 1 | 0 | [共享模块.md](./共享模块.md#entity-2) |\r\n" +
                "| 采购 | 采购 | 空节 | `EMPTY` | `T_EMPTY` | 单据头 | `EMPTY` | `FID` | 0 | 0 | 0 | [共享模块.md](./共享模块.md#entity-3) |\r\n",
                Encoding.UTF8);
            File.WriteAllText(Path.Combine(root, "共享模块.md"),
                CreateModuleSection("entity-1", "T_FIRST", "第一名称", "FNAME") +
                CreateModuleSection("entity-2", "T_SECOND", "第二名称", "FNAME") +
                "<a id=\"entity-3\"></a>\r\n\r\n## T_EMPTY — 单据头\r\n\r\n### 字段\r\n\r\n" +
                "| 标题 | 标识 | 实体属性 | 数据库字段 | 字段类型 | 引用对象 | 引用表 | 枚举类型内码 | 枚举名称 | 枚举项目 | 分表 |\r\n" +
                "| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |\r\n\r\n" +
                CreateModuleSection("entity-2", "T_SECOND", "错误重复名称", "FNAME"),
                Encoding.UTF8);
            return root;
        }

        /// <summary>XMZADD 20260901 创建索引存在但模块暂缺的恢复夹具。</summary>
        private static string CreateRecoverableModuleFixture()
        {
            string root = Path.Combine(Path.GetTempPath(), "SHB_KB_RECOVER_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "全量表索引.md"),
                CreateIndexHeader() +
                "| 采购 | 采购 | 恢复 | `RECOVER` | `T_RECOVER` | 单据头 | `RECOVER` | `FID` | 1 | 1 | 0 | [恢复模块.md](./恢复模块.md#entity-1) |\r\n",
                Encoding.UTF8);
            return root;
        }

        /// <summary>XMZADD 20260901 创建同一物理表依次引用正常模块和损坏模块的夹具，用于验证单次查询事务边界。</summary>
        private static string CreatePartiallyDamagedTableFixture()
        {
            string root = Path.Combine(Path.GetTempPath(), "SHB_KB_PARTIAL_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "全量表索引.md"),
                CreateIndexHeader() +
                "| 采购 | 采购 | 正常 | `PARTIAL_ONE` | `T_PARTIAL` | 单据头 | `PARTIAL_ONE` | `FID` | 1 | 1 | 0 | [正常模块.md](./正常模块.md#entity-1) |\r\n" +
                "| 采购 | 采购 | 损坏 | `PARTIAL_TWO` | `T_PARTIAL` | 单据头 | `PARTIAL_TWO` | `FID` | 1 | 1 | 0 | [损坏模块.md](./损坏模块.md#entity-2) |\r\n",
                Encoding.UTF8);
            File.WriteAllText(Path.Combine(root, "正常模块.md"),
                CreateModuleSection("entity-1", "T_PARTIAL", "局部名称", "FNAME"), Encoding.UTF8);
            File.WriteAllText(Path.Combine(root, "损坏模块.md"), "损坏模块，没有实体锚点", Encoding.UTF8);
            return root;
        }

        /// <summary>XMZADD 20260901 写入恢复夹具的模块字段章节。</summary>
        private static void WriteRecoverableModule(string root)
        {
            File.WriteAllText(Path.Combine(root, "恢复模块.md"),
                CreateModuleSection("entity-1", "T_RECOVER", "恢复名称", "FNAME"), Encoding.UTF8);
        }

        /// <summary>XMZADD 20260901 创建同时包含可靠和不可靠精确标题的知识库夹具。</summary>
        private static string CreateUnreliableTitleFixture()
        {
            string root = Path.Combine(Path.GetTempPath(), "SHB_KB_TITLE_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "全量表索引.md"),
                CreateIndexHeader() +
                "| 采购 | 采购 | 标题 | `TITLE` | `T_BAD_TITLE` | 单据头 | `TITLE` | `FID` | 4 | 4 | 0 | [标题模块.md](./标题模块.md#entity-1) |\r\n",
                Encoding.UTF8);
            string module =
                "<a id=\"entity-1\"></a>\r\n\r\n## T_BAD_TITLE — 单据头\r\n\r\n### 字段\r\n\r\n" +
                "| 标题 | 标识 | 实体属性 | 数据库字段 | 字段类型 | 引用对象 | 引用表 | 枚举类型内码 | 枚举名称 | 枚举项目 | 分表 |\r\n" +
                "| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |\r\n" +
                "| DROP TABLE | FENGLISH | ENGLISH | FENGLISH | 文本 | | | | | | |\r\n" +
                "| 用于处理订单状态 | FPROCEDURE | PROCEDURE | FPROCEDURE | 文本 | | | | | | |\r\n" +
                "| FNAME=名称 | FFORMAT | FORMAT | FFORMAT | 文本 | | | | | | |\r\n" +
                "| 订单状态 | FSTATUS | STATUS | FSTATUS | 文本 | | | | | | |\r\n";
            File.WriteAllText(Path.Combine(root, "标题模块.md"), module, Encoding.UTF8);
            return root;
        }

        /// <summary>XMZADD 20260901 创建知识库全量索引的固定表头。</summary>
        private static string CreateIndexHeader()
        {
            return "| 顶级分类 | 子系统 | 表单 | 表单标识 | 表名称 | 实体名称 | 实体入口名 | 主键字段 | 字段数 | 物理字段数 | 引用表字段数 | 详情 |\r\n" +
                   "| --- | --- | --- | --- | --- | --- | --- | --- | ---: | ---: | ---: | --- |\r\n";
        }

        /// <summary>XMZADD 20260901 创建一个最小知识库模块字段章节。</summary>
        private static string CreateModuleSection(string anchor, string tableName, string title, string fieldName)
        {
            return "<a id=\"" + anchor + "\"></a>\r\n\r\n## " + tableName + " — 单据头\r\n\r\n### 字段\r\n\r\n" +
                   "| 标题 | 标识 | 实体属性 | 数据库字段 | 字段类型 | 引用对象 | 引用表 | 枚举类型内码 | 枚举名称 | 枚举项目 | 分表 |\r\n" +
                   "| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |\r\n" +
                   "| " + title + " | " + fieldName + " | VALUE | " + fieldName + " | 文本 | | | | | | |\r\n\r\n";
        }

        /// <summary>XMZADD 20260901 读取知识库服务的私有章节索引，验证性能行为而不扩大生产 API。</summary>
        private static IDictionary GetSectionCache(EosKnowledgeBaseTranslationService service)
        {
            FieldInfo field = typeof(EosKnowledgeBaseTranslationService).GetField(
                "_sectionCache", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field);
            return (IDictionary)field.GetValue(service);
        }

        /// <summary>XMZADD 20260901 设置测试所需的私有累计预算，避免创建超大临时文件消耗测试环境。</summary>
        private static void SetPrivateField(EosKnowledgeBaseTranslationService service, string fieldName, object value)
        {
            FieldInfo field = typeof(EosKnowledgeBaseTranslationService).GetField(
                fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field);
            field.SetValue(service, value);
        }
    }
}
