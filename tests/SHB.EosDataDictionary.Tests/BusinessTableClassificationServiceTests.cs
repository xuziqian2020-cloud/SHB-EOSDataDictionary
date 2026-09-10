using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHB.EosDataDictionary.Models;
using SHB.EosDataDictionary.Services;

namespace SHB.EosDataDictionary.Tests
{
    [TestClass]
    public sealed class BusinessTableClassificationServiceTests
    {
        /// <summary>XMZADD 20260903 验证第一版分类只排除明确临时表，并把无证据旧业务分类降为待识别。</summary>
        [TestMethod]
        public void Classify_ConservativeSignals_SeparateBusinessTechnicalExcludedAndUnknown()
        {
            var snapshot = new SnapshotData
            {
                Tables = new List<TableMetadata>
                {
                    CreateTable("Item"),
                    CreateTable("tmp_import_2020"),
                    CreateTable("System_Run_Log"),
                    CreateTable("Unknown_Archive_Data")
                }
            };
            var evidence = new List<SourceEvidence>
            {
                new SourceEvidence
                {
                    ObjectName = "Item",
                    EntityName = "t_Item",
                    Evidence = new EvidenceItem { RuleName = "TableNameProperty" }
                }
            };

            new BusinessTableClassificationService().Classify(snapshot, evidence, null);

            Assert.AreEqual(DictionaryTableCategory.Business, snapshot.Tables[0].Category);
            Assert.AreEqual(DictionaryTableCategory.Excluded, snapshot.Tables[1].Category);
            Assert.AreEqual(DictionaryTableCategory.Technical, snapshot.Tables[2].Category);
            Assert.AreEqual(DictionaryTableCategory.Unclassified, snapshot.Tables[3].Category);
            Assert.AreEqual("LowValueTableName", snapshot.Tables[1].Remark.Evidence[0].RuleName);
        }

        /// <summary>XMZADD 20260903 验证实体证据优先于临时命名规则，防止真实业务表被错误排除。</summary>
        [TestMethod]
        public void Classify_TemporaryNameWithEntityEvidence_RemainsBusiness()
        {
            SnapshotData snapshot = new SnapshotData
            {
                Tables = new List<TableMetadata> { CreateTable("tmp_business") }
            };
            var evidence = new List<SourceEvidence>
            {
                new SourceEvidence
                {
                    ObjectName = "tmp_business",
                    EntityName = "t_tmp_business",
                    Evidence = new EvidenceItem { RuleName = "TableNameProperty" }
                }
            };

            new BusinessTableClassificationService().Classify(snapshot, evidence, null);

            Assert.AreEqual(DictionaryTableCategory.Business, snapshot.Tables[0].Category);
        }

        /// <summary>XMZADD 20260903 创建带旧自动业务分类的最小表，验证第一版会重新评估而非沿用错误默认值。</summary>
        private static TableMetadata CreateTable(string objectName)
        {
            return new TableMetadata
            {
                ObjectName = objectName,
                Category = DictionaryTableCategory.Business,
                ChineseName = new MetadataValue { Value = "推测名称", Status = ConfidenceStatus.Guessed }
            };
        }
    }
}
