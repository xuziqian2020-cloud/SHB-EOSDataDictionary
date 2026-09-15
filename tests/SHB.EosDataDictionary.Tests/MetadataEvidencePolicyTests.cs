using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHB.EosDataDictionary.Models;
using SHB.EosDataDictionary.Services;

namespace SHB.EosDataDictionary.Tests
{
    /// <summary>XMZADD 20260903 验证中文名称与模块富化遵守统一证据优先级。</summary>
    [TestClass]
    public sealed class MetadataEvidencePolicyTests
    {
        /// <summary>XMZADD 20260903 验证源码实体证据可修正通用知识条目，但不能覆盖精确表字段条目。</summary>
        [TestMethod]
        public void CanReplace_CodeEvidence_ReplacesGenericKnowledgeButPreservesExactKnowledge()
        {
            MetadataValue genericKnowledge = CreateValue(ConfidenceStatus.KnowledgeBaseEvidence, "CommonFieldGlossary");
            MetadataValue exactKnowledge = CreateValue(ConfidenceStatus.KnowledgeBaseEvidence, "ExactProjectField");

            Assert.IsTrue(MetadataEvidencePolicy.CanReplace(genericKnowledge, ConfidenceStatus.CodeEvidence, "EntityProperty"));
            Assert.IsFalse(MetadataEvidencePolicy.CanReplace(exactKnowledge, ConfidenceStatus.CodeEvidence, "EntityProperty"));
        }

        /// <summary>XMZADD 20260903 验证精确知识库名称只补弱值，不反向覆盖已有源码业务证据。</summary>
        [TestMethod]
        public void CanReplace_ExactKnowledge_FillsWeakValueButPreservesCodeEvidence()
        {
            MetadataValue guessed = CreateValue(ConfidenceStatus.Guessed, "IdentifierTranslation");
            MetadataValue code = CreateValue(ConfidenceStatus.CodeEvidence, "EntityProperty");

            Assert.IsTrue(MetadataEvidencePolicy.CanReplace(guessed, ConfidenceStatus.KnowledgeBaseEvidence, "ExactProjectField"));
            Assert.IsFalse(MetadataEvidencePolicy.CanReplace(code, ConfidenceStatus.KnowledgeBaseEvidence, "ExactProjectField"));
        }

        /// <summary>XMZADD 20260904 验证真实业务用途可以纠正知识库简称，但仍不能越过数据库和人工结论。</summary>
        [TestMethod]
        public void CanReplace_BusinessUsageContext_RefinesKnowledgeButPreservesAuthority()
        {
            MetadataValue exactKnowledge = CreateValue(ConfidenceStatus.KnowledgeBaseEvidence, "ExactProjectTable");
            MetadataValue database = CreateValue(ConfidenceStatus.DatabaseEvidence, "DatabaseComment");
            MetadataValue manual = CreateValue(ConfidenceStatus.LocalOverride, "ManualEdit");
            manual.IsManualOverride = true;

            Assert.IsTrue(MetadataEvidencePolicy.CanReplace(
                exactKnowledge, ConfidenceStatus.CodeEvidence, "BusinessUsageContext"));
            Assert.IsFalse(MetadataEvidencePolicy.CanReplace(
                database, ConfidenceStatus.CodeEvidence, "BusinessUsageContext"));
            Assert.IsFalse(MetadataEvidencePolicy.CanReplace(
                manual, ConfidenceStatus.CodeEvidence, "BusinessUsageContext"));
        }

        /// <summary>XMZADD 20260903 验证用户确认的业务消歧可覆盖自动证据，但保护数据库和人工维护结论。</summary>
        [TestMethod]
        public void CanReplace_ConfirmedBusinessRule_PreservesAuthoritativeValues()
        {
            MetadataValue code = CreateValue(ConfidenceStatus.CodeEvidence, "EntityProperty");
            MetadataValue database = CreateValue(ConfidenceStatus.DatabaseEvidence, "DatabaseComment");
            MetadataValue manual = CreateValue(ConfidenceStatus.LocalOverride, "ManualEdit");
            manual.IsManualOverride = true;

            Assert.IsTrue(MetadataEvidencePolicy.CanReplace(code, ConfidenceStatus.Confirmed, "ConfirmedBusinessSemantic"));
            Assert.IsFalse(MetadataEvidencePolicy.CanReplace(database, ConfidenceStatus.Confirmed, "ConfirmedBusinessSemantic"));
            Assert.IsFalse(MetadataEvidencePolicy.CanReplace(manual, ConfidenceStatus.Confirmed, "ConfirmedBusinessSemantic"));
        }

        /// <summary>XMZADD 20260905 验证数据库简称仅在高置信业务候选增加已识别修饰语且证据充分时允许细化。</summary>
        [TestMethod]
        public void CanReplaceBusinessName_DatabaseSuffix_AllowsRecognizedModifierWithStrongEvidence()
        {
            MetadataValue database = CreateValue(ConfidenceStatus.DatabaseEvidence, "DatabaseComment");
            database.Value = "公司ID";

            Assert.IsTrue(MetadataEvidencePolicy.CanReplaceBusinessName(
                database, "货主公司ID", 90, 3, "GridColumnCaption"));
            Assert.IsTrue(MetadataEvidencePolicy.CanReplaceBusinessName(
                database, "货主公司ID", 85, 1, "SqlFieldRelation"));
            Assert.IsFalse(MetadataEvidencePolicy.CanReplaceBusinessName(
                database, "货主公司ID", 84, 3, "GridColumnCaption"));
            Assert.IsFalse(MetadataEvidencePolicy.CanReplaceBusinessName(
                database, "货主公司ID", 90, 1, "EntityProperty"));
            Assert.IsFalse(MetadataEvidencePolicy.CanReplaceBusinessName(
                database, "临时公司ID", 90, 3, "GridColumnCaption"));
        }

        /// <summary>XMZADD 20260908 验证数据库中的英文简称或字段说明句可由九十五分以上的完整中文业务短语修正。</summary>
        [DataTestMethod]
        [DataRow("MPI_WC", "车间计划ID")]
        [DataRow("Bu", "事业部ID")]
        [DataRow("订单条目ID 属于Purchase_Order_Item", "采购订单明细ID")]
        public void CanReplaceBusinessName_WeakDatabaseText_AllowsExactChinesePhrase(
            string existingValue, string candidate)
        {
            MetadataValue database = CreateValue(ConfidenceStatus.DatabaseEvidence, "DatabaseComment");
            database.Value = existingValue;

            Assert.IsTrue(MetadataEvidencePolicy.CanReplaceBusinessName(
                database, candidate, 98, 1, "EntityProperty"));
            Assert.IsFalse(MetadataEvidencePolicy.CanReplaceBusinessName(
                database, candidate, 94, 3, "GridColumnCaption"));
        }

        /// <summary>XMZADD 20260908 验证已有明确中文数据库注释仍不允许被不同的自动候选替换。</summary>
        [TestMethod]
        public void CanReplaceBusinessName_ReliableChineseDatabaseText_RemainsAuthoritative()
        {
            MetadataValue database = CreateValue(ConfidenceStatus.DatabaseEvidence, "DatabaseComment");
            database.Value = "结算公司ID";

            Assert.IsFalse(MetadataEvidencePolicy.CanReplaceBusinessName(
                database, "货主公司ID", 98, 3, "GridColumnCaption"));
        }

        /// <summary>XMZADD 20260905 验证数据库简称只接受完整前置业务修饰语，拒绝夹杂或追加未知文本。</summary>
        [TestMethod]
        public void CanReplaceBusinessName_DatabaseSuffix_RequiresFullyConsumedLeadingModifiers()
        {
            MetadataValue database = CreateValue(ConfidenceStatus.DatabaseEvidence, "DatabaseComment");
            database.Value = "公司ID";

            Assert.IsTrue(MetadataEvidencePolicy.CanReplaceBusinessName(
                database, "货主公司ID", 90, 2, "BusinessUsageContext"));
            Assert.IsTrue(MetadataEvidencePolicy.CanReplaceBusinessName(
                database, "所属公司ID", 90, 2, "BusinessUsageContext"));
            Assert.IsFalse(MetadataEvidencePolicy.CanReplaceBusinessName(
                database, "货主X公司ID", 90, 2, "BusinessUsageContext"));
            Assert.IsFalse(MetadataEvidencePolicy.CanReplaceBusinessName(
                database, "货主公司ID附加", 90, 2, "BusinessUsageContext"));
            Assert.IsFalse(MetadataEvidencePolicy.CanReplaceBusinessName(
                database, "前货主后公司ID", 90, 2, "BusinessUsageContext"));
            Assert.IsFalse(MetadataEvidencePolicy.CanReplaceBusinessName(
                database, "操作公司ID垃圾", 90, 2, "BusinessUsageContext"));
        }

        /// <summary>XMZADD 20260905 验证冲突数据库名称以及人工、锁定和已确认名称永不被业务候选覆盖。</summary>
        [TestMethod]
        public void CanReplaceBusinessName_AuthoritativeOrConflictingName_RemainsUnchanged()
        {
            MetadataValue database = CreateValue(ConfidenceStatus.DatabaseEvidence, "DatabaseComment");
            database.Value = "结算公司ID";
            MetadataValue manual = CreateValue(ConfidenceStatus.LocalOverride, "ManualEdit");
            manual.IsManualOverride = true;
            MetadataValue locked = CreateValue(ConfidenceStatus.Guessed, "IdentifierTranslation");
            locked.IsLocked = true;
            MetadataValue confirmed = CreateValue(ConfidenceStatus.Confirmed, "ConfirmedBusinessSemantic");

            Assert.IsFalse(MetadataEvidencePolicy.CanReplaceBusinessName(
                database, "货主公司ID", 94, 3, "GridColumnCaption"));
            Assert.IsFalse(MetadataEvidencePolicy.CanReplaceBusinessName(
                manual, "货主公司ID", 94, 3, "GridColumnCaption"));
            Assert.IsFalse(MetadataEvidencePolicy.CanReplaceBusinessName(
                locked, "货主公司ID", 94, 3, "GridColumnCaption"));
            Assert.IsFalse(MetadataEvidencePolicy.CanReplaceBusinessName(
                confirmed, "货主公司ID", 94, 3, "GridColumnCaption"));
        }

        /// <summary>XMZADD 20260905 验证完整业务上下文可细化知识库简称，弱值仍需达到最低七十五分。</summary>
        [TestMethod]
        public void CanReplaceBusinessName_KnowledgeAndWeakValues_RequirePlannedThresholds()
        {
            MetadataValue knowledge = CreateValue(ConfidenceStatus.KnowledgeBaseEvidence, "ExactProjectField");
            knowledge.Value = "公司ID";
            MetadataValue guessed = CreateValue(ConfidenceStatus.Guessed, "IdentifierTranslation");

            Assert.IsTrue(MetadataEvidencePolicy.CanReplaceBusinessName(
                knowledge, "货主公司ID", 85, 2, "BusinessUsageContext"));
            Assert.IsFalse(MetadataEvidencePolicy.CanReplaceBusinessName(
                knowledge, "货主公司ID", 84, 3, "GridColumnCaption"));
            Assert.IsTrue(MetadataEvidencePolicy.CanReplaceBusinessName(
                guessed, "货主公司ID", 75, 1, "EntityProperty"));
            Assert.IsFalse(MetadataEvidencePolicy.CanReplaceBusinessName(
                guessed, "货主公司ID", 74, 3, "GridColumnCaption"));
        }

        /// <summary>XMZADD 20260905 验证弱状态同名候选可晋升源码证据，同时权威状态不会因同名候选降级。</summary>
        [TestMethod]
        public void CanReplaceBusinessName_SameValue_OnlyPromotesWeakStatusAtThreshold()
        {
            ConfidenceStatus[] weakStatuses =
            {
                ConfidenceStatus.Guessed,
                ConfidenceStatus.PendingConfirmation
            };
            for (int index = 0; index < weakStatuses.Length; index++)
            {
                MetadataValue weak = CreateValue(weakStatuses[index], "IdentifierTranslation");
                weak.Value = "货主公司ID";

                Assert.IsTrue(MetadataEvidencePolicy.CanReplaceBusinessName(
                    weak, "货主公司ID", 75, 1, "EntityProperty"));
                Assert.IsFalse(MetadataEvidencePolicy.CanReplaceBusinessName(
                    weak, "货主公司ID", 74, 3, "GridColumnCaption"));
            }

            MetadataValue manual = CreateValue(ConfidenceStatus.Guessed, "IdentifierTranslation");
            manual.Value = "货主公司ID";
            manual.IsManualOverride = true;
            MetadataValue locked = CreateValue(ConfidenceStatus.Guessed, "IdentifierTranslation");
            locked.Value = "货主公司ID";
            locked.IsLocked = true;
            MetadataValue localOverride = CreateValue(ConfidenceStatus.LocalOverride, "ManualEdit");
            localOverride.Value = "货主公司ID";
            MetadataValue confirmed = CreateValue(ConfidenceStatus.Confirmed, "ConfirmedBusinessSemantic");
            confirmed.Value = "货主公司ID";
            MetadataValue database = CreateValue(ConfidenceStatus.DatabaseEvidence, "DatabaseComment");
            database.Value = "货主公司ID";

            Assert.IsFalse(MetadataEvidencePolicy.CanReplaceBusinessName(
                manual, "货主公司ID", 94, 3, "GridColumnCaption"));
            Assert.IsFalse(MetadataEvidencePolicy.CanReplaceBusinessName(
                locked, "货主公司ID", 94, 3, "GridColumnCaption"));
            Assert.IsFalse(MetadataEvidencePolicy.CanReplaceBusinessName(
                localOverride, "货主公司ID", 94, 3, "GridColumnCaption"));
            Assert.IsFalse(MetadataEvidencePolicy.CanReplaceBusinessName(
                confirmed, "货主公司ID", 94, 3, "GridColumnCaption"));
            Assert.IsFalse(MetadataEvidencePolicy.CanReplaceBusinessName(
                database, "货主公司ID", 94, 3, "GridColumnCaption"));
        }

        /// <summary>XMZADD 20260915 验证只有人工、可靠数据库注释和精确知识条目属于自动流水线必须保护的正式名称。</summary>
        [TestMethod]
        public void IsAuthoritativeBusinessName_DistinguishesFormalAndReferenceValues()
        {
            MetadataValue manual = CreateValue(ConfidenceStatus.LocalOverride, "ManualEdit");
            manual.Value = "Manual English Name";
            manual.IsManualOverride = true;
            MetadataValue database = CreateValue(ConfidenceStatus.DatabaseEvidence, "DatabaseComment");
            database.Value = "货主公司ID";
            MetadataValue weakDatabase = CreateValue(ConfidenceStatus.DatabaseEvidence, "DatabaseComment");
            weakDatabase.Value = "Owner_Company_ID";
            MetadataValue exactKnowledge = CreateValue(
                ConfidenceStatus.KnowledgeBaseEvidence, "ExactProjectField");
            exactKnowledge.Value = "货主公司ID";
            MetadataValue genericKnowledge = CreateValue(
                ConfidenceStatus.KnowledgeBaseEvidence, "CommonFieldGlossary");
            genericKnowledge.Value = "公司ID";
            MetadataValue translation = CreateValue(ConfidenceStatus.Guessed, "IdentifierTranslation");
            translation.Value = "公司ID";

            Assert.IsTrue(MetadataEvidencePolicy.IsAuthoritativeBusinessName(manual));
            Assert.IsTrue(MetadataEvidencePolicy.IsAuthoritativeBusinessName(database));
            Assert.IsFalse(MetadataEvidencePolicy.IsAuthoritativeBusinessName(weakDatabase));
            Assert.IsTrue(MetadataEvidencePolicy.IsAuthoritativeBusinessName(exactKnowledge));
            Assert.IsFalse(MetadataEvidencePolicy.IsAuthoritativeBusinessName(genericKnowledge));
            Assert.IsFalse(MetadataEvidencePolicy.IsAuthoritativeBusinessName(translation));
        }

        /// <summary>XMZADD 20260903 创建包含单条规则证据的测试元数据值。</summary>
        private static MetadataValue CreateValue(ConfidenceStatus status, string ruleName)
        {
            return new MetadataValue
            {
                Value = "现有值",
                Status = status,
                Evidence = new List<EvidenceItem>
                {
                    new EvidenceItem { RuleName = ruleName }
                }
            };
        }
    }
}
