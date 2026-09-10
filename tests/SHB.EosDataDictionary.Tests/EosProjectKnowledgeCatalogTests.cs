using System;
using System.IO;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHB.EosDataDictionary.Services;

namespace SHB.EosDataDictionary.Tests
{
    [TestClass]
    public sealed class EosProjectKnowledgeCatalogTests
    {
        /// <summary>XMZADD 20260903 验证 EOS 项目知识库可提供表名、模块、字段名和业务枚举的精确证据。</summary>
        [TestMethod]
        public void Load_CurrentEosMarkdownFormat_ReturnsBusinessMetadata()
        {
            string root = Path.Combine(Path.GetTempPath(), "shb-eos-kb-" + Guid.NewGuid().ToString("N"));
            string tableDirectory = Path.Combine(root, "01_数据库表结构");
            string fieldDirectory = Path.Combine(root, "02_字段字典");
            Directory.CreateDirectory(tableDirectory);
            Directory.CreateDirectory(fieldDirectory);
            File.WriteAllText(Path.Combine(tableDirectory, "_00_表索引.md"),
                "# EOS 数据库表索引\r\n\r\n" +
                "## 物料/BOM相关\r\n\r\n" +
                "| 表名 | 中文含义 | 备注 |\r\n" +
                "|------|---------|------|\r\n" +
                "| Item | 物料主数据 | 物料编码、名称和规格 |\r\n",
                Encoding.UTF8);
            File.WriteAllText(Path.Combine(fieldDirectory, "_00_核心字段字典.md"),
                "# EOS 数据库核心字段字典\r\n\r\n" +
                "## 一、物料相关字段\r\n\r\n" +
                "| 字段名 | 中文含义 | 出现表 | 数据类型 |\r\n" +
                "|--------|---------|--------|---------|\r\n" +
                "| Item_Name | 物料名称 | Item | nvarchar(100) |\r\n" +
                "| Item_Make_Buy | 自制/外购标识（M=自制, B=外购, P=委外） | Item | nvarchar(1) |\r\n\r\n" +
                "| Version_Only | 版本专用字段 | Item_Version | int |\r\n\r\n" +
                "## 十一、常用枚举/常量说明\r\n\r\n" +
                "| 字段 | 值 | 含义 |\r\n" +
                "|------|----|------|\r\n" +
                "| Item_Make_Buy | M | 自制件 |\r\n" +
                "| Item_Make_Buy | B | 外购件 |\r\n" +
                "| Item_Make_Buy | P | 委外加工件 |\r\n",
                Encoding.UTF8);

            try
            {
                var catalog = new EosProjectKnowledgeCatalog(root);

                EosKnowledgeTableEntry table = catalog.FindTable("dbo.Item");
                EosKnowledgeFieldEntry nameField = catalog.FindField("Item", "Item_Name");
                EosKnowledgeFieldEntry enumField = catalog.FindField("Item", "Item_Make_Buy");

                Assert.IsNotNull(table);
                Assert.AreEqual("物料主数据", table.ChineseName);
                Assert.AreEqual("物料与BOM", table.ModuleName);
                Assert.AreEqual("物料名称", nameField.ChineseName);
                Assert.AreEqual(3, enumField.EnumItems.Count);
                Assert.AreEqual("M", enumField.EnumItems[0].Value);
                Assert.AreEqual("自制件", enumField.EnumItems[0].ChineseName);
                Assert.IsNull(catalog.FindField("Item", "Version_Only"));
                Assert.IsTrue(table.LineNumber > 0);
                Assert.IsTrue(nameField.LineNumber > 0);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        /// <summary>XMZADD 20260907 验证仅将知识库明确声明且中文含义一致的通用字段安全应用到其他业务表。</summary>
        [TestMethod]
        public void FindField_ExplicitConsistentSharedKnowledge_AppliesAcrossBusinessTables()
        {
            string root = CreateFieldKnowledgeFixture(
                "| op_Date | 操作日期 | 多表 | datetime |\r\n" +
                "| op_Date | 操作日期 | 几乎所有业务表 | datetime |\r\n" +
                "| op_Insert_Time | 插入时间 | 通用字段 | datetime |\r\n" +
                "| RID | 关联记录ID | Purchase_Order | int |\r\n");

            try
            {
                var catalog = new EosProjectKnowledgeCatalog(root);

                Assert.AreEqual("操作日期", catalog.FindField("Inventory_Event", "op_Date").ChineseName);
                Assert.AreEqual("插入时间", catalog.FindField("DA_Acceptance", "op_Insert_Time").ChineseName);
                Assert.AreEqual("关联记录ID", catalog.FindField("Purchase_Order", "RID").ChineseName);
                Assert.IsNull(catalog.FindField("Inventory_Event", "RID"));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        /// <summary>XMZADD 20260907 验证同名通用知识存在中文冲突时拒绝全局套用，同时仍优先采用当前表的精确条目。</summary>
        [TestMethod]
        public void FindField_ConflictingSharedKnowledge_ReturnsOnlyExactTableEntry()
        {
            string root = CreateFieldKnowledgeFixture(
                "| Status | 状态 | 通用 | tinyint |\r\n" +
                "| Status | 审核状态 | 多表 | tinyint |\r\n" +
                "| Status | 订单状态 | Purchase_Order | tinyint |\r\n");

            try
            {
                var catalog = new EosProjectKnowledgeCatalog(root);

                Assert.IsNull(catalog.FindField("Inventory_Event", "Status"));
                Assert.AreEqual("订单状态", catalog.FindField("Purchase_Order", "Status").ChineseName);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        /// <summary>XMZADD 20260907 创建只包含核心字段字典的最小知识库夹具。</summary>
        private static string CreateFieldKnowledgeFixture(string rows)
        {
            string root = Path.Combine(Path.GetTempPath(), "shb-eos-shared-kb-" + Guid.NewGuid().ToString("N"));
            string fieldDirectory = Path.Combine(root, "02_字段字典");
            Directory.CreateDirectory(fieldDirectory);
            File.WriteAllText(Path.Combine(fieldDirectory, "_00_核心字段字典.md"),
                "# EOS 数据库核心字段字典\r\n\r\n" +
                "## 一、通用字段\r\n\r\n" +
                "| 字段名 | 中文含义 | 出现表 | 数据类型 |\r\n" +
                "|---|---|---|---|\r\n" + rows,
                Encoding.UTF8);
            return root;
        }
    }
}
