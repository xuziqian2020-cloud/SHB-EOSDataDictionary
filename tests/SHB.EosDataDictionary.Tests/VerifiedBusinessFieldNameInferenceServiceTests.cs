using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHB.EosDataDictionary.Models;
using SHB.EosDataDictionary.Services;

namespace SHB.EosDataDictionary.Tests
{
    /// <summary>XMZADD 20260917 验证仅在源码已核验业务上下文中补全多义字段的中文参考名。</summary>
    [TestClass]
    public sealed class VerifiedBusinessFieldNameInferenceServiceTests
    {
        /// <summary>XMZADD 20260917 验证跨表语义稳定字段使用源码证明的自然中文名并保留证据路径。</summary>
        [DataTestMethod]
        [DataRow("Shift", "ALF_ID", "生产线ID")]
        [DataRow("CC_Complain_ActionPlan", "CCC_ID", "客户投诉ID")]
        [DataRow("MPI_Compare_Result", "MPIC_ID", "计划对比ID")]
        [DataRow("DS_Item_Income", "IBS_N1", "一级包装数量")]
        [DataRow("Supplier_Material", "IBS_N2", "二级包装数量")]
        [DataRow("SM_Price_History", "IBS_N3", "三级包装数量")]
        [DataRow("DS_Item_Income", "WSS_Case", "入库业务场景")]
        [DataRow("MPIWC_Done", "PSegment_Des", "工序段描述")]
        [DataRow("P_B", "Solo", "是否工序独立执行")]
        [DataRow("LoginLog", "PC", "计算机名")]
        [DataRow("Account_AL", "RID", "关联记录ID")]
        [DataRow("Account_Storage_Event", "Ac_ID", "流水账记录ID")]
        [DataRow("Ac_POI", "Ac_ID", "流水账记录ID")]
        [DataRow("DP_Order_Exe", "Ac_ID", "执行流水ID")]
        [DataRow("Access", "Ac_ID", "访问记录ID")]
        [DataRow("FacAuditLog", "Ac_ID", "设施审核日志ID")]
        [DataRow("P_MPIWC", "PID", "工序ID")]
        [DataRow("Item_KBPM", "PID", "工序ID")]
        [DataRow("MPI_Copy", "LID", "清单ID")]
        [DataRow("M_5S", "LID", "检查清单ID")]
        [DataRow("CapacityReportItem", "CR_ID", "产能报告ID")]
        [DataRow("WMCR", "CR_ID", "五金月度领用报表ID")]
        [DataRow("MM_Node", "Map_ID", "思维导图ID")]
        [DataRow("Supplier_Material_log", "SM_ID", "供应商物料关系ID")]
        [DataRow("MM_Node_Task", "TID", "任务ID")]
        [DataRow("CF_Base_Program", "CFB_ID", "客户工厂基础资料ID")]
        [DataRow("File_Lib_Permit", "FSS_ID", "文件共享范围ID")]
        [DataRow("SP_Folder_CAD_Ex_Version_Item", "SPFV_ID", "计划文件版本ID")]
        [DataRow("Facility", "PEP_ID", "设备原型ID")]
        [DataRow("MPIWC_DONE_Flow_Card", "MIDFC_ID", "生产流转卡ID")]
        [DataRow("OQC_Detail", "OQC_ID", "出货质量检验记录ID")]
        [DataRow("ERP_Module_Ver", "EM_ID", "ERP模块ID")]
        [DataRow("QRQC_Org_Manager", "QO_ID", "快速质量响应组织ID")]
        [DataRow("DS_Item_Income", "DIS_ID", "入库状态ID")]
        [DataRow("Code_Motor", "DID", "产品条码定义ID")]
        [DataRow("Code_Motor", "Cap", "条码前缀字符")]
        [DataRow("OQC", "QT_ID", "质量检验类型ID")]
        [DataRow("WPWorker_Account", "LPK_ID", "计薪方式ID")]
        [DataRow("WPWorker_Account_CZ", "PMID", "执行工序ID")]
        [DataRow("Pay_ChangeMemo", "Asure", "是否财务核算")]
        public void Apply_VerifiedStableRules_CreateAuditableSuggestion(
            string tableName, string fieldName, string expected)
        {
            FieldMetadata field = CreateField(fieldName);
            TableMetadata table = CreateTable(tableName, field);

            new VerifiedBusinessFieldNameInferenceService().Apply(CreateSnapshot(table));

            Assert.AreEqual(string.Empty, field.ChineseName.Value);
            Assert.AreEqual(expected, field.SuggestedChineseName.Value);
            Assert.AreEqual(ConfidenceStatus.Guessed, field.SuggestedChineseName.Status);
            Assert.IsTrue(field.SuggestedChineseName.ConfidenceScore >= 85);
            Assert.AreEqual("VerifiedBusinessFieldContext",
                field.SuggestedChineseName.Evidence[0].RuleName);
            Assert.IsFalse(string.IsNullOrWhiteSpace(
                field.SuggestedChineseName.Evidence[0].SourcePath));
        }

        /// <summary>XMZADD 20260917 验证多义缩写按所属表族解释，不能把同一缩写全库套成一个含义。</summary>
        [DataTestMethod]
        [DataRow("EDisciplines_Main", "EDS_ID", "供应商质量问题报告ID")]
        [DataRow("DP_Order", "DO_ID", "发货指令ID")]
        [DataRow("DeliveryOrder_Box", "DO_ID", "发货指令ID")]
        [DataRow("Document_Type", "DT_ID", "文档类型ID")]
        [DataRow("Defect_Treat", "DT_ID", "缺陷处理ID")]
        [DataRow("Account_Delivery", "DT_ID", "运输方式ID")]
        [DataRow("DP_Template", "DPT_ID", "发货指令模板ID")]
        [DataRow("SP_Folder_CAD_Ex_Version_Item", "SPF_ID", "计划文件ID")]
        [DataRow("TTask_Msg_File", "TTM_ID", "任务消息ID")]
        [DataRow("Manage_PC", "MPC_ID", "计算机管理记录ID")]
        [DataRow("MPI", "MPC_ID", "生产计划产品类别ID")]
        [DataRow("Purchase_Order", "Mass", "是否批量订单")]
        [DataRow("Account_Delivery", "Mass", "批量或样品标识")]
        [DataRow("Bu_Plan_Goal", "Ye", "年份")]
        [DataRow("Bu_Plan_Goal", "Mo", "月份")]
        [DataRow("MPI_Month", "Y", "年份")]
        [DataRow("MPI_Month", "M", "月份")]
        [DataRow("MPI_Month", "D", "日期")]
        [DataRow("Mould_StatusTransaction_Sheet", "MST_ID", "模具状态流转类型ID")]
        [DataRow("Consign", "CID", "委托记录ID")]
        [DataRow("Currency_Rate_Log", "CID", "币别ID")]
        [DataRow("Lead_Time", "LT_ID", "提前期ID")]
        [DataRow("Supplier_Material_Log_Type", "LT_ID", "供应商物料日志类型ID")]
        [DataRow("Control_Parameter", "CP_ID", "控制参数ID")]
        [DataRow("GMreport_Month_Item", "GM_ID", "五金月报ID")]
        [DataRow("GM_Item_KisCode", "GM_ID", "五金物料金蝶编码映射ID")]
        [DataRow("DA_Fi_Budget_Log", "FB_ID", "财务预算ID")]
        [DataRow("QC_CustomerFB", "FB_ID", "客户质量反馈ID")]
        [DataRow("Cu_Invoice_AR_FollowUp", "CI_ID", "客户发票ID")]
        public void Apply_ContextSpecificRules_UseTableFamilyMeaning(
            string tableName, string fieldName, string expected)
        {
            FieldMetadata field = CreateField(fieldName);

            new VerifiedBusinessFieldNameInferenceService().Apply(
                CreateSnapshot(CreateTable(tableName, field)));

            Assert.AreEqual(expected, field.SuggestedChineseName.Value);
            StringAssert.Contains(field.SuggestedChineseName.SourceSummary, "源码");
        }

        /// <summary>XMZADD 20260917 验证源码标题、计算公式和对象关系能够补全实际使用的业务值字段。</summary>
        [DataTestMethod]
        [DataRow("Account_01_Month_Report", "AM_SaveCName", "保存计算机名")]
        [DataRow("Account_Delivery", "ETA", "预计到货天数")]
        [DataRow("Account_Pallet_Trade_Item", "Leave", "是否已出库")]
        [DataRow("Bu_Plan_Goal", "ExpDP", "销售期望")]
        [DataRow("Bu_Plan_Goal", "ExpSP", "要货期望")]
        [DataRow("Bu_Plan_Goal", "ExpMP", "生产期望")]
        [DataRow("CheckInventory", "CI_Ye", "盘点年份")]
        [DataRow("CheckInventory", "CI_Mo", "盘点月份")]
        [DataRow("CPO_R", "LastDML", "最后数据操作类型")]
        [DataRow("Customer", "Cu_Abb", "客户简称")]
        [DataRow("DS_Purchase", "DSP_BackIfo", "采购申请备注")]
        [DataRow("DS_Sample", "DSS_Lastest_Act", "最近一次操作")]
        [DataRow("DS_Sample_Item", "Lastest_Income", "最近一次入库记录ID")]
        [DataRow("FQC", "FQC_Sample_NC", "抽样不合格数量")]
        [DataRow("Item", "CustomerTaxCode_MY", "马来西亚海关税则编码")]
        [DataRow("Line_Issue", "Liner_Resonded", "线长是否已响应")]
        [DataRow("NR_File", "Downed", "是否已下载")]
        [DataRow("NR_File", "FV", "文件版本")]
        [DataRow("PFT", "Ext", "文件扩展名")]
        [DataRow("PFT", "FF", "是否文件夹")]
        [DataRow("PhoneVerificationCodes", "Clicks", "点击次数")]
        [DataRow("SPI_Paste", "SPIP_OP", "操作类型")]
        [DataRow("SPI_Paste", "SPIP_op_TIme", "操作时间")]
        [DataRow("SP_Audit", "Source_Mass", "来源计划类型")]
        [DataRow("SqlAuditHeader", "WindowsIdentity", "操作系统登录身份")]
        [DataRow("Supplier_Manu_Lot_Item", "Creatime", "创建时间")]
        [DataRow("TTask_Msg", "Content_RTF", "富文本内容")]
        [DataRow("TTask_Msg", "ReaderList", "阅读人列表")]
        [DataRow("TTask_Ver", "Exer_Confirm_Method", "执行人确认方式")]
        [DataRow("WMCR", "Savedate", "保存时间")]
        public void Apply_VerifiedBusinessValueRules_UseCodeMeaning(
            string tableName, string fieldName, string expected)
        {
            FieldMetadata field = CreateField(fieldName);

            new VerifiedBusinessFieldNameInferenceService().Apply(
                CreateSnapshot(CreateTable(tableName, field)));

            Assert.AreEqual(expected, field.SuggestedChineseName.Value);
            Assert.IsTrue(IdentifierTranslationService.IsReliableChineseName(
                field.SuggestedChineseName.Value));
        }

        /// <summary>XMZADD 20260917 验证账套、计划、公积金、文件和成本字段按业务代码中的完整含义命名。</summary>
        [DataTestMethod]
        [DataRow("Bu_Ac", "Wujin", "是否五金账")]
        [DataRow("DataOPLocalTable", "DML", "数据操作类型")]
        [DataRow("listp_Log", "DML", "数据操作类型")]
        [DataRow("DP_Cast", "Y", "年份")]
        [DataRow("DP_Cast", "W", "周次")]
        [DataRow("DP_Cast", "SD", "开始日期")]
        [DataRow("DP_Cast", "ED", "结束日期")]
        [DataRow("EditMsg", "IDVal", "目标记录ID")]
        [DataRow("ERP_OA_Definition_Table", "indexdesc", "字段释义")]
        [DataRow("FC_Month", "Cal_Ye", "核算年份")]
        [DataRow("FC_Month", "Cal_Mo", "核算月份")]
        [DataRow("HFR", "HF_PxP", "补充公积金比例")]
        [DataRow("HouseFund", "HF_PxP", "补充公积金比例")]
        [DataRow("HFR_Plus", "HF_P_x", "个人补充公积金金额")]
        [DataRow("HFR_Plus", "HF_E_x", "企业补充公积金金额")]
        [DataRow("InvForOA_Synchronize_Time", "ST", "下次同步执行时间")]
        [DataRow("Item_Half_BBP", "BBP_BOM", "物料清单不变价构成金额")]
        [DataRow("Item_Half_BBP", "BBP_BOM_Fuliao", "辅料不变价构成金额")]
        [DataRow("ListIcon", "Icon16", "16像素图标")]
        [DataRow("PFT_V", "VF_Creater", "版本创建人ID")]
        [DataRow("PFT_V", "VF_FF", "是否文件夹")]
        [DataRow("P_MPS", "MPS_Des", "主生产计划描述")]
        [DataRow("QC_CustomerFB", "FB_8D", "是否有八步法报告")]
        [DataRow("SPV", "SPV", "标准工序版本")]
        [DataRow("Supplier_Material", "BD", "采购方式")]
        [DataRow("SM_Price_History", "BD", "采购方式")]
        [DataRow("Supplier_Material", "PSW", "是否批量采购")]
        [DataRow("SM_Price_History", "PSW", "是否批量采购")]
        [DataRow("SM_Price_History", "SMR", "是否纳入供应商报表")]
        [DataRow("Supplier_WMS_Label_AHSHB", "WMS_SKU", "仓储系统物料编码")]
        [DataRow("Supplier_WMS_ShipBatch_Item_AHSHB", "WMS_SKU", "仓储系统物料编码")]
        [DataRow("WMRi_Item", "V_LM", "期初结存金额")]
        public void Apply_AdditionalVerifiedValueRules_UseCompleteBusinessMeaning(
            string tableName, string fieldName, string expected)
        {
            FieldMetadata field = CreateField(fieldName);

            new VerifiedBusinessFieldNameInferenceService().Apply(
                CreateSnapshot(CreateTable(tableName, field)));

            Assert.AreEqual(expected, field.SuggestedChineseName.Value);
            Assert.IsTrue(IdentifierTranslationService.IsReliableChineseName(
                field.SuggestedChineseName.Value));
        }

        /// <summary>XMZADD 20260918 验证经验教训、质量试验、社保和扫码防错字段采用界面已明确的业务名称。</summary>
        [DataTestMethod]
        [DataRow("LessonsLearned", "PDRC", "问题描述及根本原因")]
        [DataRow("LessonsLearned", "LLFNP", "新项目经验教训")]
        [DataRow("LessonsLearned", "P_D", "设计")]
        [DataRow("LessonsLearned", "P_P", "过程")]
        [DataRow("LessonsLearned", "YY", "应用范围")]
        [DataRow("Quality_Project", "QP_1", "适用范围")]
        [DataRow("Quality_Project", "QP_2", "试验目的")]
        [DataRow("Quality_Project", "QP_3", "试验步骤")]
        [DataRow("Quality_Project", "QP_4", "试验结果")]
        [DataRow("Quality_Project", "QP_5", "结论")]
        [DataRow("ExpenseReimbursement", "ER_P_C", "收款或付款人")]
        [DataRow("Facility", "CMK", "设备能力指数")]
        [DataRow("RD_HR_Month", "SS", "是否缴纳社保")]
        [DataRow("QR_Log", "QR1", "外箱条码")]
        [DataRow("QR_Log", "QR2", "内箱条码")]
        [DataRow("QR_Log", "PF", "内外箱条码是否匹配")]
        public void Apply_VerifiedUiCaptionRules_UseBusinessFacingChineseNames(
            string tableName, string fieldName, string expected)
        {
            FieldMetadata field = CreateField(fieldName);

            new VerifiedBusinessFieldNameInferenceService().Apply(
                CreateSnapshot(CreateTable(tableName, field)));

            Assert.AreEqual(expected, field.SuggestedChineseName.Value);
            Assert.IsTrue(IdentifierTranslationService.IsReliableChineseName(
                field.SuggestedChineseName.Value));
        }

        /// <summary>XMZADD 20260918 验证剩余业务值字段依据界面、赋值链和计算公式采用完整中文名称。</summary>
        [DataTestMethod]
        [DataRow("Cu_Pr_Month_List", "SY", "起始年份")]
        [DataRow("Cu_Pr_Month_List", "EY", "结束年份")]
        [DataRow("Customer_Situation", "NCIR", "不合格品评审报告编号")]
        [DataRow("CZ_Out", "SB_P_N", "单张钢板出产数")]
        [DataRow("DS_Item_Income_Item", "DIIS", "来料送仓记录ID")]
        [DataRow("EDisciplines_PR_Set", "ED_PRS_FMEA", "是否更新失效模式及影响分析")]
        [DataRow("HrmResource", "seclevel", "办公自动化系统安全级别")]
        [DataRow("LM_JL", "Qry", "数量")]
        [DataRow("LM_Notes", "Kids", "操作类型")]
        [DataRow("M_ST_A", "MST_LO", "线长ID")]
        [DataRow("M_TP", "MTP_WDs", "工作日天数")]
        [DataRow("Mold_Transfer_Tracing", "Woker", "模具负责人ID")]
        [DataRow("Mold_Type_Cycle", "T1", "首次试模交付周期天数")]
        [DataRow("Mold_Development_Tracking", "T1", "首次试模交付周期天数")]
        [DataRow("OAOP", "OP_Json", "回传办公自动化系统数据")]
        [DataRow("P", "POH", "小时产能")]
        [DataRow("Product_Assembly_Line", "POH", "计划每小时产量")]
        [DataRow("PickPalletInLog", "Insql", "入托盘数据库语句")]
        [DataRow("PickPalletInLog", "Deletesql", "出托盘数据库语句")]
        [DataRow("Product_BBP", "BBP", "产品不变价")]
        [DataRow("Program", "RuiAn", "是否按瑞安结算")]
        [DataRow("Purchaseplan_Save_Temp", "St_Q", "暂存发货数量")]
        [DataRow("Supplier", "YG_Ven_Kiscode", "安徽金蝶供应商编码")]
        [DataRow("Supplier_Manu_Lot_FBDC", "Kiscode", "金蝶物料编码")]
        [DataRow("Supplier_Material", "KISTYPE", "金蝶账套类型")]
        [DataRow("WMRi_Item", "P", "不变价")]
        [DataRow("WMRi_Item", "P2", "实际材料成本单价")]
        [DataRow("ztemp_purchase", "Q1", "前一个月发货数量")]
        [DataRow("ztemp_purchase", "Q2", "前两个月发货数量")]
        [DataRow("Country", "Abbreviation", "国家缩写")]
        [DataRow("Country", "Continent", "大洲ID")]
        public void Apply_RemainingVerifiedValueRules_UseSourceProvenMeaning(
            string tableName, string fieldName, string expected)
        {
            FieldMetadata field = CreateField(fieldName);

            new VerifiedBusinessFieldNameInferenceService().Apply(
                CreateSnapshot(CreateTable(tableName, field)));

            Assert.AreEqual(expected, field.SuggestedChineseName.Value);
            Assert.IsTrue(IdentifierTranslationService.IsReliableChineseName(
                field.SuggestedChineseName.Value));
        }

        /// <summary>XMZADD 20260917 验证汽车项目节点和控制计划缩写按真实业务标题展开为中文。</summary>
        [DataTestMethod]
        [DataRow("CF_PRM_PRD", "DV", "设计验证日期")]
        [DataRow("CF_PRM_PRD", "EMC", "电磁兼容验证日期")]
        [DataRow("CF_PRM_PRD", "PPAP", "生产件批准日期")]
        [DataRow("CF_PRM_PRD", "PV", "生产验证日期")]
        [DataRow("CF_PRM_PRD", "SOPplus3M", "量产后三个月日期")]
        [DataRow("CF_PRM_PRD", "T0", "首次试模日期")]
        [DataRow("P_CP", "M_PPT", "产品或过程规范及公差")]
        [DataRow("P_CP", "M_EMT", "评价方法与技术")]
        [DataRow("P_CP", "M_S_S", "样本数量")]
        [DataRow("P_CP", "M_S_F", "抽样频率")]
        [DataRow("P_CP", "M_C_M", "控制方法")]
        [DataRow("P_CP", "M_R", "责任人")]
        [DataRow("P_CP", "RP", "反应计划")]
        [DataRow("P_CP", "SCC", "特殊特性分类")]
        public void Apply_VerifiedDomainAbbreviations_AreExpanded(
            string tableName, string fieldName, string expected)
        {
            FieldMetadata field = CreateField(fieldName);

            new VerifiedBusinessFieldNameInferenceService().Apply(
                CreateSnapshot(CreateTable(tableName, field)));

            Assert.AreEqual(expected, field.SuggestedChineseName.Value);
        }

        /// <summary>XMZADD 20260917 验证成本年份字段按所在业务对象区分金额和采购价来源标记。</summary>
        [DataTestMethod]
        [DataRow("Cost_Bom", "BOMF2024", "2024年BOM成本金额")]
        [DataRow("Cost_Bom", "BOMF2024PP", "2024年成本是否取采购价")]
        [DataRow("Item_Cost", "F2024", "2024年物料成本金额")]
        [DataRow("Product_Cost_Ver", "F2018", "2018年产品成本金额")]
        public void Apply_AnnualCostFields_PreserveObjectAndYearMeaning(
            string tableName, string fieldName, string expected)
        {
            FieldMetadata field = CreateField(fieldName);

            new VerifiedBusinessFieldNameInferenceService().Apply(
                CreateSnapshot(CreateTable(tableName, field)));

            Assert.AreEqual(expected, field.SuggestedChineseName.Value);
        }

        /// <summary>XMZADD 20260917 验证界面布局坐标和金蝶基础资料字段使用新人可理解的完整中文。</summary>
        [DataTestMethod]
        [DataRow("P_WC", "Location_X", "横向坐标")]
        [DataRow("P_WC_Station", "Location_Y", "纵向坐标")]
        [DataRow("Kis_Stock", "FSTOCKID", "金蝶仓库内码")]
        [DataRow("Kis_Stock", "FSTOCKNAME", "金蝶仓库名称")]
        [DataRow("Kis_Stock", "FSTOCKNUMBER", "金蝶仓库编码")]
        [DataRow("Kis_Material_Category", "FCATEGORYID", "金蝶物料类别内码")]
        [DataRow("Kis_Item_Allocate", "FMASTERID", "金蝶物料主数据内码")]
        [DataRow("Kis_Supplier", "FID", "金蝶供应商内码")]
        public void Apply_VerifiedLayoutAndKingdeeFields_UseCompleteChinese(
            string tableName, string fieldName, string expected)
        {
            FieldMetadata field = CreateField(fieldName);

            new VerifiedBusinessFieldNameInferenceService().Apply(
                CreateSnapshot(CreateTable(tableName, field)));

            Assert.AreEqual(expected, field.SuggestedChineseName.Value);
        }

        /// <summary>XMZADD 20260919 验证仓储、采购、客户和合同主外键按源码关系补全业务对象名称。</summary>
        [DataTestMethod]
        [DataRow("ABOMZ_DirtyIVID", "AZD_ID", "待更新物料版本记录ID")]
        [DataRow("AcBT", "AcBT_ID", "日记账账本科目关联ID")]
        [DataRow("Account_Packing_Item", "AP_ID", "包装记录ID")]
        [DataRow("Account_Pallet_Trade_Item", "Leave_DII_ID", "出库生成的物料入库记录ID")]
        [DataRow("Account_Pallet_Trade_Item", "Leave_DIII_ID", "出库生成的物料入库明细ID")]
        [DataRow("Account_Sheet", "DO_ID", "发货指令ID")]
        [DataRow("Account_Sheet", "SID_UID", "流水账单据全局唯一标识")]
        [DataRow("Account_Sheet_Trade_Pallet", "PI_ID", "贸易托盘流水明细ID")]
        [DataRow("Account_Sheet_WJ", "SM_ID", "供应商物料关系ID")]
        [DataRow("Account_Storage_Part2_Event", "ASE_ID2", "对应调拨事件ID")]
        [DataRow("Ac_Kis_Extra_Link", "FID", "金蝶单据内码")]
        [DataRow("Ac_POI", "AP_ID", "流水账与采购订单明细关联ID")]
        [DataRow("Ac_Title", "Ace_ID", "日记账相关方类别")]
        [DataRow("Asset_Requisition", "R_ID", "领用归还类型")]
        [DataRow("AWarehouse_Overdue_Alarm_Record", "Sender_HR_ID", "发送人ID")]
        [DataRow("Block_Log", "Spid", "数据库会话进程ID")]
        [DataRow("Bu_Period_Manu_Item", "PM_ID", "事业部生产期间ID")]
        [DataRow("CC_Complain", "CCT_ID", "客户投诉类型ID")]
        [DataRow("CC_Complain_ActionPlan_Executer", "CCA_ID", "客户投诉行动计划ID")]
        [DataRow("CC_Complain_Parts", "Related_CCPS_ID", "关联投诉部件ID")]
        [DataRow("ContainShipGap_Rework_Snapshot_Item", "CSGS_ID", "发运缺口返工快照ID")]
        [DataRow("Contract", "CMT_ID", "合同模板类型ID")]
        [DataRow("Contract_Invoice", "CI_SI_ID", "供应商发票ID")]
        [DataRow("Cost_Bom", "CBE_ID", "成本BOM额外物料ID")]
        [DataRow("Cost_BOM_WW", "AZ_ID", "成本BOM明细ID")]
        [DataRow("Cost_MR", "MR_ID", "成本月报ID")]
        [DataRow("Cost_MR_Part", "MRI_ID", "成本月报物料明细ID")]
        [DataRow("CPO_R", "CRx_ID", "客户订单需求记录ID")]
        [DataRow("CPP_Score_Mark", "CSM_ID", "客户项目产品评分标记ID")]
        [DataRow("CPP_Score_Mark", "PPS_ID", "项目产品系列ID")]
        [DataRow("Cu_Business_Fee_Pay", "CU_SV_ID", "关联销售款ID")]
        [DataRow("Cu_Invoice_Analysis_Remark", "AR_ID", "客户发票分析备注ID")]
        [DataRow("Cu_PRi", "CP_ID", "客户价格ID")]
        [DataRow("Cu_Pr_Month_List", "CPML_ID", "客户价格月度清单ID")]
        [DataRow("Cu_Pr_Month_List_Item", "CPMLI_ID", "客户价格月度清单明细ID")]
        [DataRow("Cu_Pr_Month_List_Item", "CPML_ID", "客户价格月度清单ID")]
        [DataRow("Cu_Pr_Month_List_Item", "CP_ID", "客户价格ID")]
        [DataRow("CU_PR_PA", "SPA_ID", "供应商价格协议ID")]
        [DataRow("Cu_Pr_QuickUpdateLog", "CP_ID", "客户价格ID")]
        public void Apply_VerifiedCommercialAndWarehouseIdentifiers_UseBusinessObjectNames(
            string tableName, string fieldName, string expected)
        {
            FieldMetadata field = CreateField(fieldName);

            new VerifiedBusinessFieldNameInferenceService().Apply(
                CreateSnapshot(CreateTable(tableName, field)));

            Assert.AreEqual(expected, field.SuggestedChineseName.Value);
            Assert.AreEqual("VerifiedBusinessFieldContext",
                field.SuggestedChineseName.Evidence[0].RuleName);
        }

        /// <summary>XMZADD 20260919 验证发货、申请、费用和设施字段保留完整上级业务对象含义。</summary>
        [DataTestMethod]
        [DataRow("DA_Fee_Type", "FT_DBM_ID", "预算方法ID")]
        [DataRow("Department", "OA_ID2", "泛微OA部门ID")]
        [DataRow("HR", "OA_ID2", "泛微OA人员ID")]
        [DataRow("Dep_Post", "HPt_ID", "人事岗位ID")]
        [DataRow("DPI", "ForAwarehouse_ID", "发货目标外部仓库ID")]
        [DataRow("DP_Order", "ForAwarehouse_ID", "发货目标外部仓库ID")]
        [DataRow("DP_Order_PreSplit_Item", "DOP_ID", "发货指令预拆分记录ID")]
        [DataRow("DP_Order_Receipt_Item_DPI_AC", "DORI_ID", "发货签收明细ID")]
        [DataRow("DP_Template_Item_Plan", "DPTI_ID", "发货指令模板明细ID")]
        [DataRow("DP_Template_Item_Plan_Item", "DPTIP_ID", "发货指令模板明细计划ID")]
        [DataRow("Ds_Consign_Log", "DSC_ID", "工装委托记录ID")]
        [DataRow("DS_Dep_Handler", "DSD_ID", "申请业务类型ID")]
        [DataRow("DS_HR_Application", "DSD_ID", "申请业务类型ID")]
        [DataRow("DS_Purchase", "DSD_ID", "申请业务类型ID")]
        [DataRow("DS_Sample", "DSD_ID", "申请业务类型ID")]
        [DataRow("Material_Info_Management", "DSD_ID", "申请业务类型ID")]
        [DataRow("Purchase_Order", "DSD_ID", "申请业务类型ID")]
        [DataRow("DS_Sample", "WWT_ID", "工装工时记录ID")]
        [DataRow("ERP_OA_Definition_Table", "billid", "泛微OA表单ID")]
        [DataRow("ExpenseDetails", "ER_ID", "费用报销单ID")]
        [DataRow("ExpenseDetailsTraffic", "ER_ID", "费用报销单ID")]
        [DataRow("FacAuditLog", "FAI_ID", "收发存来源类别ID")]
        [DataRow("FacAuditLog", "FAT_ID", "收发存定义ID")]
        [DataRow("Facility_Operation", "FGD_ID", "设施组部署ID")]
        [DataRow("FG_Deploy", "FGD_ID", "设施组部署ID")]
        [DataRow("Fi_Standard_Product_Type", "P_FiPT_ID", "上级标准产品类型ID")]
        [DataRow("Fi_Standard_Product_Type_Hr", "FSPTH_ID", "标准产品类型人员ID")]
        [DataRow("Fi_Standard_Product_Type_Hr", "FSPT_ID", "标准产品类型ID")]
        [DataRow("FM_Permit", "XID", "授权范围对象ID")]
        [DataRow("FT", "FT_ID", "文件模板ID")]
        [DataRow("FT_Dep", "FT_ID", "文件模板ID")]
        public void Apply_VerifiedWorkflowAndFacilityIdentifiers_UseCompleteNames(
            string tableName, string fieldName, string expected)
        {
            FieldMetadata field = CreateField(fieldName);

            new VerifiedBusinessFieldNameInferenceService().Apply(
                CreateSnapshot(CreateTable(tableName, field)));

            Assert.AreEqual(expected, field.SuggestedChineseName.Value);
            Assert.IsTrue(IdentifierTranslationService.IsReliableChineseName(
                field.SuggestedChineseName.Value));
        }

        /// <summary>XMZADD 20260919 验证物料文件、成本价格和生产计件缩写按真实业务对象展开。</summary>
        [DataTestMethod]
        [DataRow("Invoice_Type", "IT_ID", "发票类型ID")]
        [DataRow("Item_BBP", "IB_ID", "物料不变价ID")]
        [DataRow("Item_Half_BBP", "IB_ID", "半成品不变价ID")]
        [DataRow("Item_File_3D", "Catia_FB_ID", "CATIA文件库ID")]
        [DataRow("Item_File_3D", "Catia_FI_ID", "CATIA文件ID")]
        [DataRow("Item_File_3D", "cgr_FB_ID", "CGR文件库ID")]
        [DataRow("Item_File_3D", "cgr_FI_ID", "CGR文件ID")]
        [DataRow("Item_File_3D", "SW_FB_ID", "SolidWorks文件库ID")]
        [DataRow("Item_File_3D", "SW_FI_ID", "SolidWorks文件ID")]
        [DataRow("Item_File_3D", "UG_FB_ID", "UG文件库ID")]
        [DataRow("Item_File_3D", "UG_FI_ID", "UG文件ID")]
        [DataRow("Item_File_Drawing", "CAD_FB_ID", "CAD文件库ID")]
        [DataRow("Item_File_Drawing", "CAD_FI_ID", "CAD文件ID")]
        [DataRow("Item_File_Drawing", "PDF_FB_ID", "PDF文件库ID")]
        [DataRow("Item_File_Drawing", "PDF_FI_ID", "PDF文件ID")]
        [DataRow("Item_KBP", "KB_ID", "物料可变价明细ID")]
        [DataRow("Item_KBP_Main", "KBP_ID", "物料可变价版本ID")]
        [DataRow("Item_KBP_Price", "IKP_ID", "物料可变价基础价格ID")]
        [DataRow("Item_KBPM", "KBM_ID", "物料月度可变价明细ID")]
        [DataRow("Item_KBPM", "KBPM_ID", "物料月度可变价版本ID")]
        [DataRow("Item_KBPM_Main", "KBPM_ID", "物料月度可变价版本ID")]
        [DataRow("Item_KBPM_Price", "IKPM_ID", "物料月度可变价基础价格ID")]
        [DataRow("KSOS_Month_Report_Item", "KSOS_ID", "金蝶委外仓月报ID")]
        [DataRow("KSWL_Month_Report_Item", "KSWL_ID", "金蝶线边仓月报ID")]
        [DataRow("Lead_Time", "xLT_ID", "物料提前期设置ID")]
        [DataRow("M_IA", "MIA_ID", "顶工位调整记录ID")]
        [DataRow("M_IA", "MIA_MPO_ID", "加工组工序ID")]
        [DataRow("M_IA", "MIA_MS_ID", "加工组ID")]
        [DataRow("M_PO", "MPO_PID", "工序ID")]
        [DataRow("M_PO", "MS_ID", "加工组ID")]
        [DataRow("M_PR", "MPR_ID", "生产计件记录ID")]
        [DataRow("M_PR", "MPR_RID", "来源调整记录ID")]
        [DataRow("M_RA", "MRA_ID", "合格率补贴记录ID")]
        [DataRow("M_SE", "MSE_ID", "加工组工序操作工配置ID")]
        [DataRow("M_SE", "MSE_MPO_ID", "加工组工序ID")]
        [DataRow("M_ST_A", "MST_ID", "生产统计记录ID")]
        [DataRow("M_TS", "MTS_ID", "生产计时记录ID")]
        [DataRow("MPR_Detail", "MPRD_MPO_ID", "加工组工序ID")]
        [DataRow("MPR_Detail", "MPRD_MPR_ID", "生产计件记录ID")]
        [DataRow("MPR_Detail", "MPRD_MS_ID", "加工组ID")]
        public void Apply_VerifiedMaterialAndProductionIdentifiers_ExpandDomainAbbreviations(
            string tableName, string fieldName, string expected)
        {
            FieldMetadata field = CreateField(fieldName);

            new VerifiedBusinessFieldNameInferenceService().Apply(
                CreateSnapshot(CreateTable(tableName, field)));

            Assert.AreEqual(expected, field.SuggestedChineseName.Value);
        }

        /// <summary>XMZADD 20260919 验证质量分析、供应商协议和仓库月报层级字段使用完整父对象名称。</summary>
        [DataTestMethod]
        [DataRow("P_FEMA", "PFE_ID", "过程失效模式及影响分析ID")]
        [DataRow("P_FEMA1", "PFE1_ID", "过程失效模式及影响分析一级条目ID")]
        [DataRow("P_FEMA1", "PFE_ID", "过程失效模式及影响分析ID")]
        [DataRow("P_FEMA2", "PFE1_ID", "过程失效模式及影响分析一级条目ID")]
        [DataRow("P_FEMA2", "PFE2_ID", "过程失效模式及影响分析二级条目ID")]
        [DataRow("P_FEMA3", "PFE2_ID", "过程失效模式及影响分析二级条目ID")]
        [DataRow("P_FEMA3", "PFE3_ID", "过程失效模式及影响分析三级条目ID")]
        [DataRow("ProgramProcess_Group", "PPG_ID", "项目工艺组ID")]
        [DataRow("ProgramProcess_GroupMember", "PPG_ID", "项目工艺组ID")]
        [DataRow("ProgramTech_Group", "PTG_ID", "项目技术组ID")]
        [DataRow("ProgramTech_GroupMember", "PTG_ID", "项目技术组ID")]
        [DataRow("SMR_CC", "MR_ID", "供应商月报成本中心ID")]
        [DataRow("SMR_CC_Item", "MRI_ID", "供应商月报成本中心明细ID")]
        [DataRow("Supplier_Material_PA", "SPA_SM_ID", "供应商价格协议物料明细ID")]
        [DataRow("Supplier_Material_PAX", "SPA_SM_ID", "供应商价格协议物料明细ID")]
        [DataRow("Supplier_Material_PAX", "SPA_SM_IDX", "供应商价格协议物料修订明细ID")]
        [DataRow("Supplier_PP", "PP_ID", "供应商结算日期定义ID")]
        [DataRow("WMRO", "WMRO_ID", "仓库月报物料排序ID")]
        [DataRow("WMRol", "WR_ID", "仓库月报ID")]
        public void Apply_VerifiedQualitySupplierAndReportIdentifiers_PreserveHierarchy(
            string tableName, string fieldName, string expected)
        {
            FieldMetadata field = CreateField(fieldName);

            new VerifiedBusinessFieldNameInferenceService().Apply(
                CreateSnapshot(CreateTable(tableName, field)));

            Assert.AreEqual(expected, field.SuggestedChineseName.Value);
        }

        /// <summary>XMZADD 20260919 验证人员、生产、采购和文件业务外键显示完整的上级业务对象名称。</summary>
        [DataTestMethod]
        [DataRow("GMreProduct_Type", "GMRPT_ID", "工模产品报表大类ID")]
        [DataRow("HRS_OK", "OK_ID", "批准人ID")]
        [DataRow("HR_Adm", "HRA_ID", "人员任职记录ID")]
        [DataRow("HR_Change", "OK_ID", "批准人ID")]
        [DataRow("HR_Etc", "Hre_ID", "请假记录ID")]
        [DataRow("HR_Operation", "HRD_ID", "生产人员配置ID")]
        [DataRow("HR_PB", "HRPBD_ID", "人事个人业务定义ID")]
        [DataRow("HR_WorkTime_Dep_XLS", "PMD_ID", "工资结算月份ID")]
        [DataRow("HTML_Doc", "HFID", "HTML文档文件夹ID")]
        [DataRow("IQCCI", "IQCCI_ID", "来料检验项目ID")]
        [DataRow("IQC_Detail", "IQCCI_ID", "来料检验项目ID")]
        [DataRow("Item_Info_Audit_HR", "IAH_ID", "物料信息审核人员记录ID")]
        [DataRow("Item_Logo", "IL_ID", "物料图标ID")]
        [DataRow("Kis_YGRKD_UpdateSM", "YG_FID", "雨刮金蝶入库单内码")]
        [DataRow("Lists", "LT_ID", "清单类型ID")]
        [DataRow("LM_QC", "JL_ID", "计量器具ID")]
        [DataRow("Lot", "Des_ID", "目标客户工厂ID")]
        [DataRow("Manage_PC", "MPCG_ID", "计算机管理分组ID")]
        [DataRow("MouldProduction_record_CZZS", "Pshift_ID", "生产班次ID")]
        [DataRow("MP", "MP_ID", "主生产计划ID")]
        [DataRow("MPIWC_Break_Time_Status", "MBTS_ID", "车间计划休息时间状态ID")]
        [DataRow("MpiWc_Iss", "MIS_ID", "车间发料单ID")]
        [DataRow("MPI_WC", "CPOI_ID", "客户订单明细ID")]
        [DataRow("MPI_WC", "MPIWC_FIPT_ID", "标准产品类型ID")]
        [DataRow("MP_Template", "MPT_ID", "生产过程模板ID")]
        [DataRow("Msg_Business_System", "OA_MBS_ID", "泛微OA业务系统ID")]
        [DataRow("MyExp", "ME_ID", "文件期待记录ID")]
        [DataRow("M_5S", "Insertid", "创建人ID")]
        [DataRow("m_Return", "MR_Ac_ID", "退货流水账记录ID")]
        [DataRow("Nodes", "NP_ID", "项目节点阶段ID")]
        [DataRow("Nodes_Commit", "NC_ID", "项目节点完成形式ID")]
        [DataRow("Pallets_Preseted", "PN_ID", "预排库存记录ID")]
        [DataRow("PartScanOut", "MIS_ID", "车间发料单ID")]
        [DataRow("Pay_DelFeeTab", "FCM_ID", "财务中心核算月份ID")]
        [DataRow("Pay_Month_Define", "FCM_ID", "财务中心核算月份ID")]
        [DataRow("PP", "MPP_ID", "月度采购计划ID")]
        [DataRow("PPI", "MPP_ID", "月度采购计划ID")]
        [DataRow("PPAP_Supplier", "PPAP_ID", "生产件批准程序定义ID")]
        [DataRow("PPAP_Supplier_OP", "PPAPS_ID", "供应商生产件批准记录ID")]
        [DataRow("PPP_Purchase_Order", "PPP_ID", "供应商采购计划ID")]
        [DataRow("PPP_Supplier_Change", "Ori_PPP_ID", "原供应商采购计划ID")]
        [DataRow("Product", "MPT_ID", "生产过程模板ID")]
        public void Apply_VerifiedOperationalIdentifiers_UseCompleteParentObjectNames(
            string tableName, string fieldName, string expected)
        {
            FieldMetadata field = CreateField(fieldName);

            new VerifiedBusinessFieldNameInferenceService().Apply(
                CreateSnapshot(CreateTable(tableName, field)));

            Assert.AreEqual(expected, field.SuggestedChineseName.Value);
            Assert.AreEqual("VerifiedBusinessFieldContext",
                field.SuggestedChineseName.Evidence[0].RuleName);
        }

        /// <summary>XMZADD 20260919 验证流水、OA、工艺路线、质量和要货计划字段采用源码可证明的业务名称。</summary>
        [DataTestMethod]
        [DataRow("Code_Motor_Out", "ID2", "流水ID")]
        [DataRow("MPIWC_Code_Motor_WIP", "ID2", "流水ID")]
        [DataRow("DA_Acceptance", "Used_DA_ID", "使用承兑票据的财务流水账ID")]
        [DataRow("DA_Account", "OA_ID", "泛微OA流程请求ID")]
        [DataRow("InvForOA", "COMPANYOA_ID", "泛微OA分部ID")]
        [DataRow("PFT", "Des_Lib_ID", "快捷方式目标文件库ID")]
        [DataRow("Program_Develop", "Pro_FL_ID", "项目开发文件库ID")]
        [DataRow("Purchase_Value", "WID", "仓库ID")]
        [DataRow("P_B", "PB_ID", "工艺路线工序ID")]
        [DataRow("P_B_Segment", "SG_PP_ID", "工艺路线ID")]
        [DataRow("P_B_Segment", "SG_CP_ID", "工序ID")]
        [DataRow("QC_Task_Action", "tl_id", "质量任务问题ID")]
        [DataRow("RD_Dep", "RDT_ID", "研发人员类别ID")]
        [DataRow("ReWJItem_Type", "RIT_ID", "五金月报物料分类ID")]
        [DataRow("SafeInv", "SaiX_ID", "原安全库存设置ID")]
        [DataRow("SI_Status", "SIS_ID", "供应商发票状态ID")]
        [DataRow("SMP_Receiver", "SMP_ID", "桌面共享会话ID")]
        [DataRow("spi", "SPIx_ID", "原要货计划明细ID")]
        [DataRow("SPI_Adjust", "SPIx_ID", "原要货计划明细ID")]
        [DataRow("spi", "SPI_ID", "要货计划明细ID")]
        [DataRow("SPI_MPI", "SPI_ID", "要货计划明细ID")]
        [DataRow("SPI_MPI", "SMPii_ID", "要货计划与生产计划期初库存明细ID")]
        [DataRow("SPI_Paste_Item", "SPIP_ID", "要货计划粘贴导入批次ID")]
        public void Apply_VerifiedWorkflowIdentifiers_UseSourceProvenBusinessNames(
            string tableName, string fieldName, string expected)
        {
            FieldMetadata field = CreateField(fieldName);

            new VerifiedBusinessFieldNameInferenceService().Apply(
                CreateSnapshot(CreateTable(tableName, field)));

            Assert.AreEqual(expected, field.SuggestedChineseName.Value);
            Assert.AreEqual("VerifiedBusinessFieldContext",
                field.SuggestedChineseName.Evidence[0].RuleName);
        }

        /// <summary>XMZADD 20260919 验证工模申请、计费与车间字段完整表达其上级业务对象。</summary>
        [DataTestMethod]
        [DataRow("WM_Application", "Sources_WM_Application_ID", "来源工模开发单ID")]
        [DataRow("WM_Application", "WmM_ID", "工模父级项目ID")]
        [DataRow("Wm_Application_PreAudit", "WM_ID", "工模预审采购需求申请单ID")]
        [DataRow("Wm_Application_PreAuditLog", "WmPID", "工模父级项目预审核记录ID")]
        [DataRow("WM_Application_WorkTime", "DSC_ID", "工模委外申请单ID")]
        [DataRow("WM_Application_WorkTime", "WTF_ID", "工模加工类型计费标准ID")]
        [DataRow("WM_Purchase_Item", "WM_ID", "工模部门采购需求申请单ID")]
        [DataRow("WS", "WS_ID", "生产车间ID")]
        [DataRow("WS_MPI", "WS_ID", "生产车间ID")]
        public void Apply_VerifiedToolingAndWorkshopIdentifiers_PreserveBusinessHierarchy(
            string tableName, string fieldName, string expected)
        {
            FieldMetadata field = CreateField(fieldName);

            new VerifiedBusinessFieldNameInferenceService().Apply(
                CreateSnapshot(CreateTable(tableName, field)));

            Assert.AreEqual(expected, field.SuggestedChineseName.Value);
            Assert.AreEqual("VerifiedBusinessFieldContext",
                field.SuggestedChineseName.Evidence[0].RuleName);
        }

        /// <summary>XMZADD 20260919 验证销售来源、文件、付款、任务和月报字段采用业务对象全称。</summary>
        [DataTestMethod]
        [DataRow("Sale_Value", "SV_DD_ID", "来源发货执行记录ID")]
        [DataRow("Sale_Value", "SV_DORI_ID", "来源发货签收明细ID")]
        [DataRow("Sale_Value", "SV_DORIAC_ID", "来源签收与发货关联记录ID")]
        [DataRow("SP_File_OP_Define", "SPOD_ID", "文件操作定义ID")]
        [DataRow("SP_Folder_CPP_EX", "CPP_FI_ID", "客户项目产品根文件夹ID")]
        [DataRow("SP_Image", "SP_ID", "标准工序ID")]
        [DataRow("SP_MP_Ini_Inv", "SMP_ID", "要货计划与生产计划关联配置ID")]
        [DataRow("Supplier_AheadBudget_Operate", "SPMI_ID", "供应商应付账款明细ID")]
        [DataRow("Supplier_Manu_Lot_FBDC", "FBDC_FID", "金蝶分步式调出单内码")]
        [DataRow("Supplier_MSG", "ReMSID", "被回复消息ID")]
        [DataRow("Supplier_PayAP_Hardware", "SPMI_ID", "供应商应付账款明细ID")]
        [DataRow("Supplier_PayPlan_Budget_Company_Item_Pay", "SPBI_ID", "公司级供应商付款预算明细ID")]
        [DataRow("Supplier_PayPlan_Budget_Item_Pay", "SPBI_ID", "事业部供应商付款预算明细ID")]
        [DataRow("Supplier_Price_Agreement", "OA_SPA_ID", "泛微OA价格协议归档记录ID")]
        [DataRow("SV_Pay", "CP_ID", "客户支付记录ID")]
        [DataRow("Table_Activity_Mark", "DO_ID", "操作事件ID")]
        [DataRow("TTask", "Pro_FL_ID", "所属项目文件库ID")]
        [DataRow("TTask_Sub", "CT_ID", "子任务ID")]
        [DataRow("TTask_Ver", "Exer_ID", "任务责任方ID")]
        [DataRow("Warehouse", "WT_ID", "仓库类型ID")]
        [DataRow("WI", "WJ_ID", "五金仓库领用报表部门分组ID")]
        [DataRow("Wiper_Dep_Cost_Center", "WDC_ID", "雨刮部门成本中心ID")]
        [DataRow("WJ", "WJ_ID", "五金仓库领用报表部门分组ID")]
        [DataRow("WMRE_Item", "WMRE_ID", "试制领料月报ID")]
        public void Apply_VerifiedRemainingBusinessIdentifiers_UseSourceProvenObjectNames(
            string tableName, string fieldName, string expected)
        {
            FieldMetadata field = CreateField(fieldName);

            new VerifiedBusinessFieldNameInferenceService().Apply(
                CreateSnapshot(CreateTable(tableName, field)));

            Assert.AreEqual(expected, field.SuggestedChineseName.Value);
            Assert.AreEqual("VerifiedBusinessFieldContext",
                field.SuggestedChineseName.Evidence[0].RuleName);
        }

        /// <summary>XMZADD 20260917 验证未核验表族不接收多义字段规则，防止为追求覆盖率制造错误译名。</summary>
        [DataTestMethod]
        [DataRow("DO_Data", "DO_ID")]
        [DataRow("TTask_Confirm", "TTM_ID")]
        [DataRow("Unknown_Table", "DT_ID")]
        [DataRow("Access", "MPC_ID")]
        [DataRow("LessonsLearned", "Y")]
        public void Apply_AmbiguousFieldOutsideVerifiedContext_RemainsPending(
            string tableName, string fieldName)
        {
            FieldMetadata field = CreateField(fieldName);

            new VerifiedBusinessFieldNameInferenceService().Apply(
                CreateSnapshot(CreateTable(tableName, field)));

            Assert.IsNull(field.SuggestedChineseName);
        }

        /// <summary>XMZADD 20260917 验证源码推断只作为参考名，不能覆盖人工或数据库正式结论。</summary>
        [TestMethod]
        public void Apply_ExistingAuthoritativeName_IsPreserved()
        {
            FieldMetadata field = CreateField("ALF_ID");
            field.ChineseName = new MetadataValue
            {
                Value = "人工确认生产线ID",
                Status = ConfidenceStatus.LocalOverride,
                IsManualOverride = true,
                IsLocked = true
            };

            new VerifiedBusinessFieldNameInferenceService().Apply(
                CreateSnapshot(CreateTable("Shift", field)));

            Assert.AreEqual("人工确认生产线ID", field.ChineseName.Value);
            Assert.IsNotNull(field.SuggestedChineseName);
            Assert.AreEqual("生产线ID", field.SuggestedChineseName.Value);
        }

        /// <summary>XMZADD 20260917 创建包含指定字段的最小业务表测试数据。</summary>
        private static TableMetadata CreateTable(string objectName, FieldMetadata field)
        {
            return new TableMetadata
            {
                ScopeKey = "github-shared-dictionary",
                SchemaName = "dbo",
                ObjectName = objectName,
                Fields = new List<FieldMetadata> { field }
            };
        }

        /// <summary>XMZADD 20260917 创建尚无可靠中文名的字段测试数据。</summary>
        private static FieldMetadata CreateField(string fieldName)
        {
            return new FieldMetadata
            {
                FieldName = fieldName,
                ChineseName = new MetadataValue { Value = string.Empty }
            };
        }

        /// <summary>XMZADD 20260917 创建仅包含指定表的最小快照。</summary>
        private static SnapshotData CreateSnapshot(params TableMetadata[] tables)
        {
            return new SnapshotData { Tables = new List<TableMetadata>(tables) };
        }
    }
}
