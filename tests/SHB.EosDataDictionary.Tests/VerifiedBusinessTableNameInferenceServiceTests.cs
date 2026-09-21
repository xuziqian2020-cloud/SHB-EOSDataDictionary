using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHB.EosDataDictionary.Models;
using SHB.EosDataDictionary.Services;

namespace SHB.EosDataDictionary.Tests
{
    /// <summary>XMZADD 20260919 验证源码和项目知识已核验的缩写表能够获得完整中文业务参考名。</summary>
    [TestClass]
    public sealed class VerifiedBusinessTableNameInferenceServiceTests
    {
        /// <summary>XMZADD 20260919 验证仓储、计划、销售和质量表按真实业务对象展开缩写。</summary>
        [DataTestMethod]
        [DataRow("Abom_Ex_New", "物料清单扩展新版本")]
        [DataRow("Abom_Ex_Old", "物料清单扩展旧版本")]
        [DataRow("Account_Pallet_Trade_BuSpec", "贸易托盘流水账事业部规格")]
        [DataRow("Account_Pallet_Trade_Item_POI", "贸易托盘流水明细与采购订单明细关联")]
        [DataRow("CII", "客户发票内容")]
        [DataRow("CPO_Item", "客户订单明细")]
        [DataRow("DP_Cast", "发货指令铸件信息")]
        [DataRow("DP_Order_Exe", "发货指令执行记录")]
        [DataRow("DP_Template_Item", "发货指令模板明细")]
        [DataRow("DPO_Reject_Info", "发货指令驳回信息")]
        [DataRow("Ds_Item_Income_POI", "物料入库与采购订单明细关联")]
        [DataRow("DS_Sample_Item_Bu", "样品物料事业部")]
        [DataRow("MRP_Plan_Type", "物料需求计划类型")]
        [DataRow("NQS_NC_Review", "不符合项评审")]
        [DataRow("NQS_NC_Review_HR", "不符合项评审人员")]
        [DataRow("POI_Delivery_State", "采购订单明细交付状态")]
        [DataRow("POI_Delivery_State_Detail", "采购订单明细交付状态明细")]
        [DataRow("PPAP_Define", "生产件批准程序定义")]
        [DataRow("QRQC_Org_Group_Type", "快速质量响应组织组类型")]
        [DataRow("Sale_ReportV2", "销售报表第二版")]
        [DataRow("SV_CII", "销售来源与客户发票内容关联")]
        [DataRow("SV_CIIi", "销售来源与客户发票内容关联明细")]
        [DataRow("WMR2", "仓库月报第二版")]
        public void Apply_VerifiedDomainTables_UseCompleteBusinessNames(
            string tableName, string expected)
        {
            TableMetadata table = CreateTable(tableName);

            new VerifiedBusinessTableNameInferenceService().Apply(
                CreateSnapshot(table));

            Assert.AreEqual(string.Empty, table.ChineseName.Value);
            Assert.AreEqual(expected, table.SuggestedChineseName.Value);
            Assert.AreEqual(ConfidenceStatus.Guessed,
                table.SuggestedChineseName.Status);
            Assert.IsTrue(table.SuggestedChineseName.ConfidenceScore >= 88);
            Assert.AreEqual("VerifiedBusinessTableContext",
                table.SuggestedChineseName.Evidence[0].RuleName);
            Assert.IsFalse(string.IsNullOrWhiteSpace(
                table.SuggestedChineseName.Evidence[0].SourcePath));
        }

        /// <summary>XMZADD 20260919 验证人事、条码、工艺及财务关联表采用界面和 SQL 已证明的名称。</summary>
        [DataTestMethod]
        [DataRow("Code_Box_MW", "雨刮托盘箱码入库明细")]
        [DataRow("FA_IO", "收发存定义")]
        [DataRow("FA_Source", "收发存来源类别")]
        [DataRow("HFR_Plus", "公积金补缴记录")]
        [DataRow("HR_PB", "人事个人业务记录")]
        [DataRow("HR_PB_Define", "人事个人业务定义")]
        [DataRow("P_PEPWC", "工序设备原型工作中心配置")]
        [DataRow("QC_SQC", "供应商来料品质检验记录")]
        [DataRow("SILog", "社保日志")]
        [DataRow("SP_MP_Config", "要货计划与生产计划关联配置")]
        [DataRow("SP_MP_Ini_Inv", "要货计划与生产计划关联期初库存")]
        [DataRow("Supplier_Pay_AP_Month", "供应商应付账款月度记录")]
        public void Apply_VerifiedSourceTables_UseBusinessFacingNames(
            string tableName, string expected)
        {
            TableMetadata table = CreateTable(tableName);

            new VerifiedBusinessTableNameInferenceService().Apply(
                CreateSnapshot(table));

            Assert.AreEqual(expected, table.SuggestedChineseName.Value);
            Assert.IsTrue(IdentifierTranslationService.IsReliableChineseName(
                table.SuggestedChineseName.Value));
            StringAssert.Contains(table.SuggestedChineseName.SourceSummary,
                "核验");
        }

        /// <summary>XMZADD 20260919 验证客户工厂与供应商八步法阶段表使用新人可理解的完整业务名称。</summary>
        [DataTestMethod]
        [DataRow("CF_Base_Program", "客户工厂基础项目")]
        [DataRow("CF_Money_Notice", "客户工厂资金通知")]
        [DataRow("CF_PRM_PRD", "客户工厂项目产品关系")]
        [DataRow("CFSettlementRelation", "客户工厂结算关系")]
        [DataRow("EDisciplines_TA_ICA", "供应商质量八步法临时围堵措施")]
        [DataRow("EDisciplines_ISTA", "供应商质量八步法短期措施")]
        [DataRow("EDisciplines_DRC_WH", "供应商质量八步法根本原因分析")]
        [DataRow("EDisciplines_CPCA", "供应商质量八步法长期措施制定")]
        [DataRow("EDisciplines_PCA", "供应商质量八步法长期措施执行")]
        [DataRow("EDisciplines_PR", "供应商质量八步法防止再发生")]
        [DataRow("EDisciplines_PR_Set", "供应商质量八步法标准化设置")]
        [DataRow("EDisciplines_RTFImage", "供应商质量八步法富文本图片")]
        public void Apply_VerifiedWorkflowTables_ExposeTheirBusinessStage(
            string tableName, string expected)
        {
            TableMetadata table = CreateTable(tableName);

            new VerifiedBusinessTableNameInferenceService().Apply(
                CreateSnapshot(table));

            Assert.AreEqual(expected, table.SuggestedChineseName.Value);
        }

        /// <summary>XMZADD 20260919 验证未核验表不会因相似缩写被误命名。</summary>
        [TestMethod]
        public void Apply_UnknownTable_RemainsPending()
        {
            TableMetadata table = CreateTable("Unknown_CII_Table");

            new VerifiedBusinessTableNameInferenceService().Apply(
                CreateSnapshot(table));

            Assert.IsNull(table.SuggestedChineseName);
        }

        /// <summary>XMZADD 20260919 验证源码核验候选不能覆盖人工锁定的正式表名。</summary>
        [TestMethod]
        public void Apply_ManualOfficialName_IsPreserved()
        {
            TableMetadata table = CreateTable("FA_IO");
            table.ChineseName = new MetadataValue
            {
                Value = "人工确认的收发存定义",
                Status = ConfidenceStatus.LocalOverride,
                IsManualOverride = true,
                IsLocked = true
            };

            new VerifiedBusinessTableNameInferenceService().Apply(
                CreateSnapshot(table));

            Assert.AreEqual("人工确认的收发存定义",
                table.ChineseName.Value);
            Assert.AreEqual("收发存定义",
                table.SuggestedChineseName.Value);
        }

        /// <summary>XMZADD 20260919 创建尚无可靠中文名的最小表测试数据。</summary>
        private static TableMetadata CreateTable(string objectName)
        {
            return new TableMetadata
            {
                ScopeKey = "github-shared-dictionary",
                SchemaName = "dbo",
                ObjectName = objectName,
                ChineseName = new MetadataValue { Value = string.Empty }
            };
        }

        /// <summary>XMZADD 20260919 创建只包含指定表的最小快照。</summary>
        private static SnapshotData CreateSnapshot(params TableMetadata[] tables)
        {
            return new SnapshotData { Tables = new List<TableMetadata>(tables) };
        }
    }
}
