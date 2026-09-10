using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHB.EosDataDictionary.Models;
using SHB.EosDataDictionary.Services;

namespace SHB.EosDataDictionary.Tests
{
    [TestClass]
    public sealed class BusinessDictionaryV1ReportServiceTests
    {
        /// <summary>XMZADD 20260903 验证第一版报告统计实体、知识库、枚举、关系和分类覆盖情况。</summary>
        [TestMethod]
        public void CreateAndWrite_EnrichedSnapshot_ProducesCoverageAndAuditFiles()
        {
            SnapshotData snapshot = CreateSnapshot();
            var service = new BusinessDictionaryV1ReportService();

            var sourceEvidence = new List<SourceEvidence>
            {
                new SourceEvidence
                {
                    ObjectName = "Item",
                    RelationFieldName = "Company_ID",
                    RelationTargetEntity = "t_Company",
                    Evidence = new EvidenceItem { RuleName = "EntityObjectRelation", SourcePath = "t_Item.vb", SourceLine = 10 }
                },
                new SourceEvidence
                {
                    ObjectName = "Missing",
                    RelationFieldName = "Missing_ID",
                    RelationTargetEntity = "t_Missing",
                    Evidence = new EvidenceItem { RuleName = "EntityObjectRelation", SourcePath = "t_Missing.vb", SourceLine = 20 }
                }
            };
            BusinessDictionaryV1Report report = service.Create(snapshot, sourceEvidence);

            Assert.AreEqual(3, report.TotalTableCount);
            Assert.AreEqual(3, report.TotalFieldCount);
            Assert.AreEqual(1, report.EntityTableCount);
            Assert.AreEqual(1, report.KnowledgeTableCount);
            Assert.AreEqual(1, report.EnumFieldCount);
            Assert.AreEqual(1, report.CodeRelationCount);
            Assert.AreEqual(1, report.HighConfidenceFieldNameCount);
            Assert.AreEqual(2, report.LowConfidenceTableNameCount);
            Assert.AreEqual(2, report.ObjectRelationEvidenceCount);
            Assert.AreEqual(1, report.UnresolvedObjectRelationCount);
            Assert.AreEqual(1, report.TechnicalTableCount);
            Assert.AreEqual(1, report.ExcludedTableCount);

            string root = Path.Combine(Path.GetTempPath(), "shb-v1-report-" + Guid.NewGuid().ToString("N"));
            try
            {
                service.Write(root, snapshot, report, sourceEvidence);

                Assert.IsTrue(File.Exists(Path.Combine(root, "推断运行报告.md")));
                Assert.IsTrue(File.Exists(Path.Combine(root, "核心业务表.csv")));
                Assert.IsTrue(File.Exists(Path.Combine(root, "核心业务字段.csv")));
                Assert.IsTrue(File.Exists(Path.Combine(root, "低价值表排除清单.csv")));
                Assert.IsTrue(File.Exists(Path.Combine(root, "关联关系清单.csv")));
                Assert.IsTrue(File.Exists(Path.Combine(root, "未落地代码关系列表.csv")));
                Assert.IsTrue(File.Exists(Path.Combine(root, "待复核中文名称.csv")));
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        /// <summary>XMZADD 20260904 验证纯英文占位、混入英文和低置信中文会进入独立复核清单。</summary>
        [TestMethod]
        public void Write_UnresolvedChineseNames_ProducesReviewCsv()
        {
            var snapshot = new SnapshotData
            {
                Tables = new List<TableMetadata>
                {
                    new TableMetadata
                    {
                        ObjectName = "AB01",
                        Category = DictionaryTableCategory.Business,
                        ChineseName = new MetadataValue
                        {
                            Value = "暂无可靠中文名称",
                            Status = ConfidenceStatus.PendingConfirmation
                        },
                        Fields = new List<FieldMetadata>
                        {
                            new FieldMetadata
                            {
                                FieldName = "ABC_Order",
                                ChineseName = new MetadataValue
                                {
                                    Value = "ABC订单",
                                    Status = ConfidenceStatus.Guessed
                                }
                            }
                        }
                    },
                    new TableMetadata
                    {
                        ObjectName = "Item",
                        Category = DictionaryTableCategory.BaseData,
                        ChineseName = new MetadataValue
                        {
                            Value = "物料主数据",
                            Status = ConfidenceStatus.CodeEvidence
                        },
                        Fields = new List<FieldMetadata>
                        {
                            new FieldMetadata
                            {
                                FieldName = "Item_ID",
                                ChineseName = new MetadataValue
                                {
                                    Value = "物料ID",
                                    Status = ConfidenceStatus.CodeEvidence
                                }
                            }
                        }
                    },
                    new TableMetadata
                    {
                        ObjectName = "Temp_Archive_20200101",
                        Category = DictionaryTableCategory.Unclassified,
                        ChineseName = new MetadataValue
                        {
                            Value = "暂无可靠中文名称",
                            Status = ConfidenceStatus.PendingConfirmation
                        }
                    }
                }
            };
            string root = Path.Combine(Path.GetTempPath(), "shb-v3-review-" + Guid.NewGuid().ToString("N"));

            try
            {
                new BusinessDictionaryV1ReportService().Write(root, snapshot, null);

                string path = Path.Combine(root, "待复核中文名称.csv");
                Assert.IsTrue(File.Exists(path));
                string content = File.ReadAllText(path);
                StringAssert.Contains(content, "AB01");
                StringAssert.Contains(content, "ABC_Order");
                Assert.IsFalse(content.Contains("\"Item\",\"Item_ID\""));
                Assert.IsFalse(content.Contains("Temp_Archive_20200101"));
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        /// <summary>XMZADD 20260907 验证 V4 只审计真实名称细化，并仅把业务代码实际使用的未决字段送入专项复核。</summary>
        [TestMethod]
        public void Write_V4AuditFiles_OnlyIncludeRefinementAndActuallyUsedPendingField()
        {
            SnapshotData snapshot = CreateV4Snapshot();
            IList<SourceEvidence> sourceEvidence = CreateV4SourceEvidence();
            string root = Path.Combine(Path.GetTempPath(), "shb-v4-report-" + Guid.NewGuid().ToString("N"));

            try
            {
                File.WriteAllText(Path.Combine(Directory.CreateDirectory(root).FullName, "用户保留.txt"), "不得删除");
                new BusinessDictionaryV1ReportService().Write(root, snapshot, null, sourceEvidence);

                string audit = File.ReadAllText(Path.Combine(root, "中文名称变更审计.csv"));
                string review = File.ReadAllText(Path.Combine(root, "实际使用字段待复核.csv"));
                string abbreviation = File.ReadAllText(Path.Combine(root, "未知缩写频率.csv"));

                StringAssert.StartsWith(audit, "ObjectName,FieldName,OldChineseName,NewChineseName,ConfidenceScore,RuleName,Explanation,SourcePath,SourceLine\r\n");
                StringAssert.Contains(audit, "\"DA_Acceptance\",\"Owner_Company_ID\",\"公司ID\",\"货主公司ID\",\"92\",\"GridColumnCaption\",\"界面列标题确认货主公司\",\"ERP/采购/f_acceptance.vb\",\"55\"");
                Assert.IsFalse(audit.Contains("op_createtime"), "旧值与新值相同不应误报为名称变更。");

                StringAssert.StartsWith(review, "ObjectName,FieldName,CurrentChineseName,UnknownTokens,UsageKinds,SourcePath,SourceLine,Reason\r\n");
                StringAssert.Contains(review, "\"DA_Acceptance\",\"IBS\",\"暂无可靠中文名称\",\"IBS\",\"SqlFieldUsage\",\"ERP/采购/f_acceptance.vb\",\"90\"");
                Assert.IsFalse(review.Contains("Owner_Company_ID"));
                Assert.IsFalse(review.Contains("op_createtime"));
                Assert.IsFalse(review.Contains("Archive_IBS"), "实体结构和 DataColumn 标题不属于普通代码实际使用证据。");

                StringAssert.StartsWith(abbreviation, "Token,Count,ExampleObject,ModuleName,SourcePath,SourceLine\r\n");
                StringAssert.Contains(abbreviation, "\"IBS\",\"1\",\"DA_Acceptance\",\"采购管理\",\"ERP/采购/f_acceptance.vb\",\"90\"");
                Assert.IsTrue(File.Exists(Path.Combine(root, "用户保留.txt")), "写报告不能清理用户目录中的无关文件。");
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        /// <summary>XMZADD 20260907 验证 V4 指标按表字段去重统计实际用途、可靠名称、名称细化和多义冲突。</summary>
        [TestMethod]
        public void Create_V4Metrics_DeduplicateEvidenceByTableAndField()
        {
            BusinessDictionaryV1Report report = new BusinessDictionaryV1ReportService().Create(
                CreateV4Snapshot(), CreateV4SourceEvidence());

            Assert.AreEqual(3, report.ActualUsedFieldCount);
            Assert.AreEqual(2, report.ActualUsedNamedFieldCount);
            Assert.AreEqual(1, report.V4RefinedNameCount);
            Assert.AreEqual(1, report.ActualUsedReviewFieldCount);
            Assert.AreEqual(1, report.AmbiguousConflictCount);
        }

        /// <summary>XMZADD 20260907 验证未知缩写按不同实际字段计数，同字段重复证据不重复累计且行业标识不会误报。</summary>
        [TestMethod]
        public void Write_UnknownAbbreviationFrequency_CountsDistinctFieldsAndAllowsIndustryIdentifiers()
        {
            var snapshot = new SnapshotData
            {
                Tables = new List<TableMetadata>
                {
                    CreatePendingTable("B_Table", "仓储", "IBS"),
                    CreatePendingTable("A_Table", "采购", "IBS_Code"),
                    CreatePendingTable("C_Table", "系统", "ID_GUID_ERP_EOS_KIS_K3_SQL_IP_MAC_URL")
                }
            };
            var sourceEvidence = new List<SourceEvidence>
            {
                CreateUsageEvidence("B_Table", "IBS", "SqlFieldUsage", "b.vb", 20),
                CreateUsageEvidence("A_Table", "IBS_Code", "SqlFieldUsage", "a.vb", 10),
                CreateUsageEvidence("A_Table", "IBS_Code", "SqlFieldRelation", "a.vb", 11),
                CreateUsageEvidence("A_Table", "IBS_Code", "SqlFieldRelation", "a.vb", 11),
                CreateUsageEvidence("C_Table", "ID_GUID_ERP_EOS_KIS_K3_SQL_IP_MAC_URL", "SqlFieldUsage", "c.vb", 30)
            };
            string root = Path.Combine(Path.GetTempPath(), "shb-v4-abbreviation-" + Guid.NewGuid().ToString("N"));

            try
            {
                new BusinessDictionaryV1ReportService().Write(root, snapshot, null, sourceEvidence);

                string content = File.ReadAllText(Path.Combine(root, "未知缩写频率.csv"));
                StringAssert.Contains(content, "\"IBS\",\"2\",\"A_Table\",\"采购\",\"a.vb\",\"11\"");
                Assert.IsFalse(content.Contains("\"ID\""));
                Assert.IsFalse(content.Contains("\"GUID\""));
                Assert.IsFalse(content.Contains("\"ERP\""));
                Assert.IsFalse(content.Contains("\"EOS\""));
                Assert.IsFalse(content.Contains("\"KIS\""));
                Assert.IsFalse(content.Contains("\"K3\""));
                Assert.IsFalse(content.Contains("\"SQL\""));
                Assert.IsFalse(content.Contains("\"IP\""));
                Assert.IsFalse(content.Contains("\"MAC\""));
                Assert.IsFalse(content.Contains("\"URL\""));
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        /// <summary>XMZADD 20260907 验证 IP、MAC、URL 等允许标识不降低实际字段命名状态，而未知英文仍进入专项复核。</summary>
        [TestMethod]
        public void CreateAndWrite_AllowedEnglishIdentifiers_AreReliablyNamedButUnknownEnglishIsReviewed()
        {
            var table = new TableMetadata
            {
                ObjectName = "Network_Setting",
                Category = DictionaryTableCategory.Business,
                Fields = new List<FieldMetadata>
                {
                    CreateNamedField("IP_Address", "IP地址"),
                    CreateNamedField("MAC_Address", "MAC地址"),
                    CreateNamedField("Callback_URL", "回调URL"),
                    CreateNamedField("ABC_Address", "ABC地址")
                }
            };
            var snapshot = new SnapshotData { Tables = new List<TableMetadata> { table } };
            var sourceEvidence = new List<SourceEvidence>
            {
                CreateUsageEvidence("Network_Setting", "IP_Address", "SqlFieldUsage", "network.vb", 10),
                CreateUsageEvidence("Network_Setting", "MAC_Address", "SqlFieldUsage", "network.vb", 11),
                CreateUsageEvidence("Network_Setting", "Callback_URL", "SqlFieldUsage", "network.vb", 12),
                CreateUsageEvidence("Network_Setting", "ABC_Address", "SqlFieldUsage", "network.vb", 13)
            };
            var service = new BusinessDictionaryV1ReportService();
            BusinessDictionaryV1Report report = service.Create(snapshot, sourceEvidence);
            string root = Path.Combine(Path.GetTempPath(), "shb-v4-allowed-identifiers-" + Guid.NewGuid().ToString("N"));

            try
            {
                service.Write(root, snapshot, report, sourceEvidence);
                string review = File.ReadAllText(Path.Combine(root, "实际使用字段待复核.csv"));

                Assert.AreEqual(4, report.ActualUsedFieldCount);
                Assert.AreEqual(3, report.ActualUsedNamedFieldCount);
                Assert.AreEqual(1, report.ActualUsedReviewFieldCount);
                Assert.IsFalse(review.Contains("IP_Address"));
                Assert.IsFalse(review.Contains("MAC_Address"));
                Assert.IsFalse(review.Contains("Callback_URL"));
                StringAssert.Contains(review, "ABC_Address");
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        /// <summary>XMZADD 20260903 创建包含业务、技术和排除对象的最小报告快照。</summary>
        private static SnapshotData CreateSnapshot()
        {
            var relation = new RelationMetadata
            {
                ParentTableName = "Company",
                ParentFieldName = "Company_ID",
                ChildTableName = "Item",
                ChildFieldName = "Company_ID",
                RelationType = new MetadataValue { Value = "代码关联：多对一", Status = ConfidenceStatus.CodeEvidence }
            };
            return new SnapshotData
            {
                Tables = new List<TableMetadata>
                {
                    new TableMetadata
                    {
                        ObjectName = "Item",
                        Category = DictionaryTableCategory.Business,
                        ChineseName = new MetadataValue { Value = "物料主数据", Status = ConfidenceStatus.KnowledgeBaseEvidence },
                        EntityName = new MetadataValue { Value = "t_Item", Status = ConfidenceStatus.CodeEvidence },
                        Relations = new List<RelationMetadata> { relation },
                        Fields = new List<FieldMetadata>
                        {
                            new FieldMetadata
                            {
                                FieldName = "Item_Make_Buy",
                                ChineseName = new MetadataValue { Value = "自制外购标识", Status = ConfidenceStatus.KnowledgeBaseEvidence },
                                EnumItems = new List<EnumItemMetadata>
                                {
                                    new EnumItemMetadata { Value = "M", ChineseName = new MetadataValue { Value = "自制件" } }
                                }
                            }
                        }
                    },
                    new TableMetadata
                    {
                        ObjectName = "System_Log",
                        Category = DictionaryTableCategory.Technical,
                        Fields = new List<FieldMetadata> { new FieldMetadata { FieldName = "ID" } }
                    },
                    new TableMetadata
                    {
                        ObjectName = "tmp_copy",
                        Category = DictionaryTableCategory.Excluded,
                        Fields = new List<FieldMetadata> { new FieldMetadata { FieldName = "ID" } }
                    }
                }
            };
        }

        /// <summary>XMZADD 20260907 创建覆盖名称细化、已准确命名、实际待复核和非实际归档字段的 V4 快照。</summary>
        private static SnapshotData CreateV4Snapshot()
        {
            return new SnapshotData
            {
                Tables = new List<TableMetadata>
                {
                    new TableMetadata
                    {
                        ObjectName = "DA_Acceptance",
                        Category = DictionaryTableCategory.Business,
                        ModuleName = new MetadataValue { Value = "采购管理", Status = ConfidenceStatus.CodeEvidence },
                        Fields = new List<FieldMetadata>
                        {
                            new FieldMetadata
                            {
                                FieldName = "Owner_Company_ID",
                                ChineseName = new MetadataValue
                                {
                                    Value = "货主公司ID",
                                    OriginalAutomaticValue = "公司ID",
                                    Status = ConfidenceStatus.CodeEvidence,
                                    ConfidenceScore = 92,
                                    Evidence = new List<EvidenceItem>
                                    {
                                        new EvidenceItem { RuleName = "EntityProperty", SourcePath = "ERP/表-类定义/code_DA.vb", SourceLine = 12, Explanation = "实体字段结构" },
                                        new EvidenceItem { RuleName = "SqlFieldRelation", SourcePath = "ERP/采购/f_acceptance.vb", SourceLine = 41, Explanation = "关联到公司主键" },
                                        new EvidenceItem { RuleName = "GridColumnCaption", SourcePath = "ERP/采购/f_acceptance.vb", SourceLine = 55, Explanation = "界面列标题确认货主公司" }
                                    }
                                }
                            },
                            new FieldMetadata
                            {
                                FieldName = "op_createtime",
                                ChineseName = new MetadataValue
                                {
                                    Value = "操作记录创建时间",
                                    OriginalAutomaticValue = "操作记录创建时间",
                                    Status = ConfidenceStatus.CodeEvidence,
                                    ConfidenceScore = 98,
                                    Evidence = new List<EvidenceItem>
                                    {
                                        new EvidenceItem { RuleName = "ExactBusinessPhrase", SourcePath = "ERP/表-类定义/code_DA.vb", SourceLine = 80 }
                                    }
                                }
                            },
                            new FieldMetadata
                            {
                                FieldName = "IBS",
                                ChineseName = new MetadataValue
                                {
                                    Value = "暂无可靠中文名称",
                                    Status = ConfidenceStatus.PendingConfirmation,
                                    ConfidenceScore = 60
                                }
                            }
                        }
                    },
                    new TableMetadata
                    {
                        ObjectName = "DA_Acceptance_Archive_2020",
                        Category = DictionaryTableCategory.Business,
                        ModuleName = new MetadataValue { Value = "采购管理" },
                        Fields = new List<FieldMetadata>
                        {
                            new FieldMetadata
                            {
                                FieldName = "Archive_IBS",
                                ChineseName = new MetadataValue
                                {
                                    Value = "暂无可靠中文名称",
                                    Status = ConfidenceStatus.GuessedConflict
                                }
                            }
                        }
                    }
                }
            };
        }

        /// <summary>XMZADD 20260907 创建含重复 SQL 证据、强界面证据和不计入实际使用的结构证据集合。</summary>
        private static IList<SourceEvidence> CreateV4SourceEvidence()
        {
            return new List<SourceEvidence>
            {
                CreateUsageEvidence("DA_Acceptance", "Owner_Company_ID", "SqlFieldUsage", "ERP/采购/f_acceptance.vb", 40),
                CreateUsageEvidence("DA_Acceptance", "Owner_Company_ID", "SqlFieldUsage", "ERP/采购/f_acceptance.vb", 40),
                CreateUsageEvidence("DA_Acceptance", "Owner_Company_ID", "SqlFieldRelation", "ERP/采购/f_acceptance.vb", 41),
                CreateUsageEvidence("DA_Acceptance", "Owner_Company_ID", "GridColumnCaption", "ERP/采购/f_acceptance.vb", 55),
                CreateUsageEvidence("DA_Acceptance", "Owner_Company_ID", "DynamicTableFieldUsage", "ERP/采购/f_acceptance.vb", 60),
                CreateUsageEvidence("DA_Acceptance", "op_createtime", "EntityFieldAssignment", "ERP/采购/f_acceptance.vb", 72),
                CreateUsageEvidence("DA_Acceptance", "IBS", "SqlFieldUsage", "ERP/采购/f_acceptance.vb", 90),
                CreateUsageEvidence("DA_Acceptance", "IBS", "SqlFieldUsage", "ERP/采购/f_acceptance.vb", 91),
                CreateUsageEvidence("DA_Acceptance_Archive_2020", "Archive_IBS", "EntityProperty", "ERP/表-类定义/archive.vb", 10),
                CreateUsageEvidence("DA_Acceptance_Archive_2020", "Archive_IBS", "DataColumnCaption", "ERP/采购/archive.vb", 20)
            };
        }

        /// <summary>XMZADD 20260907 创建单个低置信业务字段表用于未知缩写频率测试。</summary>
        private static TableMetadata CreatePendingTable(string objectName, string moduleName, string fieldName)
        {
            return new TableMetadata
            {
                ObjectName = objectName,
                Category = DictionaryTableCategory.Business,
                ModuleName = new MetadataValue { Value = moduleName },
                Fields = new List<FieldMetadata>
                {
                    new FieldMetadata
                    {
                        FieldName = fieldName,
                        ChineseName = new MetadataValue
                        {
                            Value = "暂无可靠中文名称",
                            Status = ConfidenceStatus.PendingConfirmation
                        }
                    }
                }
            };
        }

        /// <summary>XMZADD 20260907 创建带可靠代码证据状态的中文字段用于允许英文标识测试。</summary>
        private static FieldMetadata CreateNamedField(string fieldName, string chineseName)
        {
            return new FieldMetadata
            {
                FieldName = fieldName,
                ChineseName = new MetadataValue
                {
                    Value = chineseName,
                    Status = ConfidenceStatus.CodeEvidence,
                    ConfidenceScore = 90
                }
            };
        }

        /// <summary>XMZADD 20260907 创建可定位到普通业务源码的字段用途证据。</summary>
        private static SourceEvidence CreateUsageEvidence(string objectName, string fieldName, string ruleName,
            string sourcePath, int sourceLine)
        {
            return new SourceEvidence
            {
                ObjectName = objectName,
                FieldName = fieldName,
                Evidence = new EvidenceItem
                {
                    RuleName = ruleName,
                    SourcePath = sourcePath,
                    SourceLine = sourceLine,
                    Explanation = "测试业务用途证据"
                }
            };
        }
    }
}
