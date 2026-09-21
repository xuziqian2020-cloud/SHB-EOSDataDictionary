using System;
using System.Collections.Generic;
using SHB.EosDataDictionary.Models;

namespace SHB.EosDataDictionary.Services
{
    /// <summary>XMZADD 20260919 按 EOS 源码和项目知识已核验结论补全缩写业务表的中文参考名。</summary>
    public sealed class VerifiedBusinessTableNameInferenceService
    {
        private static readonly IDictionary<string, VerifiedTableRule> Rules =
            CreateRules();

        /// <summary>XMZADD 20260919 逐表应用精确匹配规则并保留源码位置和业务判断依据。</summary>
        public void Apply(SnapshotData snapshot)
        {
            if (snapshot == null || snapshot.Tables == null)
            {
                return;
            }

            var nameLayer = new BusinessNameLayerService();
            for (int index = 0; index < snapshot.Tables.Count; index++)
            {
                TableMetadata table = snapshot.Tables[index];
                VerifiedTableRule rule;
                if (table == null || string.IsNullOrWhiteSpace(table.ObjectName) ||
                    !Rules.TryGetValue(table.ObjectName, out rule))
                {
                    continue;
                }

                // 只发布精确物理表名规则，避免把同一缩写在其他业务域中错误展开。
                nameLayer.ApplyTableCandidate(table, CreateCandidate(table, rule));
            }
        }

        /// <summary>XMZADD 20260919 创建不冒充正式结论但可追溯到 EOS 依据的表名候选。</summary>
        private static MetadataValue CreateCandidate(TableMetadata table,
            VerifiedTableRule rule)
        {
            string rawValue = table.ObjectName + "=" + rule.ChineseName;
            return new MetadataValue
            {
                Value = rule.ChineseName,
                Status = ConfidenceStatus.Guessed,
                ConfidenceScore = 90,
                SourceType = "EOS业务代码",
                SourceSummary = "EOS 源码或项目知识核验：" + rule.Explanation,
                Evidence = new List<EvidenceItem>
                {
                    new EvidenceItem
                    {
                        SourceType = "EOS业务代码",
                        SourcePath = rule.SourcePath,
                        SourceLine = rule.SourceLine,
                        RuleName = "VerifiedBusinessTableContext",
                        RawValue = rawValue,
                        OriginalText = table.ObjectName,
                        Explanation = rule.Explanation
                    }
                }
            };
        }

        /// <summary>XMZADD 20260919 建立以物理表名索引的业务规则以保证七万张表扫描性能稳定。</summary>
        private static IDictionary<string, VerifiedTableRule> CreateRules()
        {
            var result = new Dictionary<string, VerifiedTableRule>(
                StringComparer.OrdinalIgnoreCase);

            AddRule(result, "Abom_Ex_New", "物料清单扩展新版本",
                "docs_knowledge/01_数据库表结构/_00_表索引.md", 203,
                "项目表索引将该表归入物料清单扩展新版本业务对象");
            AddRule(result, "Abom_Ex_Old", "物料清单扩展旧版本",
                "docs_knowledge/01_数据库表结构/_00_表索引.md", 204,
                "项目表索引将该表归入物料清单扩展旧版本业务对象");
            AddRule(result, "Account_Pallet_Trade_BuSpec", "贸易托盘流水账事业部规格",
                "docs_knowledge/01_数据库表结构/_00_表索引.md", 44,
                "Account 在仓储托盘表族中表示出入库流水账，BuSpec 表示事业部规格");
            AddRule(result, "Account_Pallet_Trade_Item_POI", "贸易托盘流水明细与采购订单明细关联",
                "docs_knowledge/01_数据库表结构/_00_表索引.md", 46,
                "项目表索引和字段关系共同证明该表连接贸易托盘流水明细与采购订单明细");
            AddRule(result, "CII", "客户发票内容",
                "docs_knowledge/客户应收全流程学习讲义.md", 1,
                "客户应收流程将 CII 定义为客户发票内容明细对象");
            AddRule(result, "CPO_Item", "客户订单明细",
                "docs_knowledge/01_数据库表结构/_00_表索引.md", 345,
                "CPO 表族表示客户订单，Item 保存订单明细行");
            AddRule(result, "DP_Cast", "发货指令铸件信息",
                "docs_knowledge/01_数据库表结构/_00_表索引.md", 411,
                "该表保存发货指令模板对应的铸件业务信息");
            AddRule(result, "DP_Order_Exe", "发货指令执行记录",
                "docs_knowledge/01_数据库表结构/_00_表索引.md", 416,
                "该表以发货指令 ID 和执行流水 ID 保存执行记录");
            AddRule(result, "DP_Template_Item", "发货指令模板明细",
                "docs_knowledge/01_数据库表结构/_00_表索引.md", 423,
                "该表是发货指令模板的明细业务对象");
            AddRule(result, "DPO_Reject_Info", "发货指令驳回信息",
                "docs_knowledge/01_数据库表结构/_00_表索引.md", 425,
                "该表保存发货指令业务的驳回原因和处理信息");
            AddRule(result, "Ds_Item_Income_POI", "物料入库与采购订单明细关联",
                "docs_knowledge/01_数据库表结构/_00_表索引.md", 271,
                "该表用入库记录 ID 与采购订单明细 ID 建立业务关联");
            AddRule(result, "DS_Sample_Item_Bu", "样品物料事业部",
                "docs_knowledge/01_数据库表结构/_00_表索引.md", 1181,
                "项目表索引将该表定义为样品物料与事业部的归属记录");
            AddRule(result, "MRP_Plan_Type", "物料需求计划类型",
                "docs_knowledge/01_数据库表结构/_00_表索引.md", 396,
                "MRP 是物料需求计划，表中记录计划类型定义");
            AddRule(result, "NQS_NC_Review", "不符合项评审",
                "docs_knowledge/01_数据库表结构/_00_表索引.md", 667,
                "质量体系表族以 NC 表示不符合项并保存评审结论");
            AddRule(result, "NQS_NC_Review_HR", "不符合项评审人员",
                "docs_knowledge/01_数据库表结构/_00_表索引.md", 670,
                "该表保存不符合项评审参与人员");
            AddRule(result, "POI_Delivery_State", "采购订单明细交付状态",
                "docs_knowledge/01_数据库表结构/_00_表索引.md", 270,
                "POI 表示采购订单明细，该表保存明细行交付状态");
            AddRule(result, "POI_Delivery_State_Detail", "采购订单明细交付状态明细",
                "docs_knowledge/01_数据库表结构/_00_表索引.md", 271,
                "该表保存采购订单明细交付状态的变化明细");
            AddRule(result, "PPAP_Define", "生产件批准程序定义",
                "docs_knowledge/01_数据库表结构/_00_表索引.md", 672,
                "PPAP 是生产件批准程序，该表保存其定义");
            AddRule(result, "QRQC_Org_Group_Type", "快速质量响应组织组类型",
                "docs_knowledge/01_数据库表结构/_00_表索引.md", 662,
                "QRQC 是快速质量响应，表中定义组织组类型");
            AddRule(result, "Sale_ReportV2", "销售报表第二版",
                "docs_knowledge/01_数据库表结构/_00_表索引.md", 623,
                "表名版本后缀和报表代码共同表明这是销售报表第二版");
            AddRule(result, "SV_CII", "销售来源与客户发票内容关联",
                "docs_knowledge/客户应收全流程学习讲义.md", 1,
                "客户应收流程以该表连接销售来源项和客户发票内容");
            AddRule(result, "SV_CIIi", "销售来源与客户发票内容关联明细",
                "docs_knowledge/客户应收全流程学习讲义.md", 1,
                "该表保存销售来源与客户发票内容关联的明细行");
            AddRule(result, "WMR2", "仓库月报第二版",
                "docs_knowledge/01_数据库表结构/_00_表索引.md", 627,
                "仓库月报代码和版本后缀共同证明该表为第二版月报");

            AddRule(result, "Code_Box_MW", "雨刮托盘箱码入库明细",
                "MotorBarcodeScan/雨刮托盘/frmGenerateWiperPallet.vb", 145,
                "雨刮托盘扫码流程按生产计划工序记录箱码及实际入库数量");
            AddRule(result, "FA_IO", "收发存定义",
                "ERP/Account 仓库帐/AC.vb", 1834,
                "仓库流水代码将 FA_IO.FA_IO 明确显示为收发存定义");
            AddRule(result, "FA_Source", "收发存来源类别",
                "ERP/Account 仓库帐/AC.vb", 1835,
                "仓库流水代码将 FA_Source.Item_Source 明确显示为来源类别");
            AddRule(result, "HFR_Plus", "公积金补缴记录",
                "Welfare/公积金/frmHFR_Edit.vb", 84,
                "公积金维护窗体通过该表保存个人和企业补充公积金金额");
            AddRule(result, "HR_PB", "人事个人业务记录",
                "HR_X/个人业务/frmHR_PersonalBusiness.vb", 1,
                "个人业务窗体以该表保存员工个人业务办理记录");
            AddRule(result, "HR_PB_Define", "人事个人业务定义",
                "HR_X/个人业务/frmHR_PersonalBusiness.vb", 1,
                "个人业务窗体从该表读取可办理的个人业务定义");
            AddRule(result, "P_PEPWC", "工序设备原型工作中心配置",
                "ERP/Process 工艺路线/op_PR.vb", 1772,
                "工艺路线代码以该表关联工序、设备原型和工作中心");
            AddRule(result, "QC_SQC", "供应商来料品质检验记录",
                "TQC1/Forms/frmSheet_QC.vb", 175,
                "质量检验窗体使用该表保存供应商来料品质检验业务记录");
            AddRule(result, "SILog", "社保日志",
                "Welfare/frmWelfare.Designer.vb", 1555,
                "社保福利界面将该业务对象明确显示为社保日志");
            AddRule(result, "SP_MP_Config", "要货计划与生产计划关联配置",
                "ERP/表-类定义/Code_Plan3_partial.vb", 33,
                "计划代码使用该表配置要货计划与生产计划的对应关系");
            AddRule(result, "SP_MP_Ini_Inv", "要货计划与生产计划关联期初库存",
                "ERP/表-类定义/Code_Plan3_partial.vb", 62,
                "计划代码依据关联配置复制库存并保存期初库存");
            AddRule(result, "Supplier_Pay_AP_Month", "供应商应付账款月度记录",
                "docs_knowledge/01_数据库表结构/_00_表索引.md", 310,
                "该表按供应商和月份保存应付账款业务记录");

            AddRule(result, "CF_Base_Program", "客户工厂基础项目",
                "docs_knowledge/01_数据库表结构/_00_表索引.md", 579,
                "客户工厂基础资料通过该表配置所属项目");
            AddRule(result, "CF_Money_Notice", "客户工厂资金通知",
                "docs_knowledge/01_数据库表结构/_00_表索引.md", 582,
                "该表保存客户工厂相关的资金通知业务");
            AddRule(result, "CF_PRM_PRD", "客户工厂项目产品关系",
                "docs_knowledge/01_数据库表结构/_00_表索引.md", 584,
                "该表保存客户工厂项目与产品的节点和日期关系");
            AddRule(result, "CFSettlementRelation", "客户工厂结算关系",
                "docs_knowledge/01_数据库表结构/_00_表索引.md", 587,
                "该表保存客户工厂业务结算关系");
            AddRule(result, "EDisciplines_TA_ICA", "供应商质量八步法临时围堵措施",
                "ERP/Purchase 采购/ucED.vb", 798,
                "八步法界面将该阶段明确显示为临时围堵措施");
            AddRule(result, "EDisciplines_ISTA", "供应商质量八步法短期措施",
                "ERP/Purchase 采购/ucED.vb", 814,
                "八步法界面使用该表执行和跟踪短期措施");
            AddRule(result, "EDisciplines_DRC_WH", "供应商质量八步法根本原因分析",
                "ERP/Purchase 采购/ucED.vb", 829,
                "八步法界面使用该表识别并验证发生原因和流出原因");
            AddRule(result, "EDisciplines_CPCA", "供应商质量八步法长期措施制定",
                "ERP/Purchase 采购/ucED.vb", 903,
                "八步法界面将该阶段定义为制定并选择长期措施");
            AddRule(result, "EDisciplines_PCA", "供应商质量八步法长期措施执行",
                "ERP/Purchase 采购/ucED.vb", 920,
                "八步法界面使用该表执行和验证长期措施");
            AddRule(result, "EDisciplines_PR", "供应商质量八步法防止再发生",
                "ERP/Purchase 采购/ucED.vb", 934,
                "八步法界面将该阶段定义为预防再发生及标准化");
            AddRule(result, "EDisciplines_PR_Set", "供应商质量八步法标准化设置",
                "ERP/Purchase 采购/ucED.vb", 946,
                "该表保存流程图、失效模式、控制计划等标准化更新选项");
            AddRule(result, "EDisciplines_RTFImage", "供应商质量八步法富文本图片",
                "docs_knowledge/01_数据库表结构/_00_表索引.md", 910,
                "该表保存供应商质量八步法富文本内容引用的图片");

            return result;
        }

        /// <summary>XMZADD 20260919 加入一条精确表名规则并拒绝静默覆盖重复声明。</summary>
        private static void AddRule(IDictionary<string, VerifiedTableRule> result,
            string tableName, string chineseName, string sourcePath, int sourceLine,
            string explanation)
        {
            result.Add(tableName, new VerifiedTableRule(chineseName, sourcePath,
                sourceLine, explanation));
        }

        /// <summary>XMZADD 20260919 保存一条表中文名及其可审计来源和业务解释。</summary>
        private sealed class VerifiedTableRule
        {
            /// <summary>XMZADD 20260919 初始化不可变的精确表名业务规则。</summary>
            public VerifiedTableRule(string chineseName, string sourcePath,
                int sourceLine, string explanation)
            {
                ChineseName = chineseName;
                SourcePath = sourcePath;
                SourceLine = sourceLine;
                Explanation = explanation;
            }

            public string ChineseName { get; private set; }
            public string SourcePath { get; private set; }
            public int SourceLine { get; private set; }
            public string Explanation { get; private set; }
        }
    }
}
