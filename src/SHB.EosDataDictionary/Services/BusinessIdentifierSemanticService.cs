using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace SHB.EosDataDictionary.Services
{
    /// <summary>XMZADD 20260904 保存字段语义推断所需的所属表、模块、关联字段和业务候选上下文。</summary>
    public sealed class BusinessFieldNameContext
    {
        public string TableName { get; set; }
        public string TableChineseName { get; set; }
        public string ModuleName { get; set; }
        public string FieldName { get; set; }
        public string DataType { get; set; }
        public string RelatedTableName { get; set; }
        public string RelatedFieldName { get; set; }
        public string BusinessIdentifierCandidate { get; set; }
    }

    /// <summary>XMZADD 20260904 保存字段中文语义、置信度、命中规则、解释和待确认分词。</summary>
    public sealed class BusinessFieldNameResult
    {
        /// <summary>XMZADD 20260904 初始化字段语义结果并保证未知分词集合始终可安全枚举。</summary>
        public BusinessFieldNameResult(string value, int confidenceScore, string ruleName,
            string explanation, IList<string> unknownTokens)
        {
            Value = value ?? string.Empty;
            ConfidenceScore = confidenceScore;
            RuleName = ruleName ?? string.Empty;
            Explanation = explanation ?? string.Empty;
            UnknownTokens = unknownTokens ?? new List<string>();
        }

        public string Value { get; private set; }
        public int ConfidenceScore { get; private set; }
        public string RuleName { get; private set; }
        public string Explanation { get; private set; }
        public IList<string> UnknownTokens { get; private set; }
    }

    /// <summary>XMZADD 20260904 按精确业务短语、所属业务上下文和保守词根翻译生成完整字段中文语义。</summary>
    public sealed class BusinessIdentifierSemanticService
    {
        private static readonly Dictionary<string, string> ExactBusinessPhrases = CreateExactBusinessPhrases();
        private static readonly Regex IdentifierTokenRegex = new Regex(
            @"[A-Z]+(?=[A-Z][a-z]|\d|$)|[A-Z]?[a-z]+|\d+|[\u4e00-\u9fff]+",
            RegexOptions.Compiled);

        /// <summary>XMZADD 20260904 推断字段中文语义并在每条返回路径保留未识别分词供后续人工核验。</summary>
        public BusinessFieldNameResult Infer(BusinessFieldNameContext context)
        {
            if (context == null)
            {
                return CreateResult(string.Empty, 0, "EmptyIdentifier", "未提供字段业务上下文。", null);
            }

            string identifier = string.IsNullOrWhiteSpace(context.FieldName)
                ? context.BusinessIdentifierCandidate
                : context.FieldName;
            if (string.IsNullOrWhiteSpace(identifier))
            {
                return CreateResult(string.Empty, 0, "EmptyIdentifier", "未提供可推断的字段标识符。", null);
            }

            string normalizedIdentifier = identifier.Trim().ToUpperInvariant();
            string exactName;
            if (ExactBusinessPhrases.TryGetValue(normalizedIdentifier, out exactName))
            {
                return CreateResult(exactName, 98, "ExactBusinessPhrase",
                    "字段命中稳定业务短语，采用已核验的完整中文语义。", null);
            }

            if (string.Equals(normalizedIdentifier, "OWNER_COMPANY_ID", StringComparison.Ordinal))
            {
                bool hasExplicitRelation = !string.IsNullOrWhiteSpace(context.RelatedTableName) ||
                                           !string.IsNullOrWhiteSpace(context.RelatedFieldName);
                bool hasCompanyRelation = string.Equals(context.RelatedTableName, "Company", StringComparison.OrdinalIgnoreCase) &&
                                          string.Equals(context.RelatedFieldName, "Company_ID", StringComparison.OrdinalIgnoreCase);
                // 显式外键与公司主键不一致时，关系证据优先于词面和模块线索，避免把供应商等对象误标为货主公司。
                if (hasExplicitRelation && !hasCompanyRelation)
                {
                    return CreateResult("所属公司ID", 70, "OwnerRelationConflict",
                        "字段词面为公司，但显式关联并非 Company.Company_ID，需按保守所属关系处理。", null);
                }

                // 流水账、仓储库存、DA 单据和财务票据均记录实际承担货权的公司，因此采用货主语义。
                if (!HasSystemPermissionContext(context) && HasGoodsOwnerContext(context))
                {
                    return CreateResult("货主公司ID", hasCompanyRelation ? 88 : 80, "GoodsOwnerContext",
                        hasCompanyRelation
                            ? "货权业务上下文及 Company.Company_ID 关联共同确认货主公司。"
                            : "货权业务上下文确认货主公司，但未提供显式关联字段。",
                        null);
                }

                // 缺少货权业务证据时采用保守所属语义，避免系统权限等对象被误标为货主。
                return CreateResult("所属公司ID", hasCompanyRelation ? 84 : 78, "OwnerBelongingContext",
                    "非货权业务上下文中的 Owner Company 按所属公司解释。", null);
            }

            IdentifierTranslationResult translation = new IdentifierTranslationService().Translate(identifier);
            int confidenceScore = translation.UnknownTokens.Count == 0 ? 85 : 60;
            return CreateResult(translation.Value, confidenceScore, "IdentifierTranslation",
                translation.UnknownTokens.Count == 0
                    ? "字段由稳定业务词根完整翻译。"
                    : "字段已保留可确认语义，未知缩写需结合更多业务证据核验。",
                translation.UnknownTokens);
        }

        /// <summary>XMZADD 20260904 创建所有精确审计、文件库和金蝶库存字段短语映射。</summary>
        private static Dictionary<string, string> CreateExactBusinessPhrases()
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "OP_CREATETIME", "操作记录创建时间" },
                { "OP_CREATE_TIME", "操作记录创建时间" },
                { "OP_INSERT_TIME", "操作记录插入时间" },
                { "OP_DATE", "操作日期" },
                { "OP_TIME", "操作时间" },
                { "CREATE_TIME", "创建时间" },
                { "CREATETIME", "创建时间" },
                { "INSERT_TIME", "插入时间" },
                { "INSERTTIME", "插入时间" },
                { "UPDATE_TIME", "更新时间" },
                { "LAST_UPDATE_TIME", "最后更新时间" },
                { "DELETE_TIME", "删除时间" },
                { "DELETEDTIME", "删除时间" },
                { "DELETEDBYWHO", "删除人ID" },
                { "EDITORNAME", "编辑人姓名" },
                { "FILE_LIB_ID", "文件库ID" },
                { "KIS_FSTOCK_BILLNO", "金蝶库存单据编号" },
                { "KIS_FSTOCK_BILLID", "金蝶库存单据ID" },
                { "KIS_FSTOCK_ENTRYID", "金蝶库存分录ID" },
                { "DIRTY_BYWHO", "脏数据标记人ID" },
                { "DIRTY_BYWHONAME", "脏数据标记人姓名" },
                { "OP_DONE_BY", "操作完成人ID" },
                { "OP_DONE_TIME", "操作完成时间" },
                { "CHECKEDBYWHONAME", "封箱人" },
                { "LOADFORDELIVERYBYWHONAME", "装柜人" },
                { "LOTID", "批次ID" },
                { "LOTNO", "批次号" },
                { "PS_ID", "产品结构ID" },
                { "BU_ID", "事业部ID" },
                { "PO_ID", "采购订单ID" },
                { "POI_ID", "采购订单明细ID" },
                { "POI_QUANTITY", "采购数量" },
                { "MPI_ID", "主计划ID" },
                { "MPIWC_ID", "车间计划ID" },
                { "WC_ID", "工作中心ID" },
                { "P_WC_ID", "工序工作中心ID" },
                { "DPI_ID", "交货计划ID" },
                { "SID", "仓库ID" },
                { "CF_ID", "客户工厂及结算主体ID" },
                { "IST_ID", "存储区域ID" },
                { "SUB_IST_ID", "存储单元ID" },
                { "AC_IO", "出入库标识" },
                { "AC_DATE", "业务日期" },
                { "AC_TITLE_ID", "业务类型ID" },
                { "AC_ENTITY", "业务实体" },
                { "AC_ENTITY_NAME", "业务实体名称" },
                { "AC_RECORDNO", "流水记录编号" },
                { "TAXRATE", "税率" }
            };
        }

        /// <summary>XMZADD 20260904 判断业务上下文是否包含指定中文线索并安全处理空值。</summary>
        private static bool Contains(string value, string expected)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                   value.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>XMZADD 20260904 综合表名、中文表名和模块识别流水账、仓储库存、DA 单据及财务票据货权上下文。</summary>
        private static bool HasGoodsOwnerContext(BusinessFieldNameContext context)
        {
            return ContainsGoodsOwnerSignal(context.TableName) ||
                   ContainsGoodsOwnerSignal(context.TableChineseName) ||
                   ContainsGoodsOwnerSignal(context.ModuleName);
        }

        /// <summary>XMZADD 20260904 识别系统权限配置并优先采用所属关系，防止财务权限名称触发货主规则。</summary>
        private static bool HasSystemPermissionContext(BusinessFieldNameContext context)
        {
            return ContainsSystemPermissionSignal(context.TableName) ||
                   ContainsSystemPermissionSignal(context.TableChineseName) ||
                   ContainsSystemPermissionSignal(context.ModuleName);
        }

        /// <summary>XMZADD 20260904 判断单项业务描述是否包含系统或权限配置线索。</summary>
        private static bool ContainsSystemPermissionSignal(string value)
        {
            return ContainsIdentifierToken(value, "Permission") ||
                   ContainsIdentifierToken(value, "System") ||
                   ContainsIdentifierToken(value, "Setting") ||
                   ContainsIdentifierToken(value, "Config") ||
                   Contains(value, "权限") ||
                   Contains(value, "系统") ||
                   Contains(value, "设置") ||
                   Contains(value, "配置");
        }

        /// <summary>XMZADD 20260904 判断单项业务描述是否具有明确货权线索，并避免把普通单词中的 DA 字母误作单据域。</summary>
        private static bool ContainsGoodsOwnerSignal(string value)
        {
            return ContainsIdentifierToken(value, "Storage") ||
                   ContainsIdentifierToken(value, "Inventory") ||
                   ContainsIdentifierToken(value, "IO") ||
                   ContainsIdentifierToken(value, "Pallet") ||
                   Contains(value, "仓储") ||
                   Contains(value, "库存") ||
                   Contains(value, "流水账") ||
                   Contains(value, "票据") ||
                   Contains(value, "财务") ||
                   ContainsIdentifierToken(value, "DA");
        }

        /// <summary>XMZADD 20260904 按下划线和驼峰完整分词匹配业务域代码，防止 Account 命中 Accounting 等无关名称。</summary>
        private static bool ContainsIdentifierToken(string value, string expected)
        {
            if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(expected))
            {
                return false;
            }

            string[] sections = Regex.Split(value, @"[_\-\.\s/\\]+");
            for (int sectionIndex = 0; sectionIndex < sections.Length; sectionIndex++)
            {
                MatchCollection matches = IdentifierTokenRegex.Matches(sections[sectionIndex]);
                for (int matchIndex = 0; matchIndex < matches.Count; matchIndex++)
                {
                    if (string.Equals(matches[matchIndex].Value, expected, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>XMZADD 20260904 统一构造字段推断结果，防止新增规则遗漏未知分词集合初始化。</summary>
        private static BusinessFieldNameResult CreateResult(string value, int confidenceScore, string ruleName,
            string explanation, IList<string> unknownTokens)
        {
            return new BusinessFieldNameResult(value, confidenceScore, ruleName, explanation, unknownTokens);
        }
    }
}
