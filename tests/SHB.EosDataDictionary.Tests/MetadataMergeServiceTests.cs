using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHB.EosDataDictionary.Models;
using SHB.EosDataDictionary.Services;

namespace SHB.EosDataDictionary.Tests
{
    [TestClass]
    public sealed class MetadataMergeServiceTests
    {
        [TestMethod]
        public void Merge_UsesManualValueAndKeepsAutomaticEvidence()
        {
            var automatic = new MetadataCandidate("采购订单", ConfidenceStatus.Guessed, "表名拆分");
            var manual = new MetadataCandidate("采购申请单", ConfidenceStatus.Confirmed, "用户确认");

            MetadataValue result = MetadataMergeService.MergeValue(automatic, manual);

            Assert.AreEqual("采购申请单", result.Value);
            Assert.AreEqual(ConfidenceStatus.Confirmed, result.Status);
            Assert.AreEqual("采购订单", result.OriginalAutomaticValue);
        }

        [TestMethod]
        public void Merge_MarksConflictingAutomaticValuesAsGuessedConflict()
        {
            var first = new MetadataCandidate("采购订单", ConfidenceStatus.CodeEvidence, "Purchase 注释");
            var second = new MetadataCandidate("采购合同", ConfidenceStatus.CodeEvidence, "另一个实体注释");

            MetadataValue result = MetadataMergeService.MergeValue(first, second);

            Assert.AreEqual(ConfidenceStatus.GuessedConflict, result.Status);
            Assert.IsTrue(result.Value.Contains("采购订单"));
            Assert.IsTrue(result.Value.Contains("采购合同"));
        }
    }
}
