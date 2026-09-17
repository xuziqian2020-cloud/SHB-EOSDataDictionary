using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHB.EosDataDictionary.Models;
using SHB.EosDataDictionary.Services;

namespace SHB.EosDataDictionary.Tests
{
    /// <summary>XMZADD 20260916 验证 V6 候选只有在名称、关系、覆盖和冲突指标全部达标时才能提升。</summary>
    [TestClass]
    public sealed class DictionaryV6QualityGateServiceTests
    {
        /// <summary>XMZADD 20260916 验证正式名称准确率不足百分之九十九时禁止发布。</summary>
        [TestMethod]
        public void Evaluate_OfficialAccuracyBelowNinetyNinePercent_BlocksPromotion()
        {
            DictionaryV6QualityInput input = CreatePassingInput();
            input.OfficialNameAccuracy = 0.989D;

            DictionaryV6QualityResult result = new DictionaryV6QualityGateService().Evaluate(input);

            Assert.IsFalse(result.CanPromote);
            AssertContains(result.Failures, "正式名称准确率");
        }

        /// <summary>XMZADD 20260916 验证最佳参考译名准确率不足百分之九十五时禁止发布。</summary>
        [TestMethod]
        public void Evaluate_SuggestedAccuracyBelowNinetyFivePercent_BlocksPromotion()
        {
            DictionaryV6QualityInput input = CreatePassingInput();
            input.SuggestedNameAccuracy = 0.949D;

            DictionaryV6QualityResult result = new DictionaryV6QualityGateService().Evaluate(input);

            Assert.IsFalse(result.CanPromote);
            AssertContains(result.Failures, "参考译名准确率");
        }

        /// <summary>XMZADD 20260916 验证关系方向准确率不足百分之九十八时禁止发布。</summary>
        [TestMethod]
        public void Evaluate_RelationAccuracyBelowNinetyEightPercent_BlocksPromotion()
        {
            DictionaryV6QualityInput input = CreatePassingInput();
            input.RelationDirectionAccuracy = 0.979D;

            DictionaryV6QualityResult result = new DictionaryV6QualityGateService().Evaluate(input);

            Assert.IsFalse(result.CanPromote);
            AssertContains(result.Failures, "关系方向准确率");
        }

        /// <summary>XMZADD 20260916 验证任一实际使用字段同时缺少正式名和参考名时禁止发布。</summary>
        [TestMethod]
        public void Evaluate_UsedFieldWithoutAnyReliableName_BlocksPromotion()
        {
            DictionaryV6QualityInput input = CreatePassingInput();
            input.UsedFieldGapCount = 1;

            DictionaryV6QualityResult result = new DictionaryV6QualityGateService().Evaluate(input);

            Assert.IsFalse(result.CanPromote);
            AssertContains(result.Failures, "实际使用字段");
        }

        /// <summary>XMZADD 20260916 验证伪中文正式值不能进入新人默认字典。</summary>
        [TestMethod]
        public void Evaluate_PseudoChineseOfficialValue_BlocksPromotion()
        {
            DictionaryV6QualityInput input = CreatePassingInput();
            input.PseudoChineseOfficialCount = 1;

            DictionaryV6QualityResult result = new DictionaryV6QualityGateService().Evaluate(input);

            Assert.IsFalse(result.CanPromote);
            AssertContains(result.Failures, "伪中文正式名称");
        }

        /// <summary>XMZADD 20260916 验证正式名称仍处于冲突状态时禁止发布。</summary>
        [TestMethod]
        public void Evaluate_OfficialNameConflict_BlocksPromotion()
        {
            DictionaryV6QualityInput input = CreatePassingInput();
            input.OfficialConflictCount = 1;

            DictionaryV6QualityResult result = new DictionaryV6QualityGateService().Evaluate(input);

            Assert.IsFalse(result.CanPromote);
            AssertContains(result.Failures, "正式名称冲突");
        }

        /// <summary>XMZADD 20260916 验证任一 EOS 实体表未完成名称、分类和模块审计时禁止发布。</summary>
        [TestMethod]
        public void Evaluate_EntityTableCoverageIncomplete_BlocksPromotion()
        {
            DictionaryV6QualityInput input = CreatePassingInput();
            input.AuditedEntityTableCount--;

            DictionaryV6QualityResult result = new DictionaryV6QualityGateService().Evaluate(input);

            Assert.IsFalse(result.CanPromote);
            AssertContains(result.Failures, "实体表审计覆盖");
        }

        /// <summary>XMZADD 20260916 验证金标规模低于表、字段或关系最低样本数时禁止发布。</summary>
        [TestMethod]
        public void Evaluate_GoldStandardBelowMinimumScope_BlocksPromotion()
        {
            DictionaryV6QualityInput input = CreatePassingInput();
            input.GoldFieldCount = 999;

            DictionaryV6QualityResult result = new DictionaryV6QualityGateService().Evaluate(input);

            Assert.IsFalse(result.CanPromote);
            AssertContains(result.Failures, "金标准规模");
        }

        /// <summary>XMZADD 20260916 验证全部发布指标达标时返回唯一可提升结论。</summary>
        [TestMethod]
        public void Evaluate_AllThresholdsPass_AllowsPromotion()
        {
            DictionaryV6QualityResult result = new DictionaryV6QualityGateService().Evaluate(CreatePassingInput());

            Assert.IsTrue(result.CanPromote);
            Assert.AreEqual(0, result.Failures.Count);
        }

        /// <summary>XMZADD 20260916 验证仓库金标达到八个业务模块和固定样本规模且没有重复稳定键。</summary>
        [TestMethod]
        public void LoadGoldStandard_RepositoryArtifact_MeetsScopeAndEvidenceRequirements()
        {
            string root = FindRepositoryRoot();
            string path = Path.Combine(root, "glossary", "quality", "dictionary-v6-gold.json");

            DictionaryV6GoldStandard gold = new DictionaryV6QualityGateService().LoadGoldStandard(path);

            Assert.AreEqual(200, CountKind(gold, "Table"));
            Assert.AreEqual(1200, CountKind(gold, "Field"));
            Assert.AreEqual(100, CountKind(gold, "Relation"));
            Assert.AreEqual(8, CountNameModules(gold));
            Assert.AreEqual(gold.Entries.Count, CountStableKeys(gold));
        }

        /// <summary>XMZADD 20260916 验证全量评估按正式名、最佳参考名、关系方向和实体审计计算真实指标。</summary>
        [TestMethod]
        public void Evaluate_SnapshotAndGold_ComputesMeasuredMetrics()
        {
            var table = new TableMetadata
            {
                SchemaName = "dbo",
                ObjectName = "Purchase_Order",
                Category = DictionaryTableCategory.Business,
                ChineseName = new MetadataValue { Value = "采购订单", Status = ConfidenceStatus.KnowledgeBaseEvidence },
                ModuleName = new MetadataValue { Value = "采购管理", Status = ConfidenceStatus.CodeEvidence },
                EntityName = new MetadataValue { Value = "t_Purchase_Order", Status = ConfidenceStatus.CodeEvidence },
                Fields = new List<FieldMetadata>
                {
                    new FieldMetadata
                    {
                        FieldName = "PO_ID",
                        ChineseName = new MetadataValue { Value = "采购订单ID", Status = ConfidenceStatus.KnowledgeBaseEvidence },
                        Usage = new MetadataValue { Value = "业务代码读取", Status = ConfidenceStatus.CodeEvidence }
                    }
                }
            };
            table.Relations.Add(new RelationMetadata
            {
                ParentSchemaName = "dbo",
                ParentTableName = "Purchase_Order",
                ParentFieldName = "PO_ID",
                ChildSchemaName = "dbo",
                ChildTableName = "Purchase_Order_Item",
                ChildFieldName = "PO_ID",
                RelationType = new MetadataValue { Value = "多对一", Status = ConfidenceStatus.DatabaseEvidence }
            });
            var gold = new DictionaryV6GoldStandard
            {
                Entries = new List<DictionaryV6GoldEntry>
                {
                    CreateGoldName("Table", "Table|dbo|Purchase_Order", "Purchase_Order", "", "采购订单"),
                    CreateGoldName("Field", "Field|dbo|Purchase_Order|PO_ID", "Purchase_Order", "PO_ID", "采购订单ID"),
                    new DictionaryV6GoldEntry
                    {
                        Kind = "Relation",
                        StableKey = "Relation|dbo|Purchase_Order|PO_ID|dbo|Purchase_Order_Item|PO_ID",
                        ObjectName = "Purchase_Order",
                        ExpectedChineseName = "数据库外键关联",
                        ExpectedDisposition = "CorrectRelation",
                        EvidenceType = "DatabaseForeignKey",
                        Source = "SQLServerForeignKey:FK_PO_ITEM_PO",
                        ParentSchemaName = "dbo",
                        ParentTableName = "Purchase_Order",
                        ParentFieldName = "PO_ID",
                        ChildSchemaName = "dbo",
                        ChildTableName = "Purchase_Order_Item",
                        ChildFieldName = "PO_ID"
                    }
                }
            };

            DictionaryV6QualityResult result = new DictionaryV6QualityGateService().Evaluate(
                new SnapshotData { Tables = new List<TableMetadata> { table } },
                new List<SourceEvidence>(), gold);

            Assert.AreEqual(1D, result.OfficialNameAccuracy);
            Assert.AreEqual(1D, result.SuggestedNameAccuracy);
            Assert.AreEqual(1D, result.RelationDirectionAccuracy);
            Assert.AreEqual(0, result.UsedFieldGapCount);
            Assert.AreEqual(0, result.PseudoChineseOfficialCount);
            Assert.AreEqual(1, result.EntityTableCount);
            Assert.AreEqual(1, result.AuditedEntityTableCount);
        }

        /// <summary>XMZADD 20260916 验证全量评估能识别实际字段缺名、伪中文正式值和未审计实体表。</summary>
        [TestMethod]
        public void Evaluate_SnapshotWithUnsafeOfficialName_ReportsBlockingAudits()
        {
            var table = new TableMetadata
            {
                SchemaName = "dbo",
                ObjectName = "Unsafe_Table",
                Category = DictionaryTableCategory.Unclassified,
                EntityName = new MetadataValue { Value = "t_Unsafe_Table", Status = ConfidenceStatus.CodeEvidence },
                Fields = new List<FieldMetadata>
                {
                    new FieldMetadata
                    {
                        FieldName = "Owner_Company_ID",
                        ChineseName = new MetadataValue
                        {
                            Value = "Owner公司ID",
                            Status = ConfidenceStatus.GuessedConflict
                        },
                        Usage = new MetadataValue { Value = "业务代码读取", Status = ConfidenceStatus.CodeEvidence }
                    }
                }
            };
            var gold = new DictionaryV6GoldStandard { Entries = new List<DictionaryV6GoldEntry>() };

            DictionaryV6QualityResult result = new DictionaryV6QualityGateService().Evaluate(
                new SnapshotData { Tables = new List<TableMetadata> { table } },
                new List<SourceEvidence>(), gold);

            Assert.AreEqual(1, result.UsedFieldGapCount);
            Assert.AreEqual(1, result.PseudoChineseOfficialCount);
            Assert.AreEqual(1, result.OfficialConflictCount);
            Assert.AreEqual(1, result.EntityTableCount);
            Assert.AreEqual(0, result.AuditedEntityTableCount);
            Assert.IsFalse(result.CanPromote);
        }

        /// <summary>XMZADD 20260917 验证实体分类审计不被名称参考层或尚待裁决的模块归属重复阻塞。</summary>
        [TestMethod]
        public void Evaluate_ClassifiedEntityWithPendingNameAndModule_CountsAsAudited()
        {
            var table = new TableMetadata
            {
                SchemaName = "dbo",
                ObjectName = "Domain_Acronym_Table",
                Category = DictionaryTableCategory.Business,
                EntityName = new MetadataValue { Value = "t_Domain_Acronym_Table", Status = ConfidenceStatus.CodeEvidence },
                ChineseName = new MetadataValue { Value = string.Empty, Status = ConfidenceStatus.PendingConfirmation },
                SuggestedChineseName = new MetadataValue { Value = "领域XYZ表", Status = ConfidenceStatus.Guessed },
                ModuleName = new MetadataValue { Value = string.Empty, Status = ConfidenceStatus.PendingConfirmation }
            };

            DictionaryV6QualityResult result = new DictionaryV6QualityGateService().Evaluate(
                new SnapshotData { Tables = new List<TableMetadata> { table } },
                new List<SourceEvidence>(),
                new DictionaryV6GoldStandard { Entries = new List<DictionaryV6GoldEntry>() });

            Assert.AreEqual(1, result.EntityTableCount);
            Assert.AreEqual(1, result.AuditedEntityTableCount);
        }

        /// <summary>XMZADD 20260916 创建恰好满足全部硬门槛的质量输入，确保单项失败测试互不干扰。</summary>
        private static DictionaryV6QualityInput CreatePassingInput()
        {
            return new DictionaryV6QualityInput
            {
                OfficialNameAccuracy = 0.99D,
                SuggestedNameAccuracy = 0.95D,
                RelationDirectionAccuracy = 0.98D,
                UsedFieldGapCount = 0,
                PseudoChineseOfficialCount = 0,
                OfficialConflictCount = 0,
                EntityTableCount = 1287,
                AuditedEntityTableCount = 1287,
                GoldTableCount = 150,
                GoldFieldCount = 1000,
                GoldRelationCount = 100
            };
        }

        /// <summary>XMZADD 20260916 创建可定位的正确表名或字段名金标。</summary>
        private static DictionaryV6GoldEntry CreateGoldName(string kind, string stableKey,
            string objectName, string fieldName, string expectedName)
        {
            return new DictionaryV6GoldEntry
            {
                Kind = kind,
                StableKey = stableKey,
                SchemaName = "dbo",
                ObjectName = objectName,
                FieldName = fieldName,
                ExpectedChineseName = expectedName,
                ExpectedDisposition = "CorrectName",
                ModuleName = "采购管理",
                EvidenceType = "KnowledgeExact",
                Source = "docs_knowledge/fixture.md:1"
            };
        }

        /// <summary>XMZADD 20260916 验证失败原因包含用户可执行的中文指标名称。</summary>
        private static void AssertContains(IList<string> failures, string expected)
        {
            for (int index = 0; index < failures.Count; index++)
            {
                if ((failures[index] ?? string.Empty).IndexOf(expected, StringComparison.Ordinal) >= 0)
                {
                    return;
                }
            }
            Assert.Fail("未找到失败原因：" + expected);
        }

        /// <summary>XMZADD 20260916 从测试输出目录向上定位包含解决方案和金标目录的仓库根目录。</summary>
        private static string FindRepositoryRoot()
        {
            DirectoryInfo current = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (current != null)
            {
                if (File.Exists(Path.Combine(current.FullName, "global.json")) &&
                    Directory.Exists(Path.Combine(current.FullName, "glossary")))
                {
                    return current.FullName;
                }
                current = current.Parent;
            }
            Assert.Fail("无法定位数据字典仓库根目录。");
            return string.Empty;
        }

        /// <summary>XMZADD 20260916 统计指定类型金标数量。</summary>
        private static int CountKind(DictionaryV6GoldStandard gold, string kind)
        {
            int count = 0;
            for (int index = 0; index < gold.Entries.Count; index++)
            {
                if (string.Equals(gold.Entries[index].Kind, kind, StringComparison.Ordinal)) count++;
            }
            return count;
        }

        /// <summary>XMZADD 20260916 统计名称金标覆盖的不同业务模块数量。</summary>
        private static int CountNameModules(DictionaryV6GoldStandard gold)
        {
            var modules = new HashSet<string>(StringComparer.Ordinal);
            for (int index = 0; index < gold.Entries.Count; index++)
            {
                DictionaryV6GoldEntry entry = gold.Entries[index];
                if (entry.Kind != "Relation" && !string.IsNullOrWhiteSpace(entry.ModuleName))
                {
                    modules.Add(entry.ModuleName);
                }
            }
            return modules.Count;
        }

        /// <summary>XMZADD 20260916 统计大小写不敏感的唯一金标稳定键数量。</summary>
        private static int CountStableKeys(DictionaryV6GoldStandard gold)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < gold.Entries.Count; index++)
            {
                keys.Add(gold.Entries[index].StableKey);
            }
            return keys.Count;
        }
    }
}
