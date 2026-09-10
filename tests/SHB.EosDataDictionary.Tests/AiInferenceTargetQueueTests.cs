using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHB.EosDataDictionary.Models;
using SHB.EosDataDictionary.Services;

namespace SHB.EosDataDictionary.Tests
{
    /// <summary>XMZADD 20260831 验证大快照 AI 队列只向前扫描，避免每批次从百万字段开头重新遍历。</summary>
    [TestClass]
    public sealed class AiInferenceTargetQueueTests
    {
        /// <summary>XMZADD 20260831 验证即使上一批尚未改写状态，下一批也会从游标位置继续取项。</summary>
        [TestMethod]
        public void Take_RepeatedCalls_ContinueFromCursor()
        {
            var snapshot = new SnapshotData
            {
                Tables = new List<TableMetadata>
                {
                    new TableMetadata
                    {
                        ObjectName = "T_ORDER",
                        ChineseName = PendingValue(),
                        Fields = new List<FieldMetadata>
                        {
                            new FieldMetadata { FieldName = "F1", ChineseName = PendingValue() },
                            new FieldMetadata { FieldName = "F2", ChineseName = PendingValue() }
                        }
                    },
                    new TableMetadata
                    {
                        ObjectName = "T_ITEM",
                        ChineseName = PendingValue(),
                        Fields = new List<FieldMetadata>()
                    }
                }
            };
            var queue = new AiInferenceTargetQueue(snapshot);

            IList<AiInferenceTarget> first = queue.Take(2);
            IList<AiInferenceTarget> second = queue.Take(2);

            Assert.AreEqual("T_ORDER", first[0].Table.ObjectName);
            Assert.IsNull(first[0].Field);
            Assert.AreEqual("F1", first[1].Field.FieldName);
            Assert.AreEqual("F2", second[0].Field.FieldName);
            Assert.AreEqual("T_ITEM", second[1].Table.ObjectName);
            Assert.IsNull(second[1].Field);
        }

        /// <summary>XMZADD 20260831 验证规则推测可继续交给 AI，而人工、锁定、本地、代码和数据库结论始终不进入队列。</summary>
        [TestMethod]
        public void Take_IncludesRuleGuessesAndExcludesProtectedEvidence()
        {
            var snapshot = new SnapshotData
            {
                Tables = new List<TableMetadata>
                {
                    new TableMetadata
                    {
                        ObjectName = "T_ORDER",
                        ChineseName = new MetadataValue { Value = "推测：订单", Status = ConfidenceStatus.Guessed },
                        Fields = new List<FieldMetadata>
                        {
                            new FieldMetadata { FieldName = "FPENDING", ChineseName = PendingValue() },
                            new FieldMetadata { FieldName = "FCONFLICT", ChineseName = new MetadataValue { Value = "冲突", Status = ConfidenceStatus.GuessedConflict } },
                            new FieldMetadata { FieldName = "FRULE", ChineseName = new MetadataValue { Value = "推测：规则", Status = ConfidenceStatus.Guessed } },
                            new FieldMetadata { FieldName = "FMANUAL", ChineseName = new MetadataValue { Value = "人工", Status = ConfidenceStatus.Guessed, IsManualOverride = true } },
                            new FieldMetadata { FieldName = "FLOCKED", ChineseName = new MetadataValue { Value = "锁定", Status = ConfidenceStatus.PendingConfirmation, IsLocked = true } },
                            new FieldMetadata { FieldName = "FLOCAL", ChineseName = new MetadataValue { Value = "本地", Status = ConfidenceStatus.LocalOverride } },
                            new FieldMetadata { FieldName = "FCODE", ChineseName = new MetadataValue { Value = "代码", Status = ConfidenceStatus.CodeEvidence } },
                            new FieldMetadata { FieldName = "FDATABASE", ChineseName = new MetadataValue { Value = "数据库", Status = ConfidenceStatus.DatabaseEvidence } }
                        }
                    }
                }
            };
            var queue = new AiInferenceTargetQueue(snapshot);

            IList<AiInferenceTarget> targets = queue.Take(10);

            Assert.AreEqual(4, targets.Count);
            Assert.IsNull(targets[0].Field);
            Assert.AreEqual("FPENDING", targets[1].Field.FieldName);
            Assert.AreEqual("FCONFLICT", targets[2].Field.FieldName);
            Assert.AreEqual("FRULE", targets[3].Field.FieldName);
        }

        /// <summary>XMZADD 20260831 创建待 AI 推测的最小元数据值。</summary>
        private static MetadataValue PendingValue()
        {
            return new MetadataValue { Status = ConfidenceStatus.PendingConfirmation, Value = string.Empty };
        }
    }
}
