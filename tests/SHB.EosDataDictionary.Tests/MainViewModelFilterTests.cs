using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHB.EosDataDictionary.Models;
using SHB.EosDataDictionary.Services;
using SHB.EosDataDictionary.ViewModels;

namespace SHB.EosDataDictionary.Tests
{
    /// <summary>XMZADD 20260828 验证主界面将逐列筛选状态正确应用到测试快照。</summary>
    [TestClass]
    public sealed class MainViewModelFilterTests
    {
        /// <summary>XMZADD 20260828 验证表目录多列筛选采用 AND 组合并可单独清除。</summary>
        [TestMethod]
        public void SetTableColumnFilter_CombinesAndClearsColumns()
        {
            MainViewModel viewModel = new MainViewModel();
            viewModel.LoadSnapshotForTesting(PreviewDataFactory.Create());

            viewModel.SetTableColumnFilter("ObjectName", "t_pur_order");
            Assert.AreEqual(1, viewModel.TableRows.Count);

            viewModel.SetTableColumnFilter("ChineseName", "供应商");
            Assert.AreEqual(0, viewModel.TableRows.Count);

            viewModel.SetTableColumnFilter("ObjectName", string.Empty);
            Assert.AreEqual(1, viewModel.TableRows.Count);
            Assert.AreEqual("供应商", viewModel.TableRows[0].ChineseName);
        }

        /// <summary>XMZADD 20260828 验证字段字典按字段列过滤后只保留匹配字段。</summary>
        [TestMethod]
        public void SetFieldColumnFilter_FiltersCurrentTableFields()
        {
            MainViewModel viewModel = new MainViewModel();
            viewModel.LoadSnapshotForTesting(PreviewDataFactory.Create());

            viewModel.SetFieldColumnFilter("FieldName", "FNUMBER");

            Assert.AreEqual(1, viewModel.FieldRows.Count);
            Assert.AreEqual("FNUMBER", viewModel.FieldRows[0].FieldName);
        }

        /// <summary>XMZADD 20260828 验证关联关系列表能够按父表列执行模糊筛选。</summary>
        [TestMethod]
        public void SetRelationColumnFilter_FiltersCurrentRelations()
        {
            MainViewModel viewModel = new MainViewModel();
            viewModel.LoadSnapshotForTesting(PreviewDataFactory.Create());

            viewModel.SetRelationColumnFilter("ParentTableName", "pur_order");

            Assert.IsTrue(viewModel.RelationRows.Count > 0);
            for (int i = 0; i < viewModel.RelationRows.Count; i++)
            {
                StringAssert.Contains(viewModel.RelationRows[i].ParentTableName.ToUpperInvariant(), "PUR_ORDER");
            }
        }

        /// <summary>XMZADD 20260828 验证关联跳转不会被当前表头筛选条件静默拦截。</summary>
        [TestMethod]
        public void NavigateToTable_ClearsFiltersThatExcludeTargetTable()
        {
            MainViewModel viewModel = new MainViewModel();
            viewModel.LoadSnapshotForTesting(PreviewDataFactory.Create());
            viewModel.SearchTables("T_PUR_ORDER");
            viewModel.SetTableColumnFilter("ObjectName", "T_PUR_ORDER");
            viewModel.SetFieldColumnFilter("FieldName", "不存在的字段");
            viewModel.SetRelationColumnFilter("ParentTableName", "不存在的表");

            viewModel.NavigateToTable("T_BD_SUPPLIER");

            Assert.IsNotNull(viewModel.SelectedTable);
            Assert.AreEqual("T_BD_SUPPLIER", viewModel.SelectedTable.ObjectName);
            Assert.IsTrue(viewModel.TableRows.Count > 1);
            Assert.AreEqual(viewModel.SelectedTable.Metadata.Fields.Count, viewModel.FieldRows.Count);
            Assert.AreEqual(viewModel.SelectedTable.Metadata.Relations.Count, viewModel.RelationRows.Count);

            viewModel.SearchTables(string.Empty);
            Assert.IsTrue(viewModel.TableRows.Count > 1);
        }

        /// <summary>XMZADD 20260903 验证默认目录展示共享快照中的全部物理表并明确当前数量和总数。</summary>
        [TestMethod]
        public void LoadSnapshotForTesting_DefaultView_DisplaysEveryPhysicalTable()
        {
            MainViewModel viewModel = new MainViewModel();
            viewModel.LoadSnapshotForTesting(CreateProjectEvidenceSnapshot());

            Assert.IsTrue(viewModel.ShowAllObjects);
            Assert.IsNotNull(FindTable(viewModel, "T_SOURCE_USAGE"));
            Assert.IsNotNull(FindTable(viewModel, "T_ENTITY"));
            Assert.IsNotNull(FindTable(viewModel, "T_MANUAL"));
            Assert.IsNotNull(FindTable(viewModel, "T_RELATED_RULE"));
            Assert.IsNotNull(FindTable(viewModel, "T_ISOLATED_RULE"));
            Assert.AreEqual("当前显示：5 / 当前范围：5 / 全部：5 张表", viewModel.TableCountText);
        }

        /// <summary>XMZADD 20260903 验证 EOS 实体范围只接受可追溯源码实体，并与全部对象范围保持互斥。</summary>
        [TestMethod]
        public void ShowEosEntityObjectsOnly_UsesSourceEntityEvidenceAndIsMutuallyExclusive()
        {
            MainViewModel viewModel = new MainViewModel();
            viewModel.LoadSnapshotForTesting(CreateProjectEvidenceSnapshot());

            viewModel.ShowEosEntityObjectsOnly = true;

            Assert.IsFalse(viewModel.ShowAllObjects);
            Assert.AreEqual(1, viewModel.TableRows.Count);
            Assert.AreEqual("T_ENTITY", viewModel.TableRows[0].ObjectName);
            Assert.AreEqual("当前显示：1 / 当前范围：1 / 全部：5 张表", viewModel.TableCountText);

            viewModel.ShowAllObjects = true;

            Assert.IsFalse(viewModel.ShowEosEntityObjectsOnly);
            Assert.AreEqual(5, viewModel.TableRows.Count);
        }

        /// <summary>XMZADD 20260903 验证实体范围不会把仅有实体字符串、但缺少源码规则证据的对象误判为 EOS 实体。</summary>
        [TestMethod]
        public void ShowEosEntityObjectsOnly_RejectsEntityNameWithoutSourceRule()
        {
            SnapshotData snapshot = CreateProjectEvidenceSnapshot();
            snapshot.Tables.Add(new TableMetadata
            {
                ObjectName = "T_FAKE_ENTITY",
                ObjectType = "TABLE",
                EntityName = new MetadataValue
                {
                    Value = "t_FakeEntity",
                    Status = ConfidenceStatus.CodeEvidence,
                    SourceSummary = "普通代码名称"
                }
            });
            MainViewModel viewModel = new MainViewModel();
            viewModel.LoadSnapshotForTesting(snapshot);

            viewModel.ShowEosEntityObjectsOnly = true;

            Assert.AreEqual(1, viewModel.TableRows.Count);
            Assert.AreEqual("T_ENTITY", viewModel.TableRows[0].ObjectName);
        }

        /// <summary>XMZADD 20260903 验证综合搜索和表头筛选只在当前 EOS 实体范围内继续叠加。</summary>
        [TestMethod]
        public void EntityScope_SearchAndColumnFilterRemainInsideCurrentScope()
        {
            MainViewModel viewModel = new MainViewModel();
            viewModel.LoadSnapshotForTesting(CreateProjectEvidenceSnapshot());
            viewModel.ShowEosEntityObjectsOnly = true;

            viewModel.SearchTables("ENTITY");
            viewModel.SetTableColumnFilter("ObjectName", "T_ENTITY");

            Assert.AreEqual(1, viewModel.TableRows.Count);
            Assert.AreEqual("T_ENTITY", viewModel.TableRows[0].ObjectName);

            viewModel.SearchTables("MANUAL");

            Assert.AreEqual(0, viewModel.TableRows.Count);
            Assert.AreEqual("当前显示：0 / 当前范围：1 / 全部：5 张表", viewModel.TableCountText);
        }

        /// <summary>XMZADD 20260917 验证参考译名、消费模块和证据说明参与当前 EOS 实体范围内的综合搜索。</summary>
        [TestMethod]
        public void EntityScope_SearchesSuggestionUsedModulesAndEvidenceInsideCurrentScope()
        {
            SnapshotData snapshot = CreateProjectEvidenceSnapshot();
            TableMetadata entity = FindMetadata(snapshot, "T_ENTITY");
            entity.SuggestedChineseName = new MetadataValue
            {
                Value = "仓储区定义",
                Status = ConfidenceStatus.Guessed,
                SourceSummary = "根据仓储区维护窗口推断",
                Evidence = new List<EvidenceItem>
                {
                    new EvidenceItem { RuleName = "IdentifierTranslation", Explanation = "仓储区布局维护" }
                }
            };
            entity.UsedByModules.Add(new MetadataValue { Value = "仓储与库存" });
            var viewModel = new MainViewModel();
            viewModel.LoadSnapshotForTesting(snapshot);
            viewModel.ShowEosEntityObjectsOnly = true;

            viewModel.SearchTables("仓储区定义");
            Assert.AreEqual(1, viewModel.TableRows.Count);
            Assert.IsTrue(viewModel.TableRows[0].HasEntity);

            viewModel.SearchTables("仓储与库存");
            Assert.AreEqual(1, viewModel.TableRows.Count);

            viewModel.SearchTables("布局维护");
            Assert.AreEqual(1, viewModel.TableRows.Count);

            viewModel.SearchTables("T_MANUAL");
            Assert.AreEqual(0, viewModel.TableRows.Count);
        }

        /// <summary>XMZADD 20260917 验证缺正式名、含参考译名和冲突筛选可与当前对象范围组合。</summary>
        [TestMethod]
        public void SemanticNameFilters_CombineWithoutEscapingCurrentScope()
        {
            var pending = CreateTable("T_PENDING", ConfidenceStatus.PendingConfirmation);
            pending.ChineseName.Value = string.Empty;
            pending.SuggestedChineseName = new MetadataValue { Value = "待确认订单", Status = ConfidenceStatus.Guessed };
            var conflict = CreateTable("T_CONFLICT", ConfidenceStatus.CodeEvidence);
            conflict.ChineseName.Value = "采购订单";
            conflict.SuggestedChineseName = new MetadataValue { Value = "采购单", Status = ConfidenceStatus.Guessed };
            conflict.AlternativeChineseNames.Add(new MetadataValue { Value = "采购业务单" });
            var ordinary = CreateTable("T_ORDINARY", ConfidenceStatus.CodeEvidence);
            ordinary.ChineseName.Value = "普通订单";
            var snapshot = new SnapshotData();
            snapshot.Tables.Add(pending);
            snapshot.Tables.Add(conflict);
            snapshot.Tables.Add(ordinary);
            var viewModel = new MainViewModel();
            viewModel.LoadSnapshotForTesting(snapshot);

            viewModel.ShowOnlyMissingOfficialNames = true;
            viewModel.ShowOnlyWithSuggestions = true;

            Assert.AreEqual(1, viewModel.TableRows.Count);
            Assert.AreEqual("T_PENDING", viewModel.TableRows[0].ObjectName);

            viewModel.ShowOnlyMissingOfficialNames = false;
            viewModel.ShowOnlyConflicts = true;

            Assert.AreEqual(1, viewModel.TableRows.Count);
            Assert.AreEqual("T_CONFLICT", viewModel.TableRows[0].ObjectName);
        }

        /// <summary>XMZADD 20260917 验证业务实际使用字段筛选仍允许在参考译名、枚举中文和证据摘要中搜索。</summary>
        [TestMethod]
        public void ActualUsedFieldFilter_SearchesSuggestionEnumAndEvidence()
        {
            TableMetadata table = CreateTable("T_FIELD_SEARCH", ConfidenceStatus.CodeEvidence);
            var used = new FieldMetadata
            {
                FieldName = "Owner_Company_ID",
                OwnerTableName = table.ObjectName,
                SuggestedChineseName = new MetadataValue
                {
                    Value = "货主公司ID",
                    SourceSummary = "货主维度业务字段",
                    Evidence = new List<EvidenceItem>
                    {
                        new EvidenceItem { RuleName = "GridColumnCaption", Explanation = "货主公司筛选条件" }
                    }
                },
                Usage = new MetadataValue { Value = "业务代码读取" }
            };
            used.EnumItems.Add(new EnumItemMetadata
            {
                Value = "1",
                ChineseName = new MetadataValue { Value = "启用" }
            });
            table.Fields.Add(used);
            table.Fields.Add(new FieldMetadata
            {
                FieldName = "UNUSED_COLUMN",
                OwnerTableName = table.ObjectName,
                ChineseName = new MetadataValue { Value = "未使用列" }
            });
            var snapshot = new SnapshotData();
            snapshot.Tables.Add(table);
            var viewModel = new MainViewModel();
            viewModel.LoadSnapshotForTesting(snapshot);
            viewModel.ShowOnlyActualUsedFields = true;

            Assert.AreEqual(1, viewModel.FieldRows.Count);
            Assert.AreEqual("Owner_Company_ID", viewModel.FieldRows[0].FieldName);

            viewModel.SearchFields("货主公司ID");
            Assert.AreEqual(1, viewModel.FieldRows.Count);
            viewModel.SearchFields("启用");
            Assert.AreEqual(1, viewModel.FieldRows.Count);
            viewModel.SearchFields("筛选条件");
            Assert.AreEqual(1, viewModel.FieldRows.Count);
        }

        /// <summary>XMZADD 20260903 验证用户主动关闭显示全部后仍可使用原有项目证据范围。</summary>
        [TestMethod]
        public void ShowAllObjects_False_HidesIsolatedRuleTable()
        {
            MainViewModel viewModel = new MainViewModel();
            viewModel.LoadSnapshotForTesting(CreateProjectEvidenceSnapshot());

            viewModel.ShowAllObjects = false;

            Assert.IsNull(FindTable(viewModel, "T_ISOLATED_RULE"));
        }

        /// <summary>XMZADD 20260831 验证没有任何项目证据时目录安全回退显示全部对象。</summary>
        [TestMethod]
        public void LoadSnapshotForTesting_WithoutProjectEvidence_FallsBackToAllObjects()
        {
            MainViewModel viewModel = new MainViewModel();
            SnapshotData snapshot = new SnapshotData();
            snapshot.Tables.Add(CreateTable("T_RULE_A", ConfidenceStatus.Guessed));
            snapshot.Tables.Add(CreateTable("T_RULE_B", ConfidenceStatus.PendingConfirmation));

            viewModel.LoadSnapshotForTesting(snapshot);

            Assert.AreEqual(2, viewModel.TableRows.Count);
        }

        /// <summary>XMZADD 20260903 验证通用字段词典不会把海量无业务对象误纳入项目范围，精确字段条目仍保留。</summary>
        [TestMethod]
        public void ProjectScope_GenericFieldGlossaryDoesNotPromoteTableButExactFieldEvidenceDoes()
        {
            TableMetadata source = CreateTable("T_SOURCE", ConfidenceStatus.CodeEvidence);
            TableMetadata generic = CreateTable("T_GENERIC", ConfidenceStatus.Guessed);
            generic.Fields.Add(new FieldMetadata
            {
                FieldName = "ID",
                ChineseName = CreateKnowledgeFieldValue("CommonFieldGlossary")
            });
            TableMetadata exact = CreateTable("T_EXACT", ConfidenceStatus.Guessed);
            exact.Fields.Add(new FieldMetadata
            {
                FieldName = "BusinessNo",
                ChineseName = CreateKnowledgeFieldValue("ExactProjectField")
            });
            var snapshot = new SnapshotData { Tables = new List<TableMetadata> { source, generic, exact } };
            var viewModel = new MainViewModel { ShowAllObjects = false };

            viewModel.LoadSnapshotForTesting(snapshot);

            Assert.IsNotNull(FindTable(viewModel, "T_SOURCE"));
            Assert.IsNull(FindTable(viewModel, "T_GENERIC"));
            Assert.IsNotNull(FindTable(viewModel, "T_EXACT"));
        }

        /// <summary>XMZADD 20260903 验证与强项目证据无连接的纯命名推测关系不会自行提升为项目对象。</summary>
        [TestMethod]
        public void ProjectScope_DisconnectedLogicalRelationDoesNotBecomeDirectProjectEvidence()
        {
            TableMetadata source = CreateTable("T_SOURCE", ConfidenceStatus.CodeEvidence);
            TableMetadata parent = CreateTable("T_LOGICAL_PARENT", ConfidenceStatus.Guessed);
            TableMetadata child = CreateTable("T_LOGICAL_CHILD", ConfidenceStatus.Guessed);
            var relation = new RelationMetadata
            {
                ParentTableName = parent.ObjectName,
                ChildTableName = child.ObjectName,
                RelationType = new MetadataValue
                {
                    Value = "推测：多对一",
                    Status = ConfidenceStatus.Guessed,
                    Evidence = new List<EvidenceItem>
                    {
                        new EvidenceItem { RuleName = "LogicalForeignKeyName" }
                    }
                }
            };
            parent.Relations.Add(relation);
            child.Relations.Add(relation);
            var snapshot = new SnapshotData { Tables = new List<TableMetadata> { source, parent, child } };
            var viewModel = new MainViewModel { ShowAllObjects = false };

            viewModel.LoadSnapshotForTesting(snapshot);

            Assert.IsNotNull(FindTable(viewModel, "T_SOURCE"));
            Assert.IsNull(FindTable(viewModel, "T_LOGICAL_PARENT"));
            Assert.IsNull(FindTable(viewModel, "T_LOGICAL_CHILD"));
        }

        /// <summary>XMZADD 20260831 验证默认目录只保留直接项目对象的一跳关联，不递归扩散规则表链路。</summary>
        [TestMethod]
        public void LoadSnapshotForTesting_DefaultView_StopsAfterOneRelationHop()
        {
            TableMetadata source = CreateTable("T_SOURCE", ConfidenceStatus.CodeEvidence);
            TableMetadata firstHop = CreateTable("T_FIRST_HOP", ConfidenceStatus.Guessed);
            TableMetadata secondHop = CreateTable("T_SECOND_HOP", ConfidenceStatus.Guessed);
            source.Relations.Add(new RelationMetadata
            {
                ParentTableName = source.ObjectName,
                ChildTableName = firstHop.ObjectName,
                RelationType = new MetadataValue { Value = "推测：一对多", Status = ConfidenceStatus.Guessed }
            });
            firstHop.Relations.Add(new RelationMetadata
            {
                ParentTableName = firstHop.ObjectName,
                ChildTableName = secondHop.ObjectName,
                RelationType = new MetadataValue { Value = "推测：一对多", Status = ConfidenceStatus.Guessed }
            });
            SnapshotData snapshot = new SnapshotData();
            snapshot.Tables.Add(source);
            snapshot.Tables.Add(firstHop);
            snapshot.Tables.Add(secondHop);
            MainViewModel viewModel = new MainViewModel();
            viewModel.ShowAllObjects = false;

            viewModel.LoadSnapshotForTesting(snapshot);

            Assert.IsNotNull(FindTable(viewModel, "T_FIRST_HOP"));
            Assert.IsNull(FindTable(viewModel, "T_SECOND_HOP"));
        }

        /// <summary>XMZADD 20260831 验证二跳关系跳转会自动显示目标并选中当前可见列表中的同一实例。</summary>
        [TestMethod]
        public void NavigateToTable_TwoHopTargetOutsideDefaultView_ShowsAllAndSelectsVisibleInstance()
        {
            TableMetadata source = CreateTable("T_SOURCE", ConfidenceStatus.CodeEvidence);
            TableMetadata firstHop = CreateTable("T_FIRST_HOP", ConfidenceStatus.Guessed);
            TableMetadata secondHop = CreateTable("T_SECOND_HOP", ConfidenceStatus.Guessed);
            source.Relations.Add(new RelationMetadata
            {
                ParentTableName = source.ObjectName,
                ChildTableName = firstHop.ObjectName,
                RelationType = new MetadataValue { Value = "推测：一对多", Status = ConfidenceStatus.Guessed }
            });
            firstHop.Relations.Add(new RelationMetadata
            {
                ParentTableName = firstHop.ObjectName,
                ChildTableName = secondHop.ObjectName,
                RelationType = new MetadataValue { Value = "推测：一对多", Status = ConfidenceStatus.Guessed }
            });
            SnapshotData snapshot = new SnapshotData();
            snapshot.Tables.Add(source);
            snapshot.Tables.Add(firstHop);
            snapshot.Tables.Add(secondHop);
            MainViewModel viewModel = new MainViewModel();
            viewModel.ShowAllObjects = false;
            viewModel.LoadSnapshotForTesting(snapshot);

            viewModel.NavigateToTable(secondHop.ObjectName);

            Assert.IsTrue(viewModel.ShowAllObjects);
            Assert.IsNotNull(viewModel.SelectedTable);
            Assert.AreSame(FindTable(viewModel, secondHop.ObjectName), viewModel.SelectedTable);
        }

        /// <summary>XMZADD 20260831 验证默认项目范围首次建立后，连续输入搜索不会重复扫描全量对象依据。</summary>
        [TestMethod]
        public void SearchTables_WhenProjectScopeIsUnchanged_ReusesCachedProjectScope()
        {
            MainViewModel viewModel = new MainViewModel();
            viewModel.ShowAllObjects = false;
            viewModel.LoadSnapshotForTesting(CreateProjectEvidenceSnapshot());
            object initialScope = GetProjectScopeCache(viewModel);

            viewModel.SearchTables("SOURCE");
            viewModel.SearchTables("MANUAL");

            Assert.IsNotNull(initialScope);
            Assert.AreSame(initialScope, GetProjectScopeCache(viewModel));
        }

        /// <summary>XMZADD 20260903 验证每次搜索复用加载时创建的表行，避免七万级对象反复分配。</summary>
        [TestMethod]
        public void SearchTables_ReusesCachedDisplayInstances()
        {
            MainViewModel viewModel = new MainViewModel();
            viewModel.LoadSnapshotForTesting(CreateProjectEvidenceSnapshot());
            TableDisplayModel original = FindTable(viewModel, "T_ISOLATED_RULE");

            viewModel.SearchTables("ISOLATED");

            Assert.AreEqual(1, viewModel.TableRows.Count);
            Assert.AreSame(original, viewModel.TableRows[0]);
            Assert.AreEqual("当前显示：1 / 当前范围：5 / 全部：5 张表", viewModel.TableCountText);
        }

        /// <summary>XMZADD 20260903 验证关系跳转目标位于实体范围外时自动展开全部对象并选中目标。</summary>
        [TestMethod]
        public void NavigateToTable_TargetOutsideEntityScope_ExpandsAllAndSelectsTarget()
        {
            MainViewModel viewModel = new MainViewModel();
            viewModel.LoadSnapshotForTesting(CreateProjectEvidenceSnapshot());
            viewModel.ShowEosEntityObjectsOnly = true;

            viewModel.NavigateToTable("T_MANUAL");

            Assert.IsTrue(viewModel.ShowAllObjects);
            Assert.IsFalse(viewModel.ShowEosEntityObjectsOnly);
            Assert.IsNotNull(viewModel.SelectedTable);
            Assert.AreEqual("T_MANUAL", viewModel.SelectedTable.ObjectName);
        }

        /// <summary>XMZADD 20260903 验证人工修改模块后资源树立即重新归类并保持当前表实例。</summary>
        [TestMethod]
        public void NotifyLocalDictionarySaved_ModuleChanged_MovesSelectedTableToNewModule()
        {
            MainViewModel viewModel = new MainViewModel();
            viewModel.LoadSnapshotForTesting(CreateProjectEvidenceSnapshot());
            TableDisplayModel selected = FindTable(viewModel, "T_MANUAL");
            viewModel.SelectedTable = selected;
            selected.Metadata.ModuleName = new MetadataValue
            {
                Value = "财务",
                Status = ConfidenceStatus.LocalOverride,
                IsManualOverride = true
            };

            viewModel.NotifyLocalDictionarySaved();

            ModuleNode finance = FindModule(viewModel, "财务");
            Assert.IsNotNull(finance);
            Assert.AreEqual(1, finance.Tables.Count);
            Assert.AreSame(selected, finance.Tables[0]);
            Assert.AreSame(selected, viewModel.SelectedTable);
        }

        /// <summary>XMZADD 20260901 验证表搜索重建显示行后按稳定键恢复集合内实例和当前字段选择。</summary>
        [TestMethod]
        public void SearchTables_WhenSelectedTableRemainsVisible_RestoresVisibleInstanceAndSelectedField()
        {
            TableMetadata first = CreateTable("T_FIRST", ConfidenceStatus.CodeEvidence);
            first.SchemaName = "dbo";
            first.Fields.Add(new FieldMetadata { OwnerTableName = first.ObjectName, FieldName = "FID", DataType = "int" });
            TableMetadata second = CreateTable("T_SECOND", ConfidenceStatus.CodeEvidence);
            second.SchemaName = "audit";
            second.Fields.Add(new FieldMetadata { OwnerTableName = second.ObjectName, FieldName = "FNUMBER", DataType = "nvarchar" });
            SnapshotData snapshot = new SnapshotData();
            snapshot.Tables.Add(first);
            snapshot.Tables.Add(second);
            var viewModel = new MainViewModel();
            viewModel.LoadSnapshotForTesting(snapshot);
            viewModel.SelectedTable = viewModel.TableRows[1];
            viewModel.SelectedField = viewModel.FieldRows[0];

            viewModel.SearchTables("T_SECOND");

            Assert.AreEqual(1, viewModel.TableRows.Count);
            Assert.AreSame(viewModel.TableRows[0], viewModel.SelectedTable);
            Assert.AreEqual("audit", viewModel.SelectedTable.Metadata.SchemaName);
            Assert.IsNotNull(viewModel.SelectedField);
            Assert.AreEqual("FNUMBER", viewModel.SelectedField.FieldName);
            Assert.AreSame(viewModel.SelectedField.Source, viewModel.CurrentEditTarget);
        }

        /// <summary>XMZADD 20260901 验证表搜索排除当前表时清空表字段选择，避免编辑集合外旧对象。</summary>
        [TestMethod]
        public void SearchTables_WhenSelectedTableIsFilteredOut_ClearsSelection()
        {
            SnapshotData snapshot = new SnapshotData();
            TableMetadata first = CreateTable("T_FIRST", ConfidenceStatus.CodeEvidence);
            first.Fields.Add(new FieldMetadata { OwnerTableName = first.ObjectName, FieldName = "FID", DataType = "int" });
            TableMetadata second = CreateTable("T_SECOND", ConfidenceStatus.CodeEvidence);
            second.Fields.Add(new FieldMetadata { OwnerTableName = second.ObjectName, FieldName = "FNUMBER", DataType = "nvarchar" });
            snapshot.Tables.Add(first);
            snapshot.Tables.Add(second);
            var viewModel = new MainViewModel();
            viewModel.LoadSnapshotForTesting(snapshot);
            viewModel.SelectedTable = viewModel.TableRows[1];
            viewModel.SelectedField = viewModel.FieldRows[0];

            viewModel.SearchTables("T_FIRST");

            Assert.AreEqual(1, viewModel.TableRows.Count);
            Assert.IsNull(viewModel.SelectedTable);
            Assert.IsNull(viewModel.SelectedField);
            Assert.IsNull(viewModel.CurrentEditTarget);
        }

        /// <summary>XMZADD 20260831 验证显示全部对象时加载快照不会构建默认项目范围缓存。</summary>
        [TestMethod]
        public void LoadSnapshotForTesting_WhenShowAllObjectsIsTrue_DoesNotBuildProjectScope()
        {
            MainViewModel viewModel = new MainViewModel();
            viewModel.ShowAllObjects = true;

            viewModel.LoadSnapshotForTesting(CreateProjectEvidenceSnapshot());

            Assert.IsNull(GetProjectScopeCache(viewModel));
        }

        /// <summary>XMZADD 20260903 验证七万二千张表的最差位置包含检索保持为纯内存即时响应。</summary>
        [TestMethod]
        public void SearchTables_SeventyTwoThousandRows_CompletesWithinInteractiveBudget()
        {
            var snapshot = new SnapshotData();
            for (int index = 0; index < 72000; index++)
            {
                snapshot.Tables.Add(CreateTable("T_TABLE_" + index.ToString("D5"), ConfidenceStatus.Guessed));
            }
            var viewModel = new MainViewModel();
            viewModel.LoadSnapshotForTesting(snapshot);
            var stopwatch = Stopwatch.StartNew();

            viewModel.SearchTables("T_TABLE_");

            stopwatch.Stop();
            Assert.AreEqual(72000, viewModel.TableRows.Count);
            Assert.IsTrue(stopwatch.Elapsed < System.TimeSpan.FromSeconds(2D),
                "七万二千张表的单次内存搜索耗时：" + stopwatch.Elapsed);
        }

        /// <summary>XMZADD 20260831 构造覆盖源码、实体、人工、一跳关联和孤立规则表的最小测试快照。</summary>
        private static SnapshotData CreateProjectEvidenceSnapshot()
        {
            TableMetadata source = CreateTable("T_SOURCE_USAGE", ConfidenceStatus.CodeEvidence);
            TableMetadata entity = CreateTable("T_ENTITY", ConfidenceStatus.Guessed);
            entity.EntityName = new MetadataValue
            {
                Value = "t_Entity",
                Status = ConfidenceStatus.CodeEvidence,
                SourceType = "EOS源码",
                SourceSummary = "EOS 源码实体类",
                Evidence = new List<EvidenceItem>
                {
                    new EvidenceItem
                    {
                        SourceType = "EOS源码",
                        SourcePath = "ERP/表-类定义/t_Entity.vb",
                        SourceLine = 8,
                        RuleName = "TableNameProperty",
                        RawValue = "T_ENTITY",
                        Explanation = "EOS 实体类表名映射"
                    }
                }
            };
            TableMetadata manual = CreateTable("T_MANUAL", ConfidenceStatus.LocalOverride);
            TableMetadata related = CreateTable("T_RELATED_RULE", ConfidenceStatus.Guessed);
            TableMetadata isolated = CreateTable("T_ISOLATED_RULE", ConfidenceStatus.Guessed);
            source.Relations.Add(new RelationMetadata
            {
                ParentTableName = source.ObjectName,
                ParentFieldName = "FID",
                ChildTableName = related.ObjectName,
                ChildFieldName = "FSOURCEID",
                RelationType = new MetadataValue { Value = "推测：一对多", Status = ConfidenceStatus.Guessed }
            });

            SnapshotData snapshot = new SnapshotData();
            snapshot.Tables.Add(source);
            snapshot.Tables.Add(entity);
            snapshot.Tables.Add(manual);
            snapshot.Tables.Add(related);
            snapshot.Tables.Add(isolated);
            return snapshot;
        }

        /// <summary>XMZADD 20260917 按对象名读取测试快照中的表元数据。</summary>
        private static TableMetadata FindMetadata(SnapshotData snapshot, string objectName)
        {
            for (int index = 0; index < snapshot.Tables.Count; index++)
            {
                if (snapshot.Tables[index] != null && snapshot.Tables[index].ObjectName == objectName)
                {
                    return snapshot.Tables[index];
                }
            }
            Assert.Fail("测试快照中未找到表：" + objectName);
            return null;
        }

        /// <summary>XMZADD 20260831 构造仅含筛选所需属性的测试表，避免测试依赖演示数据。</summary>
        private static TableMetadata CreateTable(string objectName, ConfidenceStatus status)
        {
            return new TableMetadata
            {
                ObjectName = objectName,
                ObjectType = "TABLE",
                ChineseName = new MetadataValue { Value = objectName, Status = status }
            };
        }

        /// <summary>XMZADD 20260903 创建字段知识库证据，用于区分通用词条和项目精确条目。</summary>
        private static MetadataValue CreateKnowledgeFieldValue(string ruleName)
        {
            return new MetadataValue
            {
                Value = "字段名称",
                Status = ConfidenceStatus.KnowledgeBaseEvidence,
                SourceType = "EOS知识库",
                Evidence = new List<EvidenceItem>
                {
                    new EvidenceItem { RuleName = ruleName, SourcePath = "docs_knowledge/02_字段字典/_00_核心字段字典.md" }
                }
            };
        }

        /// <summary>XMZADD 20260831 按物理表名从当前可见列表定位测试对象。</summary>
        private static TableDisplayModel FindTable(MainViewModel viewModel, string objectName)
        {
            for (int index = 0; index < viewModel.TableRows.Count; index++)
            {
                if (viewModel.TableRows[index].ObjectName == objectName)
                {
                    return viewModel.TableRows[index];
                }
            }

            return null;
        }

        /// <summary>XMZADD 20260903 按模块名定位资源树测试节点。</summary>
        private static ModuleNode FindModule(MainViewModel viewModel, string moduleName)
        {
            for (int index = 0; index < viewModel.ModuleNodes.Count; index++)
            {
                if (viewModel.ModuleNodes[index].Name == moduleName)
                {
                    return viewModel.ModuleNodes[index];
                }
            }
            return null;
        }

        /// <summary>XMZADD 20260831 读取项目范围私有缓存，以验证搜索不会因每个字符输入重复重建依据集合。</summary>
        private static object GetProjectScopeCache(MainViewModel viewModel)
        {
            System.Reflection.FieldInfo field = typeof(MainViewModel).GetField("_projectRelatedObjectNames",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(field);
            return field.GetValue(viewModel);
        }

    }
}
