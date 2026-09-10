using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHB.EosDataDictionary.Models;
using SHB.EosDataDictionary.Services;
using SHB.EosDataDictionary.ViewModels;

namespace SHB.EosDataDictionary.Tests
{
    /// <summary>XMZADD 20260831 验证主界面启动时优先恢复真实本地快照而非加载演示数据。</summary>
    [TestClass]
    public sealed class MainViewModelTests
    {
        /// <summary>XMZADD 20260831 验证构造函数不阻塞窗口创建，显式异步恢复后仍回到最近完整快照。</summary>
        [TestMethod]
        public async Task RestoreMostRecentSnapshotAsync_LoadsSnapshotWithoutConstructorBlocking()
        {
            string path = Path.Combine(Path.GetTempPath(), "shb-view-model-" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                new SnapshotStore(path).ReplaceScope(new DictionaryRepositoryOptions().ScopeKey, new SnapshotData
                {
                    RefreshedAt = new DateTime(2026, 8, 31, 12, 30, 0),
                    Tables = new List<TableMetadata>
                    {
                        new TableMetadata
                        {
                            ScopeKey = new DictionaryRepositoryOptions().ScopeKey,
                            SchemaName = "dbo",
                            ObjectName = "T_ORDER",
                            ObjectType = "TABLE",
                            ChineseName = new MetadataValue { Value = "采购订单", Status = ConfidenceStatus.LocalOverride },
                            Fields = new List<FieldMetadata>()
                        }
                    }
                });

                var viewModel = new MainViewModel(path, Path.GetTempPath());

                Assert.AreEqual(0, viewModel.TableRows.Count);

                bool restored = await viewModel.RestoreMostRecentSnapshotAsync(CancellationToken.None);

                Assert.IsTrue(restored);
                Assert.AreEqual(1, viewModel.TableRows.Count);
                Assert.AreEqual("采购订单", viewModel.TableRows[0].ChineseName);
                Assert.IsTrue(viewModel.BannerText.IndexOf("已恢复本地规范缓存", StringComparison.Ordinal) >= 0);
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        /// <summary>XMZADD 20260903 验证共享快照恢复时会按唯一表名和真实主键补充明确标记的逻辑关系。</summary>
        [TestMethod]
        public async Task RestoreMostRecentSnapshotAsync_DiscoversUniqueNamedRelation()
        {
            string path = Path.Combine(Path.GetTempPath(), "shb-view-model-" + Guid.NewGuid().ToString("N") + ".db");
            string scopeKey = new DictionaryRepositoryOptions().ScopeKey;
            try
            {
                var parent = new TableMetadata
                {
                    ScopeKey = scopeKey,
                    SchemaName = "dbo",
                    ObjectName = "Item",
                    ObjectType = "TABLE",
                    Fields = new List<FieldMetadata>
                    {
                        new FieldMetadata { OwnerTableName = "Item", FieldName = "ID", DataType = "int", IsPrimaryKey = true }
                    }
                };
                var child = new TableMetadata
                {
                    ScopeKey = scopeKey,
                    SchemaName = "dbo",
                    ObjectName = "Item_Image",
                    ObjectType = "TABLE",
                    Fields = new List<FieldMetadata>
                    {
                        new FieldMetadata { OwnerTableName = "Item_Image", FieldName = "Img_ID", DataType = "int", IsPrimaryKey = true },
                        new FieldMetadata { OwnerTableName = "Item_Image", FieldName = "Item_ID", DataType = "int" }
                    }
                };
                new SnapshotStore(path).ReplaceScope(scopeKey, new SnapshotData
                {
                    RefreshedAt = new DateTime(2026, 9, 3, 9, 0, 0),
                    Tables = new List<TableMetadata> { parent, child }
                });
                var viewModel = new MainViewModel(path, Path.GetTempPath());

                Assert.IsTrue(await viewModel.RestoreMostRecentSnapshotAsync(CancellationToken.None));
                TableDisplayModel childRow = null;
                for (int index = 0; index < viewModel.TableRows.Count; index++)
                {
                    if (string.Equals(viewModel.TableRows[index].ObjectName, "Item_Image", StringComparison.Ordinal))
                    {
                        childRow = viewModel.TableRows[index];
                        break;
                    }
                }
                Assert.IsNotNull(childRow);
                Assert.AreEqual(0, childRow.Metadata.Relations.Count);
                viewModel.NavigateToTable("Item_Image");

                Assert.AreEqual(1, viewModel.RelationRows.Count);
                StringAssert.Contains(viewModel.RelationRows[0].RelationType, "推测");
                Assert.AreEqual("Item", viewModel.RelationRows[0].ParentTableName);
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        /// <summary>XMZADD 20260831 验证连接探测成功会在主界面提供明确可见的成功状态。</summary>
        [TestMethod]
        public void MarkConnectionValidated_ExposesVisibleSuccessState()
        {
            string path = Path.Combine(Path.GetTempPath(), "shb-view-model-" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                var viewModel = new MainViewModel(path, Path.GetTempPath());

                viewModel.MarkConnectionValidated(new ConnectionProfile
                {
                    Name = "EOS开发环境",
                    Database = "SHB"
                }, "SHB");

                Assert.AreEqual("连接成功", viewModel.ConnectionStatusText);
                Assert.IsTrue(viewModel.StatusLine.IndexOf("连接成功", StringComparison.Ordinal) >= 0);
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        /// <summary>XMZADD 20260831 验证资源树表项会提供中文主标题与物理表名副标题，并对空中文名显示暂无中文名称。</summary>
        [TestMethod]
        public void TableDisplayModel_UsesChineseTreeTitleAndPhysicalNameSubtitle()
        {
            TableDisplayModel unnamed = new TableDisplayModel(new TableMetadata
            {
                ObjectName = "T_PENDING",
                ObjectType = "TABLE"
            });
            TableDisplayModel named = new TableDisplayModel(new TableMetadata
            {
                ObjectName = "T_ORDER",
                ObjectType = "TABLE",
                ChineseName = new MetadataValue { Value = "采购订单", Status = ConfidenceStatus.CodeEvidence }
            });

            Assert.AreEqual("暂无中文名称", unnamed.TreeTitle);
            Assert.AreEqual("T_PENDING", unnamed.TreeSubtitle);
            Assert.AreEqual("采购订单", named.TreeTitle);
            Assert.AreEqual("T_ORDER", named.TreeSubtitle);
        }

        /// <summary>XMZADD 20260831 验证选中表会同时填充字段、关系和带来源定位的结构化证据。</summary>
        [TestMethod]
        public void SelectedTable_PopulatesFieldsRelationsAndEvidenceRows()
        {
            TableMetadata table = new TableMetadata
            {
                ObjectName = "T_ORDER",
                ObjectType = "TABLE",
                ChineseName = new MetadataValue
                {
                    Value = "采购订单",
                    Status = ConfidenceStatus.CodeEvidence,
                    Evidence = new List<EvidenceItem>
                    {
                        new EvidenceItem
                        {
                            SourceType = "EOS源码",
                            SourcePath = "D:\\EOS\\Order.cs",
                            SourceLine = 28,
                            RuleName = "SqlTableUsage",
                            Explanation = "采购单据查询使用该表"
                        }
                    }
                }
            };
            table.Fields.Add(new FieldMetadata { FieldName = "FNUMBER", OwnerTableName = table.ObjectName, DataType = "nvarchar" });
            table.Relations.Add(new RelationMetadata
            {
                ParentTableName = table.ObjectName,
                ParentFieldName = "FSUPPLIERID",
                ChildTableName = "T_BD_SUPPLIER",
                ChildFieldName = "FSUPPLIERID",
                RelationType = new MetadataValue { Value = "一对多", Status = ConfidenceStatus.Guessed }
            });
            SnapshotData snapshot = new SnapshotData();
            snapshot.Tables.Add(table);
            MainViewModel viewModel = new MainViewModel();

            viewModel.LoadSnapshotForTesting(snapshot);

            Assert.AreEqual(1, viewModel.FieldRows.Count);
            Assert.AreEqual(1, viewModel.RelationRows.Count);
            Assert.AreEqual(1, viewModel.EvidenceRows.Count);
            Assert.AreEqual("EOS源码", viewModel.EvidenceRows[0].SourceType);
            Assert.AreEqual("D:\\EOS\\Order.cs", viewModel.EvidenceRows[0].SourcePath);
            Assert.AreEqual(28, viewModel.EvidenceRows[0].SourceLine);
            Assert.AreEqual("SqlTableUsage", viewModel.EvidenceRows[0].RuleName);
        }

        /// <summary>XMZADD 20260831 验证同一 AI 证据被多个元数据复用时来源明细只保留一条可核验记录。</summary>
        [TestMethod]
        public void SelectedTable_DeduplicatesSameEvidenceAssignedToMultipleMetadataValues()
        {
            EvidenceItem evidence = new EvidenceItem
            {
                SourceType = "AI推测",
                SourcePath = "https://ai.example.com",
                SourceLine = 0,
                RuleName = "AiInference",
                RawValue = "T_ORDER",
                Explanation = "根据表名推断采购订单"
            };
            TableMetadata table = new TableMetadata
            {
                ObjectName = "T_ORDER",
                ObjectType = "TABLE",
                ChineseName = CreateEvidenceValue("采购订单", evidence),
                BusinessMeaning = CreateEvidenceValue("采购业务单据", evidence)
            };
            table.Fields.Add(new FieldMetadata
            {
                FieldName = "FNUMBER",
                OwnerTableName = table.ObjectName,
                Usage = CreateEvidenceValue("用于单据编号", evidence)
            });
            SnapshotData snapshot = new SnapshotData();
            snapshot.Tables.Add(table);
            MainViewModel viewModel = new MainViewModel();

            viewModel.LoadSnapshotForTesting(snapshot);

            Assert.AreEqual(1, viewModel.EvidenceRows.Count);
        }

        /// <summary>XMZADD 20260901 验证切换数据表会先清除旧字段选中，再完整刷新字段、关系和来源证据。</summary>
        [TestMethod]
        public void SelectTable_ClearsOldSelectedFieldAndRefreshesAllDetails()
        {
            SnapshotData snapshot = new SnapshotData();
            TableMetadata first = CreateSelectableTable("T_FIRST", "第一张表", "FID");
            TableMetadata second = CreateSelectableTable("T_SECOND", "第二张表", "FNUMBER");
            second.Fields.Add(new FieldMetadata { FieldName = "FSTATUS", OwnerTableName = second.ObjectName, DataType = "int" });
            second.Relations.Add(new RelationMetadata
            {
                ParentTableName = second.ObjectName,
                ParentFieldName = "FID",
                ChildTableName = first.ObjectName,
                ChildFieldName = "FID",
                RelationType = new MetadataValue { Value = "多对一", Status = ConfidenceStatus.CodeEvidence }
            });
            snapshot.Tables.Add(first);
            snapshot.Tables.Add(second);
            var viewModel = new MainViewModel();
            viewModel.LoadSnapshotForTesting(snapshot);
            viewModel.SelectedField = viewModel.FieldRows[0];

            viewModel.SelectedTable = viewModel.TableRows[1];

            Assert.IsNull(viewModel.SelectedField);
            Assert.AreEqual(2, viewModel.FieldRows.Count);
            Assert.AreEqual(1, viewModel.RelationRows.Count);
            Assert.AreEqual(1, viewModel.EvidenceRows.Count);
            Assert.AreSame(viewModel.SelectedTable.Metadata, viewModel.CurrentEditTarget);
            Assert.AreEqual("请选择一个数据表", viewModel.EmptyObjectTitle);
        }

        /// <summary>XMZADD 20260901 验证表选择通知发出时旧字段已清除，避免界面瞬间把上一表字段作为编辑目标。</summary>
        [TestMethod]
        public void SelectedTable_PropertyChanged_ObservesClearedFieldAndNewTableEditTarget()
        {
            SnapshotData snapshot = new SnapshotData();
            snapshot.Tables.Add(CreateSelectableTable("T_FIRST", "第一张表", "FID"));
            snapshot.Tables.Add(CreateSelectableTable("T_SECOND", "第二张表", "FNUMBER"));
            var viewModel = new MainViewModel();
            viewModel.LoadSnapshotForTesting(snapshot);
            viewModel.SelectedField = viewModel.FieldRows[0];
            TableDisplayModel target = viewModel.TableRows[1];
            bool notificationObserved = false;
            viewModel.PropertyChanged += delegate(object sender, System.ComponentModel.PropertyChangedEventArgs args)
            {
                if (args.PropertyName == "SelectedTable")
                {
                    notificationObserved = true;
                    Assert.IsNull(viewModel.SelectedField);
                    Assert.AreSame(target.Source, viewModel.CurrentEditTarget);
                }
            };

            viewModel.SelectedTable = target;

            Assert.IsTrue(notificationObserved);
        }

        /// <summary>XMZADD 20260901 验证资源节点携带架构名时能精确跳转到跨架构同名表并刷新完整详情。</summary>
        [TestMethod]
        public void NavigateToTable_WithSchema_SelectsExactSameNamedTableAndRefreshesDetails()
        {
            TableMetadata dboTable = CreateSelectableTable("T_ORDER", "业务订单", "FDOCNO");
            dboTable.SchemaName = "dbo";
            TableMetadata auditTable = CreateSelectableTable("T_ORDER", "审计订单", "FAUDITID");
            auditTable.SchemaName = "audit";
            auditTable.Relations.Add(new RelationMetadata
            {
                ParentTableName = auditTable.ObjectName,
                ParentFieldName = "FAUDITID",
                ChildTableName = dboTable.ObjectName,
                ChildFieldName = "FDOCNO",
                RelationType = new MetadataValue { Value = "审计来源", Status = ConfidenceStatus.CodeEvidence }
            });
            SnapshotData snapshot = new SnapshotData();
            snapshot.Tables.Add(dboTable);
            snapshot.Tables.Add(auditTable);
            var viewModel = new MainViewModel();
            viewModel.LoadSnapshotForTesting(snapshot);

            viewModel.NavigateToTable("audit", "T_ORDER");

            Assert.IsNotNull(viewModel.SelectedTable);
            Assert.AreEqual("audit", viewModel.SelectedTable.Metadata.SchemaName);
            Assert.AreEqual("FAUDITID", viewModel.FieldRows[0].FieldName);
            Assert.AreEqual(1, viewModel.RelationRows.Count);
            Assert.AreEqual(1, viewModel.EvidenceRows.Count);
        }

        /// <summary>XMZADD 20260901 验证字段选中决定字段编辑目标，清除字段后编辑目标安全回到当前数据表。</summary>
        [TestMethod]
        public void SelectedField_UpdatesCurrentEditTargetAndFallsBackToSelectedTable()
        {
            SnapshotData snapshot = new SnapshotData();
            snapshot.Tables.Add(CreateSelectableTable("T_ORDER", "采购订单", "FNUMBER"));
            var viewModel = new MainViewModel();
            viewModel.LoadSnapshotForTesting(snapshot);

            viewModel.SelectedField = viewModel.FieldRows[0];

            Assert.AreSame(viewModel.SelectedField.Metadata, viewModel.CurrentEditTarget);

            viewModel.SelectedField = null;

            Assert.AreSame(viewModel.SelectedTable.Metadata, viewModel.CurrentEditTarget);
        }

        /// <summary>XMZADD 20260901 验证统一规范快照即使残留视图对象，主界面也只展示数据表。</summary>
        [TestMethod]
        public void LoadSnapshotForTesting_OnlyDisplaysTables()
        {
            SnapshotData snapshot = new SnapshotData();
            snapshot.Tables.Add(CreateSelectableTable("T_ORDER", "采购订单", "FID"));
            TableMetadata view = CreateSelectableTable("V_ORDER", "采购订单视图", "FID");
            view.ObjectType = "VIEW";
            snapshot.Tables.Add(view);
            var viewModel = new MainViewModel();

            viewModel.LoadSnapshotForTesting(snapshot);

            Assert.AreEqual(1, viewModel.TableRows.Count);
            Assert.AreEqual("T_ORDER", viewModel.TableRows[0].ObjectName);
            StringAssert.Contains(viewModel.BannerText, "共享字典");
        }

        /// <summary>XMZADD 20260901 验证规范快照更新后按稳定表字段键恢复用户当前编辑上下文。</summary>
        [TestMethod]
        public void LoadSnapshotForTesting_ReappliesSelectionByStableTableAndFieldKeys()
        {
            SnapshotData firstSnapshot = new SnapshotData();
            firstSnapshot.Tables.Add(CreateSelectableTable("T_FIRST", "第一张表", "FID"));
            firstSnapshot.Tables.Add(CreateSelectableTable("T_SECOND", "第二张表", "FNUMBER"));
            var viewModel = new MainViewModel();
            viewModel.LoadSnapshotForTesting(firstSnapshot);
            viewModel.SelectedTable = viewModel.TableRows[1];
            viewModel.SelectedField = viewModel.FieldRows[0];

            SnapshotData refreshedSnapshot = new SnapshotData();
            refreshedSnapshot.Tables.Add(CreateSelectableTable("T_FIRST", "第一张表新名称", "FID"));
            refreshedSnapshot.Tables.Add(CreateSelectableTable("T_SECOND", "第二张表新名称", "FNUMBER"));
            viewModel.LoadSnapshotForTesting(refreshedSnapshot);

            Assert.AreEqual("T_SECOND", viewModel.SelectedTable.ObjectName);
            Assert.AreEqual("FNUMBER", viewModel.SelectedField.FieldName);
            Assert.AreSame(viewModel.SelectedField.Metadata, viewModel.CurrentEditTarget);
        }

        /// <summary>XMZADD 20260831 验证人工本地字典保存通知会废弃旧项目范围并按新的人工证据重建默认目录。</summary>
        [TestMethod]
        public void NotifyLocalDictionarySaved_RebuildsDefaultProjectScopeAfterManualOverride()
        {
            var source = new TableMetadata
            {
                ObjectName = "T_SOURCE",
                ObjectType = "TABLE",
                ChineseName = new MetadataValue { Value = "源码订单", Status = ConfidenceStatus.CodeEvidence }
            };
            var manual = new TableMetadata
            {
                ObjectName = "T_MANUAL",
                ObjectType = "TABLE",
                ChineseName = new MetadataValue { Value = "推测：人工表", Status = ConfidenceStatus.Guessed }
            };
            var snapshot = new SnapshotData();
            snapshot.Tables.Add(source);
            snapshot.Tables.Add(manual);
            var viewModel = new MainViewModel();
            viewModel.ShowAllObjects = false;

            viewModel.LoadSnapshotForTesting(snapshot);
            object initialScope = GetProjectScopeCache(viewModel);
            manual.ChineseName = new MetadataValue { Value = "人工维护表", Status = ConfidenceStatus.LocalOverride, IsManualOverride = true };
            viewModel.NotifyLocalDictionarySaved();

            Assert.IsNotNull(FindTable(viewModel, "T_MANUAL"));
            Assert.IsNotNull(GetProjectScopeCache(viewModel));
            Assert.AreNotSame(initialScope, GetProjectScopeCache(viewModel));
        }

        /// <summary>XMZADD 20260831 构造携带指定来源证据的元数据值，保持去重测试只关注展示规则。</summary>
        private static MetadataValue CreateEvidenceValue(string value, EvidenceItem evidence)
        {
            return new MetadataValue
            {
                Value = value,
                Status = ConfidenceStatus.AiGuessed,
                Evidence = new List<EvidenceItem> { evidence }
            };
        }

        /// <summary>XMZADD 20260901 构造含字段和表级证据的数据表，使选中语义测试不依赖演示快照。</summary>
        private static TableMetadata CreateSelectableTable(string objectName, string chineseName, string fieldName)
        {
            return new TableMetadata
            {
                ScopeKey = new DictionaryRepositoryOptions().ScopeKey,
                SchemaName = "dbo",
                ObjectName = objectName,
                ObjectType = "TABLE",
                ChineseName = new MetadataValue
                {
                    Value = chineseName,
                    Status = ConfidenceStatus.CodeEvidence,
                    Evidence = new List<EvidenceItem>
                    {
                        new EvidenceItem
                        {
                            SourceType = "EOS源码",
                            SourcePath = "业务/" + objectName + ".vb",
                            SourceLine = 10,
                            RuleName = "TableUsage",
                            Explanation = "业务代码明确使用该数据表"
                        }
                    }
                },
                Fields = new List<FieldMetadata>
                {
                    new FieldMetadata
                    {
                        OwnerTableName = objectName,
                        FieldName = fieldName,
                        DataType = "nvarchar"
                    }
                }
            };
        }

        /// <summary>XMZADD 20260831 按物理表名定位默认目录中的测试对象，验证人工覆盖后的可见范围。</summary>
        private static TableDisplayModel FindTable(MainViewModel viewModel, string objectName)
        {
            for (int index = 0; index < viewModel.TableRows.Count; index++)
            {
                if (string.Equals(viewModel.TableRows[index].ObjectName, objectName, StringComparison.OrdinalIgnoreCase))
                {
                    return viewModel.TableRows[index];
                }
            }

            return null;
        }

        /// <summary>XMZADD 20260831 读取项目范围缓存实例，确认人工维护后不会继续复用旧范围。</summary>
        private static object GetProjectScopeCache(MainViewModel viewModel)
        {
            System.Reflection.FieldInfo field = typeof(MainViewModel).GetField("_projectRelatedObjectNames",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(field);
            return field.GetValue(viewModel);
        }
    }
}
