using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHB.EosDataDictionary.Models;
using SHB.EosDataDictionary.Services;
using SHB.EosDataDictionary.ViewModels;

namespace SHB.EosDataDictionary.Tests
{
    /// <summary>XMZADD 20260828 验证表格逐列模糊筛选只匹配指定列并正确组合多个条件。</summary>
    [TestClass]
    public sealed class ColumnFilterServiceTests
    {
        /// <summary>XMZADD 20260828 验证中文和英文表名均支持不区分大小写的包含匹配。</summary>
        [TestMethod]
        public void MatchesTable_UsesSubstringAndIgnoresCase()
        {
            ColumnFilterService service = new ColumnFilterService();
            TableDisplayModel row = new TableDisplayModel(FindTable("T_PUR_ORDER"));
            var filters = new Dictionary<string, string>();

            filters.Add("ChineseName", "采购");
            Assert.IsTrue(service.MatchesTable(row, filters));

            filters.Clear();
            filters.Add("ObjectName", "t_pur_order");
            Assert.IsTrue(service.MatchesTable(row, filters));
        }

        /// <summary>XMZADD 20260828 验证多个表列条件采用 AND 组合，避免跨列误命中。</summary>
        [TestMethod]
        public void MatchesTable_CombinesActiveColumnsWithAnd()
        {
            ColumnFilterService service = new ColumnFilterService();
            TableDisplayModel row = new TableDisplayModel(FindTable("T_PUR_ORDER"));
            var filters = new Dictionary<string, string>();

            filters.Add("ChineseName", "采购");
            filters.Add("ObjectName", "supplier");

            Assert.IsFalse(service.MatchesTable(row, filters));
        }

        /// <summary>XMZADD 20260828 验证清空某列筛选后不会保留该列的历史过滤条件。</summary>
        [TestMethod]
        public void MatchesTable_IgnoresEmptyColumnFilter()
        {
            ColumnFilterService service = new ColumnFilterService();
            TableDisplayModel row = new TableDisplayModel(FindTable("T_PUR_ORDER"));
            var filters = new Dictionary<string, string>();

            filters.Add("ChineseName", "采购");
            filters.Add("ObjectName", string.Empty);

            Assert.IsTrue(service.MatchesTable(row, filters));
        }

        /// <summary>XMZADD 20260917 验证参考译名、冲突状态和消费模块均可独立执行表头筛选。</summary>
        [TestMethod]
        public void MatchesTable_FiltersSuggestionConflictAndUsedModules()
        {
            var table = new TableMetadata
            {
                ObjectName = "T_STORAGE",
                SuggestedChineseName = new MetadataValue { Value = "仓储区定义" }
            };
            table.AlternativeChineseNames.Add(new MetadataValue { Value = "存储区域定义" });
            table.UsedByModules.Add(new MetadataValue { Value = "仓储与库存" });
            var row = new TableDisplayModel(table);
            var service = new ColumnFilterService();

            Assert.IsTrue(service.MatchesTable(row,
                new Dictionary<string, string> { { "SuggestedChineseName", "仓储区" } }));
            Assert.IsTrue(service.MatchesTable(row,
                new Dictionary<string, string> { { "ConflictText", "有冲突" } }));
            Assert.IsTrue(service.MatchesTable(row,
                new Dictionary<string, string> { { "UsedByModulesText", "库存" } }));
        }

        /// <summary>XMZADD 20260828 验证字段筛选仅从指定字段属性中执行模糊匹配。</summary>
        [TestMethod]
        public void MatchesField_UsesSpecifiedColumnOnly()
        {
            ColumnFilterService service = new ColumnFilterService();
            FieldDisplayModel row = new FieldDisplayModel(FindField("T_PUR_ORDER", "FNUMBER"));
            var filters = new Dictionary<string, string>();

            filters.Add("FieldName", "number");
            Assert.IsTrue(service.MatchesField(row, filters));

            filters.Clear();
            filters.Add("DataType", "订单编号");
            Assert.IsFalse(service.MatchesField(row, filters));
        }

        /// <summary>XMZADD 20260831 验证新增键列可按主键、外键或推测外键筛选。</summary>
        [TestMethod]
        public void MatchesField_FiltersKeyText()
        {
            var service = new ColumnFilterService();
            var row = new FieldDisplayModel(new FieldMetadata { FieldName = "Img_ID", IsPrimaryKey = true });
            var filters = new Dictionary<string, string> { { "KeyText", "主键" } };

            Assert.IsTrue(service.MatchesField(row, filters));
        }

        /// <summary>XMZADD 20260917 验证字段参考译名、冲突状态和实际使用状态可按列筛选。</summary>
        [TestMethod]
        public void MatchesField_FiltersSuggestionConflictAndActualUsage()
        {
            var field = new FieldMetadata
            {
                FieldName = "Owner_Company_ID",
                SuggestedChineseName = new MetadataValue { Value = "货主公司ID" },
                Usage = new MetadataValue { Value = "业务代码读取" }
            };
            field.AlternativeChineseNames.Add(new MetadataValue { Value = "所属公司ID" });
            var row = new FieldDisplayModel(field);
            var service = new ColumnFilterService();

            Assert.IsTrue(service.MatchesField(row,
                new Dictionary<string, string> { { "SuggestedChineseName", "货主" } }));
            Assert.IsTrue(service.MatchesField(row,
                new Dictionary<string, string> { { "ConflictText", "有冲突" } }));
            Assert.IsTrue(service.MatchesField(row,
                new Dictionary<string, string> { { "ActualUsageText", "已使用" } }));
        }

        /// <summary>XMZADD 20260828 验证关联关系列表按父表列单独筛选。</summary>
        [TestMethod]
        public void MatchesRelation_UsesSpecifiedColumnOnly()
        {
            ColumnFilterService service = new ColumnFilterService();
            RelationDisplayModel row = new RelationDisplayModel(FindTable("T_PUR_ORDER").Relations[0]);
            var filters = new Dictionary<string, string>();

            filters.Add("ParentTableName", "pur_order");
            Assert.IsTrue(service.MatchesRelation(row, filters));

            filters.Clear();
            filters.Add("RelationType", "不存在的关系");
            Assert.IsFalse(service.MatchesRelation(row, filters));
        }

        /// <summary>XMZADD 20260828 从演示快照中取得指定表，确保筛选测试使用真实显示模型。</summary>
        private static TableMetadata FindTable(string objectName)
        {
            SnapshotData snapshot = PreviewDataFactory.Create();
            for (int i = 0; i < snapshot.Tables.Count; i++)
            {
                if (snapshot.Tables[i].ObjectName == objectName)
                {
                    return snapshot.Tables[i];
                }
            }
            Assert.Fail("演示快照中未找到表：" + objectName);
            return null;
        }

        /// <summary>XMZADD 20260828 从指定演示表中取得字段，避免测试自行猜测字段元数据。</summary>
        private static FieldMetadata FindField(string objectName, string fieldName)
        {
            TableMetadata table = FindTable(objectName);
            for (int i = 0; i < table.Fields.Count; i++)
            {
                if (table.Fields[i].FieldName == fieldName)
                {
                    return table.Fields[i];
                }
            }
            Assert.Fail("演示快照中未找到字段：" + fieldName);
            return null;
        }
    }
}
