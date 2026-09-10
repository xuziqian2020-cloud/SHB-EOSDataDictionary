using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHB.EosDataDictionary.Services;

namespace SHB.EosDataDictionary.Tests
{
    /// <summary>XMZADD 20260904 验证字段标识符结合所属业务和关联字段生成完整且不丢修饰词的中文语义。</summary>
    [TestClass]
    public sealed class BusinessIdentifierSemanticServiceTests
    {
        /// <summary>XMZADD 20260904 验证同一货主字段仅在明确的财务验收业务中解释为货主公司。</summary>
        [TestMethod]
        public void Infer_OwnerCompanyInAcceptanceFinance_ReturnsGoodsOwnerCompanyId()
        {
            BusinessFieldNameResult result = new BusinessIdentifierSemanticService().Infer(new BusinessFieldNameContext
            {
                TableName = "DA_Acceptance",
                TableChineseName = "验收单",
                ModuleName = "财务",
                FieldName = "Owner_Company_ID",
                DataType = "int",
                RelatedTableName = "Company",
                RelatedFieldName = "Company_ID"
            });

            Assert.AreEqual("货主公司ID", result.Value);
            Assert.IsTrue(result.ConfidenceScore > 0);
            Assert.IsFalse(string.IsNullOrWhiteSpace(result.RuleName));
            Assert.IsNotNull(result.UnknownTokens);
            Assert.AreEqual(0, result.UnknownTokens.Count);
        }

        /// <summary>XMZADD 20260904 验证仓储流水和财务票据等货权上下文统一把 Owner Company 解释为货主公司。</summary>
        [DataTestMethod]
        [DataRow("Account_Storage_IO", "仓储出入库流水", "仓储与库存")]
        [DataRow("Account_Storage", "", "")]
        [DataRow("DA_Account", "票据流水", "财务")]
        public void Infer_OwnerCompanyInGoodsOwnershipContext_ReturnsGoodsOwnerCompanyId(
            string tableName, string tableChineseName, string moduleName)
        {
            BusinessFieldNameResult result = new BusinessIdentifierSemanticService().Infer(new BusinessFieldNameContext
            {
                TableName = tableName,
                TableChineseName = tableChineseName,
                ModuleName = moduleName,
                FieldName = "Owner_Company_ID",
                RelatedTableName = "Company",
                RelatedFieldName = "Company_ID"
            });

            Assert.AreEqual("货主公司ID", result.Value);
            Assert.AreEqual("GoodsOwnerContext", result.RuleName);
            Assert.IsNotNull(result.UnknownTokens);
        }

        /// <summary>XMZADD 20260904 验证明示为供应商关系时不得仅凭仓储上下文生成高置信货主公司语义。</summary>
        [TestMethod]
        public void Infer_OwnerCompanyWithConflictingRelation_ReturnsLowerConfidenceBelongingCompanyId()
        {
            BusinessFieldNameResult result = new BusinessIdentifierSemanticService().Infer(new BusinessFieldNameContext
            {
                TableName = "Account_Storage_IO",
                TableChineseName = "仓储出入库流水",
                ModuleName = "仓储与库存",
                FieldName = "Owner_Company_ID",
                RelatedTableName = "Supplier",
                RelatedFieldName = "Supplier_ID"
            });

            Assert.AreEqual("所属公司ID", result.Value);
            Assert.IsTrue(result.ConfidenceScore < 96);
            Assert.AreEqual("OwnerRelationConflict", result.RuleName);
            Assert.IsNotNull(result.UnknownTokens);
        }

        /// <summary>XMZADD 20260904 验证 Owner 规则按上下文、正确关系和冲突关系分层计分，避免提前达到多证据聚合等级。</summary>
        [TestMethod]
        public void Infer_OwnerCompanyConfidence_UsesSingleRuleScoreBands()
        {
            BusinessIdentifierSemanticService service = new BusinessIdentifierSemanticService();
            BusinessFieldNameResult contextOnly = service.Infer(new BusinessFieldNameContext
            {
                TableName = "Account_Storage_IO",
                TableChineseName = "仓储出入库流水",
                ModuleName = "仓储与库存",
                FieldName = "Owner_Company_ID"
            });
            BusinessFieldNameResult confirmedRelation = service.Infer(new BusinessFieldNameContext
            {
                TableName = "Account_Storage_IO",
                TableChineseName = "仓储出入库流水",
                ModuleName = "仓储与库存",
                FieldName = "Owner_Company_ID",
                DataType = "int",
                RelatedTableName = "Company",
                RelatedFieldName = "Company_ID"
            });
            BusinessFieldNameResult conflictingRelation = service.Infer(new BusinessFieldNameContext
            {
                TableName = "Account_Storage_IO",
                TableChineseName = "仓储出入库流水",
                ModuleName = "仓储与库存",
                FieldName = "Owner_Company_ID",
                RelatedTableName = "Supplier",
                RelatedFieldName = "Supplier_ID"
            });

            Assert.IsTrue(contextOnly.ConfidenceScore >= 75 && contextOnly.ConfidenceScore <= 84);
            Assert.IsTrue(confirmedRelation.ConfidenceScore >= 85 && confirmedRelation.ConfidenceScore <= 89);
            Assert.IsTrue(conflictingRelation.ConfidenceScore < 75);
        }

        /// <summary>XMZADD 20260904 验证包含 Account 字样的配置对象及 Accounting 会计期间不被子串规则误判为货权业务。</summary>
        [DataTestMethod]
        [DataRow("UserAccountSetting", "用户账户")]
        [DataRow("AccountingPeriod", "会计期间")]
        public void Infer_OwnerCompanyInNonGoodsAccountName_ReturnsBelongingCompanyId(
            string tableName, string tableChineseName)
        {
            BusinessFieldNameResult result = new BusinessIdentifierSemanticService().Infer(new BusinessFieldNameContext
            {
                TableName = tableName,
                TableChineseName = tableChineseName,
                ModuleName = "基础数据",
                FieldName = "Owner_Company_ID",
                RelatedTableName = "Company",
                RelatedFieldName = "Company_ID"
            });

            Assert.AreEqual("所属公司ID", result.Value);
            Assert.AreEqual("OwnerBelongingContext", result.RuleName);
            Assert.IsTrue(result.ConfidenceScore < 85);
        }

        /// <summary>XMZADD 20260904 验证客户和用户账户中的裸 Account 仅表达账户对象，不能作为货权上下文证据。</summary>
        [DataTestMethod]
        [DataRow("CustomerAccount")]
        [DataRow("UserAccount")]
        public void Infer_OwnerCompanyWithBareAccountToken_ReturnsBelongingCompanyId(string tableName)
        {
            BusinessFieldNameResult result = new BusinessIdentifierSemanticService().Infer(new BusinessFieldNameContext
            {
                TableName = tableName,
                ModuleName = "基础数据",
                FieldName = "Owner_Company_ID",
                RelatedTableName = "Company",
                RelatedFieldName = "Company_ID"
            });

            Assert.AreEqual("所属公司ID", result.Value);
            Assert.AreEqual("OwnerBelongingContext", result.RuleName);
            Assert.IsTrue(result.ConfidenceScore < 85);
        }

        /// <summary>XMZADD 20260904 验证系统权限上下文中的 Owner 表达所属关系，避免误套仓储货主语义。</summary>
        [TestMethod]
        public void Infer_OwnerCompanyInSystemPermission_ReturnsBelongingCompanyId()
        {
            BusinessFieldNameResult result = new BusinessIdentifierSemanticService().Infer(new BusinessFieldNameContext
            {
                TableName = "Permission_Role",
                TableChineseName = "权限角色",
                ModuleName = "系统管理",
                FieldName = "Owner_Company_ID",
                RelatedTableName = "Company",
                RelatedFieldName = "Company_ID"
            });

            Assert.AreEqual("所属公司ID", result.Value);
            Assert.IsNotNull(result.UnknownTokens);
        }

        /// <summary>XMZADD 20260904 验证财务权限配置仍由系统权限语义优先判定为所属公司。</summary>
        [TestMethod]
        public void Infer_OwnerCompanyInFinancialPermission_ReturnsBelongingCompanyId()
        {
            BusinessFieldNameResult result = new BusinessIdentifierSemanticService().Infer(new BusinessFieldNameContext
            {
                TableName = "Finance_Permission",
                TableChineseName = "财务权限",
                ModuleName = "系统权限",
                FieldName = "Owner_Company_ID",
                RelatedTableName = "Company",
                RelatedFieldName = "Company_ID"
            });

            Assert.AreEqual("所属公司ID", result.Value);
            Assert.AreEqual("OwnerBelongingContext", result.RuleName);
            Assert.IsNotNull(result.UnknownTokens);
        }

        /// <summary>XMZADD 20260904 验证来源、目标和最后等字段修饰词在组合语义中完整保留。</summary>
        [DataTestMethod]
        [DataRow("Account_Storage_IO", "仓储与库存", "Source_Company_ID", "来源公司ID")]
        [DataRow("Account_Storage_IO", "仓储与库存", "Target_Company_ID", "目标公司ID")]
        [DataRow("Purchase_Order", "采购管理", "Last_Update_Time", "最后更新时间")]
        public void Infer_ModifiedBusinessField_KeepsModifier(
            string tableName, string moduleName, string fieldName, string expected)
        {
            BusinessFieldNameResult result = new BusinessIdentifierSemanticService().Infer(new BusinessFieldNameContext
            {
                TableName = tableName,
                ModuleName = moduleName,
                FieldName = fieldName
            });

            Assert.AreEqual(expected, result.Value);
            Assert.IsNotNull(result.UnknownTokens);
        }

        /// <summary>XMZADD 20260904 验证关联人字段的精确业务短语优先于普通逐词翻译。</summary>
        [TestMethod]
        public void Infer_DeletedByWhoRelatedToHr_ReturnsDeletedPersonId()
        {
            BusinessFieldNameResult result = new BusinessIdentifierSemanticService().Infer(new BusinessFieldNameContext
            {
                TableName = "Purchase_Order",
                ModuleName = "采购管理",
                FieldName = "DeletedByWho",
                RelatedTableName = "HR",
                RelatedFieldName = "HR_ID"
            });

            Assert.AreEqual("删除人ID", result.Value);
            Assert.IsNotNull(result.UnknownTokens);
            Assert.AreEqual(0, result.UnknownTokens.Count);
        }

        /// <summary>XMZADD 20260904 验证常见审计和金蝶字段使用稳定精确短语，避免驼峰拆分改变既有业务含义。</summary>
        [DataTestMethod]
        [DataRow("op_createtime", "操作记录创建时间")]
        [DataRow("OP_CREATE_TIME", "操作记录创建时间")]
        [DataRow("OP_INSERT_TIME", "操作记录插入时间")]
        [DataRow("OP_DATE", "操作日期")]
        [DataRow("OP_TIME", "操作时间")]
        [DataRow("CREATE_TIME", "创建时间")]
        [DataRow("CREATETIME", "创建时间")]
        [DataRow("INSERT_TIME", "插入时间")]
        [DataRow("INSERTTIME", "插入时间")]
        [DataRow("UPDATE_TIME", "更新时间")]
        [DataRow("LAST_UPDATE_TIME", "最后更新时间")]
        [DataRow("DELETE_TIME", "删除时间")]
        [DataRow("DELETEDTIME", "删除时间")]
        [DataRow("DELETEDBYWHO", "删除人ID")]
        [DataRow("EDITORNAME", "编辑人姓名")]
        [DataRow("FILE_LIB_ID", "文件库ID")]
        [DataRow("KIS_FSTOCK_BILLNO", "金蝶库存单据编号")]
        [DataRow("KIS_FSTOCK_BILLID", "金蝶库存单据ID")]
        [DataRow("KIS_FSTOCK_ENTRYID", "金蝶库存分录ID")]
        [DataRow("DIRTY_BYWHO", "脏数据标记人ID")]
        [DataRow("DIRTY_BYWHONAME", "脏数据标记人姓名")]
        [DataRow("OP_DONE_BY", "操作完成人ID")]
        [DataRow("OP_DONE_TIME", "操作完成时间")]
        [DataRow("CHECKEDBYWHONAME", "封箱人")]
        [DataRow("LOADFORDELIVERYBYWHONAME", "装柜人")]
        [DataRow("LOTID", "批次ID")]
        [DataRow("PS_ID", "产品结构ID")]
        [DataRow("BU_ID", "事业部ID")]
        [DataRow("POI_ID", "采购订单明细ID")]
        [DataRow("MPIWC_ID", "车间计划ID")]
        [DataRow("WC_ID", "工作中心ID")]
        [DataRow("SID", "仓库ID")]
        [DataRow("CF_ID", "客户工厂及结算主体ID")]
        [DataRow("IST_ID", "存储区域ID")]
        [DataRow("SUB_IST_ID", "存储单元ID")]
        [DataRow("AC_IO", "出入库标识")]
        [DataRow("AC_DATE", "业务日期")]
        [DataRow("AC_TITLE_ID", "业务类型ID")]
        [DataRow("AC_ENTITY", "业务实体")]
        [DataRow("AC_ENTITY_NAME", "业务实体名称")]
        [DataRow("AC_RECORDNO", "流水记录编号")]
        public void Infer_ExactBusinessPhrase_ReturnsStableChineseName(string fieldName, string expected)
        {
            BusinessFieldNameResult result = new BusinessIdentifierSemanticService().Infer(new BusinessFieldNameContext
            {
                TableName = "Any_Table",
                ModuleName = "其他",
                FieldName = fieldName
            });

            Assert.AreEqual(expected, result.Value);
            Assert.AreEqual("ExactBusinessPhrase", result.RuleName);
            Assert.IsNotNull(result.UnknownTokens);
            Assert.AreEqual(0, result.UnknownTokens.Count);
        }

        /// <summary>XMZADD 20260904 验证空上下文和未知字段也始终返回可枚举的未知词集合。</summary>
        [TestMethod]
        public void Infer_EmptyAndUnknownContext_AlwaysReturnsUnknownTokenCollection()
        {
            BusinessIdentifierSemanticService service = new BusinessIdentifierSemanticService();

            BusinessFieldNameResult empty = service.Infer(null);
            BusinessFieldNameResult unknown = service.Infer(new BusinessFieldNameContext { FieldName = "XYZ_Company" });

            Assert.IsNotNull(empty.UnknownTokens);
            Assert.IsNotNull(unknown.UnknownTokens);
            Assert.AreEqual(1, unknown.UnknownTokens.Count);
            Assert.AreEqual("XYZ", unknown.UnknownTokens[0]);
        }
    }
}
