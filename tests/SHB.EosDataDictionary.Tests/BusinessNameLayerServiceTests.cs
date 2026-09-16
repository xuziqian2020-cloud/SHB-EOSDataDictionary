using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHB.EosDataDictionary.Models;
using SHB.EosDataDictionary.Services;

namespace SHB.EosDataDictionary.Tests
{
    /// <summary>XMZADD 20260915 验证正式中文名与参考译名严格按业务证据分层。</summary>
    [TestClass]
    public sealed class BusinessNameLayerServiceTests
    {
        /// <summary>XMZADD 20260915 验证单纯词法翻译不能成为正式业务名称。</summary>
        [TestMethod]
        public void Apply_NamingOnlyCandidate_IsStoredAsSuggestion()
        {
            TableMetadata table = CreateTable("Account_Storage_Part_Definition");
            MetadataValue candidate = CreateCandidate(
                "仓储区定义", ConfidenceStatus.Guessed, 70,
                "IdentifierTranslation", string.Empty, 0);

            new BusinessNameLayerService().ApplyTableCandidate(table, candidate);

            Assert.AreEqual(string.Empty, table.ChineseName.Value);
            Assert.AreEqual("仓储区定义", table.SuggestedChineseName.Value);
            Assert.AreEqual(ConfidenceStatus.Guessed, table.SuggestedChineseName.Status);
        }

        /// <summary>XMZADD 20260915 验证人工锁定正式名始终保留且不同自动候选只进入参考层。</summary>
        [TestMethod]
        public void Apply_ManualLockedName_IsNeverReplaced()
        {
            TableMetadata table = CreateTable("DA_Acceptance");
            table.ChineseName = CreateCandidate(
                "承兑验收单", ConfidenceStatus.LocalOverride, 100,
                "ManualEdit", string.Empty, 0);
            table.ChineseName.IsManualOverride = true;
            table.ChineseName.IsLocked = true;
            MetadataValue candidate = CreateCandidate(
                "验收记录", ConfidenceStatus.CodeEvidence, 96,
                "SqlColumnAlias", "ERP/DA/AcceptanceQuery.vb", 20);

            new BusinessNameLayerService().ApplyTableCandidate(table, candidate);

            Assert.AreEqual("承兑验收单", table.ChineseName.Value);
            Assert.AreEqual("验收记录", table.SuggestedChineseName.Value);
        }

        /// <summary>XMZADD 20260915 验证可靠数据库注释可直接成为正式中文名。</summary>
        [TestMethod]
        public void Apply_DatabaseComment_IsPromotedToOfficial()
        {
            TableMetadata table = CreateTable("DA_Acceptance");
            MetadataValue candidate = CreateCandidate(
                "承兑验收单", ConfidenceStatus.DatabaseEvidence, 100,
                "DatabaseComment", string.Empty, 0);

            new BusinessNameLayerService().ApplyTableCandidate(table, candidate);

            Assert.AreEqual("承兑验收单", table.ChineseName.Value);
            Assert.IsNull(table.SuggestedChineseName);
        }

        /// <summary>XMZADD 20260916 验证数据库来源不能绕过伪中文检查，未翻译英文残片只能进入参考层。</summary>
        [TestMethod]
        public void Apply_DatabasePseudoChinese_IsStoredAsSuggestion()
        {
            FieldMetadata field = CreateField("Owner_Company_ID");
            MetadataValue candidate = CreateCandidate(
                "Owner公司ID", ConfidenceStatus.DatabaseEvidence, 100,
                "DatabaseComment", string.Empty, 0);

            new BusinessNameLayerService().ApplyFieldCandidate(field, candidate);

            Assert.AreEqual(string.Empty, field.ChineseName.Value);
            Assert.AreEqual("Owner公司ID", field.SuggestedChineseName.Value);
            Assert.AreEqual(ConfidenceStatus.DatabaseEvidence, field.SuggestedChineseName.Status);
        }

        /// <summary>XMZADD 20260915 验证精确到对象的项目知识条目可成为正式中文名。</summary>
        [TestMethod]
        public void Apply_ExactKnowledge_IsPromotedToOfficial()
        {
            FieldMetadata field = CreateField("Owner_Company_ID");
            MetadataValue candidate = CreateCandidate(
                "货主公司ID", ConfidenceStatus.KnowledgeBaseEvidence, 98,
                "ExactProjectField", "docs_knowledge/02_字段字典/_00_核心字段字典.md", 88);

            new BusinessNameLayerService().ApplyFieldCandidate(field, candidate);

            Assert.AreEqual("货主公司ID", field.ChineseName.Value);
            Assert.IsNull(field.SuggestedChineseName);
        }

        /// <summary>XMZADD 20260915 验证唯一绑定物理字段的单个界面标题足以成为正式中文名。</summary>
        [TestMethod]
        public void Apply_SingleDirectUiCaption_IsPromotedToOfficial()
        {
            FieldMetadata field = CreateField("Owner_Company_ID");
            MetadataValue candidate = CreateCandidate(
                "货主公司ID", ConfidenceStatus.CodeEvidence, 94,
                "GridColumnCaption", "ERP/DA/AcceptanceEdit.vb", 35);

            new BusinessNameLayerService().ApplyFieldCandidate(field, candidate);

            Assert.AreEqual("货主公司ID", field.ChineseName.Value);
            Assert.AreEqual(ConfidenceStatus.CodeEvidence, field.ChineseName.Status);
        }

        /// <summary>XMZADD 20260915 验证两个独立业务文件给出相同含义时可升级为正式中文名。</summary>
        [TestMethod]
        public void Apply_TwoIndependentSourcesAgree_PromotesMergedCandidate()
        {
            FieldMetadata field = CreateField("Owner_Company_ID");
            var candidates = new List<MetadataValue>
            {
                CreateCandidate("货主公司ID", ConfidenceStatus.CodeEvidence, 84,
                    "EntityProperty", "ERP/DA/AcceptanceEdit.vb", 35),
                CreateCandidate("货主公司ID", ConfidenceStatus.CodeEvidence, 84,
                    "EntityProperty", "Logistics/AcceptanceQuery.vb", 62)
            };

            new BusinessNameLayerService().ApplyFieldCandidates(field, candidates);

            Assert.AreEqual("货主公司ID", field.ChineseName.Value);
            Assert.AreEqual(2, field.ChineseName.Evidence.Count);
        }

        /// <summary>XMZADD 20260915 验证复制到不同文件的同一 SQL 文本只计算为一个证据源。</summary>
        [TestMethod]
        public void Apply_CopiedSqlText_DoesNotSatisfyTwoSourceAdmission()
        {
            FieldMetadata field = CreateField("Status");
            MetadataValue first = CreateCandidate("单据状态", ConfidenceStatus.CodeEvidence, 84,
                "SqlUsageCaption", "ERP/Order/QueryA.vb", 20);
            MetadataValue second = CreateCandidate("单据状态", ConfidenceStatus.CodeEvidence, 84,
                "SqlUsageCaption", "ERP/Order/QueryB.vb", 42);
            first.Evidence[0].OriginalText = "SELECT Status FROM T_ORDER";
            second.Evidence[0].OriginalText = "SELECT Status FROM T_ORDER";

            new BusinessNameLayerService().ApplyFieldCandidates(
                field, new List<MetadataValue> { first, second });

            Assert.AreEqual(string.Empty, field.ChineseName.Value);
            Assert.AreEqual("单据状态", field.SuggestedChineseName.Value);
        }

        /// <summary>XMZADD 20260915 验证不同强业务证据互相冲突时不自动生成正式名并保留全部候选。</summary>
        [TestMethod]
        public void Apply_StrongEvidenceConflicts_PreservesSuggestionsWithoutOfficialName()
        {
            FieldMetadata field = CreateField("Status");
            var candidates = new List<MetadataValue>
            {
                CreateCandidate("审核状态", ConfidenceStatus.CodeEvidence, 96,
                    "SqlColumnAlias", "ERP/DA/AcceptanceQuery.vb", 20),
                CreateCandidate("单据状态", ConfidenceStatus.CodeEvidence, 94,
                    "GridColumnCaption", "ERP/DA/AcceptanceEdit.vb", 35)
            };

            new BusinessNameLayerService().ApplyFieldCandidates(field, candidates);

            Assert.AreEqual(string.Empty, field.ChineseName.Value);
            Assert.AreEqual("审核状态", field.SuggestedChineseName.Value);
            Assert.AreEqual(ConfidenceStatus.GuessedConflict, field.SuggestedChineseName.Status);
            Assert.AreEqual(1, field.AlternativeChineseNames.Count);
            Assert.AreEqual("单据状态", field.AlternativeChineseNames[0].Value);
            Assert.AreEqual(ConfidenceStatus.GuessedConflict,
                field.AlternativeChineseNames[0].Status);
        }

        /// <summary>XMZADD 20260915 验证冲突候选跨分批刷新仍保留原证据等级，不因单个候选先到达而自动解冲突。</summary>
        [TestMethod]
        public void Apply_ConflictCandidateArrivesAgain_DoesNotSilentlyResolveOtherCandidate()
        {
            FieldMetadata field = CreateField("Status");
            var service = new BusinessNameLayerService();
            service.ApplyFieldCandidates(field, new List<MetadataValue>
            {
                CreateCandidate("审核状态", ConfidenceStatus.CodeEvidence, 96,
                    "SqlColumnAlias", "ERP/DA/AcceptanceQuery.vb", 20),
                CreateCandidate("单据状态", ConfidenceStatus.CodeEvidence, 94,
                    "GridColumnCaption", "ERP/DA/AcceptanceEdit.vb", 35)
            });

            service.ApplyFieldCandidate(field, CreateCandidate(
                "审核状态", ConfidenceStatus.CodeEvidence, 96,
                "SqlColumnAlias", "ERP/DA/AcceptanceQuery.vb", 20));

            Assert.AreEqual(string.Empty, field.ChineseName.Value);
            Assert.AreEqual(1, field.AlternativeChineseNames.Count);
            Assert.AreEqual(ConfidenceStatus.GuessedConflict, field.SuggestedChineseName.Status);
            Assert.AreEqual(ConfidenceStatus.GuessedConflict,
                field.AlternativeChineseNames[0].Status);
        }

        /// <summary>XMZADD 20260915 验证冲突后的同名低级代码候选不能覆盖该名称原有的数据库证据等级。</summary>
        [TestMethod]
        public void Apply_AuthoritativeConflictWithFreshCode_PreservesBothAuthoritativeGrades()
        {
            FieldMetadata field = CreateField("Status");
            var service = new BusinessNameLayerService();
            service.ApplyFieldCandidates(field, new List<MetadataValue>
            {
                CreateCandidate("数据库状态", ConfidenceStatus.DatabaseEvidence, 100,
                    "DatabaseComment", string.Empty, 0),
                CreateCandidate("知识状态", ConfidenceStatus.KnowledgeBaseEvidence, 98,
                    "ExactProjectField", "docs_knowledge/02_字段字典/_00_核心字段字典.md", 88)
            });

            service.ApplyFieldCandidate(field, CreateCandidate(
                "数据库状态", ConfidenceStatus.CodeEvidence, 94,
                "GridColumnCaption", "ERP/DA/AcceptanceEdit.vb", 35));

            Assert.AreEqual(string.Empty, field.ChineseName.Value);
            Assert.AreEqual(1, field.AlternativeChineseNames.Count);
            Assert.AreEqual(ConfidenceStatus.GuessedConflict, field.SuggestedChineseName.Status);
            Assert.AreEqual(ConfidenceStatus.GuessedConflict,
                field.AlternativeChineseNames[0].Status);
        }

        /// <summary>XMZADD 20260915 验证 A 级精确知识与 B 级直接代码冲突时保留更高等级正式名并展示冲突候选。</summary>
        [TestMethod]
        public void Apply_HigherGradeKnowledgeConflict_KeepsKnowledgeAsOfficialName()
        {
            FieldMetadata field = CreateField("Status");
            var candidates = new List<MetadataValue>
            {
                CreateCandidate("单据状态", ConfidenceStatus.KnowledgeBaseEvidence, 98,
                    "ExactProjectField", "docs_knowledge/02_字段字典/_00_核心字段字典.md", 88),
                CreateCandidate("审核状态", ConfidenceStatus.CodeEvidence, 96,
                    "SqlColumnAlias", "ERP/DA/AcceptanceQuery.vb", 20)
            };

            new BusinessNameLayerService().ApplyFieldCandidates(field, candidates);

            Assert.AreEqual("单据状态", field.ChineseName.Value);
            Assert.AreEqual(ConfidenceStatus.KnowledgeBaseEvidence, field.ChineseName.Status);
            Assert.AreEqual("审核状态", field.SuggestedChineseName.Value);
            Assert.AreEqual(ConfidenceStatus.GuessedConflict, field.SuggestedChineseName.Status);
        }

        /// <summary>XMZADD 20260915 验证两个不同 A 级权威名称冲突时双方均显示冲突且不生成正式名。</summary>
        [TestMethod]
        public void Apply_SameGradeAuthoritativeConflict_MarksEveryCandidateAsConflict()
        {
            FieldMetadata field = CreateField("Status");
            var candidates = new List<MetadataValue>
            {
                CreateCandidate("审核状态", ConfidenceStatus.DatabaseEvidence, 100,
                    "DatabaseComment", string.Empty, 0),
                CreateCandidate("单据状态", ConfidenceStatus.KnowledgeBaseEvidence, 98,
                    "ExactProjectField", "docs_knowledge/02_字段字典/_00_核心字段字典.md", 88)
            };

            new BusinessNameLayerService().ApplyFieldCandidates(field, candidates);

            Assert.AreEqual(string.Empty, field.ChineseName.Value);
            Assert.AreEqual(ConfidenceStatus.GuessedConflict, field.SuggestedChineseName.Status);
            Assert.AreEqual(ConfidenceStatus.GuessedConflict,
                field.AlternativeChineseNames[0].Status);
        }

        /// <summary>XMZADD 20260915 验证后到达的 A 级精确知识可替换既有 B 级代码正式名并保留旧名供审阅。</summary>
        [TestMethod]
        public void Apply_HigherGradeKnowledgeAfterCode_ReplacesLowerGradeOfficialName()
        {
            FieldMetadata field = CreateField("Status");
            field.ChineseName = CreateCandidate("审核状态", ConfidenceStatus.CodeEvidence, 94,
                "GridColumnCaption", "ERP/DA/AcceptanceEdit.vb", 35);
            MetadataValue knowledge = CreateCandidate(
                "单据状态", ConfidenceStatus.KnowledgeBaseEvidence, 98,
                "ExactProjectField", "docs_knowledge/02_字段字典/_00_核心字段字典.md", 88);

            new BusinessNameLayerService().ApplyFieldCandidate(field, knowledge);

            Assert.AreEqual("单据状态", field.ChineseName.Value);
            Assert.AreEqual(ConfidenceStatus.KnowledgeBaseEvidence, field.ChineseName.Status);
            Assert.AreEqual("审核状态", field.SuggestedChineseName.Value);
            Assert.AreEqual(ConfidenceStatus.GuessedConflict, field.SuggestedChineseName.Status);
        }

        /// <summary>XMZADD 20260915 验证人工否决且证据未改变的候选不会再次进入参考列。</summary>
        [TestMethod]
        public void Apply_RejectedFingerprint_DoesNotSuggestUnchangedCandidate()
        {
            TableMetadata table = CreateTable("Account_Storage_Part_Definition");
            MetadataValue candidate = CreateCandidate(
                "仓储区定义", ConfidenceStatus.Guessed, 70,
                "IdentifierTranslation", "Account/Storage.vb", 15);
            string fingerprint = new NameSuggestionFingerprintService().CreateFingerprint(candidate);
            table.RejectedSuggestionFingerprints.Add(fingerprint);

            new BusinessNameLayerService().ApplyTableCandidate(table, candidate);

            Assert.IsNull(table.SuggestedChineseName);
            Assert.AreEqual(0, table.AlternativeChineseNames.Count);
        }

        /// <summary>XMZADD 20260915 验证证据位置变化后会形成新候选指纹并允许重新审阅。</summary>
        [TestMethod]
        public void Apply_RejectedCandidateWithChangedEvidence_CanBeSuggestedAgain()
        {
            TableMetadata table = CreateTable("Account_Storage_Part_Definition");
            MetadataValue oldCandidate = CreateCandidate(
                "仓储区定义", ConfidenceStatus.Guessed, 70,
                "IdentifierTranslation", "Account/Storage.vb", 15);
            table.RejectedSuggestionFingerprints.Add(
                new NameSuggestionFingerprintService().CreateFingerprint(oldCandidate));
            MetadataValue changedCandidate = CreateCandidate(
                "仓储区定义", ConfidenceStatus.Guessed, 70,
                "IdentifierTranslation", "Account/Storage.vb", 28);

            new BusinessNameLayerService().ApplyTableCandidate(table, changedCandidate);

            Assert.AreEqual("仓储区定义", table.SuggestedChineseName.Value);
        }

        /// <summary>XMZADD 20260915 验证证据输入顺序不影响建议指纹，确保不同客户端可共享否决决定。</summary>
        [TestMethod]
        public void Fingerprint_EvidenceOrderChanges_RemainsStable()
        {
            MetadataValue first = CreateCandidate("仓储区定义", ConfidenceStatus.Guessed, 70,
                "IdentifierTranslation", "Account/Storage.vb", 15);
            first.Evidence.Add(new EvidenceItem
            {
                SourceType = "测试证据",
                RuleName = "EntityProperty",
                SourcePath = "Account/Storage_Partial.vb",
                SourceLine = 28
            });
            MetadataValue second = CreateCandidate("仓储区定义", ConfidenceStatus.Guessed, 70,
                "EntityProperty", "Account/Storage_Partial.vb", 28);
            second.Evidence.Add(new EvidenceItem
            {
                SourceType = "测试证据",
                RuleName = "IdentifierTranslation",
                SourcePath = "Account/Storage.vb",
                SourceLine = 15
            });

            var service = new NameSuggestionFingerprintService();
            Assert.AreEqual(service.CreateFingerprint(first), service.CreateFingerprint(second));
        }

        /// <summary>XMZADD 20260915 验证名称分层复制证据项，防止发布脱敏反向修改扫描候选。</summary>
        [TestMethod]
        public void Apply_SuggestionEvidence_IsIndependentFromInputCandidate()
        {
            TableMetadata table = CreateTable("Account_Storage_Part_Definition");
            MetadataValue candidate = CreateCandidate(
                "仓储区定义", ConfidenceStatus.Guessed, 70,
                "IdentifierTranslation", "Account/Storage.vb", 15);

            new BusinessNameLayerService().ApplyTableCandidate(table, candidate);
            table.SuggestedChineseName.Evidence[0].SourcePath = "Sanitized/Storage.vb";

            Assert.AreEqual("Account/Storage.vb", candidate.Evidence[0].SourcePath);
        }

        /// <summary>XMZADD 20260915 验证分批到达的不同候选不会让后一次调用静默丢弃先前参考依据。</summary>
        [TestMethod]
        public void Apply_SequentialCandidates_PreservesEarlierCandidateAsAlternative()
        {
            TableMetadata table = CreateTable("Account_Storage_Part_Definition");
            var service = new BusinessNameLayerService();
            service.ApplyTableCandidate(table, CreateCandidate(
                "仓储区定义", ConfidenceStatus.Guessed, 70,
                "IdentifierTranslation", "Account/Storage.vb", 15));

            service.ApplyTableCandidate(table, CreateCandidate(
                "存储区域定义", ConfidenceStatus.Guessed, 80,
                "BusinessContextTranslation", "Account/StorageEdit.vb", 28));

            Assert.AreEqual("存储区域定义", table.SuggestedChineseName.Value);
            Assert.AreEqual(1, table.AlternativeChineseNames.Count);
            Assert.AreEqual("仓储区定义", table.AlternativeChineseNames[0].Value);
        }

        /// <summary>XMZADD 20260915 验证 Revision 11 的弱正式值迁入参考层且不再伪装成正式名称。</summary>
        [TestMethod]
        public void NormalizeSnapshot_Revision11WeakOfficialName_MovesToSuggestion()
        {
            TableMetadata table = CreateTable("Account_Storage_Part_Definition");
            table.ChineseName = CreateCandidate(
                "仓储区定义", ConfidenceStatus.Guessed, 70,
                "IdentifierTranslation", string.Empty, 0);
            table.ChineseName.OriginalAutomaticValue = "旧版自动正式名";
            var snapshot = new SnapshotData { Tables = new List<TableMetadata> { table } };

            new BusinessNameLayerService().NormalizeSnapshot(snapshot);

            Assert.AreEqual(string.Empty, table.ChineseName.Value);
            Assert.AreEqual("仓储区定义", table.SuggestedChineseName.Value);
            Assert.IsNull(table.SuggestedChineseName.OriginalAutomaticValue);
        }

        /// <summary>XMZADD 20260915 创建具有空正式名称和可写候选集合的表元数据。</summary>
        private static TableMetadata CreateTable(string objectName)
        {
            return new TableMetadata
            {
                ObjectName = objectName,
                ChineseName = new MetadataValue { Value = string.Empty },
                Fields = new List<FieldMetadata>()
            };
        }

        /// <summary>XMZADD 20260915 创建具有空正式名称和可写候选集合的字段元数据。</summary>
        private static FieldMetadata CreateField(string fieldName)
        {
            return new FieldMetadata
            {
                FieldName = fieldName,
                ChineseName = new MetadataValue { Value = string.Empty }
            };
        }

        /// <summary>XMZADD 20260915 创建包含可信状态、评分、规则和相对源码位置的名称候选。</summary>
        private static MetadataValue CreateCandidate(string value, ConfidenceStatus status, int score,
            string ruleName, string sourcePath, int sourceLine)
        {
            return new MetadataValue
            {
                Value = value,
                Status = status,
                ConfidenceScore = score,
                SourceType = "测试证据",
                Evidence = new List<EvidenceItem>
                {
                    new EvidenceItem
                    {
                        SourceType = "测试证据",
                        RuleName = ruleName,
                        SourcePath = sourcePath,
                        SourceLine = sourceLine,
                        RawValue = value,
                        OriginalText = value
                    }
                }
            };
        }
    }
}
