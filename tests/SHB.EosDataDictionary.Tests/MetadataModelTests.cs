using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHB.EosDataDictionary.Models;
using SHB.EosDataDictionary.ViewModels;

namespace SHB.EosDataDictionary.Tests
{
    /// <summary>XMZADD 20260831 验证 AI 推测、人工维护和结构变化模型能够承载后续数据字典流程。</summary>
    [TestClass]
    public sealed class MetadataModelTests
    {
        /// <summary>XMZADD 20260831 验证本地人工维护始终高于自动推测结果，避免刷新覆盖用户确认内容。</summary>
        [TestMethod]
        public void LocalOverride_IsHigherThanAutomaticResult()
        {
            Assert.IsTrue(MetadataPriority.LocalOverride > MetadataPriority.AiGuessed);
            Assert.IsTrue(MetadataPriority.LocalOverride > MetadataPriority.CodeEvidence);
            Assert.IsTrue(MetadataPriority.LocalOverride > MetadataPriority.DatabaseEvidence);
            Assert.IsTrue(MetadataPriority.LocalOverride > MetadataPriority.NamingRule);
            Assert.IsTrue(MetadataPriority.AiGuessed > MetadataPriority.PendingConfirmation);
        }

        /// <summary>XMZADD 20260901 验证自动名称严格遵守数据库、知识库、源码、词典、AI 和命名规则顺序。</summary>
        [TestMethod]
        public void MetadataPriority_UsesConfirmedTranslationOrder()
        {
            Assert.IsTrue(MetadataPriority.LocalOverride > MetadataPriority.DatabaseEvidence);
            Assert.IsTrue(MetadataPriority.DatabaseEvidence > MetadataPriority.KnowledgeBaseEvidence);
            Assert.IsTrue(MetadataPriority.KnowledgeBaseEvidence > MetadataPriority.SqlUsageEvidence);
            Assert.IsTrue(MetadataPriority.KnowledgeBaseEvidence > MetadataPriority.CodeEvidence);
            Assert.IsTrue(MetadataPriority.CodeEvidence > MetadataPriority.ConfirmedGlossary);
            Assert.IsTrue(MetadataPriority.ConfirmedGlossary > MetadataPriority.AiGuessed);
            Assert.IsTrue(MetadataPriority.AiGuessed > MetadataPriority.NamingRule);
        }

        /// <summary>XMZADD 20260831 验证人工维护记录保留自动原值和定位键，供后续刷新继续应用同一条修改。</summary>
        [TestMethod]
        public void DictionaryOverride_KeepsManualValueAndStableLocation()
        {
            DateTime updatedAt = new DateTime(2026, 8, 31, 10, 30, 0);
            var item = new DictionaryOverride
            {
                ScopeKey = "SHB|192.0.2.21",
                ObjectName = "T_BD_MATERIAL",
                FieldName = "FMATERIALID",
                ObjectKey = "dbo.T_BD_MATERIAL",
                FieldKey = "FMATERIALID",
                PropertyName = "ChineseName",
                ManualValue = "物料内码",
                OriginalAutomaticValue = "物料 ID",
                IsLocked = true,
                Remark = "与项目术语保持一致",
                UpdatedAt = updatedAt
            };

            Assert.AreEqual("SHB|192.0.2.21", item.ScopeKey);
            Assert.AreEqual("T_BD_MATERIAL", item.ObjectName);
            Assert.AreEqual("FMATERIALID", item.FieldName);
            Assert.AreEqual("dbo.T_BD_MATERIAL", item.ObjectKey);
            Assert.AreEqual("FMATERIALID", item.FieldKey);
            Assert.AreEqual("ChineseName", item.PropertyName);
            Assert.AreEqual("物料内码", item.ManualValue);
            Assert.AreEqual("物料 ID", item.OriginalAutomaticValue);
            Assert.IsTrue(item.IsLocked);
            Assert.AreEqual("与项目术语保持一致", item.Remark);
            Assert.AreEqual(updatedAt, item.UpdatedAt);
        }

        /// <summary>XMZADD 20260831 验证 AI 推测结果能保存名称、业务含义、用途、候选项和可信度。</summary>
        [TestMethod]
        public void AiExplanationResult_KeepsBusinessExplanation()
        {
            var result = new AiExplanationResult
            {
                ObjectName = "T_BD_MATERIAL",
                FieldName = "FMATERIALID",
                ChineseName = "物料内码",
                BusinessMeaning = "物料基础资料的内部标识",
                Usage = "用于关联物料基础资料",
                ConfidenceScore = 82
            };

            result.Alternatives.Add("物料 ID");

            Assert.AreEqual("物料内码", result.ChineseName);
            Assert.AreEqual("物料基础资料的内部标识", result.BusinessMeaning);
            Assert.AreEqual("用于关联物料基础资料", result.Usage);
            Assert.AreEqual(82, result.ConfidenceScore);
            Assert.AreEqual(1, result.Alternatives.Count);
            Assert.AreEqual("物料 ID", result.Alternatives[0]);
        }

        /// <summary>XMZADD 20260831 验证 AI 供应商配置可区分默认项和后续调用所需的连接参数。</summary>
        [TestMethod]
        public void AiProviderConfiguration_KeepsDefaultProviderSettings()
        {
            var provider = new AiProviderConfiguration
            {
                Id = "deepseek-default",
                Name = "DeepSeek",
                Protocol = AiProviderProtocol.OpenAiCompatible,
                Endpoint = "https://api.deepseek.com/v1",
                Model = "deepseek-chat",
                ApiKey = "encrypted-key",
                IsDefault = true,
                IsEnabled = true,
                TimeoutSeconds = 90,
                MaxConcurrency = 2,
                BatchSize = 30
            };

            Assert.AreEqual("deepseek-default", provider.Id);
            Assert.AreEqual(AiProviderProtocol.OpenAiCompatible, provider.Protocol);
            Assert.IsTrue(provider.IsDefault);
            Assert.IsTrue(provider.IsEnabled);
            Assert.AreEqual(90, provider.TimeoutSeconds);
            Assert.AreEqual(2, provider.MaxConcurrency);
            Assert.AreEqual(30, provider.BatchSize);
        }

        /// <summary>XMZADD 20260831 验证表字段和关系均可承载业务解释与用户备注，不改变数据库结构事实。</summary>
        [TestMethod]
        public void MetadataObjects_CarryBusinessExplanationAndRemark()
        {
            var table = new TableMetadata();
            var field = new FieldMetadata();
            var relation = new RelationMetadata();

            table.BusinessMeaning = new MetadataValue { Value = "物料基础资料" };
            table.Remark = new MetadataValue { Value = "供新手查阅" };
            field.BusinessMeaning = new MetadataValue { Value = "物料内部标识" };
            field.Usage = new MetadataValue { Value = "用于业务单据关联" };
            field.Remark = new MetadataValue { Value = "不可人工修改物理字段名" };
            relation.BusinessMeaning = new MetadataValue { Value = "单据引用物料" };
            relation.Remark = new MetadataValue { Value = "由外键识别" };

            Assert.AreEqual("物料基础资料", table.BusinessMeaning.Value);
            Assert.AreEqual("供新手查阅", table.Remark.Value);
            Assert.AreEqual("物料内部标识", field.BusinessMeaning.Value);
            Assert.AreEqual("用于业务单据关联", field.Usage.Value);
            Assert.AreEqual("不可人工修改物理字段名", field.Remark.Value);
            Assert.AreEqual("单据引用物料", relation.BusinessMeaning.Value);
            Assert.AreEqual("由外键识别", relation.Remark.Value);
        }

        /// <summary>XMZADD 20260831 验证 AI 队列任务和结构变化记录可定位新增字段的后续解释工作。</summary>
        [TestMethod]
        public void AiTaskAndSchemaChange_IdentifyAddedField()
        {
            var task = new AiInferenceTask
            {
                ScopeKey = "SHB|192.0.2.21",
                ObjectName = "T_DEMO_ORDER",
                FieldName = "FNEWFIELD",
                Status = AiInferenceTaskStatus.Pending
            };
            var change = new SchemaChange
            {
                ScopeKey = "SHB|192.0.2.21",
                ObjectName = "T_DEMO_ORDER",
                FieldName = "FNEWFIELD",
                ChangeType = SchemaChangeType.FieldAdded
            };

            Assert.AreEqual(AiInferenceTaskStatus.Pending, task.Status);
            Assert.AreEqual("FNEWFIELD", task.FieldName);
            Assert.AreEqual(SchemaChangeType.FieldAdded, change.ChangeType);
            Assert.AreEqual("T_DEMO_ORDER", change.ObjectName);
        }

        /// <summary>XMZADD 20260831 验证列表准确区分 AI 推测和人工修改，并让缺失实体保持空白。</summary>
        [TestMethod]
        public void DisplayModels_ShowAiAndManualStatusesWithoutEntityPlaceholder()
        {
            var aiTable = new TableDisplayModel(new TableMetadata
            {
                ChineseName = new MetadataValue { Value = "图片表", Status = ConfidenceStatus.AiGuessed }
            });
            var manualTable = new TableDisplayModel(new TableMetadata
            {
                ChineseName = new MetadataValue { Value = "物料图片表", Status = ConfidenceStatus.LocalOverride }
            });

            Assert.AreEqual("AI推测", aiTable.StatusText);
            Assert.AreEqual(string.Empty, aiTable.EntityName);
            Assert.AreEqual("人工修改", manualTable.StatusText);
        }

        /// <summary>XMZADD 20260901 验证新表默认进入未分类队列，并保持空表保留规则关闭。</summary>
        [TestMethod]
        public void TableMetadata_DefaultCategory_IsUnclassified()
        {
            var table = new TableMetadata();

            Assert.AreEqual(DictionaryTableCategory.Unclassified, table.Category);
            Assert.AreEqual(0L, table.ApproximateRowCount);
            Assert.IsFalse(table.KeepWhenEmpty);
        }

        /// <summary>XMZADD 20260901 验证共享快照的集合默认可直接用于传统循环。</summary>
        [TestMethod]
        public void SnapshotData_Collections_DefaultToEmptyLists()
        {
            var snapshot = new SnapshotData();

            Assert.IsNotNull(snapshot.Tables);
            Assert.IsNotNull(snapshot.ExcludedObjects);
            Assert.IsNotNull(snapshot.Abbreviations);
            Assert.AreEqual(0, snapshot.ExcludedObjects.Count);
            Assert.AreEqual(0, snapshot.Abbreviations.Count);
        }

        /// <summary>XMZADD 20260910 验证正式中文名与参考名称层默认具有可直接维护的空集合。</summary>
        [TestMethod]
        public void MetadataModels_NewNameLayers_AreInitialized()
        {
            var table = new TableMetadata();
            var field = new FieldMetadata();

            Assert.IsNull(table.SuggestedChineseName);
            Assert.IsNotNull(table.AlternativeChineseNames);
            Assert.IsNotNull(table.RejectedSuggestionFingerprints);
            Assert.IsNotNull(table.UsedByModules);
            Assert.AreEqual(0, table.AlternativeChineseNames.Count);
            Assert.AreEqual(0, table.RejectedSuggestionFingerprints.Count);
            Assert.AreEqual(0, table.UsedByModules.Count);
            Assert.IsNull(field.SuggestedChineseName);
            Assert.IsNotNull(field.AlternativeChineseNames);
            Assert.IsNotNull(field.RejectedSuggestionFingerprints);
            Assert.AreEqual(0, field.AlternativeChineseNames.Count);
            Assert.AreEqual(0, field.RejectedSuggestionFingerprints.Count);
        }

        /// <summary>XMZADD 20260901 验证知识库精确匹配具有独立可信状态。</summary>
        [TestMethod]
        public void ConfidenceStatus_ContainsKnowledgeBaseEvidence()
        {
            ConfidenceStatus status = ConfidenceStatus.KnowledgeBaseEvidence;

            Assert.AreEqual("KnowledgeBaseEvidence", status.ToString());
        }
    }
}
