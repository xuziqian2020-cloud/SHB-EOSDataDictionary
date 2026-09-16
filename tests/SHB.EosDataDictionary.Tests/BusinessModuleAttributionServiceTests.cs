using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHB.EosDataDictionary.Models;
using SHB.EosDataDictionary.Services;

namespace SHB.EosDataDictionary.Tests
{
    /// <summary>XMZADD 20260916 验证表的主模块与跨模块使用范围按真实业务证据归属。</summary>
    [TestClass]
    public sealed class BusinessModuleAttributionServiceTests
    {
        /// <summary>XMZADD 20260916 验证 EOS 真实目录名称稳定归并到统一业务模块，不受复合目录词序影响。</summary>
        [DataTestMethod]
        [DataRow("ERP/Item 物料", "物料与BOM")]
        [DataRow("ERP/Account 仓库帐", "仓储与库存")]
        [DataRow("Purchase_PayPlan", "采购管理")]
        [DataRow("Plan_Customer", "计划管理")]
        [DataRow("Customer_Complain", "销售与客户")]
        [DataRow("ERP/Financial", "财务管理")]
        [DataRow("HR", "人力资源")]
        [DataRow("Equipment_Maintain", "设备与工装")]
        [DataRow("Log", "日志与审计")]
        [DataRow("Drawings", "文件与图纸")]
        [DataRow("System_Config", "系统配置")]
        public void NormalizeModuleName_RealEosDirectories_ReturnCanonicalModule(string source, string expected)
        {
            Assert.AreEqual(expected, BusinessModuleAttributionService.NormalizeModuleName(source));
        }

        /// <summary>XMZADD 20260916 验证生成实体目录不能主导模块，仓储业务写入才决定主模块。</summary>
        [TestMethod]
        public void Attribute_EntityDefinitionAndWarehouseWrite_PicksWarehouseAsPrimary()
        {
            TableMetadata table = CreateTable("Account_Storage_IO");
            IList<SourceEvidence> evidence = new List<SourceEvidence>
            {
                CreateEvidence("Account_Storage_IO", "表-类定义", SourceUsageKind.Read,
                    SourceEvidenceOrigin.GeneratedEntity, "ERP/表-类定义/code_Account.vb", 20),
                CreateEvidence("Account_Storage_IO", "Account 仓库帐", SourceUsageKind.Write,
                    SourceEvidenceOrigin.BusinessCode, "ERP/Account 仓库帐/Ac_Title.vb", 3341)
            };

            new BusinessModuleAttributionService().Apply(table, evidence);

            Assert.AreEqual("仓储与库存", table.ModuleName.Value);
            Assert.AreEqual(ConfidenceStatus.CodeEvidence, table.ModuleName.Status);
            AssertContainsModule(table.UsedByModules, "仓储与库存");
            AssertDoesNotContainModule(table.UsedByModules, "其他");
        }

        /// <summary>XMZADD 20260916 验证知识库精确模块高于业务源码投票，同时仍记录其他消费模块。</summary>
        [TestMethod]
        public void Attribute_ExactKnowledgeModule_HasPrimaryPriority()
        {
            TableMetadata table = CreateTable("Inspection_Order");
            table.ModuleName = new MetadataValue
            {
                Value = "质量管理",
                Status = ConfidenceStatus.KnowledgeBaseEvidence,
                ConfidenceScore = 98,
                Evidence = new List<EvidenceItem>
                {
                    new EvidenceItem { RuleName = "ProjectModuleHeading", SourcePath = "docs_knowledge/01_数据库表结构/_00_表索引.md" }
                }
            };
            IList<SourceEvidence> evidence = new List<SourceEvidence>
            {
                CreateEvidence("Inspection_Order", "Purchase", SourceUsageKind.Write,
                    SourceEvidenceOrigin.BusinessCode, "Purchase/InspectionWriter.vb", 30)
            };

            new BusinessModuleAttributionService().Apply(table, evidence);

            Assert.AreEqual("质量管理", table.ModuleName.Value);
            Assert.AreEqual(ConfidenceStatus.KnowledgeBaseEvidence, table.ModuleName.Status);
            AssertContainsModule(table.UsedByModules, "采购管理");
        }

        /// <summary>XMZADD 20260916 验证采购写入高于财务读取，并把两个真实消费模块都保留下来。</summary>
        [TestMethod]
        public void Attribute_PurchaseWriteAndFinanceRead_PicksPurchaseAndKeepsBothConsumers()
        {
            TableMetadata table = CreateTable("Purchase_PayPlan");
            IList<SourceEvidence> evidence = new List<SourceEvidence>
            {
                CreateEvidence("Purchase_PayPlan", "Purchase", SourceUsageKind.Write,
                    SourceEvidenceOrigin.BusinessCode, "Purchase/frmPayPlanEdit.vb", 80),
                CreateEvidence("Purchase_PayPlan", "Financial", SourceUsageKind.Read,
                    SourceEvidenceOrigin.BusinessCode, "ERP/Financial/frmCashPlan.vb", 120)
            };

            new BusinessModuleAttributionService().Apply(table, evidence);

            Assert.AreEqual("采购管理", table.ModuleName.Value);
            AssertContainsModule(table.UsedByModules, "采购管理");
            AssertContainsModule(table.UsedByModules, "财务管理");
        }

        /// <summary>XMZADD 20260916 验证只有生成实体证据时不能把技术目录或旧弱值发布为业务模块。</summary>
        [TestMethod]
        public void Attribute_OnlyGeneratedEntity_KeepsPrimaryPending()
        {
            TableMetadata table = CreateTable("Generated_Only");
            table.ModuleName = new MetadataValue
            {
                Value = "表-类定义",
                Status = ConfidenceStatus.Guessed
            };
            IList<SourceEvidence> evidence = new List<SourceEvidence>
            {
                CreateEvidence("Generated_Only", "表-类定义", SourceUsageKind.Unknown,
                    SourceEvidenceOrigin.GeneratedEntity, "ERP/表-类定义/code_Generated.vb", 10)
            };

            new BusinessModuleAttributionService().Apply(table, evidence);

            Assert.AreEqual(string.Empty, table.ModuleName.Value);
            Assert.AreEqual(ConfidenceStatus.PendingConfirmation, table.ModuleName.Status);
            Assert.AreEqual(0, table.UsedByModules.Count);
        }

        /// <summary>XMZADD 20260916 验证公共数据库层即使执行写入也不能被误判为表的业务归属。</summary>
        [TestMethod]
        public void Attribute_CommonDatabaseLayer_DoesNotVoteForPrimaryModule()
        {
            TableMetadata table = CreateTable("Shared_Table");
            IList<SourceEvidence> evidence = new List<SourceEvidence>
            {
                CreateEvidence("Shared_Table", "DataControl", SourceUsageKind.Write,
                    SourceEvidenceOrigin.BusinessCode, "DataControl/DC.vb", 300)
            };

            new BusinessModuleAttributionService().Apply(table, evidence);

            Assert.AreEqual(string.Empty, table.ModuleName.Value);
            Assert.AreEqual(0, table.UsedByModules.Count);
        }

        /// <summary>XMZADD 20260916 验证多个独立仓储读取位置高于单个财务消费位置。</summary>
        [TestMethod]
        public void Attribute_MultipleIndependentReads_PickRepeatedBusinessModule()
        {
            TableMetadata table = CreateTable("Shared_Inventory");
            IList<SourceEvidence> evidence = new List<SourceEvidence>
            {
                CreateEvidence("Shared_Inventory", "Warehouse_01_Ex", SourceUsageKind.Read,
                    SourceEvidenceOrigin.BusinessCode, "Warehouse_01_Ex/InventoryQuery.vb", 20),
                CreateEvidence("Shared_Inventory", "Account 仓库帐", SourceUsageKind.Read,
                    SourceEvidenceOrigin.BusinessCode, "ERP/Account 仓库帐/InventoryAccount.vb", 35),
                CreateEvidence("Shared_Inventory", "Financial", SourceUsageKind.Read,
                    SourceEvidenceOrigin.BusinessCode, "ERP/Financial/InventoryCost.vb", 18)
            };

            new BusinessModuleAttributionService().Apply(table, evidence);

            Assert.AreEqual("仓储与库存", table.ModuleName.Value);
            AssertContainsModule(table.UsedByModules, "财务管理");
        }

        /// <summary>XMZADD 20260916 验证两个业务模块写入强度完全相同时不按目录字母顺序武断选主模块。</summary>
        [TestMethod]
        public void Attribute_EqualCrossModuleWrites_KeepsPrimaryPending()
        {
            TableMetadata table = CreateTable("Shared_Document");
            IList<SourceEvidence> evidence = new List<SourceEvidence>
            {
                CreateEvidence("Shared_Document", "Purchase", SourceUsageKind.Write,
                    SourceEvidenceOrigin.BusinessCode, "Purchase/DocumentWriter.vb", 20),
                CreateEvidence("Shared_Document", "Financial", SourceUsageKind.Write,
                    SourceEvidenceOrigin.BusinessCode, "ERP/Financial/DocumentWriter.vb", 30)
            };

            new BusinessModuleAttributionService().Apply(table, evidence);

            Assert.AreEqual(string.Empty, table.ModuleName.Value);
            Assert.AreEqual(ConfidenceStatus.PendingConfirmation, table.ModuleName.Status);
            Assert.AreEqual(2, table.UsedByModules.Count);
        }

        /// <summary>XMZADD 20260916 验证 Designer 只能证明模块消费关系，不能单独决定业务所有权。</summary>
        [TestMethod]
        public void Attribute_DesignerOnly_RecordsConsumerButKeepsPrimaryPending()
        {
            TableMetadata table = CreateTable("Sales_Query");
            IList<SourceEvidence> evidence = new List<SourceEvidence>
            {
                CreateEvidence("Sales_Query", "Sales", SourceUsageKind.Display,
                    SourceEvidenceOrigin.Designer, "ERP/Sales/Order.Designer.vb", 90)
            };

            new BusinessModuleAttributionService().Apply(table, evidence);

            Assert.AreEqual(string.Empty, table.ModuleName.Value);
            AssertContainsModule(table.UsedByModules, "销售与客户");
        }

        /// <summary>XMZADD 20260916 创建带初始化集合的最小业务表夹具。</summary>
        private static TableMetadata CreateTable(string objectName)
        {
            return new TableMetadata
            {
                ObjectName = objectName,
                ModuleName = new MetadataValue
                {
                    Value = string.Empty,
                    Status = ConfidenceStatus.PendingConfirmation
                }
            };
        }

        /// <summary>XMZADD 20260916 创建可区分来源角色、模块、读写方向和独立位置的模块证据。</summary>
        private static SourceEvidence CreateEvidence(string objectName, string modulePath,
            SourceUsageKind usageKind, SourceEvidenceOrigin origin, string sourcePath, int sourceLine)
        {
            return new SourceEvidence
            {
                ObjectName = objectName,
                ModulePath = modulePath,
                UsageKind = usageKind,
                Origin = origin,
                Strength = origin == SourceEvidenceOrigin.GeneratedEntity
                    ? SourceEvidenceStrength.Authoritative
                    : SourceEvidenceStrength.DirectBusinessCode,
                Evidence = new EvidenceItem
                {
                    SourceType = origin == SourceEvidenceOrigin.GeneratedEntity ? "EOS生成实体" : "EOS业务源码",
                    SourcePath = sourcePath,
                    SourceLine = sourceLine,
                    RuleName = origin == SourceEvidenceOrigin.GeneratedEntity ? "EntityClassConvention" : "SqlTableUsage"
                }
            };
        }

        /// <summary>XMZADD 20260916 断言模块集合包含指定规范模块。</summary>
        private static void AssertContainsModule(IList<MetadataValue> modules, string expected)
        {
            for (int index = 0; modules != null && index < modules.Count; index++)
            {
                if (modules[index] != null && string.Equals(modules[index].Value, expected,
                        StringComparison.Ordinal))
                {
                    return;
                }
            }
            Assert.Fail("未找到被使用模块：" + expected);
        }

        /// <summary>XMZADD 20260916 断言模块集合不包含技术目录归并结果。</summary>
        private static void AssertDoesNotContainModule(IList<MetadataValue> modules, string unexpected)
        {
            for (int index = 0; modules != null && index < modules.Count; index++)
            {
                Assert.AreNotEqual(unexpected, modules[index] == null ? null : modules[index].Value);
            }
        }
    }
}
