using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHB.EosDataDictionary.Models;
using SHB.EosDataDictionary.Services;

namespace SHB.EosDataDictionary.Tests
{
    [TestClass]
    public sealed class EntityMappingRetentionServiceTests
    {
        /// <summary>XMZADD 20260909 验证数据库重扫遗漏实体时继承上一规范中可追溯的 EOS 源码实体映射。</summary>
        [TestMethod]
        public void Apply_CurrentEntityMissing_RetainsPreviousEosSourceEntity()
        {
            SnapshotData previous = CreateSnapshot(CreateEntity(
                "t_DA_Account",
                ConfidenceStatus.CodeEvidence,
                "EOS 源码实体类",
                "TableNameProperty"));
            SnapshotData current = CreateSnapshot(null);

            new EntityMappingRetentionService().Apply(previous, current);

            Assert.IsNotNull(current.Tables[0].EntityName);
            Assert.AreEqual("t_DA_Account", current.Tables[0].EntityName.Value);
            Assert.AreEqual(ConfidenceStatus.CodeEvidence, current.Tables[0].EntityName.Status);
            Assert.AreNotSame(previous.Tables[0].EntityName, current.Tables[0].EntityName);
            Assert.AreNotSame(previous.Tables[0].EntityName.Evidence, current.Tables[0].EntityName.Evidence);
        }

        /// <summary>XMZADD 20260909 验证当前扫描已经找到可信实体时不被上一规范中的旧映射覆盖。</summary>
        [TestMethod]
        public void Apply_CurrentEntityPresent_PreservesCurrentValue()
        {
            SnapshotData previous = CreateSnapshot(CreateEntity(
                "t_Old_Account",
                ConfidenceStatus.CodeEvidence,
                "EOS 源码实体类",
                "TableNameProperty"));
            SnapshotData current = CreateSnapshot(CreateEntity(
                "t_DA_Account",
                ConfidenceStatus.CodeEvidence,
                "EOS 源码实体类",
                "KisEntityClass"));

            new EntityMappingRetentionService().Apply(previous, current);

            Assert.AreEqual("t_DA_Account", current.Tables[0].EntityName.Value);
        }

        /// <summary>XMZADD 20260909 验证没有 EOS 源码规则的猜测实体不会跨结构扫描继承。</summary>
        [TestMethod]
        public void Apply_PreviousEntityHasNoEosEvidence_DoesNotRetainGuess()
        {
            SnapshotData previous = CreateSnapshot(CreateEntity(
                "t_Guessed_Account",
                ConfidenceStatus.Guessed,
                "命名规则推测",
                "NamingRule"));
            SnapshotData current = CreateSnapshot(null);

            new EntityMappingRetentionService().Apply(previous, current);

            Assert.IsNull(current.Tables[0].EntityName);
        }

        /// <summary>XMZADD 20260909 创建结构扫描前后的最小同键表快照。</summary>
        private static SnapshotData CreateSnapshot(MetadataValue entityName)
        {
            return new SnapshotData
            {
                Tables = new List<TableMetadata>
                {
                    new TableMetadata
                    {
                        ScopeKey = "test|EOS",
                        SchemaName = "dbo",
                        ObjectName = "DA_Account",
                        ObjectType = "TABLE",
                        EntityName = entityName
                    }
                }
            };
        }

        /// <summary>XMZADD 20260909 创建带一条公开规则证据的实体元数据。</summary>
        private static MetadataValue CreateEntity(
            string value,
            ConfidenceStatus status,
            string sourceSummary,
            string ruleName)
        {
            return new MetadataValue
            {
                Value = value,
                Status = status,
                ConfidenceScore = status == ConfidenceStatus.CodeEvidence ? 100 : 40,
                SourceType = "Code",
                SourceSummary = sourceSummary,
                Evidence = new List<EvidenceItem>
                {
                    new EvidenceItem
                    {
                        SourceType = "源码",
                        SourcePath = "ERP/表-类定义/t_DA_Account.vb",
                        SourceLine = 3,
                        RuleName = ruleName,
                        RawValue = value,
                        Explanation = "从 EOS 实体定义确认数据库对象。"
                    }
                }
            };
        }
    }
}
