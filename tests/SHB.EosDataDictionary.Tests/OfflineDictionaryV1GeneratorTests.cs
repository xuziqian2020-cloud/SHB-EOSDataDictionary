using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHB.EosDataDictionary.Models;
using SHB.EosDataDictionary.Services;

namespace SHB.EosDataDictionary.Tests
{
    [TestClass]
    public sealed class OfflineDictionaryV1GeneratorTests
    {
        /// <summary>XMZADD 20260903 验证离线生成器只修改数据库副本，并可重新加载富化后的指定作用域。</summary>
        [TestMethod]
        public void Generate_TinySnapshot_PreservesSourceAndEnrichesOutputCopy()
        {
            string root = Path.Combine(Path.GetTempPath(), "shb-offline-v1-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string sourceDatabase = Path.Combine(root, "dictionary.db");
            string outputDatabase = Path.Combine(root, "dictionary.enriched-v1.db");
            string sourceRoot = Path.Combine(root, "source");
            string knowledgeRoot = Path.Combine(root, "knowledge");
            string reportRoot = Path.Combine(root, "report");
            Directory.CreateDirectory(sourceRoot);
            CreateKnowledgeFixture(knowledgeRoot);
            File.WriteAllText(Path.Combine(sourceRoot, "t_Item.vb"),
                "' 表-类生成代码\r\n<Serializable> Partial Public Class t_Item\r\n" +
                "Private mTable As String = \"Item\"\r\nPublic Property f_Item_ID() As Int64\r\nEnd Class\r\n",
                Encoding.UTF8);
            new SnapshotStore(sourceDatabase).ReplaceScope("test-scope", new SnapshotData
            {
                RefreshedAt = new DateTime(2026, 9, 3, 0, 0, 0, DateTimeKind.Utc),
                Tables = new List<TableMetadata>
                {
                    new TableMetadata
                    {
                        ScopeKey = "test-scope",
                        SchemaName = "dbo",
                        ObjectName = "Item",
                        ObjectType = "TABLE",
                        ChineseName = new MetadataValue { Value = "项目表", Status = ConfidenceStatus.Guessed },
                        Fields = new List<FieldMetadata>
                        {
                            new FieldMetadata { FieldName = "Item_ID", IsPrimaryKey = true },
                            new FieldMetadata
                            {
                                FieldName = "KisNumber",
                                ChineseName = new MetadataValue { Value = "编号", Status = ConfidenceStatus.Guessed }
                            }
                        }
                    },
                    new TableMetadata
                    {
                        ScopeKey = "test-scope",
                        SchemaName = "dbo",
                        ObjectName = "Account_Pallet_FA_Not_IO",
                        ObjectType = "TABLE",
                        ChineseName = new MetadataValue { Value = "财务非托盘出入库表", Status = ConfidenceStatus.Guessed },
                        ModuleName = new MetadataValue { Value = "财务", Status = ConfidenceStatus.Guessed },
                        Fields = new List<FieldMetadata>()
                    },
                    new TableMetadata
                    {
                        ScopeKey = "test-scope",
                        SchemaName = "dbo",
                        ObjectName = "DA_Account",
                        ObjectType = "TABLE",
                        ChineseName = new MetadataValue { Value = "DA财务表", Status = ConfidenceStatus.AiGuessed },
                        ModuleName = new MetadataValue { Value = "其他", Status = ConfidenceStatus.Guessed },
                        Fields = new List<FieldMetadata>()
                    }
                }
            });
            string sourceHashBefore = ComputeSha256(sourceDatabase);

            try
            {
                OfflineDictionaryV1GenerationResult result = new OfflineDictionaryV1Generator().Generate(
                    new OfflineDictionaryV1GenerationOptions
                    {
                        SourceDatabasePath = sourceDatabase,
                        OutputDatabasePath = outputDatabase,
                        ScopeKey = "test-scope",
                        SourceRoot = sourceRoot,
                        KnowledgeBaseRoot = knowledgeRoot,
                        ReportRoot = reportRoot
                    });

                Assert.AreEqual(sourceHashBefore, ComputeSha256(sourceDatabase));
                Assert.IsTrue(File.Exists(outputDatabase));
                SnapshotData output;
                Assert.IsTrue(new SnapshotStore(outputDatabase).TryLoadLatest("test-scope", out output));
                TableMetadata item = FindTable(output, "Item");
                Assert.AreEqual("物料主数据", item.ChineseName.Value);
                Assert.AreEqual("t_Item", item.EntityName.Value);
                Assert.AreEqual(string.Empty, FindField(item, "KisNumber").ChineseName.Value);
                Assert.AreEqual("金蝶编码",
                    FindField(item, "KisNumber").SuggestedChineseName.Value);
                Assert.AreEqual("非托盘出入库流水账", FindTable(output, "Account_Pallet_FA_Not_IO").ChineseName.Value);
                Assert.AreEqual("仓储", FindTable(output, "Account_Pallet_FA_Not_IO").ModuleName.Value);
                Assert.AreEqual("财务流水账", FindTable(output, "DA_Account").ChineseName.Value);
                Assert.AreEqual("财务", FindTable(output, "DA_Account").ModuleName.Value);
                Assert.AreEqual(1, result.Report.EntityTableCount);
                Assert.IsTrue(File.Exists(Path.Combine(reportRoot, "推断运行报告.md")));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        /// <summary>XMZADD 20260903 验证离线富化后同步游标记录新的本地载荷哈希，避免启动时被远端同修订快照覆盖。</summary>
        [TestMethod]
        public void Generate_SynchronizedScope_UpdatesLocalPayloadHashWithoutChangingRemoteCursor()
        {
            string root = Path.Combine(Path.GetTempPath(), "shb-offline-sync-v1-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string sourceDatabase = Path.Combine(root, "dictionary.db");
            string outputDatabase = Path.Combine(root, "dictionary.enriched-v1.db");
            string sourceRoot = Path.Combine(root, "source");
            string knowledgeRoot = Path.Combine(root, "knowledge");
            string reportRoot = Path.Combine(root, "report");
            Directory.CreateDirectory(sourceRoot);
            CreateKnowledgeFixture(knowledgeRoot);
            File.WriteAllText(Path.Combine(sourceRoot, "t_Item.vb"),
                "' 表-类生成代码\r\n<Serializable> Partial Public Class t_Item\r\n" +
                "Private mTable As String = \"Item\"\r\nPublic Property f_Item_ID() As Int64\r\nEnd Class\r\n",
                Encoding.UTF8);
            var sourceSnapshotStore = new SnapshotStore(sourceDatabase);
            sourceSnapshotStore.ReplaceScope("github-shared-dictionary", new SnapshotData
            {
                Revision = 6,
                RefreshedAt = new DateTime(2026, 9, 3, 0, 0, 0, DateTimeKind.Utc),
                Tables = new List<TableMetadata>
                {
                    new TableMetadata
                    {
                        ScopeKey = "github-shared-dictionary",
                        SchemaName = "dbo",
                        ObjectName = "Item",
                        ObjectType = "TABLE",
                        ChineseName = new MetadataValue { Value = "项目表", Status = ConfidenceStatus.Guessed },
                        Fields = new List<FieldMetadata>
                        {
                            new FieldMetadata { FieldName = "Item_ID", IsPrimaryKey = true }
                        }
                    }
                }
            });
            int sourceTableCount;
            string originalPayloadHash;
            Assert.IsTrue(sourceSnapshotStore.TryGetScopeSummary(
                "github-shared-dictionary",
                out sourceTableCount,
                out originalPayloadHash));
            DateTime originalSyncTime = new DateTime(2026, 9, 3, 1, 2, 3, DateTimeKind.Utc);
            new LocalDictionaryStore(sourceDatabase).SaveSyncState(new DictionarySyncState
            {
                StateKey = "github-shared-dictionary",
                Revision = 6,
                ManifestHash = "remote-manifest-hash",
                LocalPayloadHash = originalPayloadHash,
                LastSyncAtUtc = originalSyncTime,
                LastSuccessfulOperationId = "operation-1"
            });

            try
            {
                new OfflineDictionaryV1Generator().Generate(new OfflineDictionaryV1GenerationOptions
                {
                    SourceDatabasePath = sourceDatabase,
                    OutputDatabasePath = outputDatabase,
                    ScopeKey = "github-shared-dictionary",
                    SourceRoot = sourceRoot,
                    KnowledgeBaseRoot = knowledgeRoot,
                    ReportRoot = reportRoot
                });

                var outputSnapshotStore = new SnapshotStore(outputDatabase);
                int outputTableCount;
                string enrichedPayloadHash;
                Assert.IsTrue(outputSnapshotStore.TryGetScopeSummary(
                    "github-shared-dictionary",
                    out outputTableCount,
                    out enrichedPayloadHash));
                DictionarySyncState outputState = new LocalDictionaryStore(outputDatabase)
                    .LoadSyncState("github-shared-dictionary");

                Assert.AreNotEqual(originalPayloadHash, enrichedPayloadHash);
                Assert.AreEqual(enrichedPayloadHash, outputState.LocalPayloadHash);
                Assert.AreEqual(6L, outputState.Revision);
                Assert.AreEqual("remote-manifest-hash", outputState.ManifestHash);
                Assert.AreEqual(originalSyncTime, outputState.LastSyncAtUtc);
                Assert.AreEqual("operation-1", outputState.LastSuccessfulOperationId);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        /// <summary>XMZADD 20260904 验证离线管线用动态建表业务代码纠正仓储配置表及字段的宽泛名称。</summary>
        [TestMethod]
        public void Generate_StoragePartDefinition_AppliesBusinessCodeContext()
        {
            string root = Path.Combine(Path.GetTempPath(), "shb-offline-v3-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string sourceDatabase = Path.Combine(root, "dictionary.db");
            string outputDatabase = Path.Combine(root, "dictionary.enriched-v3.db");
            string sourceRoot = Path.Combine(root, "source");
            string knowledgeRoot = Path.Combine(root, "knowledge");
            string reportRoot = Path.Combine(root, "report");
            Directory.CreateDirectory(sourceRoot);
            CreateKnowledgeFixture(knowledgeRoot);
            File.WriteAllText(Path.Combine(sourceRoot, "code_Storage_Partial.vb"),
                "Partial Class t_Account_Storage_Part_Definition\r\n" +
                "Public Shared Sub Build_Bu_Storage_Part_Area_Data()\r\n" +
                "S = \"CREATE TABLE [dbo].[\" & BST.f_Table_Storage_Event & \"](\"\r\n" +
                "S = \"CREATE TABLE [dbo].[\" & BST.f_Table_Storage_Event_Item & \"](\"\r\n" +
                "S = \"Insert Into \" & BST.f_Table_Storage_Account & \" (Item_ID)\"\r\n" +
                "S = \"Insert Into \" & BST.f_Table_Storage_Inv & \" (Item_ID)\"\r\n" +
                "End Sub\r\n" +
                "End Class\r\n", Encoding.UTF8);
            IList<SourceEvidence> scannedEvidence = new EosSourceAnalyzer().Analyze(sourceRoot);
            bool hasEntityConvention = false;
            for (int evidenceIndex = 0; evidenceIndex < scannedEvidence.Count; evidenceIndex++)
            {
                SourceEvidence sourceEvidence = scannedEvidence[evidenceIndex];
                if (sourceEvidence != null && sourceEvidence.Evidence != null &&
                    sourceEvidence.ObjectName == "Account_Storage_Part_Definition" &&
                    sourceEvidence.Evidence.RuleName == "EntityClassConvention")
                {
                    hasEntityConvention = true;
                    break;
                }
            }
            Assert.IsTrue(hasEntityConvention, "测试源码必须先产生实体约定证据，实际证据数：" + scannedEvidence.Count);
            var table = new TableMetadata
            {
                ScopeKey = "test-scope",
                SchemaName = "dbo",
                ObjectName = "Account_Storage_Part_Definition",
                ObjectType = "TABLE",
                ChineseName = new MetadataValue { Value = "流水账仓储零件定义表", Status = ConfidenceStatus.Guessed },
                ModuleName = new MetadataValue { Value = "其他", Status = ConfidenceStatus.Guessed },
                Fields = new List<FieldMetadata>()
            };
            string[] fieldNames =
            {
                "ASPD_ID", "Bu_ID", "Table_Storage_Account", "Table_Storage_Event",
                "Table_Storage_Event_Item", "Table_Storage_Inv"
            };
            for (int index = 0; index < fieldNames.Length; index++)
            {
                table.Fields.Add(new FieldMetadata
                {
                    FieldName = fieldNames[index],
                    ChineseName = new MetadataValue { Value = fieldNames[index], Status = ConfidenceStatus.Guessed }
                });
            }
            new SnapshotStore(sourceDatabase).ReplaceScope("test-scope", new SnapshotData
            {
                RefreshedAt = new DateTime(2026, 9, 4, 0, 0, 0, DateTimeKind.Utc),
                Tables = new List<TableMetadata> { table }
            });
            string sourceHash = ComputeSha256(sourceDatabase);

            try
            {
                new OfflineDictionaryV1Generator().Generate(new OfflineDictionaryV1GenerationOptions
                {
                    SourceDatabasePath = sourceDatabase,
                    OutputDatabasePath = outputDatabase,
                    ScopeKey = "test-scope",
                    SourceRoot = sourceRoot,
                    KnowledgeBaseRoot = knowledgeRoot,
                    ReportRoot = reportRoot
                });

                SnapshotData output;
                Assert.IsTrue(new SnapshotStore(outputDatabase).TryLoadLatest("test-scope", out output));
                TableMetadata result = FindTable(output, "Account_Storage_Part_Definition");
                Assert.AreEqual("仓储区分表配置", result.ChineseName.Value);
                Assert.AreEqual("仓储与库存", result.ModuleName.Value);
                Assert.AreEqual("仓储区分表配置ID", FindField(result, "ASPD_ID").ChineseName.Value);
                Assert.AreEqual("仓储区出入库流水表名", FindField(result, "Table_Storage_Account").ChineseName.Value);
                Assert.AreEqual("仓储区事件明细表名", FindField(result, "Table_Storage_Event_Item").ChineseName.Value);
                Assert.AreEqual(sourceHash, ComputeSha256(sourceDatabase));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        /// <summary>XMZADD 20260907 验证 V4 离线管线从真实业务代码证据细化承兑字段并保留全部快照安全不变量。</summary>
        [TestMethod]
        public void Generate_DaAcceptance_UsesBusinessCodeEvidenceAndWritesV4AuditArtifacts()
        {
            string root = Path.Combine(Path.GetTempPath(), "shb-offline-v4-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string sourceDatabase = Path.Combine(root, "dictionary.db");
            string outputDatabase = Path.Combine(root, "dictionary.enriched-v4.db");
            string sourceRoot = Path.Combine(root, "source");
            string knowledgeRoot = Path.Combine(root, "knowledge");
            string reportRoot = Path.Combine(root, "report");
            string entityDirectory = Path.Combine(sourceRoot, "ERP", "表-类定义");
            string businessDirectory = Path.Combine(sourceRoot, "ERP", "DA");
            Directory.CreateDirectory(entityDirectory);
            Directory.CreateDirectory(businessDirectory);
            CreateKnowledgeFixture(knowledgeRoot);
            File.WriteAllText(Path.Combine(entityDirectory, "code_DA.vb"),
                "' 表-类生成代码\r\n" +
                "<Serializable> Partial Public Class t_DA_Acceptance\r\n" +
                "Private mTable As String = \"DA_Acceptance\"\r\n" +
                "Public Property f_Owner_Company_ID() As Int64\r\n" +
                "Public Property f_op_createtime() As DateTime\r\n" +
                "End Class\r\n", Encoding.UTF8);
            File.WriteAllText(Path.Combine(businessDirectory, "frmAcceptance.vb"),
                "Dim sql As String = \"SELECT DAA.Owner_Company_ID FROM DA_Acceptance DAA \" & _\r\n" +
                "                  \"INNER JOIN Company C ON DAA.Owner_Company_ID=C.Company_ID\"\r\n" +
                "fg.Cols(\"Owner_Company_ID\").Caption = \"货主公司\"\r\n" +
                "Dim acceptance As New t_DA_Acceptance\r\n" +
                "acceptance.f_op_createtime = Now\r\n", Encoding.UTF8);

            IList<SourceEvidence> fixtureEvidence = new EosSourceAnalyzer().Analyze(sourceRoot);
            Assert.IsTrue(ContainsEvidence(fixtureEvidence, "DA_Acceptance", "op_createtime", "EntityProperty"),
                "端到端夹具必须先证明生成实体中存在 op_createtime 字段。");
            Assert.IsTrue(ContainsEvidence(fixtureEvidence, "DA_Acceptance", "op_createtime", "EntityFieldAssignment"),
                "端到端夹具必须先证明业务代码实际写入 op_createtime 字段。");

            RelationMetadata physicalRelation = new RelationMetadata
            {
                ScopeKey = "test-scope",
                ForeignKeyName = "FK_DA_Acceptance_Company",
                ParentSchemaName = "dbo",
                ParentTableName = "Company",
                ParentFieldName = "Company_ID",
                ChildSchemaName = "dbo",
                ChildTableName = "DA_Acceptance",
                ChildFieldName = "Owner_Company_ID",
                RelationType = new MetadataValue
                {
                    Value = "多对一",
                    Status = ConfidenceStatus.DatabaseEvidence,
                    ConfidenceScore = 100
                }
            };
            var inputSnapshot = new SnapshotData
            {
                RefreshedAt = new DateTime(2026, 9, 7, 0, 0, 0, DateTimeKind.Utc),
                Tables = new List<TableMetadata>
                {
                    new TableMetadata
                    {
                        ScopeKey = "test-scope",
                        SchemaName = "dbo",
                        ObjectName = "DA_Acceptance",
                        ObjectType = "TABLE",
                        ChineseName = new MetadataValue { Value = "承兑表", Status = ConfidenceStatus.Guessed },
                        ModuleName = new MetadataValue { Value = "财务票据", Status = ConfidenceStatus.Guessed },
                        Fields = new List<FieldMetadata>
                        {
                            new FieldMetadata
                            {
                                FieldName = "Owner_Company_ID",
                                OwnerTableName = "DA_Acceptance",
                                DataType = "bigint",
                                ChineseName = new MetadataValue
                                {
                                    Value = "公司ID",
                                    Status = ConfidenceStatus.DatabaseEvidence,
                                    ConfidenceScore = 81
                                }
                            },
                            new FieldMetadata
                            {
                                FieldName = "op_createtime",
                                OwnerTableName = "DA_Acceptance",
                                DataType = "datetime",
                                ChineseName = new MetadataValue
                                {
                                    Value = "op_createtime",
                                    Status = ConfidenceStatus.PendingConfirmation,
                                    ConfidenceScore = 10
                                }
                            }
                        },
                        Relations = new List<RelationMetadata> { physicalRelation }
                    },
                    new TableMetadata
                    {
                        ScopeKey = "test-scope",
                        SchemaName = "dbo",
                        ObjectName = "Company",
                        ObjectType = "TABLE",
                        ChineseName = new MetadataValue { Value = "公司", Status = ConfidenceStatus.DatabaseEvidence },
                        Fields = new List<FieldMetadata>
                        {
                            new FieldMetadata
                            {
                                FieldName = "Company_ID",
                                OwnerTableName = "Company",
                                DataType = "bigint",
                                IsPrimaryKey = true,
                                ChineseName = new MetadataValue { Value = "公司ID", Status = ConfidenceStatus.DatabaseEvidence }
                            }
                        }
                    }
                }
            };
            new SnapshotStore(sourceDatabase).ReplaceScope("test-scope", inputSnapshot);
            SnapshotData persistedInput;
            Assert.IsTrue(new SnapshotStore(sourceDatabase).TryLoadLatest("test-scope", out persistedInput));
            string sourceHashBefore = ComputeSha256(sourceDatabase);
            int inputTableCount = CountTables(persistedInput);
            int inputFieldCount = CountFields(persistedInput);
            int inputRelationCount = CountRelations(persistedInput);
            Assert.IsTrue(inputRelationCount >= 1, "端到端夹具必须在源 SQLite 中实际保存物理关系。");

            try
            {
                OfflineDictionaryV1GenerationResult result = new OfflineDictionaryV1Generator().Generate(
                    new OfflineDictionaryV1GenerationOptions
                    {
                        SourceDatabasePath = sourceDatabase,
                        OutputDatabasePath = outputDatabase,
                        ScopeKey = "test-scope",
                        SourceRoot = sourceRoot,
                        KnowledgeBaseRoot = knowledgeRoot,
                        ReportRoot = reportRoot
                    });

                Assert.AreNotEqual(Path.GetFullPath(sourceDatabase), Path.GetFullPath(outputDatabase));
                Assert.AreEqual(sourceHashBefore, ComputeSha256(sourceDatabase));
                Assert.AreEqual("ok", ReadIntegrityCheck(outputDatabase));

                SnapshotData unchangedInput;
                Assert.IsTrue(new SnapshotStore(sourceDatabase).TryLoadLatest("test-scope", out unchangedInput));
                Assert.AreEqual("公司ID", FindField(FindTable(unchangedInput, "DA_Acceptance"),
                    "Owner_Company_ID").ChineseName.Value);

                SnapshotData output;
                Assert.IsTrue(new SnapshotStore(outputDatabase).TryLoadLatest("test-scope", out output));
                Assert.AreEqual(inputTableCount, CountTables(output));
                Assert.AreEqual(inputFieldCount, CountFields(output));
                Assert.AreEqual(inputRelationCount, CountRelations(output),
                    "端到端夹具不生成额外命名关系时，输出关系数量必须与源 SQLite 一致。");

                TableMetadata acceptance = FindTable(output, "DA_Acceptance");
                FieldMetadata ownerCompany = FindField(acceptance, "Owner_Company_ID");
                Assert.AreEqual("货主公司ID", ownerCompany.ChineseName.Value);
                Assert.AreEqual(ConfidenceStatus.CodeEvidence, ownerCompany.ChineseName.Status);
                Assert.IsTrue(ownerCompany.ChineseName.ConfidenceScore >= 90);
                Assert.AreEqual("公司ID", ownerCompany.ChineseName.OriginalAutomaticValue);
                Assert.IsTrue(CountValidEvidenceLocations(ownerCompany.ChineseName) >= 2);

                FieldMetadata operationCreateTime = FindField(acceptance, "op_createtime");
                Assert.AreEqual("操作记录创建时间", operationCreateTime.ChineseName.Value);
                Assert.IsTrue(operationCreateTime.ChineseName.ConfidenceScore >= 85,
                    "op_createtime 端到端置信度实际为：" +
                    operationCreateTime.ChineseName.ConfidenceScore.ToString());
                Assert.IsTrue(operationCreateTime.ChineseName.Status == ConfidenceStatus.CodeEvidence ||
                              operationCreateTime.ChineseName.Status == ConfidenceStatus.KnowledgeBaseEvidence);

                string changeAuditPath = Path.Combine(reportRoot, "中文名称变更审计.csv");
                string actualReviewPath = Path.Combine(reportRoot, "实际使用字段待复核.csv");
                string unknownFrequencyPath = Path.Combine(reportRoot, "未知缩写频率.csv");
                Assert.IsTrue(File.Exists(changeAuditPath));
                Assert.IsTrue(File.Exists(actualReviewPath));
                Assert.IsTrue(File.Exists(unknownFrequencyPath));
                StringAssert.Contains(File.ReadAllText(changeAuditPath),
                    "\"DA_Acceptance\",\"Owner_Company_ID\",\"公司ID\",\"货主公司ID\"");
                StringAssert.StartsWith(File.ReadAllText(actualReviewPath),
                    "ObjectName,FieldName,CurrentChineseName,UnknownTokens,UsageKinds,SourcePath,SourceLine,Reason");
                StringAssert.StartsWith(File.ReadAllText(unknownFrequencyPath),
                    "Token,Count,ExampleObject,ModuleName,SourcePath,SourceLine");
                Assert.AreEqual(3, result.Report.ActualUsedFieldCount);
                Assert.AreEqual(3, result.Report.ActualUsedNamedFieldCount);
                Assert.IsTrue(result.Report.V4RefinedNameCount >= 1);
                Assert.AreEqual(0, result.Report.ActualUsedReviewFieldCount);
                Assert.AreEqual(0, result.Report.AmbiguousConflictCount);
            }
            finally
            {
                SQLiteConnection.ClearAllPools();
                Directory.Delete(root, true);
            }
        }

        /// <summary>XMZADD 20260903 创建离线生成测试所需的最小 EOS 知识库。</summary>
        private static void CreateKnowledgeFixture(string root)
        {
            string tableDirectory = Path.Combine(root, "01_数据库表结构");
            string fieldDirectory = Path.Combine(root, "02_字段字典");
            Directory.CreateDirectory(tableDirectory);
            Directory.CreateDirectory(fieldDirectory);
            File.WriteAllText(Path.Combine(tableDirectory, "_00_表索引.md"),
                "## 物料/BOM相关\r\n\r\n| 表名 | 中文含义 | 备注 |\r\n|---|---|---|\r\n" +
                "| Item | 物料主数据 | 物料信息 |\r\n" +
                "| Account_Storage_Part_Definition | 仓储区定义 | 仓储区动态表配置 |\r\n",
                Encoding.UTF8);
            File.WriteAllText(Path.Combine(fieldDirectory, "_00_核心字段字典.md"),
                "## 物料字段\r\n\r\n| 字段名 | 中文含义 | 出现表 | 数据类型 |\r\n|---|---|---|---|\r\n| Item_ID | 物料ID | Item | bigint |\r\n",
                Encoding.UTF8);
        }

        /// <summary>XMZADD 20260903 按物理表名定位离线生成结果中的业务表。</summary>
        private static TableMetadata FindTable(SnapshotData snapshot, string objectName)
        {
            for (int tableIndex = 0; tableIndex < snapshot.Tables.Count; tableIndex++)
            {
                TableMetadata table = snapshot.Tables[tableIndex];
                if (table != null && string.Equals(table.ObjectName, objectName, StringComparison.OrdinalIgnoreCase))
                {
                    return table;
                }
            }
            Assert.Fail("未找到表：" + objectName);
            return null;
        }

        /// <summary>XMZADD 20260903 按字段名定位离线生成结果中的业务字段。</summary>
        private static FieldMetadata FindField(TableMetadata table, string fieldName)
        {
            for (int fieldIndex = 0; fieldIndex < table.Fields.Count; fieldIndex++)
            {
                FieldMetadata field = table.Fields[fieldIndex];
                if (field != null && string.Equals(field.FieldName, fieldName, StringComparison.OrdinalIgnoreCase))
                {
                    return field;
                }
            }
            Assert.Fail("未找到字段：" + fieldName);
            return null;
        }

        /// <summary>XMZADD 20260903 计算源数据库哈希以证明离线生成期间没有原地写入。</summary>
        private static string ComputeSha256(string path)
        {
            using (var stream = File.OpenRead(path))
            using (SHA256 algorithm = SHA256.Create())
            {
                return BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", string.Empty);
            }
        }

        /// <summary>XMZADD 20260907 统计快照中的有效表数以核验离线富化不改变物理对象集合。</summary>
        private static int CountTables(SnapshotData snapshot)
        {
            int count = 0;
            for (int index = 0; snapshot != null && snapshot.Tables != null && index < snapshot.Tables.Count; index++)
            {
                if (snapshot.Tables[index] != null) count++;
            }
            return count;
        }

        /// <summary>XMZADD 20260907 统计快照中的有效字段数以核验离线富化不丢失数据库结构。</summary>
        private static int CountFields(SnapshotData snapshot)
        {
            int count = 0;
            for (int tableIndex = 0; snapshot != null && snapshot.Tables != null && tableIndex < snapshot.Tables.Count; tableIndex++)
            {
                TableMetadata table = snapshot.Tables[tableIndex];
                for (int fieldIndex = 0; table != null && table.Fields != null && fieldIndex < table.Fields.Count; fieldIndex++)
                {
                    if (table.Fields[fieldIndex] != null) count++;
                }
            }
            return count;
        }

        /// <summary>XMZADD 20260907 统计快照中的关系记录数以证明已有数据库关联不会被离线推断删除。</summary>
        private static int CountRelations(SnapshotData snapshot)
        {
            int count = 0;
            for (int tableIndex = 0; snapshot != null && snapshot.Tables != null && tableIndex < snapshot.Tables.Count; tableIndex++)
            {
                TableMetadata table = snapshot.Tables[tableIndex];
                for (int relationIndex = 0; table != null && table.Relations != null && relationIndex < table.Relations.Count; relationIndex++)
                {
                    if (table.Relations[relationIndex] != null) count++;
                }
            }
            return count;
        }

        /// <summary>XMZADD 20260907 统计中文名称中具备相对路径和真实行号的独立源码证据位置。</summary>
        private static int CountValidEvidenceLocations(MetadataValue value)
        {
            var locations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; value != null && value.Evidence != null && index < value.Evidence.Count; index++)
            {
                EvidenceItem evidence = value.Evidence[index];
                if (evidence != null && !string.IsNullOrWhiteSpace(evidence.SourcePath) &&
                    !Path.IsPathRooted(evidence.SourcePath) && evidence.SourceLine > 0)
                {
                    locations.Add(evidence.SourcePath + ":" + evidence.SourceLine.ToString());
                }
            }
            return locations.Count;
        }

        /// <summary>XMZADD 20260907 检查端到端源码夹具是否产生指定表字段和规则证据。</summary>
        private static bool ContainsEvidence(IList<SourceEvidence> evidence, string objectName,
            string fieldName, string ruleName)
        {
            for (int index = 0; evidence != null && index < evidence.Count; index++)
            {
                SourceEvidence item = evidence[index];
                if (item != null && item.Evidence != null &&
                    string.Equals(item.ObjectName, objectName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(item.FieldName, fieldName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(item.Evidence.RuleName, ruleName, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>XMZADD 20260907 执行 SQLite 完整性检查，确保端到端生成结果可安全安装。</summary>
        private static string ReadIntegrityCheck(string path)
        {
            using (var connection = new SQLiteConnection("Data Source=" + path + ";Version=3;"))
            {
                connection.Open();
                using (SQLiteCommand command = connection.CreateCommand())
                {
                    command.CommandText = "PRAGMA integrity_check;";
                    return Convert.ToString(command.ExecuteScalar());
                }
            }
        }
    }
}
