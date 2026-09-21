using System;
using System.Collections.Generic;
using SHB.EosDataDictionary.Models;

namespace SHB.EosDataDictionary.Services
{
    /// <summary>XMZADD 20260917 按 EOS 业务源码已核验的表族语境补全多义字段中文参考名。</summary>
    public sealed class VerifiedBusinessFieldNameInferenceService
    {
        private static readonly IDictionary<string, IList<VerifiedFieldRule>> RulesByField =
            CreateRules();

        /// <summary>XMZADD 20260917 逐字段应用可追溯上下文规则且仅输出待审阅参考名。</summary>
        public void Apply(SnapshotData snapshot)
        {
            if (snapshot == null || snapshot.Tables == null)
            {
                return;
            }

            var nameLayer = new BusinessNameLayerService();
            for (int tableIndex = 0; tableIndex < snapshot.Tables.Count; tableIndex++)
            {
                TableMetadata table = snapshot.Tables[tableIndex];
                if (table == null || table.Fields == null || string.IsNullOrWhiteSpace(table.ObjectName))
                {
                    continue;
                }
                for (int fieldIndex = 0; fieldIndex < table.Fields.Count; fieldIndex++)
                {
                    FieldMetadata field = table.Fields[fieldIndex];
                    IList<VerifiedFieldRule> rules;
                    if (field == null || string.IsNullOrWhiteSpace(field.FieldName) ||
                        !RulesByField.TryGetValue(field.FieldName, out rules))
                    {
                        continue;
                    }
                    VerifiedFieldRule rule = FindRule(rules, table.ObjectName);
                    if (rule != null)
                    {
                        nameLayer.ApplyFieldCandidate(field, CreateCandidate(table, field, rule));
                    }
                }
            }
        }

        /// <summary>XMZADD 20260917 按规则声明顺序选择首个匹配表名的精确业务语义。</summary>
        private static VerifiedFieldRule FindRule(IList<VerifiedFieldRule> rules, string tableName)
        {
            if (rules == null)
            {
                return null;
            }
            for (int index = 0; index < rules.Count; index++)
            {
                if (rules[index] != null && rules[index].Matches(tableName))
                {
                    return rules[index];
                }
            }
            return null;
        }

        /// <summary>XMZADD 20260917 创建包含源码路径、行号和业务判断依据的保守参考译名。</summary>
        private static MetadataValue CreateCandidate(TableMetadata table, FieldMetadata field,
            VerifiedFieldRule rule)
        {
            string original = table.ObjectName + "." + field.FieldName;
            return new MetadataValue
            {
                Value = rule.ChineseName,
                Status = ConfidenceStatus.Guessed,
                ConfidenceScore = rule.ConfidenceScore,
                SourceType = "EOS业务代码",
                SourceSummary = "EOS 源码业务上下文核验：" + rule.Explanation,
                Evidence = new List<EvidenceItem>
                {
                    new EvidenceItem
                    {
                        SourceType = "EOS业务代码",
                        SourcePath = rule.SourcePath,
                        SourceLine = rule.SourceLine,
                        RuleName = "VerifiedBusinessFieldContext",
                        RawValue = original + "=" + rule.ChineseName,
                        OriginalText = original,
                        Explanation = rule.Explanation
                    }
                }
            };
        }

        /// <summary>XMZADD 20260917 建立按物理字段索引的核验规则，避免对七十余万字段反复遍历全部规则。</summary>
        private static IDictionary<string, IList<VerifiedFieldRule>> CreateRules()
        {
            var result = new Dictionary<string, IList<VerifiedFieldRule>>(
                StringComparer.OrdinalIgnoreCase);

            AddRule(result, "ALF_ID", "生产线ID",
                "Facility/frmFG_New.vb", 15,
                "ALF_ID 直接取自 Assembly_Line_Facility_ID，界面列名为生产线",
                null, null);
            AddRule(result, "CCC_ID", "客户投诉ID",
                "docs_knowledge/01_数据库表结构/_00_表索引.md", 625,
                "CC_Complain 表族均属于客户投诉业务对象",
                null, new[] { "CC_Complain" });
            AddRule(result, "MPIC_ID", "计划对比ID",
                "ERP/表-类定义/Code_Plan_Copy_Partial.vb", 313,
                "比较结果和计划副本均归属于计划对比主记录",
                null, new[] { "MPI_Compare", "MPI_Copy" });
            AddRule(result, "IBS_N1", "一级包装数量",
                "ERP/表-类定义/code_Pur_Partial.vb", 3979,
                "字段与包装样式第一层名称组合显示并参与顶层包装数量计算",
                null, null);
            AddRule(result, "IBS_N2", "二级包装数量",
                "ERP/表-类定义/code_Pur_Partial.vb", 3981,
                "字段与包装样式第二层名称组合显示并参与顶层包装数量计算",
                null, null);
            AddRule(result, "IBS_N3", "三级包装数量",
                "ERP/表-类定义/code_Pur_Partial.vb", 3983,
                "字段与包装样式第三层名称组合显示并参与顶层包装数量计算",
                null, null);
            AddRule(result, "WSS_Case", "入库业务场景",
                "ERP/表-类定义/Code_ItemIncome_Partial.vb", 23935,
                "入库场景枚举区分供应商生产、物流代理、自制车间供货等业务来源",
                null, null);
            AddRule(result, "PSegment_Des", "工序段描述",
                "ERP/表-类定义/code_PR_partial.vb", 406,
                "生产工艺查询将该字段明确显示为工序段描述",
                null, null);
            AddRule(result, "Solo", "是否工序独立执行",
                "ERP/表-类定义/code_PR_partial.vb", 409,
                "生产工艺界面将 Solo 明确显示为独立执行并传递到工序执行记录",
                new[] { "P_B", "P_B_Price_Detail", "P_MPIWC_CZ", "WPWorker_Account" }, null);
            AddRule(result, "PC", "计算机名",
                "ERP/Company.vb", 1,
                "登录、操作和采购日志均写入当前客户端计算机名称",
                null, null);
            AddRule(result, "RID", "关联记录ID",
                "docs_knowledge/02_字段字典/_00_核心字段字典.md", 1,
                "库存流水表以 RID 保存对应来源业务记录标识",
                null, null);
            AddRule(result, "Ac_ID", "访问记录ID",
                "ERP/表-类定义/code_G.vb", 1,
                "Access 表以 Ac_ID 标识一次系统访问记录",
                new[] { "Access" }, null);
            AddRule(result, "Ac_ID", "执行流水ID",
                "ERP/Bu.vb", 7928,
                "发货指令与生产计划执行表使用 Ac_ID 标识执行流水记录",
                new[] { "DP_Order_Exe", "MPI_Exe" }, null);
            AddRule(result, "Ac_ID", "设施审核日志ID",
                "ERP/表-类定义/code_Facility.vb", 1,
                "FacAuditLog 表以 Ac_ID 标识设施审核日志记录",
                new[] { "FacAuditLog" }, null);
            AddRule(result, "Ac_ID", "流水账记录ID",
                "ERP/Account 仓库帐/AC.vb", 1874,
                "Account 和 Ac 表族把 Ac_ID 作为每笔出入库或业务流水账的记录标识",
                new[] { "AcPart", "AcPro", "Bu_Part_Box_Basic_IO" },
                new[] { "Account_", "Ac_" });
            AddRule(result, "PID", "工序ID",
                "ERP/表-类定义/code_PR_partial.vb", 406,
                "生产计划、工艺、安灯和工序成本代码均以 PID 关联工序定义",
                new[]
                {
                    "Item_KBP", "Item_KBPM", "Line_Issue", "MPI_WC", "MPIWC_Done",
                    "MPIWC_Done_Box", "MPIWC_Flow", "P_Equip", "P_FEMA", "P_MPIWC",
                    "P_MPIWC_CZ", "P_PEPWC", "P_Segment_P", "WPWorker_Account"
                }, null);
            AddRule(result, "LID", "检查清单ID",
                "ERP/表-类定义/code_PR.vb", 26312,
                "M_5S 同时保存 LID 与 ListName，用于关联一份 5S 检查清单",
                new[] { "M_5S" }, null);
            AddRule(result, "LID", "清单ID",
                "ERP/表-类定义/Code_Plan_Copy_Partial.vb", 294,
                "计划复制、比较和清单附属记录均通过 LID 装载 t_Lists 清单对象",
                new[]
                {
                    "Abom_View", "CopyPlanData_Setting", "ListIcon", "listp_Log",
                    "Lists_Need", "MP_Product_Safe_Days", "MPI_Compare", "MPI_Copy",
                    "MPI_Copy_KIS", "MPI_Week", "MPIWC_Fault", "PackagingMaterialReport"
                }, null);
            AddRule(result, "CR_ID", "产能报告ID",
                "ERP/表-类定义/Code_CapacityReport.vb", 12,
                "设备、模具及汇总明细均通过 CR_ID 归属于产能报告主记录",
                new[]
                {
                    "CapacityReportItem", "EquipmentCapacityReportCounting",
                    "EquipmentCapacityReportItem", "MouldCapacityReportItem"
                }, null);
            AddRule(result, "CR_ID", "客户需求下达ID",
                "ERP/表-类定义/code_CPO.vb", 1,
                "CPO_R 保存开口订单客户需求下达内容，CR_ID 标识该需求记录",
                new[] { "CPO_R" }, null);
            AddRule(result, "CR_ID", "五金月度领用报表ID",
                "ToolingDeveloping/UserControl/ucReport.vb", 517,
                "WMCR 明细写入时使用 CR_ID 关联按年月保存的五金领用报表",
                new[] { "WMCR" }, null);
            AddRule(result, "Map_ID", "思维导图ID",
                "SHBMind/Classes/MindMapTaskStatusSyncService.vb", 81,
                "脑图节点、版本、权限日志和任务绑定均通过 Map_ID 关联思维导图主记录",
                new[] { "MM_Map_Access_Log", "MM_Map_Ver", "MM_Node", "MM_Node_Task" }, null);
            AddRule(result, "SM_ID", "供应商物料关系ID",
                "ERP/Bu.vb", 1586,
                "价格历史和供应商物料日志以 SM_ID 关联 Supplier_Material 主记录",
                new[] { "SM_Price_History", "Supplier_Material_log", "Supplier_Material_Log_Audit" }, null);
            AddRule(result, "TID", "任务ID",
                "SHBMind/Classes/MindMapTaskStatusSyncService.vb", 102,
                "任务关怀、检查项、日志和脑图任务绑定均以 TID 关联 TTask 主任务",
                new[] { "MM_Node_Task", "TTask_Care", "TTask_CheckList", "TTask_Log" }, null);
            AddRule(result, "CFB_ID", "客户工厂基础资料ID",
                "ERP/CPP 客户 项目 产品/frmCF_Base_Program.vb", 49,
                "客户工厂项目配置和开票预处理通过 CFB_ID 关联 CF_Base 基础资料",
                new[] { "CF_Base", "CF_Base_Program", "Cu_Invoice_Pre" }, null);
            AddRule(result, "FSS_ID", "文件共享范围ID",
                "ERP/SHBFile/Forms/frmFile_Add_Permit.vb", 34,
                "文件库和文件夹权限界面以 FSS_ID 选择所有人、部门或人员共享范围",
                new[] { "File_Lib_Permit", "File_Limit_Folder_Permit", "File_Share_Folder_Permit" }, null);
            AddRule(result, "SPFV_ID", "计划文件版本ID",
                "ERP/SHBFile/UserControls/ucFile_CAD.vb", 315,
                "计划文件扩展版本项使用 SPFV_ID 关联所在文件版本",
                null, new[] { "SP_Folder_" });
            AddRule(result, "PEP_ID", "设备原型ID",
                "ERP/Process 工艺路线/frmFacility.vb", 136,
                "设施、工序工资和工序设备配置均以 PEP_ID 关联 P_Equip_Prototype 设备原型",
                new[] { "Facility", "P_B_Wages", "P_PEPWC" }, null);
            AddRule(result, "MIDFC_ID", "生产流转卡ID",
                "Kanban/流转/frmFlowCard.vb", 47,
                "完工记录通过 MIDFC_ID 关联以流转卡编码展示的生产流转卡主记录",
                new[] { "MPIWC_Done", "MPIWC_DONE_Flow_Card", "MPIWC_Done_JG" }, null);
            AddRule(result, "OQC_ID", "出货质量检验记录ID",
                "Quality_x/Out_Quality_Control.vb", 390,
                "出货质量检验主表及明细均以 OQC_ID 标识同一次出货检验记录",
                new[] { "OQC", "OQC_Detail", "tmp_qc_report" }, null);
            AddRule(result, "EM_ID", "ERP模块ID",
                "AssetManagement/frmAssetManagement.vb", 43,
                "模块版本和部门配置以 EM_ID 关联 ERP_Module 模块定义",
                new[] { "Bu_Data_Table", "ERP_Module_Department", "ERP_Module_Ver" }, null);
            AddRule(result, "QO_ID", "快速质量响应组织ID",
                "ERP/QRQC/frmProblem.vb", 92,
                "质量问题组织、负责人和仓管员配置均以 QO_ID 关联快速质量响应组织",
                new[] { "QRQC_Org", "QRQC_Org_Line_Warehouser", "QRQC_Org_Manager" }, null);
            AddRule(result, "DIS_ID", "入库状态ID",
                "Logistics/Incoming/frmII_IQC.vb", 85,
                "来料检验与入库日志以 DIS_ID 关联待入库、待评审、待退货等入库状态",
                new[] { "DS_Item_Income", "DS_Item_Income_Status", "t_DS_Item_Income_ManualInWarehouse_Log" }, null);
            AddRule(result, "DID", "产品条码定义ID",
                "MotorBarcodeScan/换标签/frmChangelabel.vb", 100,
                "箱码和电机码记录以 DID 装载 NewCode_Product 产品条码定义",
                new[] { "Code_Box", "Code_Motor", "Code_Motor_Out" }, null);
            AddRule(result, "Cap", "条码前缀字符",
                "MotorBarcodeScan/类/clsCodeScan_Partial.vb", 12941,
                "扫码规则取条码首字符判断是否存在 P 前缀，并将该字符保存到 Cap",
                new[] { "Code_Motor", "Code_Motor_Out", "MPIWC_Code_Motor", "MPIWC_Code_Motor_WIP" }, null);
            AddRule(result, "QT_ID", "质量检验类型ID",
                "Quality_x/Out_Quality_Control.vb", 366,
                "出货检验和不合格项通过 QT_ID 关联 QC_Type 质量检验类型",
                new[] { "OQC", "pz_Inferior", "QC_Type" }, null);
            AddRule(result, "LPK_ID", "计薪方式ID",
                "Kanban/frmKanBanStat.vb", 111,
                "工序工资流水通过 LPK_ID 关联 Pay_Kind_Line，并在界面明确显示为计薪方式",
                new[] { "Pay_Kind_Line", "WPWorker_Account", "WPWorker_Account_Other" }, null);
            AddRule(result, "PMID", "执行工序ID",
                "Kanban/报表/frmKanBanManageStat_JG.vb", 39,
                "看板报表把 P_MPIWC 的 PMID 明确显示为执行工序ID，人员流水沿用该关联",
                new[] { "WPWorker_Account", "WPWorker_Account_CZ", "WPWorker_Account_JG" }, null);
            AddRule(result, "Asure", "是否财务核算",
                "HR_X/离职/FrmHRResign.vb", 729,
                "薪资调整和月度费用查询把 Asure 明确显示为财务核算状态",
                new[] { "Pay_ChangeMemo", "Pay_DeleteFee_ByMonth", "Pay_DormFeeList_ByMonth" }, null);

            AddRule(result, "EDS_ID", "供应商质量问题报告ID",
                "ERP/Purchase 采购/frmNewED.vb", 53,
                "字段用于装载供应商质量 8D 报告主对象及其全部子记录",
                null, new[] { "EDisciplines_" });
            AddRule(result, "DO_ID", "发货指令ID",
                "docs_knowledge/01_数据库表结构/_00_表索引.md", 348,
                "DP_Order 及其执行、回执、拆分记录属于发货指令业务",
                new[]
                {
                    "DPI", "DPO_Reject_Info", "DeliveryOrder_Box", "DeliveryOrder_Product",
                    "Logistics_FeeTypeAndFee", "Pallets_Preseted"
                },
                new[] { "DP_Order" });
            AddRule(result, "DT_ID", "文档类型ID",
                "docs_knowledge/01_数据库表结构/_00_表索引.md", 1,
                "Document_Type 为文档类型定义表",
                new[] { "Document_Type" }, null);
            AddRule(result, "DT_ID", "缺陷处理ID",
                "ERP/表-类定义/code_Defect.vb", 1,
                "Defect、Defect_Event 和 Defect_Treat 通过 DT_ID 关联缺陷处理记录",
                new[] { "Defect", "Defect_Event", "Defect_Treat" }, null);
            AddRule(result, "DT_ID", "运输方式ID",
                "ERP/Bu.vb", 12920,
                "交货流水通过 DT 关联并显示运输方式",
                new[] { "Account_Delivery", "DT" }, null);
            AddRule(result, "DT_ID", "质量处理类型ID",
                "docs_knowledge/01_数据库表结构/_00_表索引.md", 1,
                "QC_Deal_Type 为质量处理类型定义表",
                new[] { "QC_Deal_Type" }, null);
            AddRule(result, "DPT_ID", "发货指令模板ID",
                "docs_knowledge/01_数据库表结构/_00_表索引.md", 348,
                "DP_Template 是发货指令模板，DPI、DP_Order 和模板项均引用该对象",
                new[] { "DPI", "DP_Cast", "DP_Order", "DP_Template", "DP_Template_Item" }, null);
            AddRule(result, "SPF_ID", "计划文件ID",
                "ERP/表-类定义/code_SP.vb", 1,
                "SP_Folder 扩展记录使用 SPF_ID 关联计划文件夹中的文件对象",
                null, new[] { "SP_Folder_" });
            AddRule(result, "TTM_ID", "任务消息ID",
                "ERP/表-类定义/code_TTask.vb", 1,
                "任务日志、附件、阅读人、隐藏和颜色记录均从属于任务消息",
                new[] { "TTask_Log" }, new[] { "TTask_Msg_" });
            AddRule(result, "MPC_ID", "计算机管理记录ID",
                "docs_knowledge/01_数据库表结构/_00_表索引.md", 1145,
                "Manage_PC 是客户端计算机管理主记录",
                new[] { "Manage_PC" }, null);
            AddRule(result, "MPC_ID", "生产计划产品类别ID",
                "ERP/ERP.vb", 2055,
                "生产计划代码使用 MpProduct 分类筛选 MP、MPI 及车间计划",
                new[] { "MP", "MPI", "MPI_WC", "MPI_Week" }, null);
            AddRule(result, "Mass", "是否批量订单",
                "ERP/Bu.vb", 12918,
                "订单代码以 Mass=1 表示批量、Mass=0 表示样品",
                new[] { "CPO", "DP_Order", "DP_Template", "Purchase_Order" }, null);
            AddRule(result, "Mass", "批量或样品标识",
                "ERP/Bu.vb", 13068,
                "交货流水按 Mass 分别汇总批量数量和样品数量",
                new[] { "Account_Delivery" }, null);
            AddRule(result, "op", "操作类型",
                "ERP/表-类定义/code_Op.vb", 1,
                "业务日志用 OP 区分本条记录对应的操作类型",
                new[] { "Bu_Plan_Goal_Log", "DA_Fi_Budget_Log", "KisOP", "LM_Notes", "WS_Record" }, null);
            AddRule(result, "MST_ID", "模具状态流转类型ID",
                "ERP/表-类定义/code_PR2.vb", 11924,
                "模具状态流转单通过 MST_ID 关联 Mould_StatusTransaction_Type 类型定义",
                new[] { "Mould_StatusTransaction_Sheet", "Mould_StatusTransaction_Type" }, null);
            AddRule(result, "CID", "委托记录ID",
                "ERP/表-类定义/code_Consign.vb", 1,
                "Consign 表以 CID 标识一条委托业务记录",
                new[] { "Consign" }, null);
            AddRule(result, "CID", "币别ID",
                "ERP/表-类定义/code_Currency.vb", 1,
                "汇率日志和销售报表币别配置通过 CID 关联币别定义",
                new[] { "Currency_Rate_Log", "Sale_Report_Currency" }, null);
            AddRule(result, "LT_ID", "提前期ID",
                "ERP/表-类定义/code_Plan.vb", 1,
                "客户订单项和发货明细通过 LT_ID 关联 Lead_Time 提前期配置",
                new[] { "CPO_Item", "DPI", "Lead_Time" }, null);
            AddRule(result, "LT_ID", "供应商物料日志类型ID",
                "ERP/表-类定义/code_Pur.vb", 1,
                "Supplier_Material_Log_Type 以 LT_ID 标识供应商物料日志类型",
                new[] { "Supplier_Material_Log_Type" }, null);
            AddRule(result, "CP_ID", "控制参数ID",
                "ERP/表-类定义/code_Control_Parameter.vb", 1,
                "Control_Parameter 以 CP_ID 标识控制参数定义",
                new[] { "Control_Parameter" }, null);
            AddRule(result, "GM_ID", "五金月报ID",
                "W/月报/frmWJ_Generate2023.vb", 612,
                "五金月报主表和明细以 GM_ID 关联同一月份的报表记录",
                new[] { "GMreport_Month", "GMreport_Month_Item" }, null);
            AddRule(result, "GM_ID", "五金物料金蝶编码映射ID",
                "W/Kis/frmAddStandardItem.vb", 22,
                "五金物料名称与金蝶编码维护界面以 GM_ID 标识一条映射记录",
                new[] { "GM_Item_KisCode" }, null);
            AddRule(result, "FB_ID", "财务预算ID",
                "f_MIS/预算/frmFi_Budget.vb", 1,
                "财务预算格式和操作日志通过 FB_ID 关联财务预算主记录",
                new[] { "DA_Fi_Budget_Format", "DA_Fi_Budget_Log" }, null);
            AddRule(result, "FB_ID", "客户质量反馈ID",
                "ERP/表-类定义/code_QC.vb", 1,
                "QC_CustomerFB 以 FB_ID 标识一条客户质量反馈记录",
                new[] { "QC_CustomerFB" }, null);
            AddRule(result, "CI_ID", "客户发票ID",
                "f_MIS/预算/frmFi_Budget.vb", 1642,
                "开票跟进和财务更新日志以 CI_ID 关联 Cu_Invoice 客户发票主记录",
                new[] { "CII_Pre", "Cu_Invoice_AR_FollowUp", "Cu_Invoice_FinanceExcelUpdateLog" }, null);

            AddVerifiedBusinessValueRules(result);
            AddAdditionalVerifiedValueRules(result);
            AddVerifiedUiCaptionRules(result);
            AddRemainingVerifiedValueRules(result);
            AddVerifiedDomainAbbreviationRules(result);
            AddAnnualCostRules(result);
            AddLayoutAndKingdeeRules(result);
            AddRemainingUsedIdentifierRules(result);
            AddVerifiedOperationalIdentifierRules(result);
            AddVerifiedWorkflowIdentifierRules(result);
            AddVerifiedRemainingBusinessIdentifierRules(result);
            AddDateRules(result);
            return result;
        }

        /// <summary>XMZADD 20260917 登记由源码标题、计算公式和对象关系直接证明的业务值字段中文名。</summary>
        private static void AddVerifiedBusinessValueRules(
            IDictionary<string, IList<VerifiedFieldRule>> result)
        {
            AddRule(result, "AM_SaveCName", "保存计算机名",
                "ERP/表-类定义/code_Ac.vb", 1,
                "月报保存记录使用该字段保留执行保存的客户端计算机名",
                new[] { "Account_01_Month_Report" }, null);
            AddRule(result, "ETA", "预计到货天数",
                "ERP/Bu.vb", 15833,
                "代码通过 DateAdd 把 ETA 作为发货日期到预计到货日期的天数",
                new[] { "Account_Delivery" }, null);
            AddRule(result, "Leave", "是否已出库",
                "ERP/表-类定义/Code_ItemIncome_Partial.vb", 17565,
                "托盘物料完成出库事务时把 Leave 置为真并记录出库明细",
                new[] { "Account_Pallet_Trade_Item" }, null);
            AddRule(result, "ExpDP", "销售期望",
                "Plan_x/生产计划/frmManuBalance.vb", 274,
                "产销平衡界面把 ExpDP 明确显示为销售期望",
                new[] { "Bu_Plan_Goal" }, null);
            AddRule(result, "ExpSP", "要货期望",
                "Plan_x/生产计划/frmManuBalance.vb", 275,
                "产销平衡界面把 ExpSP 明确显示为要货期望",
                new[] { "Bu_Plan_Goal" }, null);
            AddRule(result, "ExpMP", "生产期望",
                "Plan_x/生产计划/frmManuBalance.vb", 276,
                "产销平衡界面把 ExpMP 明确显示为生产期望",
                new[] { "Bu_Plan_Goal" }, null);
            AddRule(result, "CI_Ye", "盘点年份",
                "ERP/Bu.vb", 31014,
                "盘点库存查询以 CI_Ye 和 CI_Mo 共同限定盘点年月",
                new[] { "CheckInventory", "CheckInventory_KisBillMO" }, null);
            AddRule(result, "CI_Mo", "盘点月份",
                "ERP/Bu.vb", 31015,
                "盘点库存查询以 CI_Ye 和 CI_Mo 共同限定盘点年月",
                new[] { "CheckInventory", "CheckInventory_KisBillMO" }, null);
            AddRule(result, "LastDML", "最后数据操作类型",
                "ERP/表-类定义/code_CPO.vb", 1,
                "字段枚举以新增、更新、删除区分最近一次数据操作",
                new[] { "CPO_R" }, null);
            AddRule(result, "Cu_Abb", "客户简称",
                "G/ucCostProject.vb", 59,
                "客户项目查询直接把 Customer.Cu_Abb 显示为客户简称",
                new[] { "Customer" }, null);
            AddRule(result, "DSP_BackIfo", "采购申请备注",
                "DepService/Hardware_Purchase_2021/uc_DS_Purchase_2021.vb", 3474,
                "采购申请导出把 DSP_BackIfo 写入备注单元格",
                new[] { "DS_Purchase" }, null);
            AddRule(result, "DSS_Lastest_Act", "最近一次操作",
                "ERP/表-类定义/code_DS_Sample_Partial.vb", 862,
                "样品申请每次状态动作都同步最近操作人、时间和动作文本",
                new[] { "DS_Sample" }, null);
            AddRule(result, "Lastest_Income", "最近一次入库记录ID",
                "ERP/表-类定义/code_DS_Sample_Partial.vb", 3677,
                "样品项用该字段加载最近一条 DS_Sample_Item_Income 入库记录",
                new[] { "DS_Sample_Item" }, null);
            AddRule(result, "FQC_Sample_NC", "抽样不合格数量",
                "Quality_x/Form/frmFQC_Input_0.vb", 160,
                "成品抽检录入把 SampleNC 数量保存到该字段",
                new[] { "FQC" }, null);
            AddRule(result, "CustomerTaxCode_MY", "马来西亚海关税则编码",
                "ERP/表-类定义/code_i.vb", 1,
                "马来西亚物料同步使用客户税则字段保存海关税则编码",
                new[] { "Item" }, null);
            AddRule(result, "Liner_Resonded", "线长是否已响应",
                "WorkStatation/frmReceiveAndShowNewIssue.vb", 147,
                "产线问题由线长响应时把该布尔字段置为真",
                new[] { "Line_Issue" }, null);
            AddRule(result, "Downed", "是否已下载",
                "ERP/表-类定义/code_Notice.vb", 1,
                "通知文件以该布尔字段记录文件是否已下载",
                new[] { "NR_File" }, null);
            AddRule(result, "FV", "文件版本",
                "ERP/表-类定义/code_Notice.vb", 1,
                "通知文件以 FV 保存所附文件版本号",
                new[] { "NR_File" }, null);
            AddRule(result, "Ext", "文件扩展名",
                "ERP/表-类定义/code_File.vb", 1,
                "文件目录树以 Ext 保存不带点号的文件扩展名",
                new[] { "PFT" }, null);
            AddRule(result, "FF", "是否文件夹",
                "ERP/表-类定义/code_File.vb", 1,
                "文件目录树用该布尔字段区分文件夹和文件",
                new[] { "PFT" }, null);
            AddRule(result, "Clicks", "点击次数",
                "ERP/表-类定义/code_G.vb", 1,
                "电话验证码记录以 Clicks 统计校验入口点击次数",
                new[] { "PhoneVerificationCodes" }, null);
            AddRule(result, "SPIP_OP", "操作类型",
                "ERP/表-类定义/Code_Plan.vb", 1,
                "计划粘贴记录使用该字段保存本次粘贴操作类型",
                new[] { "SPI_Paste" }, null);
            AddRule(result, "SPIP_op_TIme", "操作时间",
                "ERP/表-类定义/Code_Plan.vb", 1,
                "计划粘贴记录使用该字段保存本次操作时间",
                new[] { "SPI_Paste" }, null);
            AddRule(result, "Source_Mass", "来源计划类型",
                "ERP/表-类定义/code_Plan.vb", 47640,
                "SP_Audit 实体注释明确枚举 1 为量产、2 为样品计划",
                new[] { "SP_Audit" }, null);
            AddRule(result, "WindowsIdentity", "操作系统登录身份",
                "DataControl/SqlAudit.vb", 1,
                "SQL 审计头以 WindowsIdentity 保存操作系统登录身份",
                new[] { "SqlAuditHeader" }, null);
            AddRule(result, "Creatime", "创建时间",
                "ERP/表-类定义/code_Pur.vb", 1,
                "供应商批次明细字段虽存在拼写缩写但实际保存创建时间",
                new[] { "Supplier_Manu_Lot_Item" }, null);
            AddRule(result, "Content_RTF", "富文本内容",
                "ERP/Task/ERPTask.vb", 1,
                "任务消息正文使用富文本格式保存",
                new[] { "TTask_Msg" }, null);
            AddRule(result, "ReaderList", "阅读人列表",
                "ERP/Task/ERPTask.vb", 1,
                "任务消息冗余保存阅读人列表以避免重复查询",
                new[] { "TTask_Msg" }, null);
            AddRule(result, "Exer_Confirm_Method", "执行人确认方式",
                "ERP/Task/ERPTask.vb", 951,
                "任务版本以枚举值区分无需确认、接收者确认和发送者确认",
                new[] { "TTask_Ver" }, null);
            AddRule(result, "Savedate", "保存时间",
                "ToolingDeveloping/UserControl/ucReport.vb", 517,
                "五金月报记录使用该字段保存报表生成时间",
                new[] { "WMCR" }, null);
        }

        /// <summary>XMZADD 20260917 登记由界面别名、赋值链和计算公式进一步核验的业务值字段中文名。</summary>
        private static void AddAdditionalVerifiedValueRules(
            IDictionary<string, IList<VerifiedFieldRule>> result)
        {
            AddRule(result, "Wujin", "是否五金账",
                "W_Main/mod_main_w.vb", 1027,
                "车间账套查询以 Wujin 标记区分五金账，五金账仍是仓库业务流水而非财务总账",
                new[] { "Bu_Ac" }, null);
            AddRule(result, "DML", "数据操作类型",
                "ERP/表-类定义/code_Bu_Data_Partial_Sync.vb", 295,
                "本地数据同步对象把 DML 与表名、主键和值一起保存，用来表示本次数据操作类型",
                new[] { "DataOPLocalTable" }, null);
            AddRule(result, "DML", "数据操作类型",
                "Plan_x/采购计划/frmListpLog.vb", 22,
                "采购清单日志查询直接把 DML 显示为操作",
                new[] { "listp_Log" }, null);
            AddRule(result, "Y", "年份",
                "Plan_Customer/发货计划2019/发货计划2019.vb", 1,
                "发货计划转换记录把客户周历年份写入 Y",
                new[] { "DP_Cast" }, null);
            AddRule(result, "W", "周次",
                "Plan_Customer/发货计划2019/发货计划2019.vb", 1,
                "发货计划转换记录把客户周历序号写入 W",
                new[] { "DP_Cast" }, null);
            AddRule(result, "SD", "开始日期",
                "Plan_Customer/发货计划2019/发货计划2019.vb", 1,
                "发货计划转换记录把客户周历开始日期写入 SD",
                new[] { "DP_Cast" }, null);
            AddRule(result, "ED", "结束日期",
                "Plan_Customer/发货计划2019/发货计划2019.vb", 1,
                "发货计划转换记录把客户周历结束日期写入 ED",
                new[] { "DP_Cast" }, null);
            AddRule(result, "IDVal", "目标记录ID",
                "ERP/表-类定义/code_File_Partial.vb", 1,
                "编辑消息同时保存目标表、主键字段和目标记录值，IDVal 即被编辑记录的标识",
                new[] { "EditMsg" }, null);
            AddRule(result, "indexdesc", "字段释义",
                "ERP/表-类定义/code_OA_Partial.vb", 1,
                "OA 定义维护把 indexdesc 作为动态字段的释义显示",
                new[] { "ERP_OA_Definition_Table" }, null);
            AddRule(result, "Cal_Ye", "核算年份",
                "ERP/表-类定义/code_Pay_Partial.vb", 287,
                "财务中心月度初始化直接用核算日期年份填充 Cal_Ye",
                new[] { "FC_Month" }, null);
            AddRule(result, "Cal_Mo", "核算月份",
                "ERP/表-类定义/code_Pay_Partial.vb", 288,
                "财务中心月度初始化直接用核算日期月份填充 Cal_Mo",
                new[] { "FC_Month" }, null);
            AddRule(result, "HF_PxP", "补充公积金比例",
                "ERP/表-类定义/code_HR_Partial.vb", 3558,
                "公积金归档 SQL 直接把 HF_PxP 对应到补充比例",
                new[] { "HFR", "HouseFund" }, null);
            AddRule(result, "HF_P_x", "个人补充公积金金额",
                "ERP/表-类定义/code_HR_Partial.vb", 3606,
                "公积金报表直接把 HF_P_x 显示为个人补充公积金",
                new[] { "HFR_Plus" }, null);
            AddRule(result, "HF_E_x", "企业补充公积金金额",
                "ERP/表-类定义/code_HR_Partial.vb", 3607,
                "公积金报表直接把 HF_E_x 显示为企业补充公积金",
                new[] { "HFR_Plus" }, null);
            AddRule(result, "ST", "下次同步执行时间",
                "InvForOARefreshTool/Program.vb", 159,
                "库存同步工具在 UpdateNextRunTime 中把下一次执行时间写入 ST",
                new[] { "InvForOA_Synchronize_Time" }, null);
            AddRule(result, "BBP_BOM", "物料清单不变价构成金额",
                "G/frmCost.vb", 2743,
                "半成品成本计算把全部物料清单明细金额汇总写入 BBP_BOM",
                new[] { "Item_Half_BBP" }, null);
            AddRule(result, "BBP_BOM_Fuliao", "辅料不变价构成金额",
                "G/frmCost.vb", 2745,
                "半成品成本计算只汇总辅料类型明细金额写入该字段",
                new[] { "Item_Half_BBP" }, null);
            AddRule(result, "Icon16", "16像素图标",
                "ERP/SHBFile/UserControls/ucDepFile.vb", 310,
                "文件列表把该图标放入小图标集合，与三十二像素大图标成对使用",
                new[] { "ListIcon" }, null);
            AddRule(result, "VF_Creater", "版本创建人ID",
                "ERP/SHBFile/Classes/PF.vb", 887,
                "版本文件夹创建时把当前人员标识写入 VF_Creater",
                new[] { "PFT_V" }, null);
            AddRule(result, "VF_FF", "是否文件夹",
                "ERP/SHBFile/Classes/PF.vb", 887,
                "创建版本文件夹时 VF_FF 写一，创建文件记录时写零",
                new[] { "PFT_V" }, null);
            AddRule(result, "MPS_Des", "主生产计划描述",
                "Manufacture_x/生产过程/frmP2.vb", 827,
                "生产过程界面按 MPS_ID 读取 MPS_Des 并显示主生产计划类型文本",
                new[] { "P_MPS" }, null);
            AddRule(result, "FB_8D", "是否有八步法报告",
                "Quality/QualityOperation.vb", 573,
                "客户反馈导入依据是否提供八步法报告把该布尔字段置真或置假",
                new[] { "QC_CustomerFB" }, null);
            AddRule(result, "SPV", "标准工序版本",
                "Process_x/Form/frmStandard_Process.vb", 16,
                "标准工序查询直接把 SPV 显示为版本，并通过 SPV_ID 关联标准工序",
                new[] { "SPV" }, null);
            AddRule(result, "BD", "采购方式",
                "ERP/表-类定义/code_Pur.vb", 1,
                "字段注释枚举零为购买、一为委外加工，表示供应商物料采购方式",
                new[] { "Supplier_Material", "SM_Price_History" }, null);
            AddRule(result, "PSW", "是否批量采购",
                "ERP/表-类定义/code_Pur_Partial.vb", 1,
                "供应商物料维护以该布尔值区分批量采购关系",
                new[] { "Supplier_Material", "SM_Price_History" }, null);
            AddRule(result, "SMR", "是否纳入供应商报表",
                "ERP/表-类定义/code_Pur_Partial.vb", 1,
                "价格历史保存供应商物料是否纳入供应商统计报表的状态",
                new[] { "SM_Price_History" }, null);
            AddRule(result, "WMS_SKU", "仓储系统物料编码",
                "ERP/表-类定义/code_AHWMS_Partial.vb", 884,
                "供应商仓储标签的该字段与 EOS 物料金蝶编码匹配，用于定位物料",
                new[] { "Supplier_WMS_Label_AHSHB", "Supplier_WMS_ShipBatch_Item_AHSHB" }, null);
            AddRule(result, "V_LM", "期初结存金额",
                "ERP/表-类定义/code_Ac.vb", 21197,
                "仓库月报实体注释和结转计算把 V_LM 作为期初结存金额",
                new[] { "WMRi_Item" }, null);
        }

        /// <summary>XMZADD 20260918 登记由窗体标题、网格列名和扫码逻辑直接证明的业务字段中文名。</summary>
        private static void AddVerifiedUiCaptionRules(
            IDictionary<string, IList<VerifiedFieldRule>> result)
        {
            string lessonsPath = "LessonsLearned/LessonsLearnedMain.Designer.vb";
            AddRule(result, "PDRC", "问题描述及根本原因", lessonsPath, 1270,
                "经验教训主界面把 PDRC 列标题明确设置为问题描述及根本原因",
                new[] { "LessonsLearned" }, null);
            AddRule(result, "LLFNP", "新项目经验教训", lessonsPath, 1336,
                "经验教训主界面把 LLFNP 列标题明确设置为新项目经验教训",
                new[] { "LessonsLearned" }, null);
            AddRule(result, "P_D", "设计", lessonsPath, 1346,
                "经验教训主界面把 P_D 列标题明确设置为设计",
                new[] { "LessonsLearned" }, null);
            AddRule(result, "P_P", "过程", lessonsPath, 1356,
                "经验教训主界面把 P_P 列标题明确设置为过程",
                new[] { "LessonsLearned" }, null);
            AddRule(result, "YY", "应用范围", lessonsPath, 1396,
                "经验教训主界面把 YY 列标题设置为应用，编辑下拉框列出具体适用工序范围",
                new[] { "LessonsLearned" }, null);

            string qualityPath = "Quality_x/oldForms/frmQualityProject.Designer.vb";
            AddRule(result, "QP_1", "适用范围", qualityPath, 110,
                "质量试验窗体第一部分标题明确为适用范围",
                new[] { "Quality_Project" }, null);
            AddRule(result, "QP_2", "试验目的", qualityPath, 133,
                "质量试验窗体第二部分标题明确为试验目的",
                new[] { "Quality_Project" }, null);
            AddRule(result, "QP_3", "试验步骤", qualityPath, 156,
                "质量试验窗体第三部分标题明确为试验步骤",
                new[] { "Quality_Project" }, null);
            AddRule(result, "QP_4", "试验结果", qualityPath, 179,
                "质量试验窗体第四部分标题明确为试验结果",
                new[] { "Quality_Project" }, null);
            AddRule(result, "QP_5", "结论", qualityPath, 202,
                "质量试验窗体第五部分标题明确为结论",
                new[] { "Quality_Project" }, null);

            AddRule(result, "ER_P_C", "收款或付款人",
                "f_MIS/frmExpenseReimbursement.vb", 93,
                "费用报销查询直接把 ER_P_C 显示为收付款人，流水账按单据类型装载员工或供应商",
                new[] { "ExpenseReimbursement" }, null);
            AddRule(result, "CMK", "设备能力指数",
                "ERP/Process 工艺路线/frmFacility.Designer.vb", 91,
                "设备维护界面在能力参数区维护 CMK，制造业语境中该指标为设备能力指数",
                new[] { "Facility" }, null);
            AddRule(result, "SS", "是否缴纳社保",
                "ERP/研发/frmRDFee.vb", 236,
                "研发费用代码明确把 SS 作为社保标识，并按当月企业缴纳社保记录置真",
                new[] { "RD_HR_Month" }, null);
            AddRule(result, "QR1", "外箱条码",
                "ERP/表-类定义/code_SubsidiaryProgram.vb", 190,
                "扫码防错实体注释和界面都明确第一段扫码值来自外箱条码",
                new[] { "QR_Log" }, null);
            AddRule(result, "QR2", "内箱条码",
                "ERP/表-类定义/code_SubsidiaryProgram.vb", 195,
                "扫码防错实体注释和界面都明确第二段扫码值来自内箱条码",
                new[] { "QR_Log" }, null);
            AddRule(result, "PF", "内外箱条码是否匹配",
                "SubsidiaryProgram/条码扫描防错/条码防错.vb", 170,
                "扫码防错代码比较内外箱条码后把匹配布尔结果写入 PF",
                new[] { "QR_Log" }, null);
        }

        /// <summary>XMZADD 20260918 登记由业务代码赋值、查询别名和界面含义共同核实的剩余字段中文名。</summary>
        private static void AddRemainingVerifiedValueRules(
            IDictionary<string, IList<VerifiedFieldRule>> result)
        {
            string customerMonthPath = "Program_x/合同与费用/frmCu_Pr_Month.vb";
            AddRule(result, "SY", "起始年份", customerMonthPath, 111,
                "客户月度价格维护把开始年份写入 SY，并用它组成价格有效年份区间",
                new[] { "Cu_Pr_Month_List" }, null);
            AddRule(result, "EY", "结束年份", customerMonthPath, 111,
                "客户月度价格维护把结束年份写入 EY，并用它组成价格有效年份区间",
                new[] { "Cu_Pr_Month_List" }, null);
            AddRule(result, "NCIR", "不合格品评审报告编号",
                "Quality_x/oldForms/frmCustomer_Situation.Designer.vb", 248,
                "客户质量情况界面把 NCIR 明确标注为不合格品评审报告的引用编号",
                new[] { "Customer_Situation" }, null);
            AddRule(result, "SB_P_N", "单张钢板出产数",
                "W/冲制/frmCZ_SB.vb", 295,
                "冲制钢板业务录入新的出产数，并按该值乘钢板张数计算预计出产数量",
                new[] { "CZ_Out" }, null);
            AddRule(result, "DIIS", "来料送仓记录ID",
                "ERP/表-类定义/Code_ItemIncome_Partial.vb", 26607,
                "来料送仓时把送仓主记录 ID 写入 DIIS，并据此回查未完成的来料明细",
                new[] { "DS_Item_Income_Item" }, null);
            AddRule(result, "ED_PRS_FMEA", "是否更新失效模式及影响分析",
                "ERP/Purchase 采购/ucED.vb", 948,
                "工程纪律预防再发生界面用复选框记录是否更新 FMEA",
                new[] { "EDisciplines_PR_Set" }, null);
            AddRule(result, "seclevel", "办公自动化系统安全级别",
                "ERP/表-类定义/code_OAOP_Partial.vb", 2292,
                "办公自动化人员同步读取 HrmResource 安全级别并映射到安全级别字段",
                new[] { "HrmResource" }, null);
            AddRule(result, "Qry", "数量",
                "JL/Code_LM_JL_Partial.vb", 119,
                "计量记录实体把 Qry 的中文常量直接定义为数量",
                new[] { "LM_JL" }, null);
            AddRule(result, "Kids", "操作类型",
                "JL/Code_LM_JL_Partial.vb", 92,
                "计量备注按新增、修改、删除和检定等操作类型筛选 Kids",
                new[] { "LM_Notes" }, null);
            AddRule(result, "MST_LO", "线长ID",
                "Manufacture_x/Form/frmSheet_M.vb", 109,
                "制造排产界面用员工 ID 绑定线长下拉框并把所选值写入 MST_LO",
                new[] { "M_ST_A" }, null);
            AddRule(result, "MTP_WDs", "工作日天数",
                "Manufacture_x/Form/frmMTP.vb", 41,
                "制造时间计划界面把 MTP_WDs 明确作为工作日天数维护",
                new[] { "M_TP" }, null);
            AddRule(result, "Woker", "模具负责人ID",
                "ToolingDeveloping/Forms/frmMoldTransferExportToReport.vb", 36,
                "模具移交报表把人力资源 ID 写入 Woker，把姓名另存为 WokerName",
                new[] { "Mold_Transfer_Tracing" }, null);
            AddRule(result, "T1", "首次试模交付周期天数",
                "ToolingDeveloping/Forms/frmDevelopmentTracing.vb", 165,
                "模具开发跟踪把 T1 显示为首次试模交付，并以天数参与周期日期计算",
                new[] { "Mold_Type_Cycle", "Mold_Development_Tracking" }, null);
            AddRule(result, "OP_Json", "回传办公自动化系统数据",
                "ERP/表-类定义/code_OAOP_Partial.vb", 71,
                "办公自动化操作实体注释明确该字段保存回传办公自动化系统的数据",
                new[] { "OAOP" }, null);
            AddRule(result, "POH", "小时产能",
                "ERP/Process 工艺路线/frmNewPR.Designer.vb", 516,
                "工艺路线界面把 POH 列标题明确设置为小时产能",
                new[] { "P" }, null);
            AddRule(result, "POH", "计划每小时产量",
                "Manufacture_x/Form/frmProduct_Al_V5.vb", 136,
                "产品装配线维护把 POH 明确显示并保存为计划每小时产量",
                new[] { "Product_Assembly_Line" }, null);
            AddRule(result, "Insql", "入托盘数据库语句",
                "MotorBarcodeScan/托盘/frmPalletScanOutNewV1.vb", 1167,
                "托盘扫码补偿日志保存用于执行入托盘二的数据库语句",
                new[] { "PickPalletInLog" }, null);
            AddRule(result, "Deletesql", "出托盘数据库语句",
                "MotorBarcodeScan/托盘/frmPalletScanOutNewV1.vb", 1172,
                "托盘扫码补偿日志保存用于执行出托盘一的数据库语句",
                new[] { "PickPalletInLog" }, null);
            AddRule(result, "BBP", "产品不变价",
                "ERP/表-类定义/Code_Inv_Value_Report_Partial.vb", 374,
                "库存价值报表把 Product_BBP.BBP 直接显示为产品不变价",
                new[] { "Product_BBP" }, null);
            AddRule(result, "RuiAn", "是否按瑞安结算",
                "ERP/Bu.vb", 11400,
                "项目查询按 RuiAn 布尔值显示是否采用瑞安结算",
                new[] { "Program" }, null);
            AddRule(result, "St_Q", "暂存发货数量",
                "Plan_x/采购计划/frmProductList.vb", 169,
                "采购计划暂存时把发货数量写入 St_Q，重新打开后再还原到发货数量",
                new[] { "Purchaseplan_Save_Temp" }, null);
            AddRule(result, "YG_Ven_Kiscode", "安徽金蝶供应商编码",
                "Purchasex/供应商管理/frmKisBuyerMaintain.vb", 131,
                "供应商金蝶维护把该字段显示为安徽编码，并在安徽雨刮账套中作为供应商编码使用",
                new[] { "Supplier" }, null);
            AddRule(result, "Kiscode", "金蝶物料编码",
                "Web_Supplier_Service/frmManuRec_FBDC.vb", 383,
                "供应商制造批次转换记录从物料金蝶编码输入框写入 Kiscode",
                new[] { "Supplier_Manu_Lot_FBDC" }, null);
            AddRule(result, "KISTYPE", "金蝶账套类型",
                "ERP/表-类定义/code_Pur_Partial.vb", 8484,
                "采购业务枚举把 KISTYPE 限定为座椅、雨刮或马来金蝶账套",
                new[] { "Supplier_Material" }, null);
            AddRule(result, "P", "不变价",
                "ERP/Bu.vb", 27154,
                "仓库月报查询把 P 直接别名为不变价，并由物料不变价成本赋值",
                new[] { "WMRi_Item" }, null);
            AddRule(result, "P2", "实际材料成本单价",
                "ERP/表-类定义/code_Cost_Partial.vb", 2824,
                "仓库月报结转把物料实际成本写入 P2，与 P 中的不变价成本区分",
                new[] { "WMRi_Item" }, null);
            AddRule(result, "Q1", "前一个月发货数量",
                "Plan_x/采购计划/frmProductList.vb", 92,
                "采购计划查询把 Q1 明确作为前一个月的发货数量并写入临时表",
                new[] { "ztemp_purchase" }, null);
            AddRule(result, "Q2", "前两个月发货数量",
                "Plan_x/采购计划/frmProductList.vb", 92,
                "采购计划查询把 Q2 明确作为前两个月的发货数量并写入临时表",
                new[] { "ztemp_purchase" }, null);
            AddRule(result, "Abbreviation", "国家缩写",
                "HR_X/Form_Man/frmHR_New.vb", 3647,
                "员工国籍选择查询把 Abbreviation 作为 Country 国家主数据的简称搜索字段",
                new[] { "Country" }, null);
            AddRule(result, "Continent", "大洲ID",
                "ERP/CPP 客户 项目 产品/frmResearchAndDevelopmentTopic.vb", 670,
                "客户项目查询用 Country.Continent 关联 Continent.ID，明确保存所属大洲标识",
                new[] { "Country" }, null);
        }

        /// <summary>XMZADD 20260917 把汽车项目节点和控制计划中的行业缩写展开为完整中文业务名。</summary>
        private static void AddVerifiedDomainAbbreviationRules(
            IDictionary<string, IList<VerifiedFieldRule>> result)
        {
            string milestonePath = "ERP/CPP 客户 项目 产品/frmTechAnalysis.vb";
            AddRule(result, "DV", "设计验证日期", milestonePath, 1618,
                "项目技术分析把 DV 作为汽车项目设计验证节点日期", new[] { "CF_PRM_PRD" }, null);
            AddRule(result, "EMC", "电磁兼容验证日期", milestonePath, 1618,
                "项目技术分析把 EMC 作为电磁兼容验证节点日期", new[] { "CF_PRM_PRD" }, null);
            AddRule(result, "PPAP", "生产件批准日期", milestonePath, 1618,
                "项目技术分析把 PPAP 作为生产件批准节点日期", new[] { "CF_PRM_PRD" }, null);
            AddRule(result, "PV", "生产验证日期", milestonePath, 1618,
                "项目技术分析把 PV 作为生产验证节点日期", new[] { "CF_PRM_PRD" }, null);
            AddRule(result, "SOPplus3M", "量产后三个月日期", milestonePath, 1657,
                "项目技术分析 SQL 直接显示为量产后三个月节点", new[] { "CF_PRM_PRD" }, null);
            AddRule(result, "T0", "首次试模日期", milestonePath, 1618,
                "项目技术分析把 T0 作为首次试模节点日期", new[] { "CF_PRM_PRD" }, null);

            string controlPlanPath = "Manufacture_x/生产过程/frmP2.vb";
            AddRule(result, "M_PPT", "产品或过程规范及公差", controlPlanPath, 1068,
                "控制计划查询直接给出 Product/Process Specification/Tolerance 标题", new[] { "P_CP" }, null);
            AddRule(result, "M_EMT", "评价方法与技术", controlPlanPath, 1068,
                "控制计划查询直接给出 Evaluation Method Technique 标题", new[] { "P_CP" }, null);
            AddRule(result, "M_S_S", "样本数量", controlPlanPath, 1068,
                "控制计划查询直接给出 Sample Size 标题", new[] { "P_CP" }, null);
            AddRule(result, "M_S_F", "抽样频率", controlPlanPath, 1069,
                "控制计划查询直接给出 Sample Frequency 标题", new[] { "P_CP" }, null);
            AddRule(result, "M_C_M", "控制方法", controlPlanPath, 1069,
                "控制计划查询直接给出 Control Method 标题", new[] { "P_CP" }, null);
            AddRule(result, "M_R", "责任人", controlPlanPath, 1069,
                "控制计划查询直接给出 Responsible 标题", new[] { "P_CP" }, null);
            AddRule(result, "RP", "反应计划", controlPlanPath, 1069,
                "控制计划查询直接给出 Reaction Plan 标题", new[] { "P_CP" }, null);
            AddRule(result, "SCC", "特殊特性分类", controlPlanPath, 1067,
                "控制计划查询直接给出 Special Characteristic Class 标题", new[] { "P_CP" }, null);
        }

        /// <summary>XMZADD 20260917 按年份生成成本字段规则，并区分金额、成本对象和采购价来源标记。</summary>
        private static void AddAnnualCostRules(
            IDictionary<string, IList<VerifiedFieldRule>> result)
        {
            for (int year = 2018; year <= 2025; year++)
            {
                string yearText = year.ToString();
                AddRule(result, "BOMF" + yearText, yearText + "年BOM成本金额",
                    "ERP/表-类定义/code_Cost_Partial.vb", 426,
                    "成本计算按年度汇总 Cost_Bom 的 BOM 成本金额",
                    new[] { "Cost_Bom" }, null);
                AddRule(result, "BOMF" + yearText + "PP", yearText + "年成本是否取采购价",
                    "ERP/表-类定义/code_Cost_Partial.vb", 1516,
                    "年度成本取采购价格时把对应 PP 来源标记置为真",
                    new[] { "Cost_Bom" }, null);
                AddRule(result, "F" + yearText, yearText + "年物料成本金额",
                    "ERP/表-类定义/code_Cost_Partial.vb", 876,
                    "物料成本表按年度保存从 BOM 汇总得到的成本金额",
                    new[] { "Item_Cost" }, null);
                AddRule(result, "F" + yearText, yearText + "年产品成本金额",
                    "ERP/表-类定义/code_Cost_Partial.vb", 865,
                    "产品成本版本按年度保存产品成本金额",
                    new[] { "Product_Cost_Ver" }, null);
            }
        }

        /// <summary>XMZADD 20260917 登记布局坐标和金蝶基础资料内码等具有稳定外部系统含义的字段。</summary>
        private static void AddLayoutAndKingdeeRules(
            IDictionary<string, IList<VerifiedFieldRule>> result)
        {
            AddRule(result, "Location_X", "横向坐标",
                "WorkStatation/ucLineStatus.vb", 272,
                "产线看板用该值计算控件横向位置",
                new[] { "P_WC", "P_WC_Station" }, null);
            AddRule(result, "Location_Y", "纵向坐标",
                "WorkStatation/ucLineStatus.vb", 276,
                "产线看板用该值计算控件纵向位置",
                new[] { "P_WC", "P_WC_Station" }, null);
            AddRule(result, "FSTOCKID", "金蝶仓库内码",
                "CopyKis/frmSyncKis.vb", 455,
                "金蝶仓库基础资料和单据分录通过 FSTOCKID 关联",
                new[] { "Kis_Stock" }, null);
            AddRule(result, "FSTOCKNAME", "金蝶仓库名称",
                "CopyKis/frmSyncKis.vb", 455,
                "金蝶仓库本地快照保存仓库名称",
                new[] { "Kis_Stock" }, null);
            AddRule(result, "FSTOCKNUMBER", "金蝶仓库编码",
                "CopyKis/frmSyncKis.vb", 455,
                "金蝶仓库本地快照保存仓库业务编码",
                new[] { "Kis_Stock" }, null);
            AddRule(result, "FCATEGORYID", "金蝶物料类别内码",
                "CopyKis/frmSyncKis.vb", 2620,
                "金蝶物料扩展表通过 FCATEGORYID 关联物料类别基础资料",
                new[] { "Kis_Material_Category" }, null);
            AddRule(result, "FMASTERID", "金蝶物料主数据内码",
                "CopyKis/frmSyncKis.vb", 390,
                "金蝶物料查询以 FMASTERID 标识物料主数据",
                new[] { "Kis_Item_Allocate" }, null);
            AddRule(result, "FID", "金蝶供应商内码",
                "CopyKis/frmSyncKis.vb", 1,
                "金蝶供应商本地映射以 FID 保存供应商基础资料内码",
                new[] { "Kis_Supplier" }, null);
        }

        /// <summary>XMZADD 20260919 登记源码关联、界面标题或实体层级已证明的剩余业务标识字段。</summary>
        private static void AddRemainingUsedIdentifierRules(
            IDictionary<string, IList<VerifiedFieldRule>> result)
        {
            AddRule(result, "AZD_ID", "待更新物料版本记录ID",
                "Plan_x/BOM/frmP_Bom.vb", 2066,
                "BOM 更新流程通过该字段关联待处理的物料版本脏数据记录",
                new[] { "ABOMZ_DirtyIVID" }, null);
            AddRule(result, "AcBT_ID", "日记账账本科目关联ID",
                "ERP/Account 仓库帐/AC.vb", 394,
                "日记账配置使用该记录维护账本可选科目",
                new[] { "AcBT" }, null);
            AddRule(result, "AP_ID", "包装记录ID",
                "ERP/表-类定义/code_Ac.vb", 1,
                "包装明细通过该字段归属于包装主记录",
                new[] { "Account_Packing_Item" }, null);
            AddRule(result, "Leave_DII_ID", "出库生成的物料入库记录ID",
                "ERP/表-类定义/code_Trade_Partial.vb", 2296,
                "贸易托盘出库会生成物料入库主记录并保存其标识",
                new[] { "Account_Pallet_Trade_Item" }, null);
            AddRule(result, "Leave_DIII_ID", "出库生成的物料入库明细ID",
                "ERP/表-类定义/code_Trade_Partial.vb", 2302,
                "贸易托盘出库会生成物料入库明细并保存其标识",
                new[] { "Account_Pallet_Trade_Item" }, null);
            AddRule(result, "DO_ID", "发货指令ID",
                "ERP/Account 仓库帐/AC.vb", 2602,
                "仓库流水单据通过该字段追溯对应发货指令",
                new[] { "Account_Sheet" }, null);
            AddRule(result, "SID_UID", "流水账单据全局唯一标识",
                "ERP/Account 仓库帐/Ac_Sheet.vb", 1058,
                "流水账单据复制和保存时以 Guid 保持跨记录唯一性",
                new[] { "Account_Sheet" }, null);
            AddRule(result, "PI_ID", "贸易托盘流水明细ID",
                "ERP/表-类定义/code_Trade_Partial.vb", 3207,
                "流水单据关联表通过该字段指向贸易托盘流水明细",
                new[] { "Account_Sheet_Trade_Pallet" }, null);
            AddRule(result, "SM_ID", "供应商物料关系ID",
                "ERP/Bu.vb", 1586,
                "五金流水单据通过该字段关联供应商物料关系",
                new[] { "Account_Sheet_WJ" }, null);
            AddRule(result, "ASE_ID2", "对应调拨事件ID",
                "W/类/W_Pro.vb", 7511,
                "库位调拨的出入两侧事件通过该字段彼此对应",
                new[] { "Account_Storage_Part2_Event" }, null);
            AddRule(result, "FID", "金蝶单据内码",
                "CopyKis/frmSyncKis.vb", 1,
                "额外关联记录通过该字段保存金蝶业务单据内码",
                new[] { "Ac_Kis_Extra_Link" }, null);
            AddRule(result, "AP_ID", "流水账与采购订单明细关联ID",
                "ERP/Account 仓库帐/Ac_Title.vb", 1299,
                "该关联记录连接仓库流水账和采购订单明细",
                new[] { "Ac_POI" }, null);
            AddRule(result, "Ace_ID", "日记账相关方类别",
                "ERP/Account 仓库帐/AC.vb", 22041,
                "日记账按该类别区分业务相关方且不同于财务流转账户",
                new[] { "Ac_Title" }, null);
            AddRule(result, "R_ID", "领用归还类型",
                "ERP/表-类定义/code_Asset_new.vb", 467,
                "资产申请以零表示归还、一表示领用",
                new[] { "Asset_Requisition" }, null);
            AddRule(result, "Sender_HR_ID", "发送人ID",
                "ERP/frmLogin.vb", 5774,
                "外部仓库逾期提醒保存实际发送提醒的人员标识",
                new[] { "AWarehouse_Overdue_Alarm_Record" }, null);
            AddRule(result, "Spid", "数据库会话进程ID",
                "DataControl/SQLHelper.vb", 1,
                "阻塞日志以 SQL Server SPID 定位发生阻塞的数据库会话",
                new[] { "Block_Log" }, null);
            AddRule(result, "PM_ID", "事业部生产期间ID",
                "W/线边仓/frmWorkLineInAndOut.vb", 1405,
                "期间明细通过该字段归属于事业部生产期间主记录",
                new[] { "Bu_Period_Manu_Item" }, null);
            AddRule(result, "CCT_ID", "客户投诉类型ID",
                "Customer_Complain/frmCC_Manage.vb", 1352,
                "客户投诉通过该字段关联投诉类型定义",
                new[] { "CC_Complain" }, null);
            AddRule(result, "CCA_ID", "客户投诉行动计划ID",
                "Customer_Complain/frmCC_Manage.vb", 1,
                "行动计划执行人通过该字段归属于客户投诉行动计划",
                new[] { "CC_Complain_ActionPlan_Executer" }, null);
            AddRule(result, "Related_CCPS_ID", "关联投诉部件ID",
                "Customer_Complain/frmRTF_Editor_For_CC.vb", 414,
                "投诉部件编辑时把被关联部件的 CCPS_ID 保存到该字段",
                new[] { "CC_Complain_Parts" }, null);
            AddRule(result, "CSGS_ID", "发运缺口返工快照ID",
                "ERP/表-类定义/code_Rework.vb", 1,
                "快照明细通过该字段归属于发运缺口返工快照",
                new[] { "ContainShipGap_Rework_Snapshot_Item" }, null);
            AddRule(result, "CMT_ID", "合同模板类型ID",
                "ERP/合同/frmContractEdit.vb", 1036,
                "合同编辑界面通过该字段选择合同模板类型",
                new[] { "Contract" }, null);
            AddRule(result, "CI_SI_ID", "供应商发票ID",
                "ERP/Purchase 采购/Purchase.vb", 12468,
                "合同发票记录从供应商发票对象取得 SI_ID",
                new[] { "Contract_Invoice" }, null);
            AddRule(result, "CBE_ID", "成本BOM额外物料ID",
                "ERP/成本/frmCost_BOM.vb", 407,
                "成本 BOM 明细通过该字段关联额外物料记录",
                new[] { "Cost_Bom" }, null);
            AddRule(result, "AZ_ID", "成本BOM明细ID",
                "ERP/成本/frmCost_BOM.vb", 519,
                "委外成本记录通过该字段关联成本 BOM 明细",
                new[] { "Cost_BOM_WW" }, null);
            AddRule(result, "MR_ID", "成本月报ID",
                "ERP/表-类定义/code_Cost_Partial.vb", 1,
                "成本月报主表以该字段标识一个月份的成本报表",
                new[] { "Cost_MR" }, null);
            AddRule(result, "MRI_ID", "成本月报物料明细ID",
                "ERP/表-类定义/code_Cost_Partial.vb", 1,
                "成本月报物料明细以该字段标识一条物料成本记录",
                new[] { "Cost_MR_Part" }, null);
            AddRule(result, "CRx_ID", "客户订单需求记录ID",
                "ERP/表-类定义/code_CPO.vb", 1,
                "客户订单需求表以该字段标识一条需求记录",
                new[] { "CPO_R" }, null);
            AddRule(result, "CSM_ID", "客户项目产品评分标记ID",
                "ERP/CPP 客户 项目 产品/frmTechAnalysis.vb", 3034,
                "技术分析评分界面以该字段标识一条评分标记",
                new[] { "CPP_Score_Mark" }, null);
            AddRule(result, "PPS_ID", "项目产品系列ID",
                "ERP/CPP 客户 项目 产品/frmTechAnalysis.vb", 3068,
                "评分标记通过该字段关联项目产品系列",
                new[] { "CPP_Score_Mark" }, null);
            AddRule(result, "CU_SV_ID", "关联销售款ID",
                "ERP/表-类定义/code_Cu_BusinessFee_Partial.vb", 1579,
                "业务费用付款实体明确将该字段作为关联销售款 SV_ID",
                new[] { "Cu_Business_Fee_Pay" }, null);
            AddRule(result, "AR_ID", "客户发票分析备注ID",
                "ERP/表-类定义/code_Cu_Invoice.vb", 1,
                "客户发票分析备注表以该字段标识备注记录",
                new[] { "Cu_Invoice_Analysis_Remark" }, null);
            AddRule(result, "CP_ID", "客户价格ID",
                "G/frmProgramGrade.vb", 694,
                "客户价格及其月度清单通过该字段关联客户价格记录",
                new[] { "Cu_PRi", "Cu_Pr_Month_List_Item", "Cu_Pr_QuickUpdateLog" }, null);
            AddRule(result, "CPML_ID", "客户价格月度清单ID",
                "G/frmProgramGrade.vb", 694,
                "月度清单及其明细使用该字段关联同一客户价格月度清单",
                new[] { "Cu_Pr_Month_List", "Cu_Pr_Month_List_Item" }, null);
            AddRule(result, "CPMLI_ID", "客户价格月度清单明细ID",
                "G/frmProgramGrade.vb", 711,
                "客户价格月度清单明细以该字段标识一条价格记录",
                new[] { "Cu_Pr_Month_List_Item" }, null);
            AddRule(result, "SPA_ID", "供应商价格协议ID",
                "ERP/表-类定义/code_Supplier_Price.vb", 1,
                "客户价格与供应商协议关系通过该字段关联供应商价格协议",
                new[] { "CU_PR_PA" }, null);

            AddRule(result, "FT_DBM_ID", "预算方法ID",
                "f_MIS/预算/frmFi_Budget.vb", 110,
                "费用类型通过该字段关联预算方法定义",
                new[] { "DA_Fee_Type" }, null);
            AddRule(result, "OA_ID2", "泛微OA部门ID",
                "Org/OA_Match/frmDepartmentOA.vb", 1,
                "部门匹配流程把第二套泛微 OA 部门标识保存到该字段",
                new[] { "Department" }, null);
            AddRule(result, "OA_ID2", "泛微OA人员ID",
                "Org/OA_Match/frmHROA.vb", 1,
                "人员匹配流程把第二套泛微 OA 人员标识保存到该字段",
                new[] { "HR" }, null);
            AddRule(result, "HPt_ID", "人事岗位ID",
                "HR_X/Form_Man/frmHR_Position_CC.vb", 77,
                "部门岗位关系通过该字段关联 HR_Post 岗位定义",
                new[] { "Dep_Post" }, null);
            AddRule(result, "ForAwarehouse_ID", "发货目标外部仓库ID",
                "ERP/表-类定义/code_Plan_CF_Partial.vb", 2454,
                "发货计划和发货指令通过该字段关联目标外部仓库",
                new[] { "DPI", "DP_Order" }, null);
            AddRule(result, "DOP_ID", "发货指令预拆分记录ID",
                "Plan_Customer/发货计划2019/frmDPOrder_PreSplit.vb", 1,
                "预拆分明细通过该字段归属于发货指令预拆分主记录",
                new[] { "DP_Order_PreSplit_Item" }, null);
            AddRule(result, "DORI_ID", "发货签收明细ID",
                "Plan_Customer/发货计划2019/frmDeliveryDone_V3.vb", 109,
                "签收明细与仓库流水关联表通过该字段指向发货签收明细",
                new[] { "DP_Order_Receipt_Item_DPI_AC" }, null);
            AddRule(result, "DPTI_ID", "发货指令模板明细ID",
                "ERP/表-类定义/code_DP.vb", 1,
                "模板明细计划通过该字段归属于发货指令模板明细",
                new[] { "DP_Template_Item_Plan" }, null);
            AddRule(result, "DPTIP_ID", "发货指令模板明细计划ID",
                "ERP/表-类定义/code_DP.vb", 1,
                "计划子项通过该字段归属于发货指令模板明细计划",
                new[] { "DP_Template_Item_Plan_Item" }, null);
            AddRule(result, "DSC_ID", "工装委托记录ID",
                "ToolingDeveloping/Forms/frmConsign.vb", 1,
                "工装委托日志通过该字段关联委托主记录",
                new[] { "Ds_Consign_Log" }, null);
            AddRule(result, "DSD_ID", "申请业务类型ID",
                "ERP/ERP.vb", 2208,
                "申请表通过 DS_Define 枚举区分五金采购、样件采购和用人申请等业务类型",
                new[]
                {
                    "DS_Dep_Handler", "DS_HR_Application", "DS_Purchase", "DS_Sample",
                    "Material_Info_Management", "Purchase_Order"
                }, null);
            AddRule(result, "WWT_ID", "工装工时记录ID",
                "ToolingDeveloping/Forms/frmWorkTime.vb", 153,
                "样件申请通过该字段关联工装申请工时记录",
                new[] { "DS_Sample" }, null);
            AddRule(result, "billid", "泛微OA表单ID",
                "ERP/CPP 客户 项目 产品/frmMarketInfoWithOA.vb", 29,
                "OA 定义映射以该字段保存泛微表单标识",
                new[] { "ERP_OA_Definition_Table" }, null);
            AddRule(result, "ER_ID", "费用报销单ID",
                "SubsidiaryProgram/费用报销/FrmFYBXD.vb", 1,
                "费用与交通费明细通过该字段归属于费用报销单",
                new[] { "ExpenseDetails", "ExpenseDetailsTraffic" }, null);
            AddRule(result, "FAI_ID", "收发存来源类别ID",
                "ERP/Account 仓库帐/AC.vb", 2699,
                "设施审核日志通过该字段关联 FA_Source 收发存来源类别",
                new[] { "FacAuditLog" }, null);
            AddRule(result, "FAT_ID", "收发存定义ID",
                "ERP/Account 仓库帐/AC.vb", 2700,
                "设施审核日志通过该字段关联 FA_IO 收发存定义",
                new[] { "FacAuditLog" }, null);
            AddRule(result, "FGD_ID", "设施组部署ID",
                "Facility/frmFG_New.vb", 1,
                "设施组部署及其操作记录以该字段关联同一次部署",
                new[] { "Facility_Operation", "FG_Deploy" }, null);
            AddRule(result, "P_FiPT_ID", "上级标准产品类型ID",
                "ERP/表-类定义/code_Fi.vb", 1,
                "标准产品类型通过该字段建立父子层级",
                new[] { "Fi_Standard_Product_Type" }, null);
            AddRule(result, "FSPTH_ID", "标准产品类型人员ID",
                "ERP/表-类定义/code_Fi.vb", 1,
                "标准产品类型人员表以该字段标识人员配置记录",
                new[] { "Fi_Standard_Product_Type_Hr" }, null);
            AddRule(result, "FSPT_ID", "标准产品类型ID",
                "ERP/表-类定义/code_Fi.vb", 1,
                "人员配置通过该字段关联标准产品类型",
                new[] { "Fi_Standard_Product_Type_Hr" }, null);
            AddRule(result, "XID", "授权范围对象ID",
                "ERP/Company.vb", 3318,
                "财务模块权限按授权范围保存公司、成本中心或事业部对象标识",
                new[] { "FM_Permit" }, null);
            AddRule(result, "FT_ID", "文件模板ID",
                "File_x/Controls/ucFile.vb", 3380,
                "文件模板及其部门关系使用该字段关联模板定义",
                new[] { "FT", "FT_Dep" }, null);

            AddRule(result, "IT_ID", "发票类型ID",
                "ERP/表-类定义/code_Invoice.vb", 1,
                "发票类型定义表以该字段标识发票类型",
                new[] { "Invoice_Type" }, null);
            AddRule(result, "IB_ID", "物料不变价ID",
                "ERP/表-类定义/code_Cost_KBP_Partial.vb", 187,
                "物料不变价表以该字段标识一条不变价记录",
                new[] { "Item_BBP" }, null);
            AddRule(result, "IB_ID", "半成品不变价ID",
                "ERP/表-类定义/code_Cost_KBP_Partial.vb", 187,
                "半成品不变价表以该字段标识一条不变价记录",
                new[] { "Item_Half_BBP" }, null);
            AddRule(result, "Catia_FB_ID", "CATIA文件库ID",
                "ERP/SHBFile/UserControls/ucFile_3D.vb", 1259,
                "三维图档从文件库子目录取得 CATIA 文件库标识",
                new[] { "Item_File_3D" }, null);
            AddRule(result, "Catia_FI_ID", "CATIA文件ID",
                "ERP/SHBFile/UserControls/ucFile_3D.vb", 1259,
                "三维图档保存 CATIA 文件记录标识",
                new[] { "Item_File_3D" }, null);
            AddRule(result, "cgr_FB_ID", "CGR文件库ID",
                "ERP/SHBFile/UserControls/ucFile_3D.vb", 1290,
                "三维图档从文件库子目录取得 CGR 文件库标识",
                new[] { "Item_File_3D" }, null);
            AddRule(result, "cgr_FI_ID", "CGR文件ID",
                "ERP/SHBFile/UserControls/ucFile_3D.vb", 1290,
                "三维图档保存 CGR 文件记录标识",
                new[] { "Item_File_3D" }, null);
            AddRule(result, "SW_FB_ID", "SolidWorks文件库ID",
                "ERP/SHBFile/UserControls/ucFile_3D.vb", 1320,
                "三维图档从文件库子目录取得 SolidWorks 文件库标识",
                new[] { "Item_File_3D" }, null);
            AddRule(result, "SW_FI_ID", "SolidWorks文件ID",
                "ERP/SHBFile/UserControls/ucFile_3D.vb", 1320,
                "三维图档保存 SolidWorks 文件记录标识",
                new[] { "Item_File_3D" }, null);
            AddRule(result, "UG_FB_ID", "UG文件库ID",
                "ERP/SHBFile/UserControls/ucFile_3D.vb", 1361,
                "三维图档从文件库子目录取得 UG 文件库标识",
                new[] { "Item_File_3D" }, null);
            AddRule(result, "UG_FI_ID", "UG文件ID",
                "ERP/SHBFile/UserControls/ucFile_3D.vb", 1361,
                "三维图档保存 UG 文件记录标识",
                new[] { "Item_File_3D" }, null);
            AddRule(result, "CAD_FB_ID", "CAD文件库ID",
                "ERP/SHBFile/UserControls/ucFile_CAD.vb", 634,
                "二维图档从文件库子目录取得 CAD 文件库标识",
                new[] { "Item_File_Drawing" }, null);
            AddRule(result, "CAD_FI_ID", "CAD文件ID",
                "ERP/SHBFile/UserControls/ucFile_CAD.vb", 634,
                "二维图档保存 CAD 文件记录标识",
                new[] { "Item_File_Drawing" }, null);
            AddRule(result, "PDF_FB_ID", "PDF文件库ID",
                "ERP/SHBFile/UserControls/ucFile_CAD.vb", 669,
                "二维图档从文件库子目录取得 PDF 文件库标识",
                new[] { "Item_File_Drawing" }, null);
            AddRule(result, "PDF_FI_ID", "PDF文件ID",
                "ERP/SHBFile/UserControls/ucFile_CAD.vb", 669,
                "二维图档保存 PDF 文件记录标识",
                new[] { "Item_File_Drawing" }, null);
            AddRule(result, "KB_ID", "物料可变价明细ID",
                "ERP/表-类定义/code_Cost_KBP_Partial.vb", 187,
                "物料可变价明细以该字段标识一条物料价格记录",
                new[] { "Item_KBP" }, null);
            AddRule(result, "KBP_ID", "物料可变价版本ID",
                "ERP/表-类定义/code_Cost_KBP_Partial.vb", 187,
                "物料可变价版本表以该字段标识一个价格版本",
                new[] { "Item_KBP_Main" }, null);
            AddRule(result, "IKP_ID", "物料可变价基础价格ID",
                "ERP/表-类定义/code_Cost_KBP_Partial.vb", 187,
                "物料可变价基础价格表以该字段标识价格记录",
                new[] { "Item_KBP_Price" }, null);
            AddRule(result, "KBM_ID", "物料月度可变价明细ID",
                "ERP/表-类定义/code_Cost_KBP_Partial.vb", 196,
                "物料月度可变价明细以该字段标识一条价格记录",
                new[] { "Item_KBPM" }, null);
            AddRule(result, "KBPM_ID", "物料月度可变价版本ID",
                "ERP/表-类定义/code_Cost_KBP_Partial.vb", 196,
                "月度可变价明细和版本表通过该字段关联同一版本",
                new[] { "Item_KBPM", "Item_KBPM_Main" }, null);
            AddRule(result, "IKPM_ID", "物料月度可变价基础价格ID",
                "ERP/表-类定义/code_Cost_KBP_Partial.vb", 196,
                "物料月度可变价基础价格表以该字段标识价格记录",
                new[] { "Item_KBPM_Price" }, null);
            AddRule(result, "KSOS_ID", "金蝶委外仓月报ID",
                "G/frmCost.Designer.vb", 2532,
                "成本界面把 KSOS 月报明确显示为金蝶委外仓月报",
                new[] { "KSOS_Month_Report_Item" }, null);
            AddRule(result, "KSWL_ID", "金蝶线边仓月报ID",
                "G/frmCost.Designer.vb", 2550,
                "成本界面把 KSWL 月报明确显示为金蝶线边仓月报",
                new[] { "KSWL_Month_Report_Item" }, null);
            AddRule(result, "xLT_ID", "物料提前期设置ID",
                "Plan_x/采购计划/frmPPP_List_MRP_Setup.vb", 437,
                "物料需求计划设置通过该字段标识一条物料提前期配置",
                new[] { "Lead_Time" }, null);
            AddRule(result, "MIA_ID", "顶工位调整记录ID",
                "ERP/表-类定义/code_PR.vb", 1,
                "顶工位调整表以该字段标识一条调整记录",
                new[] { "M_IA" }, null);
            AddRule(result, "MIA_MPO_ID", "加工组工序ID",
                "ERP/表-类定义/code_PR.vb", 1,
                "顶工位调整记录通过该字段关联加工组工序",
                new[] { "M_IA" }, null);
            AddRule(result, "MIA_MS_ID", "加工组ID",
                "ERP/表-类定义/code_PR.vb", 1,
                "顶工位调整记录通过该字段关联加工组",
                new[] { "M_IA" }, null);
            AddRule(result, "MPO_PID", "工序ID",
                "ERP/表-类定义/code_PR.vb", 1,
                "加工组工序定义通过该字段关联工序",
                new[] { "M_PO" }, null);
            AddRule(result, "MS_ID", "加工组ID",
                "ERP/表-类定义/code_PR.vb", 1,
                "加工组工序定义通过该字段关联加工组",
                new[] { "M_PO" }, null);
            AddRule(result, "MPR_ID", "生产计件记录ID",
                "ERP/表-类定义/code_PR.vb", 1,
                "生产计件主记录以该字段标识一次计件业务",
                new[] { "M_PR" }, null);
            AddRule(result, "MPR_RID", "来源调整记录ID",
                "ERP/表-类定义/code_PR.vb", 1,
                "生产计件记录通过该字段追溯产生本记录的调整业务",
                new[] { "M_PR" }, null);
            AddRule(result, "MRA_ID", "合格率补贴记录ID",
                "ERP/表-类定义/code_PR.vb", 1,
                "合格率补贴表以该字段标识一条补贴记录",
                new[] { "M_RA" }, null);
            AddRule(result, "MSE_ID", "加工组工序操作工配置ID",
                "ERP/表-类定义/code_PR.vb", 1,
                "加工组工序操作工配置表以该字段标识配置记录",
                new[] { "M_SE" }, null);
            AddRule(result, "MSE_MPO_ID", "加工组工序ID",
                "ERP/表-类定义/code_PR.vb", 1,
                "操作工配置通过该字段关联加工组工序",
                new[] { "M_SE" }, null);
            AddRule(result, "MST_ID", "生产统计记录ID",
                "ERP/表-类定义/code_PR.vb", 1,
                "生产统计表以该字段标识一条统计记录",
                new[] { "M_ST_A" }, null);
            AddRule(result, "MTS_ID", "生产计时记录ID",
                "ERP/表-类定义/code_PR.vb", 1,
                "生产计时表以该字段标识一条计时记录",
                new[] { "M_TS" }, null);
            AddRule(result, "MPRD_MPO_ID", "加工组工序ID",
                "ERP/表-类定义/code_PR.vb", 1,
                "生产计件明细通过该字段关联加工组工序",
                new[] { "MPR_Detail" }, null);
            AddRule(result, "MPRD_MPR_ID", "生产计件记录ID",
                "ERP/表-类定义/code_PR.vb", 1,
                "生产计件明细通过该字段归属于计件主记录",
                new[] { "MPR_Detail" }, null);
            AddRule(result, "MPRD_MS_ID", "加工组ID",
                "ERP/表-类定义/code_PR.vb", 1,
                "生产计件明细通过该字段关联加工组",
                new[] { "MPR_Detail" }, null);

            AddRule(result, "PFE_ID", "过程失效模式及影响分析ID",
                "ERP/表-类定义/code_PR_partial.vb", 1,
                "过程失效模式及影响分析主表和一级条目通过该字段关联",
                new[] { "P_FEMA", "P_FEMA1" }, null);
            AddRule(result, "PFE1_ID", "过程失效模式及影响分析一级条目ID",
                "ERP/表-类定义/code_PR_partial.vb", 1,
                "一级和二级条目通过该字段关联过程失效分析一级条目",
                new[] { "P_FEMA1", "P_FEMA2" }, null);
            AddRule(result, "PFE2_ID", "过程失效模式及影响分析二级条目ID",
                "ERP/表-类定义/code_PR_partial.vb", 1,
                "二级和三级条目通过该字段关联过程失效分析二级条目",
                new[] { "P_FEMA2", "P_FEMA3" }, null);
            AddRule(result, "PFE3_ID", "过程失效模式及影响分析三级条目ID",
                "ERP/表-类定义/code_PR_partial.vb", 1,
                "过程失效分析三级表以该字段标识三级条目",
                new[] { "P_FEMA3" }, null);
            AddRule(result, "PPG_ID", "项目工艺组ID",
                "ERP/CPP 客户 项目 产品/frmTechAnalysis.vb", 1,
                "项目工艺组及其成员通过该字段关联同一个工艺组",
                new[] { "ProgramProcess_Group", "ProgramProcess_GroupMember" }, null);
            AddRule(result, "PTG_ID", "项目技术组ID",
                "ERP/CPP 客户 项目 产品/frmTechAnalysis.vb", 1,
                "项目技术组及其成员通过该字段关联同一个技术组",
                new[] { "ProgramTech_Group", "ProgramTech_GroupMember" }, null);
            AddRule(result, "MR_ID", "供应商月报成本中心ID",
                "ERP/表-类定义/code_Supplier_Month_Report.vb", 1,
                "供应商月报成本中心表以该字段标识一条成本中心记录",
                new[] { "SMR_CC" }, null);
            AddRule(result, "MRI_ID", "供应商月报成本中心明细ID",
                "ERP/表-类定义/code_Supplier_Month_Report.vb", 1,
                "供应商月报成本中心明细表以该字段标识明细记录",
                new[] { "SMR_CC_Item" }, null);
            AddRule(result, "SPA_SM_ID", "供应商价格协议物料明细ID",
                "ERP/表-类定义/code_Supplier_Price.vb", 1,
                "价格协议物料及其修订记录通过该字段关联原始物料明细",
                new[] { "Supplier_Material_PA", "Supplier_Material_PAX" }, null);
            AddRule(result, "SPA_SM_IDX", "供应商价格协议物料修订明细ID",
                "ERP/表-类定义/code_Supplier_Price.vb", 1,
                "价格协议物料修订表以该字段标识一条修订明细",
                new[] { "Supplier_Material_PAX" }, null);
            AddRule(result, "PP_ID", "供应商结算日期定义ID",
                "ERP/Purchase 采购/Purchase.vb", 1,
                "供应商结算日期定义表以该字段标识一条结算配置",
                new[] { "Supplier_PP" }, null);
            AddRule(result, "WMRO_ID", "仓库月报物料排序ID",
                "W/月报/frmWJ_Generate2023.vb", 1,
                "仓库月报物料排序表以该字段标识一条排序配置",
                new[] { "WMRO" }, null);
            AddRule(result, "WR_ID", "仓库月报ID",
                "W/月报/frmWJ_Generate2023.vb", 1,
                "仓库月报主表以该字段标识一个年月的月报",
                new[] { "WMRol" }, null);
        }

        /// <summary>XMZADD 20260919 登记人员、生产、采购和文件流程中已核验的上级业务对象标识。</summary>
        private static void AddVerifiedOperationalIdentifierRules(
            IDictionary<string, IList<VerifiedFieldRule>> result)
        {
            AddRule(result, "GMRPT_ID", "工模产品报表大类ID",
                "ERP/ERP.vb", 2194,
                "工模产品报表枚举把该对象定义为自制设备、工检模和外购设备模具的大类",
                new[] { "GMreProduct_Type" }, null);
            AddRule(result, "OK_ID", "批准人ID",
                "HR_X/离职/FrmHR_Leave.vb", 27,
                "离职审批界面通过 HRS_OK 读取批准人名称",
                new[] { "HRS_OK", "HR_Change" }, null);
            AddRule(result, "HRA_ID", "人员任职记录ID",
                "ERP/表-类定义/code_HR.vb", 1,
                "人员任职记录同时保存员工、公司、部门、岗位和任职日期",
                new[] { "HR_Adm" }, null);
            AddRule(result, "Hre_ID", "请假记录ID",
                "HR_X/ETC/frmHR_Askforleave.vb", 1,
                "请假业务窗体以 HR_Etc 保存员工请假记录",
                new[] { "HR_Etc" }, null);
            AddRule(result, "HRD_ID", "生产人员配置ID",
                "Manufacture_x/Form/frmHR_Deploy.vb", 163,
                "生产人员配置界面通过该字段关联 HR_Deploy 配置定义",
                new[] { "HR_Operation" }, null);
            AddRule(result, "HRPBD_ID", "人事个人业务定义ID",
                "HR_X/个人业务/frmHR_PB_Record.vb", 178,
                "员工个人业务记录通过该字段选择 HR_PB_Define 业务定义",
                new[] { "HR_PB" }, null);
            AddRule(result, "PMD_ID", "工资结算月份ID",
                "ERP/表-类定义/code_HR_Partial.vb", 3422,
                "部门工时导入记录通过该字段关联工资结算月份定义",
                new[] { "HR_WorkTime_Dep_XLS" }, null);
            AddRule(result, "HFID", "HTML文档文件夹ID",
                "ERP/SHBFile/Classes/HtmlDocManager.vb", 121,
                "HTML 文档创建流程通过该字段归属于文档文件夹",
                new[] { "HTML_Doc" }, null);
            AddRule(result, "IQCCI_ID", "来料检验项目ID",
                "Quality_x/oldForms/frmIQCCI.vb", 1,
                "来料检验维护界面以该字段关联检验项目和检验明细",
                new[] { "IQCCI", "IQC_Detail" }, null);
            AddRule(result, "IAH_ID", "物料信息审核人员记录ID",
                "ERP/CCP.vb", 5725,
                "物料信息审核流程以该字段标识审核人员的阅读和确认记录",
                new[] { "Item_Info_Audit_HR" }, null);
            AddRule(result, "IL_ID", "物料图标ID",
                "ERP/表-类定义/code_i.vb", 1,
                "物料图标表以该字段标识一组不同尺寸的物料图标",
                new[] { "Item_Logo" }, null);
            AddRule(result, "YG_FID", "雨刮金蝶入库单内码",
                "CopyKis/frmSyncKis.vb", 5630,
                "雨刮金蝶入库单同步价格时保存金蝶入库单 FID",
                new[] { "Kis_YGRKD_UpdateSM" }, null);
            AddRule(result, "LT_ID", "清单类型ID",
                "Plan_x/采购计划/frmPP2.vb", 14,
                "计划界面按该字段区分成品生产计划列表与采购清单",
                new[] { "Lists" }, null);
            AddRule(result, "JL_ID", "计量器具ID",
                "JL/FrmQCNew.vb", 554,
                "计量器具检定记录通过该字段关联计量器具主档",
                new[] { "LM_QC" }, null);
            AddRule(result, "Des_ID", "目标客户工厂ID",
                "W/成品帐/frmAccountProduct.vb", 1,
                "批次记录通过该字段关联发货目标客户工厂",
                new[] { "Lot" }, null);
            AddRule(result, "MPCG_ID", "计算机管理分组ID",
                "ERP/表-类定义/code_Manage_PC.vb", 1,
                "受管计算机通过该字段归属于站点管理分组",
                new[] { "Manage_PC" }, null);
            AddRule(result, "Pshift_ID", "生产班次ID",
                "ERP/表-类定义/code_MPIWC.vb", 1,
                "模具生产记录通过该字段关联生产班次定义",
                new[] { "MouldProduction_record_CZZS" }, null);
            AddRule(result, "MP_ID", "主生产计划ID",
                "Plan_x/Plan.vb", 1,
                "生产计划界面以 MP 为计划主表并以 MPI 保存计划明细",
                new[] { "MP" }, null);
            AddRule(result, "MBTS_ID", "车间计划休息时间状态ID",
                "ERP/表-类定义/code_MPIWC.vb", 1,
                "车间计划休息时间状态定义区分用餐等暂停时段",
                new[] { "MPIWC_Break_Time_Status" }, null);
            AddRule(result, "MIS_ID", "车间发料单ID",
                "ERP/表-类定义/code_Plan_Partial.vb", 3890,
                "扫码出库记录和发料记录通过该字段归属于车间发料单",
                new[] { "MpiWc_Iss", "PartScanOut" }, null);
            AddRule(result, "CPOI_ID", "客户订单明细ID",
                "ERP/表-类定义/code_MPIWC_Partial.vb", 5782,
                "车间计划从来源供应计划取得客户订单明细并装载 CPO_Item",
                new[] { "MPI_WC" }, null);
            AddRule(result, "MPIWC_FIPT_ID", "标准产品类型ID",
                "ERP/表-类定义/code_MPIWC_Partial.vb", 6367,
                "车间计划通过该字段关联 Fi_Standard_Product_Type 标准产品类型",
                new[] { "MPI_WC" }, null);
            AddRule(result, "MPT_ID", "生产过程模板ID",
                "ERP/表-类定义/code_Product.vb", 1,
                "产品和模板记录通过该字段关联生产过程模板",
                new[] { "MP_Template", "Product" }, null);
            AddRule(result, "OA_MBS_ID", "泛微OA业务系统ID",
                "SHB_Communication/Msg.vb", 1167,
                "消息同步把该字段作为泛微 OA 业务系统来源标识",
                new[] { "Msg_Business_System" }, null);
            AddRule(result, "ME_ID", "文件期待记录ID",
                "File_x/frmMyExp.vb", 1,
                "文件期待界面以该字段标识用户登记的一条期待记录",
                new[] { "MyExp" }, null);
            AddRule(result, "Insertid", "创建人ID",
                "ERP/表-类定义/code_PR.vb", 26312,
                "5S 检查记录同时保存创建人标识和创建人姓名",
                new[] { "M_5S" }, null);
            AddRule(result, "MR_Ac_ID", "退货流水账记录ID",
                "Manufacture_x/Form/frmm_Return.vb", 67,
                "座椅电机退货统计通过该字段关联对应流水账记录",
                new[] { "m_Return" }, null);
            AddRule(result, "NP_ID", "项目节点阶段ID",
                "Program_x/frmNode.vb", 17,
                "项目节点界面通过该字段关联 Nodes_Phrase 中的阶段定义",
                new[] { "Nodes" }, null);
            AddRule(result, "NC_ID", "项目节点完成形式ID",
                "Program_x/frmNode.vb", 17,
                "项目节点通过该字段关联完成形式和提交内容定义",
                new[] { "Nodes_Commit" }, null);
            AddRule(result, "PN_ID", "预排库存记录ID",
                "ERP/表-类定义/code_Package_Partial.vb", 754,
                "托盘预排记录按业务类型关联托盘或非托盘库存记录",
                new[] { "Pallets_Preseted" }, null);
            AddRule(result, "FCM_ID", "财务中心核算月份ID",
                "ERP/表-类定义/code_Pay_Partial.vb", 263,
                "工资结算记录通过该字段关联财务中心、年份和月份组成的核算月份",
                new[] { "Pay_DelFeeTab", "Pay_Month_Define" }, null);
            AddRule(result, "MPP_ID", "月度采购计划ID",
                "Plan_x/Plan.vb", 2433,
                "采购计划主表按年月保存，明细通过该字段归属于月度采购计划",
                new[] { "PP", "PPI" }, null);
            AddRule(result, "PPAP_ID", "生产件批准程序定义ID",
                "Web_Supplier_Service/UserControls/ucSQM.vb", 184,
                "供应商批准记录通过该字段关联 PPAP_Define 生产件批准程序定义",
                new[] { "PPAP_Supplier" }, null);
            AddRule(result, "PPAPS_ID", "供应商生产件批准记录ID",
                "Web_Supplier_Service/UserControls/ucSQM.vb", 252,
                "供应商批准操作日志通过该字段归属于供应商生产件批准记录",
                new[] { "PPAP_Supplier_OP" }, null);
            AddRule(result, "PPP_ID", "供应商采购计划ID",
                "ERP/表-类定义/code_Plan.vb", 1,
                "供应商采购计划与采购订单关联表通过该字段指向供应商采购计划",
                new[] { "PPP_Purchase_Order" }, null);
            AddRule(result, "Ori_PPP_ID", "原供应商采购计划ID",
                "ERP/表-类定义/code_Plan.vb", 1,
                "供应商采购计划变更记录通过该字段保留变更前计划记录",
                new[] { "PPP_Supplier_Change" }, null);
        }

        /// <summary>XMZADD 20260919 登记流水、OA、工艺路线、质量和要货计划流程中已核验的业务标识。</summary>
        private static void AddVerifiedWorkflowIdentifierRules(
            IDictionary<string, IList<VerifiedFieldRule>> result)
        {
            AddRule(result, "ID2", "流水ID",
                "Kanban/流转/frmProductLabel.vb", 60,
                "电机标签流转查询直接把 ID2 显示为流水ID",
                new[] { "Code_Motor_Out", "MPIWC_Code_Motor_WIP" }, null);
            AddRule(result, "Used_DA_ID", "使用承兑票据的财务流水账ID",
                "ERP/表-类定义/code_DA_Partial.vb", 333,
                "承兑票据被使用时保存使用该票据的 DA_Account 财务流水账记录ID",
                new[] { "DA_Acceptance" }, null);
            AddRule(result, "OA_ID", "泛微OA流程请求ID",
                "ERP/表-类定义/Code_OA_Expense_Payment_Partial.vb", 126,
                "OA付款流程生成财务流水账时把 requestId 写入该字段",
                new[] { "DA_Account" }, null);
            AddRule(result, "COMPANYOA_ID", "泛微OA分部ID",
                "InvForOARefreshTool/Program.vb", 193,
                "OA库存刷新程序把 oa_subcompanyid2 写入该字段",
                new[] { "InvForOA" }, null);
            AddRule(result, "Des_Lib_ID", "快捷方式目标文件库ID",
                "ERP/表-类定义/code_File_Partial.vb", 45,
                "PFT 快捷方式通过目标文件库ID和目标文件ID定位实际文件",
                new[] { "PFT" }, null);
            AddRule(result, "Pro_FL_ID", "项目开发文件库ID",
                "ERP/表-类定义/code_Program_Develop_Partial.vb", 215,
                "项目开发记录通过该字段保存项目开发根目录所属文件库",
                new[] { "Program_Develop" }, null);
            AddRule(result, "WID", "仓库ID",
                "f_MIS/供应商/frmF_Supplier.vb", 1021,
                "采购价值流水按 enWarehouse 固定仓库ID关联对应仓库流水账",
                new[] { "Purchase_Value" }, null);
            AddRule(result, "PB_ID", "工艺路线工序ID",
                "Manufacture_x/生产过程/frmP2.vb", 3653,
                "P_B 把工艺路线 PPID 与工序 CPID 组成一条有序工艺路线工序",
                new[] { "P_B" }, null);
            AddRule(result, "SG_PP_ID", "工艺路线ID",
                "ERP/表-类定义/code_PR_partial.vb", 2693,
                "工序段配置通过该字段保存所属工艺路线 PPID",
                new[] { "P_B_Segment" }, null);
            AddRule(result, "SG_CP_ID", "工序ID",
                "ERP/表-类定义/code_PR_partial.vb", 2693,
                "工序段配置通过该字段保存工艺路线中的子工序 CPID",
                new[] { "P_B_Segment" }, null);
            AddRule(result, "tl_id", "质量任务问题ID",
                "TQC/Task_Control.vb", 106,
                "质量任务行动项通过该字段归属于 QC_Task_List 中登记的问题",
                new[] { "QC_Task_Action" }, null);
            AddRule(result, "RDT_ID", "研发人员类别ID",
                "ERP/研发/frmRDFee.vb", 876,
                "研发部门配置界面把该字段显示为人员类别并关联 RD_Dep_HR_Type",
                new[] { "RD_Dep" }, null);
            AddRule(result, "RIT_ID", "五金月报物料分类ID",
                "W/月报/frmWJItemTypeClass.vb", 53,
                "五金月报物料分类维护通过该字段关联劳保、易损品、油品等分类",
                new[] { "ReWJItem_Type" }, null);
            AddRule(result, "SaiX_ID", "原安全库存设置ID",
                "Plan_Customer/要货计划/frmSNP_List.vb", 647,
                "安全库存设置变更时新记录通过该字段保留原记录ID",
                new[] { "SafeInv" }, null);
            AddRule(result, "SIS_ID", "供应商发票状态ID",
                "f_MIS/供应商/frmF_Supplier.vb", 1052,
                "供应商采购价值流水通过该字段关联并显示发票状态",
                new[] { "SI_Status" }, null);
            AddRule(result, "SMP_ID", "桌面共享会话ID",
                "SHB_Communication/SMP_Partial.vb", 44,
                "发起桌面共享时每个接收人通过该字段归属于同一共享会话",
                new[] { "SMP_Receiver" }, null);
            AddRule(result, "SPIx_ID", "原要货计划明细ID",
                "ERP/表-类定义/code_Plan_Partial.vb", 6115,
                "要货计划调整生成新记录时通过该字段保留调整前的计划明细ID",
                new[] { "spi", "SPI_Adjust" }, null);
            AddRule(result, "SPI_ID", "要货计划明细ID",
                "Plan_Customer/要货计划/frmSNP_Fulfill.vb", 372,
                "要货计划与生产计划分配记录通过该字段关联具体要货计划明细",
                new[] { "spi", "SPI_MPI" }, null);
            AddRule(result, "SMPii_ID", "要货计划与生产计划期初库存明细ID",
                "Plan_Customer/要货计划/frmSNP_Fulfill.vb", 2007,
                "要货计划分配记录通过该字段关联 SP_MP_Ini_Inv 期初库存明细",
                new[] { "SPI_MPI" }, null);
            AddRule(result, "SPIP_ID", "要货计划粘贴导入批次ID",
                "Plan_Customer/要货计划/frmSNP_Import.vb", 1555,
                "每条粘贴导入明细通过该字段归属于同一次导入操作批次",
                new[] { "SPI_Paste_Item" }, null);
            AddRule(result, "Sources_WM_Application_ID", "来源工模开发单ID",
                "ERP/表-类定义/code_Tooling.vb", 2754,
                "工单拆分指派时复制来源工模开发单ID并据此避免重复指派",
                new[] { "WM_Application" }, null);
            AddRule(result, "WmM_ID", "工模父级项目ID",
                "ERP/表-类定义/code_Tooling_partial.vb", 1537,
                "工模申请菜单和业务界面通过该字段关联并显示父级项目",
                new[] { "WM_Application" }, null);
            AddRule(result, "WM_ID", "工模预审采购需求申请单ID",
                "ERP/表-类定义/code_Tooling_partial.vb", 2167,
                "工模预审记录通过该字段关联预审流程创建的采购需求申请单",
                new[] { "Wm_Application_PreAudit" }, null);
            AddRule(result, "WmPID", "工模父级项目预审核记录ID",
                "ToolingDeveloping/Forms/frmWMNeedApplication.vb", 766,
                "预审核操作日志通过该字段归属于工模父级项目预审核记录",
                new[] { "Wm_Application_PreAuditLog" }, null);
            AddRule(result, "DSC_ID", "工模委外申请单ID",
                "ToolingDeveloping/Forms/frmWorkTime.vb", 356,
                "工模工时记录通过该字段关联由工模委外窗体创建的委外申请单",
                new[] { "WM_Application_WorkTime" }, null);
            AddRule(result, "WTF_ID", "工模加工类型计费标准ID",
                "ToolingDeveloping/Forms/frmWorkTime.vb", 111,
                "工模工时通过该字段取得加工类型、自制委外方式、单位和工价",
                new[] { "WM_Application_WorkTime" }, null);
            AddRule(result, "WM_ID", "工模部门采购需求申请单ID",
                "ERP/表-类定义/code_Tooling.vb", 21803,
                "工模采购物料明细通过该字段归属于部门采购需求申请单",
                new[] { "WM_Purchase_Item" }, null);
            AddRule(result, "WS_ID", "生产车间ID",
                "Plan_x/车间生产计划/frmMPC_Week.vb", 258,
                "车间计划界面直接把 WS_ID 显示为生产车间并与 WS_MPI 关联",
                new[] { "WS", "WS_MPI" }, null);
        }

        /// <summary>XMZADD 20260921 登记销售来源、文件、付款、任务与月报流程中已由源码闭环的业务标识。</summary>
        private static void AddVerifiedRemainingBusinessIdentifierRules(
            IDictionary<string, IList<VerifiedFieldRule>> result)
        {
            AddRule(result, "SV_DD_ID", "来源发货执行记录ID",
                "ERP/表-类定义/code_Plan_CF_Partial.vb", 6338,
                "销售价值记录保存其来源 DP_Order_Exe 发货执行记录ID",
                new[] { "Sale_Value" }, null);
            AddRule(result, "SV_DORI_ID", "来源发货签收明细ID",
                "ERP/表-类定义/code_Plan_CF_Partial.vb", 6336,
                "销售价值记录保存其来源 DP_Order_Receipt_Item 发货签收明细ID",
                new[] { "Sale_Value" }, null);
            AddRule(result, "SV_DORIAC_ID", "来源签收与发货关联记录ID",
                "ERP/表-类定义/code_Plan_CF_Partial.vb", 6340,
                "销售价值记录保存签收明细与发货执行记录之间的关联记录ID",
                new[] { "Sale_Value" }, null);
            AddRule(result, "SPOD_ID", "文件操作定义ID",
                "ERP/表-类定义/code_SP_File_Partial.vb", 1695,
                "文件知会记录通过该字段关联操作名称定义",
                new[] { "SP_File_OP_Define" }, null);
            AddRule(result, "CPP_FI_ID", "客户项目产品根文件夹ID",
                "ERP/SHBFile/UserControls/ucFile_CPP.vb", 242,
                "客户项目产品目录记录保存其上级业务对象根文件夹ID",
                new[] { "SP_Folder_CPP_EX" }, null);
            AddRule(result, "SP_ID", "标准工序ID",
                "Process_x/Process.vb", 37,
                "工序库把标准工序ID写入 SP_Image 以保存对应工序图片",
                new[] { "SP_Image" }, null);
            AddRule(result, "SMP_ID", "要货计划与生产计划关联配置ID",
                "ERP/表-类定义/Code_Plan3_partial.vb", 66,
                "期初库存明细通过该字段归属于要货计划与生产计划关联配置",
                new[] { "SP_MP_Ini_Inv" }, null);
            AddRule(result, "SPMI_ID", "供应商应付账款明细ID",
                "ERP/表-类定义/code_PayPlan_Partial.vb", 4167,
                "供应商预付预算操作通过该字段关联 Supplier_Pay_AP_Item 应付账款明细",
                new[] { "Supplier_AheadBudget_Operate" }, null);
            AddRule(result, "FBDC_FID", "金蝶分步式调出单内码",
                "ERP/表-类定义/Code_ItemIncome_Partial.vb", 26451,
                "供应商制造批次映射保存金蝶分步式调出单 T_STK_STKTRANSFEROUT 的 FID",
                new[] { "Supplier_Manu_Lot_FBDC" }, null);
            AddRule(result, "ReMSID", "被回复消息ID",
                "SHB_Communication/Msg.vb", 985,
                "回复消息保存被回复消息对象的 MSID",
                new[] { "Supplier_MSG" }, null);
            AddRule(result, "SPMI_ID", "供应商应付账款明细ID",
                "Purchase_PayPlan/frmPayPlan_HardWare.vb", 180,
                "五金付款申请通过该字段关联 Supplier_Pay_AP_Item 应付账款明细",
                new[] { "Supplier_PayAP_Hardware" }, null);
            AddRule(result, "SPBI_ID", "公司级供应商付款预算明细ID",
                "ERP/表-类定义/code_PayPlan_Partial.vb", 3900,
                "财务付款记录通过该字段归属于公司级供应商付款预算明细",
                new[] { "Supplier_PayPlan_Budget_Company_Item_Pay" }, null);
            AddRule(result, "SPBI_ID", "事业部供应商付款预算明细ID",
                "ERP/表-类定义/code_PayPlan_Partial.vb", 3982,
                "采购付款分配记录通过该字段归属于事业部供应商付款预算明细",
                new[] { "Supplier_PayPlan_Budget_Item_Pay" }, null);
            AddRule(result, "OA_SPA_ID", "泛微OA价格协议归档记录ID",
                "ERP/表-类定义/code_Pur_Partial.vb", 6915,
                "供应商价格协议以该字段区分泛微 OA 已回传和已归档状态",
                new[] { "Supplier_Price_Agreement" }, null);
            AddRule(result, "CP_ID", "客户支付记录ID",
                "ERP/CPP 客户 项目 产品/frmCU_SVP.vb", 71,
                "销售价值付款分配明细通过该字段归属于客户支付登记记录",
                new[] { "SV_Pay" }, null);
            AddRule(result, "DO_ID", "操作事件ID",
                "DataControl/Op.vb", 3918,
                "表活动标记保存触发当前表操作的操作事件ID",
                new[] { "Table_Activity_Mark" }, null);
            AddRule(result, "Pro_FL_ID", "所属项目文件库ID",
                "ERP/表-类定义/code_task_partial.vb", 4483,
                "项目任务保存其任务文件夹所属的项目文件库ID",
                new[] { "TTask" }, null);
            AddRule(result, "CT_ID", "子任务ID",
                "ERP/Task/ERPTask.vb", 1014,
                "任务父子关系表通过该字段保存子任务ID",
                new[] { "TTask_Sub" }, null);
            AddRule(result, "Exer_ID", "任务责任方ID",
                "ERP/表-类定义/code_task_partial.vb", 6182,
                "任务版本根据责任方类型保存员工或供应商责任方ID",
                new[] { "TTask_Ver" }, null);
            AddRule(result, "WT_ID", "仓库类型ID",
                "ERP/Warehouse.vb", 109,
                "仓库对象以 en_WT 强类型字段区分厂内、外部和寄存仓库类型",
                new[] { "Warehouse" }, null);
            AddRule(result, "WJ_ID", "五金仓库领用报表部门分组ID",
                "W/主界面/ucWJReport.vb", 161,
                "五金仓库领用报表通过 WI 映射到 WJ 部门分组并按分组排序展示",
                new[] { "WI", "WJ" }, null);
            AddRule(result, "WDC_ID", "雨刮部门成本中心ID",
                "f_MIS/Wiper/frmWiperFMS.vb", 37,
                "雨刮财务管理报表通过该字段把部门关联到雨刮部门成本中心",
                new[] { "Wiper_Dep_Cost_Center" }, null);
            AddRule(result, "WMRE_ID", "试制领料月报ID",
                "f_MIS/frmExperimentFee.vb", 69,
                "试制领料月报明细通过该字段归属于对应月份和成本中心的月报主记录",
                new[] { "WMRE_Item" }, null);
        }

        /// <summary>XMZADD 20260917 为已核验年月日拆分表登记字段规则，避免单字母在其他业务中被误译。</summary>
        private static void AddDateRules(IDictionary<string, IList<VerifiedFieldRule>> result)
        {
            string[] yearMonthTables =
            {
                "Account_Sheet", "Account_Sheet_WJ", "Ac_WJ", "Cu_Pr_Month_List_Item",
                "Department_Worker_Attendance", "GMreport_Month", "MPI", "MPI_Month",
                "MPI_WC", "MPI_Week", "MPI_Year", "MRP_Month", "PPP", "PPP_Month",
                "PPP_Supplier", "PPP_Supplier_Month", "PPP_Supplier_Year", "PPP_Year",
                "spi", "SPI_Adjust", "SPI_Month", "SPI_Year"
            };
            string[] shortDateTables =
            {
                "Bu_Plan_Goal", "Bu_Plan_Goal_Log", "Cu_Pay_Plan", "FC_Month",
                "Inv_Limit_Month", "Manu_IO_Report", "Sale_Report_SQL", "WW_Price"
            };
            AddRule(result, "Y", "年份", "ERP/表-类定义/code_MPI.vb", 1,
                "计划、报表和台账代码将 Y 与 M、D 共同保存为业务日期", yearMonthTables, null);
            AddRule(result, "M", "月份", "ERP/表-类定义/code_MPI.vb", 1,
                "计划、报表和台账代码将 M 与 Y、D 共同保存为业务日期", yearMonthTables, null);
            AddRule(result, "D", "日期", "ERP/表-类定义/code_MPI.vb", 1,
                "计划、报表和台账代码将 D 与 Y、M 共同保存为业务日期", yearMonthTables, null);
            AddRule(result, "Ye", "年份", "ERP/表-类定义/code_Bu_Plan.vb", 1,
                "月度目标和报表字段 Ye、Mo 分别表示年份和月份", shortDateTables, null);
            AddRule(result, "Mo", "月份", "ERP/表-类定义/code_Bu_Plan.vb", 1,
                "月度目标和报表字段 Ye、Mo 分别表示年份和月份", shortDateTables, null);
        }

        /// <summary>XMZADD 20260917 把一条核验规则追加到字段名索引并保留声明顺序。</summary>
        private static void AddRule(IDictionary<string, IList<VerifiedFieldRule>> result,
            string fieldName, string chineseName, string sourcePath, int sourceLine,
            string explanation, string[] exactTables, string[] tablePrefixes)
        {
            IList<VerifiedFieldRule> rules;
            if (!result.TryGetValue(fieldName, out rules))
            {
                rules = new List<VerifiedFieldRule>();
                result.Add(fieldName, rules);
            }
            rules.Add(new VerifiedFieldRule(chineseName, sourcePath, sourceLine,
                explanation, exactTables, tablePrefixes));
        }

        /// <summary>XMZADD 20260917 保存一条字段中文名及其适用表范围和源码证据。</summary>
        private sealed class VerifiedFieldRule
        {
            private readonly string[] exactTables;
            private readonly string[] tablePrefixes;

            /// <summary>XMZADD 20260917 初始化不可变的业务字段上下文规则。</summary>
            public VerifiedFieldRule(string chineseName, string sourcePath, int sourceLine,
                string explanation, string[] exactTables, string[] tablePrefixes)
            {
                ChineseName = chineseName;
                SourcePath = sourcePath;
                SourceLine = sourceLine;
                Explanation = explanation;
                this.exactTables = exactTables;
                this.tablePrefixes = tablePrefixes;
            }

            public string ChineseName { get; private set; }
            public string SourcePath { get; private set; }
            public int SourceLine { get; private set; }
            public string Explanation { get; private set; }
            public int ConfidenceScore { get { return 88; } }

            /// <summary>XMZADD 20260917 判断当前物理表是否处于规则已核验的业务范围内。</summary>
            public bool Matches(string tableName)
            {
                string value = tableName ?? string.Empty;
                if ((exactTables == null || exactTables.Length == 0) &&
                    (tablePrefixes == null || tablePrefixes.Length == 0))
                {
                    return true;
                }
                if (exactTables != null)
                {
                    for (int index = 0; index < exactTables.Length; index++)
                    {
                        if (string.Equals(value, exactTables[index], StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                }
                if (tablePrefixes != null)
                {
                    for (int index = 0; index < tablePrefixes.Length; index++)
                    {
                        if (value.StartsWith(tablePrefixes[index], StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                }
                return false;
            }
        }
    }
}
