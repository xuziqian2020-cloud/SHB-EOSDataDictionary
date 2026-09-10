using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHB.EosDataDictionary.Models;
using SHB.EosDataDictionary.Services;

namespace SHB.EosDataDictionary.Tests
{
    [TestClass]
    public sealed class PreviewDataFactoryTests
    {
        [TestMethod]
        public void Create_ReturnsTablesWithConfirmedAndGuessedStates()
        {
            SnapshotData snapshot = PreviewDataFactory.Create();

            Assert.IsTrue(snapshot.Tables.Count >= 5);
            Assert.IsTrue(HasStatus(snapshot, ConfidenceStatus.Confirmed));
            Assert.IsTrue(HasStatus(snapshot, ConfidenceStatus.Guessed));
            Assert.IsTrue(snapshot.Tables[0].Relations.Count > 0);
        }

        /// <summary>XMZADD 20260828 验证设计预览使用目标稿中的短实体属性名和视图名。</summary>
        [TestMethod]
        public void Create_UsesApprovedPreviewNames()
        {
            SnapshotData snapshot = PreviewDataFactory.Create();
            TableMetadata order = null;
            TableMetadata summary = null;
            for (int i = 0; i < snapshot.Tables.Count; i++)
            {
                if (snapshot.Tables[i].ObjectName == "T_PUR_ORDER")
                {
                    order = snapshot.Tables[i];
                }
                if (snapshot.Tables[i].ObjectType == "VIEW")
                {
                    summary = snapshot.Tables[i];
                }
            }

            Assert.IsNotNull(order);
            Assert.IsNotNull(summary);
            Assert.AreEqual("V_PUR_ORDER_SUM", summary.ObjectName);
            Assert.AreEqual("FId", order.Fields[0].EntityPropertyName.Value);
            Assert.AreEqual("FNumber", order.Fields[1].EntityPropertyName.Value);
            Assert.AreEqual("FSupplierId", order.Fields[2].EntityPropertyName.Value);
            Assert.AreEqual("FStatus", order.Fields[3].EntityPropertyName.Value);
            Assert.AreEqual("FCreateDate", order.Fields[4].EntityPropertyName.Value);
        }

        private static bool HasStatus(SnapshotData snapshot, ConfidenceStatus status)
        {
            for (int i = 0; i < snapshot.Tables.Count; i++)
            {
                if (snapshot.Tables[i].ChineseName != null && snapshot.Tables[i].ChineseName.Status == status)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
