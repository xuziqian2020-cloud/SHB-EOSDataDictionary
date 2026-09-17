using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using SHB.EosDataDictionary.Models;

namespace SHB.EosDataDictionary.Services
{
    /// <summary>XMZADD 20260831 按数据库标识符分词和常见业务缩写生成直接可读的中文表字段名称。</summary>
    public sealed class IdentifierTranslationService
    {
        private const string MissingChineseName = "暂无可靠中文名称";
        private static readonly Dictionary<string, string> ExactFieldTranslations = CreateExactFieldTranslations();
        private static readonly Dictionary<string, string> TokenTranslations = CreateTokenTranslations();
        private static readonly Regex IdentifierTokenRegex = new Regex(@"[A-Z]+(?=[A-Z][a-z]|\d|$)|[A-Z]?[a-z]+|\d+|[\u4e00-\u9fff]+", RegexOptions.Compiled);
        private static readonly Regex LatinFragmentRegex = new Regex("[A-Za-z]+", RegexOptions.Compiled);
        private static readonly Regex UnreliablePunctuationRegex = new Regex(@"[,，;；=()（）{}\[\]<>_]", RegexOptions.Compiled);
        private static readonly Regex ProceduralPhraseRegex = new Regex("表示|用于|如果|当.+时|进行|代码|方法|函数|返回|点击|必须|开始时|总数|绑定到|引用方|属于", RegexOptions.Compiled);
        private static readonly HashSet<string> ReliableLatinAbbreviations = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ID", "GUID", "UID", "BOM", "OA", "AI", "EOS", "ERP", "API", "SQL", "URL", "IP",
            "HTTP", "HTTPS", "XML", "JSON", "PDF", "CAD", "SAP", "MES", "WMS", "TMS", "A", "B"
        };

        /// <summary>XMZADD 20260901 将任意 EOS 标识符逐词翻译，并保留无法解释的缩写供后续知识库或 AI 推理。</summary>
        public IdentifierTranslationResult Translate(string identifier)
        {
            string exactValue;
            if (TryTranslateExactField(identifier, out exactValue))
            {
                return new IdentifierTranslationResult(exactValue, new List<string>(), true);
            }
            return TranslateIdentifier(identifier, true);
        }

        /// <summary>XMZADD 20260901 清理数据库说明或源码注释中的名称候选，同时保留完整原文作为证据。</summary>
        public static NameCandidate NormalizeNameCandidate(string rawCandidate)
        {
            string raw = rawCandidate ?? string.Empty;
            string value = raw.Trim();
            int firstEquals = raw.IndexOf('=');
            if (firstEquals >= 0)
            {
                string[] parts = raw.Substring(firstEquals + 1).Split('=');
                for (int index = 0; index < parts.Length; index++)
                {
                    if (!string.IsNullOrWhiteSpace(parts[index]))
                    {
                        value = parts[index].Trim();
                        break;
                    }
                }
            }
            return new NameCandidate(value, raw);
        }

        /// <summary>XMZADD 20260831 将物理表名拆分翻译成中文业务对象名称，并用“表”明确对象类型。</summary>
        public static string TranslateTableName(string objectName)
        {
            string normalized = (objectName ?? string.Empty).Trim();
            // 多义缩写仅在已核验关键表中使用精确名称，避免 FA、DA 污染其他业务域的全局翻译。
            if (string.Equals(normalized, "Account_Pallet_FA_Not_IO", StringComparison.OrdinalIgnoreCase))
            {
                return "非托盘出入库流水账";
            }
            if (string.Equals(normalized, "DA_Account", StringComparison.OrdinalIgnoreCase))
            {
                return "财务流水账";
            }

            IdentifierTranslationResult result = TranslateIdentifier(objectName, false);
            string translated = result.Value;
            if (string.IsNullOrWhiteSpace(translated) || !result.HasTranslatedToken)
            {
                return MissingChineseName;
            }
            return result.HasTranslatedToken && !translated.EndsWith("表", StringComparison.Ordinal)
                ? translated + "表"
                : translated;
        }

        /// <summary>XMZADD 20260831 将物理字段名拆分翻译成中文字段名称，不重复附加原英文字段名。</summary>
        public static string TranslateFieldName(string fieldName)
        {
            string normalized = (fieldName ?? string.Empty).Trim();
            string exactValue;
            if (TryTranslateExactField(normalized, out exactValue))
            {
                return exactValue;
            }
            if (string.Equals(normalized, "Exist", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "Exists", StringComparison.OrdinalIgnoreCase))
            {
                return "是否存在";
            }
            if (string.Equals(normalized, "KisNumber", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "KIS_NUMBER", StringComparison.OrdinalIgnoreCase))
            {
                return "金蝶编码";
            }

            IdentifierTranslationResult result = TranslateIdentifier(normalized, true);
            return string.IsNullOrWhiteSpace(result.Value) || !result.HasTranslatedToken
                ? MissingChineseName
                : result.Value;
        }

        /// <summary>XMZADD 20260831 判断表名是否可由已知词典完整翻译，以区分名称翻译和规则推测来源。</summary>
        public static bool IsTableNameFullyTranslated(string objectName)
        {
            IdentifierTranslationResult result = TranslateIdentifier(objectName, false);
            return result.UnknownTokens.Count == 0 && !string.IsNullOrWhiteSpace(result.Value);
        }

        /// <summary>XMZADD 20260831 判断字段名是否可由已知词典完整翻译，以区分名称翻译和规则推测来源。</summary>
        public static bool IsFieldNameFullyTranslated(string fieldName)
        {
            string normalized = (fieldName ?? string.Empty).Trim();
            string exactValue;
            if (TryTranslateExactField(normalized, out exactValue))
            {
                return true;
            }
            if (string.Equals(normalized, "Exist", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "Exists", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            IdentifierTranslationResult result = TranslateIdentifier(normalized, true);
            return result.UnknownTokens.Count == 0 && !string.IsNullOrWhiteSpace(result.Value);
        }

        /// <summary>XMZADD 20260831 判断模块名称是否属于技术目录，避免技术实现分类误导为业务模块。</summary>
        public static bool IsTechnicalModuleName(string moduleName)
        {
            string value = (moduleName ?? string.Empty).Trim();
            if (value.Length == 0 || value == "未分类" || value == "其他" ||
                value.EndsWith(".vb", StringComparison.OrdinalIgnoreCase) || value.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return value.IndexOf("表类定义", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   value.IndexOf("表定义", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   value.IndexOf("选择", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   value.IndexOf("界面处理", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   value.IndexOf("基类", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   value.IndexOf("公共", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   value.IndexOf("工具", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   value.IndexOf("测试", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   value.IndexOf("临时", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   value.IndexOf("缓存", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>XMZADD 20260916 判断候选是否为完整中文业务名称，拒绝流程说明及未翻译英文残片。</summary>
        public static bool IsReliableChineseName(string candidate)
        {
            string value = (candidate ?? string.Empty).Trim();
            if (value.Length == 0 || value.Length > 30 || value.IndexOf('\r') >= 0 || value.IndexOf('\n') >= 0)
            {
                return false;
            }
            if (string.Equals(value, MissingChineseName, StringComparison.Ordinal))
            {
                return false;
            }
            if (!Regex.IsMatch(value, "[\\u4e00-\\u9fff]") || UnreliablePunctuationRegex.IsMatch(value))
            {
                return false;
            }
            if (ContainsUntranslatedLatinFragment(value))
            {
                return false;
            }
            // “操作人、操作日期、操作记录创建时间”是 EOS 合法业务字段名，不能因包含“操作”被当成流程说明。
            return !string.Equals(value, "操作", StringComparison.Ordinal) &&
                   !ProceduralPhraseRegex.IsMatch(value);
        }

        /// <summary>XMZADD 20260916 识别中文名中仍未翻译的英文词根，仅放行跨系统开发约定中的稳定技术缩写。</summary>
        private static bool ContainsUntranslatedLatinFragment(string value)
        {
            MatchCollection matches = LatinFragmentRegex.Matches(value ?? string.Empty);
            for (int index = 0; index < matches.Count; index++)
            {
                string fragment = matches[index].Value;
                if (!ReliableLatinAbbreviations.Contains(fragment))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>XMZADD 20260831 修复旧快照中的弱推测名称和占位枚举，使升级后无需重新读取数据库即可看到新规则结果。</summary>
        public static void RepairWeakMetadata(SnapshotData snapshot)
        {
            if (snapshot == null || snapshot.Tables == null)
            {
                return;
            }

            for (int tableIndex = 0; tableIndex < snapshot.Tables.Count; tableIndex++)
            {
                TableMetadata table = snapshot.Tables[tableIndex];
                if (table == null)
                {
                    continue;
                }

                if (ShouldRepairName(table.ChineseName))
                {
                    MetadataValue previousName = table.ChineseName;
                    table.SuggestedChineseName = PreserveWeakNameAsReference(
                        previousName, table.SuggestedChineseName, table.AlternativeChineseNames);
                    string tableName = TranslateTableName(table.ObjectName);
                    table.ChineseName = CreateTranslatedValue(
                        tableName, IsTableNameFullyTranslated(table.ObjectName), previousName);
                }
                if (ShouldRepairModule(table.ModuleName))
                {
                    table.ModuleName = CreateOtherModuleValue();
                }
                if (IsPlaceholder(table.EntityName))
                {
                    table.EntityName = null;
                }

                if (table.Fields == null)
                {
                    continue;
                }
                for (int fieldIndex = 0; fieldIndex < table.Fields.Count; fieldIndex++)
                {
                    FieldMetadata field = table.Fields[fieldIndex];
                    if (field == null)
                    {
                        continue;
                    }
                    if (ShouldRepairName(field.ChineseName))
                    {
                        MetadataValue previousName = field.ChineseName;
                        field.SuggestedChineseName = PreserveWeakNameAsReference(
                            previousName, field.SuggestedChineseName, field.AlternativeChineseNames);
                        field.ChineseName = CreateTranslatedValue(
                            TranslateFieldName(field.FieldName), IsFieldNameFullyTranslated(field.FieldName), previousName);
                    }
                    if (IsPlaceholder(field.EnumName))
                    {
                        field.EnumName = null;
                    }
                }
            }
        }

        /// <summary>XMZADD 20260907 创建不能按单词逐段解释的 EOS 高频复合字段业务短语。</summary>
        private static Dictionary<string, string> CreateExactFieldTranslations()
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
                { "LOTID_UID", "批次唯一标识" },
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

        /// <summary>XMZADD 20260907 按忽略大小写的完整字段名读取已核验业务短语。</summary>
        private static bool TryTranslateExactField(string fieldName, out string translated)
        {
            return ExactFieldTranslations.TryGetValue((fieldName ?? string.Empty).Trim(), out translated);
        }

        /// <summary>XMZADD 20260831 创建常见 EOS 英文标识符和缩写的中文词典。</summary>
        private static Dictionary<string, string> CreateTokenTranslations()
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "ID", "ID" }, { "GUID", "GUID" }, { "UID", "UID" },
                { "IMG", "图片" }, { "IMAGE", "图片" }, { "PIC", "图片" }, { "PICTURE", "图片" },
                { "ITEM", "物料" }, { "MATERIAL", "物料" }, { "PRODUCT", "产品" },
                { "NAME", "名称" }, { "DESC", "描述" }, { "DESCRIPTION", "描述" },
                { "CODE", "编码" }, { "NO", "编号" }, { "NUMBER", "编号" },
                { "STATUS", "状态" }, { "TYPE", "类型" }, { "KIND", "类别" },
                { "DATE", "日期" }, { "TIME", "时间" }, { "YEAR", "年份" }, { "MONTH", "月份" }, { "DAY", "日期" },
                { "QTY", "数量" }, { "QUANTITY", "数量" }, { "AMOUNT", "金额" }, { "PRICE", "价格" },
                { "MONEY", "金额" }, { "CURRENCY", "币别" }, { "UNIT", "单位" }, { "SPEC", "规格" },
                { "ORDER", "订单" }, { "BILL", "单据" }, { "DETAIL", "明细" }, { "ENTRY", "明细" }, { "LINE", "明细" },
                { "CUSTOMER", "客户" }, { "SUPPLIER", "供应商" }, { "USER", "用户" },
                { "DEPT", "部门" }, { "DEPARTMENT", "部门" }, { "ORG", "组织" }, { "ORGANIZATION", "组织" },
                { "REMARK", "备注" }, { "NOTE", "备注" }, { "MEMO", "备注" },
                { "ADDRESS", "地址" }, { "PHONE", "电话" }, { "EMAIL", "邮箱" },
                { "FILE", "文件" }, { "PATH", "路径" }, { "URL", "地址" }, { "CONTENT", "内容" },
                { "DATA", "数据" }, { "VALUE", "值" }, { "TEXT", "文本" },
                { "PARENT", "父" }, { "CHILD", "子" }, { "KEY", "键" },
                { "SOURCE", "来源" }, { "TARGET", "目标" }, { "FROM", "来源" }, { "TO", "目标" },
                { "LAST", "最后" }, { "ORIGINAL", "原始" },
                { "CREATE", "创建" }, { "CREATED", "创建" }, { "CREATOR", "创建人" },
                { "UPDATE", "更新" }, { "UPDATED", "更新" }, { "INSERT", "插入" }, { "USING", "使用" },
                { "DELETE", "删除" }, { "DELETED", "删除" }, { "ENABLE", "启用" }, { "ENABLED", "启用" },
                { "DISABLE", "禁用" }, { "DISABLED", "禁用" }, { "IS", "是否" }, { "HAS", "是否有" },
                { "EXIST", "存在" }, { "EXISTS", "存在" }, { "FLAG", "标记" },
                { "AUDIT", "审核" }, { "AUDITED", "是否已审核" }, { "AUDITOR", "审核人" },
                { "OPERATOR", "操作人" }, { "RECORDER", "记录人" }, { "PLANNER", "计划员" },
                { "COMMITED", "已提交" },
                { "SORT", "排序" }, { "SEQ", "序号" }, { "SEQUENCE", "序号" }, { "INDEX", "索引" },
                { "FEE", "费用" }, { "RATE", "比率" }, { "RESULT", "结果" }, { "LEVEL", "层级" },
                { "PACKAGE", "包装" }, { "WAREHOUSE", "仓库" }, { "REPORT", "报表" },
                { "HR", "人力资源" }, { "PURCHASE", "采购" }, { "SALE", "销售" }, { "SALES", "销售" },
                { "PROGRAM", "项目" }, { "COMPANY", "公司" }, { "STORAGE", "仓储" }, { "EVENT", "事件" },
                { "STOCK", "库存" }, { "INVENTORY", "库存" }, { "QUALITY", "质量" }, { "QC", "质量" },
                { "FINANCE", "财务" }, { "ACCOUNT", "流水账" }, { "PROCESS", "工艺" }, { "PRODUCTION", "生产" },
                { "KIS", "金蝶" }, { "KINGDEE", "金蝶" }, { "PALLET", "托盘" }, { "IO", "出入库" }, { "NOT", "非" },
                { "INV", "库存" }, { "PLUS", "扩展" }, { "EDITOR", "编辑人" },
                { "OP", "操作" }, { "LOT", "批次" }, { "BU", "事业部" },
                { "PS", "产品结构" }, { "MPI", "主计划" }, { "DPI", "交货计划" },
                { "IV", "物料版本" }, { "CC", "成本中心" }, { "CF", "客户工厂及结算主体" },
                { "IST", "存储区域" }, { "ISS", "发货" },
                { "PAY", "付款" }, { "TAX", "税" }, { "CHECK", "检查" },
                { "OUT", "出库" }, { "IN", "入库" }, { "DONE", "完成" },
                { "MANAGER", "负责人" }, { "DEP", "部门" }, { "EXE", "执行" },
                { "ENTITY", "实体" }, { "START", "开始" }, { "END", "结束" },
                { "MANU", "生产" }, { "BOX", "箱" }, { "COUNT", "数量" },
                { "PLAN", "计划" }, { "DELIVER", "交付" }, { "WORK", "工作" },
                { "FINISH", "完成" }, { "PART", "零件" }, { "EDIT", "编辑" },
                { "NEW", "新" }, { "CONTRACT", "合同" }, { "SUB", "子" },
                { "FOLDER", "文件夹" }, { "BOOK", "账簿" }, { "BASE", "基础" },
                { "LOGIS", "物流" }, { "IQC", "来料检验" }, { "INVOICE", "发票" },
                { "SAMPLE", "样品" }, { "NEED", "需要" }, { "OTHER", "其他" },
                { "MSG", "消息" }, { "REPAIR", "维修" }, { "GROUP", "组" },
                { "PO", "采购订单" }, { "POI", "采购订单明细" }, { "REVIEW", "评审" },
                { "SUP", "供应商" }, { "CLASS", "类别" }, { "USE", "使用" },
                { "DAYS", "天数" }, { "STOP", "停止" }, { "CHANGE", "变更" },
                { "OLD", "原" }, { "COL", "列" }, { "LOG", "日志" },
                { "ROW", "行" }, { "RUN", "运行" }, { "TOTAL", "合计" },
                { "CONDITION", "条件" }, { "REASON", "原因" }, { "HOUR", "小时" },
                { "DEVELOPMENT", "开发" }, { "DELIVERY", "交付" },
                { "NG", "不良" }, { "WC", "工作中心" },
                { "SCAN", "扫码" }, { "EFFECTIVE", "生效" }, { "SHIFT", "班次" },
                { "TASK", "任务" }, { "NODE", "节点" }, { "APPLICATION", "申请" },
                { "STYLE", "样式" }, { "LABOR", "人工" }, { "COST", "成本" },
                { "DUE", "到期" }, { "FLOW", "流程" }, { "MAX", "最大" },
                { "TECH", "技术" }, { "TRADE", "贸易" }, { "TERMS", "条款" },
                { "ACTIVE", "有效" }, { "LIST", "列表" }, { "PROBLEM", "问题" },
                { "COLOR", "颜色" }, { "TAG", "标记" }, { "CONFIRM", "确认" },
                { "FACILITY", "设施" }, { "FULL", "完整" }, { "PACT", "合同" },
                { "CLIENT", "客户端" }, { "VERSION", "版本" }, { "ERROR", "错误" },
                { "MESSAGE", "消息" }, { "MOTOR", "电机" }, { "ACTUAL", "实际" },
                { "DRAW", "图纸" }, { "REJECT", "拒收" }, { "SAVE", "保存" },
                { "BUSINESS", "业务" }, { "SYSTEM", "系统" }, { "SIZE", "大小" },
                { "SHB", "胜华波" }, { "REAL", "实际" }, { "SHOW", "显示" },
                { "SITE", "地点" }, { "STATE", "状态" }, { "VER", "版本" },
                { "WITH", "含" }, { "INFO", "信息" }, { "PUR", "采购" },
                { "TABLE", "表" }, { "VIEW", "视图" }
            };
        }

        /// <summary>XMZADD 20260831 按下划线、空格和驼峰边界拆分数据库标识符。</summary>
        private static IList<string> SplitIdentifier(string identifier)
        {
            var result = new List<string>();
            string[] sections = Regex.Split(identifier ?? string.Empty, @"[_\-\.\s]+");
            for (int sectionIndex = 0; sectionIndex < sections.Length; sectionIndex++)
            {
                MatchCollection matches = IdentifierTokenRegex.Matches(sections[sectionIndex]);
                for (int matchIndex = 0; matchIndex < matches.Count; matchIndex++)
                {
                    if (!string.IsNullOrWhiteSpace(matches[matchIndex].Value))
                    {
                        result.Add(matches[matchIndex].Value);
                    }
                }
            }
            return result;
        }

        /// <summary>XMZADD 20260901 逐词翻译已知含义并原样保留未知缩写，确保局部有效信息不会丢失。</summary>
        private static IdentifierTranslationResult TranslateIdentifier(string identifier, bool isField)
        {
            IList<string> tokens = SplitIdentifier(identifier);
            while (tokens.Count > 0 && IsTechnicalObjectPrefix(tokens[0]))
            {
                // 字段中的 KIS 承载金蝶来源语义；只有后续仍是表对象前缀时才按技术前缀剥离。
                if (isField && string.Equals(tokens[0], "KIS", StringComparison.OrdinalIgnoreCase) &&
                    tokens.Count > 1 && !IsTechnicalObjectPrefix(tokens[1]))
                {
                    break;
                }
                tokens.RemoveAt(0);
            }
            // 金蝶字段常用 FStock/FNumber 形式，F 只是字段技术前缀，KIS 仍必须保留为来源语义。
            if (isField && tokens.Count > 2 &&
                string.Equals(tokens[0], "KIS", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(tokens[1], "F", StringComparison.OrdinalIgnoreCase) &&
                IsKnownToken(tokens[2]))
            {
                tokens.RemoveAt(1);
            }
            if (isField && tokens.Count > 1 && string.Equals(tokens[0], "F", StringComparison.OrdinalIgnoreCase) &&
                IsKnownToken(tokens[1]))
            {
                tokens.RemoveAt(0);
            }

            var unknownTokens = new List<string>();
            var unknownIndex = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new StringBuilder();
            bool hasTranslatedToken = false;
            if (tokens == null || tokens.Count == 0)
            {
                return new IdentifierTranslationResult(string.Empty, unknownTokens, false);
            }

            for (int i = 0; i < tokens.Count; i++)
            {
                string token = NormalizeFieldToken(tokens[i], isField && i == 0);
                string translated;
                if (Regex.IsMatch(token, "^[\\u4e00-\\u9fff]+$") || Regex.IsMatch(token, "^\\d+$"))
                {
                    translated = token;
                    if (Regex.IsMatch(token, "^[\\u4e00-\\u9fff]+$"))
                    {
                        hasTranslatedToken = true;
                    }
                }
                else
                {
                    translated = TranslateKnownToken(tokens, i, token);
                    if (translated != null)
                    {
                        if (!string.IsNullOrWhiteSpace(translated))
                        {
                            hasTranslatedToken = true;
                        }
                    }
                    else if (unknownIndex.Add(token))
                    {
                        unknownTokens.Add(token);
                    }
                }
                if (!string.IsNullOrWhiteSpace(translated))
                {
                    result.Append(translated);
                }
            }
            return new IdentifierTranslationResult(result.ToString(), unknownTokens, hasTranslatedToken);
        }

        /// <summary>XMZADD 20260904 根据相邻业务对象区分多义词，并翻译稳定 EOS 词根。</summary>
        private static string TranslateKnownToken(IList<string> tokens, int index, string token)
        {
            if (string.Equals(token, "ACCOUNT", StringComparison.OrdinalIgnoreCase) &&
                HasIdentityAccountContext(tokens))
            {
                // 用户、客户和供应商主体后的 Account 表示登录或往来账户，不能套用仓储流水账语义。
                return "账户";
            }
            if (string.Equals(token, "BY", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 < tokens.Count && string.Equals(tokens[index + 1], "WHO", StringComparison.OrdinalIgnoreCase))
                {
                    // ByWho 在 EOS 中共同表达操作人，By 本身不重复输出“人”。
                    return string.Empty;
                }
                if (index + 1 < tokens.Count &&
                    (string.Equals(tokens[index + 1], "BOX", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(tokens[index + 1], "SCAN", StringComparison.OrdinalIgnoreCase)))
                {
                    return "按";
                }
                return "人";
            }
            if (string.Equals(token, "WHO", StringComparison.OrdinalIgnoreCase))
            {
                return "人";
            }
            if (string.Equals(token, "NAME", StringComparison.OrdinalIgnoreCase) && index > 0 &&
                (string.Equals(tokens[index - 1], "BY", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(tokens[index - 1], "WHO", StringComparison.OrdinalIgnoreCase)))
            {
                return "姓名";
            }
            if (string.Equals(token, "ITEM", StringComparison.OrdinalIgnoreCase))
            {
                if (index > 0 &&
                    (string.Equals(tokens[index - 1], "ORDER", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(tokens[index - 1], "EVENT", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(tokens[index - 1], "BILL", StringComparison.OrdinalIgnoreCase)))
                {
                    return "明细";
                }
                return "物料";
            }

            string translated;
            return TokenTranslations.TryGetValue(token, out translated) ? translated : null;
        }

        /// <summary>XMZADD 20260916 识别 Account 与用户或往来主体组合形成的账户对象。</summary>
        private static bool HasIdentityAccountContext(IList<string> tokens)
        {
            if (tokens == null)
            {
                return false;
            }
            for (int index = 0; index < tokens.Count; index++)
            {
                string token = tokens[index];
                if (string.Equals(token, "USER", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(token, "CUSTOMER", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(token, "SUPPLIER", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>XMZADD 20260901 判断字段前缀后的分词是否有明确词义，防止误删真实业务缩写中的 F。</summary>
        private static bool IsKnownToken(string token)
        {
            return !string.IsNullOrWhiteSpace(token) &&
                   (TokenTranslations.ContainsKey(token) || Regex.IsMatch(token, "^[\\u4e00-\\u9fff]+$") || Regex.IsMatch(token, "^\\d+$"));
        }

        /// <summary>XMZADD 20260831 去除传统 EOS 字段前缀 F，使 FSTATUS、FID 等字段仍可按真实词义翻译。</summary>
        private static string NormalizeFieldToken(string token, bool isFirstFieldToken)
        {
            if (!isFirstFieldToken || string.IsNullOrWhiteSpace(token) || token.Length <= 1)
            {
                return token;
            }
            if (string.Equals(token, "FID", StringComparison.OrdinalIgnoreCase))
            {
                return "ID";
            }
            string remaining = token.Substring(1);
            return (token[0] == 'F' || token[0] == 'f') && TokenTranslations.ContainsKey(remaining) ? remaining : token;
        }

        /// <summary>XMZADD 20260831 判断表名前缀是否仅用于区分数据库对象类型。</summary>
        private static bool IsTechnicalObjectPrefix(string token)
        {
            return string.Equals(token, "KIS", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(token, "T", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(token, "TB", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(token, "V", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(token, "VW", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>XMZADD 20260831 判断旧名称是否属于可被新规则安全替换的自动弱值。</summary>
        private static bool ShouldRepairName(MetadataValue value)
        {
            // 可信状态偶有历史数据标记不完整，人工或锁定标志仍具有最高优先级，不能因弱状态被自动重算。
            if (value != null &&
                (value.IsManualOverride || value.IsLocked ||
                 value.Status == ConfidenceStatus.LocalOverride ||
                 value.Status == ConfidenceStatus.Confirmed ||
                 value.Status == ConfidenceStatus.DatabaseEvidence))
            {
                return false;
            }
            if (value == null || value.Status == ConfidenceStatus.PendingConfirmation ||
                value.Status == ConfidenceStatus.Guessed || value.Status == ConfidenceStatus.GuessedConflict ||
                value.Status == ConfidenceStatus.AiGuessed)
            {
                return true;
            }
            return value.Status == ConfidenceStatus.CodeEvidence && !IsReliableChineseName(value.Value);
        }

        /// <summary>XMZADD 20260916 将被修复的弱名称保留在参考层，避免名称改善后丢失历史结果和审计线索。</summary>
        private static MetadataValue PreserveWeakNameAsReference(MetadataValue weakName,
            MetadataValue suggestedName, IList<MetadataValue> alternativeNames)
        {
            if (weakName == null || string.IsNullOrWhiteSpace(weakName.Value))
            {
                return suggestedName;
            }
            if (suggestedName == null || string.IsNullOrWhiteSpace(suggestedName.Value))
            {
                return weakName;
            }
            if (string.Equals(suggestedName.Value, weakName.Value, StringComparison.OrdinalIgnoreCase))
            {
                return suggestedName;
            }
            if (alternativeNames != null)
            {
                for (int index = 0; index < alternativeNames.Count; index++)
                {
                    MetadataValue alternative = alternativeNames[index];
                    if (alternative != null && string.Equals(alternative.Value, weakName.Value,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return suggestedName;
                    }
                }
                alternativeNames.Add(weakName);
            }
            return suggestedName;
        }

        /// <summary>XMZADD 20260831 判断旧模块是否为文件名或未分类占位内容。</summary>
        private static bool ShouldRepairModule(MetadataValue value)
        {
            if (value == null)
            {
                return true;
            }
            // 人工维护的模块优先于自动归并规则，避免升级覆盖人工业务判断。
            if (value.IsManualOverride || value.Status == ConfidenceStatus.LocalOverride)
            {
                return false;
            }
            if (value.Status == ConfidenceStatus.PendingConfirmation || string.IsNullOrWhiteSpace(value.Value))
            {
                return true;
            }
            string module = value.Value.Trim();
            return module.StartsWith("推测：", StringComparison.Ordinal) || IsTechnicalModuleName(module);
        }

        /// <summary>XMZADD 20260831 判断元数据值是否只是旧版等待确认占位文本。</summary>
        private static bool IsPlaceholder(MetadataValue value)
        {
            if (value == null)
            {
                return false;
            }
            string text = value.Value ?? string.Empty;
            return value.Status == ConfidenceStatus.PendingConfirmation || text.StartsWith("推测：待确认", StringComparison.Ordinal) || text.StartsWith("推测：未", StringComparison.Ordinal) || text == "未发现明确枚举";
        }

        /// <summary>XMZADD 20260916 创建名称翻译值并保留最初自动名称，确保纠错全过程可以回溯。</summary>
        private static MetadataValue CreateTranslatedValue(string translated, bool isFullyTranslated, MetadataValue existing)
        {
            bool isMissing = string.Equals(translated, MissingChineseName, StringComparison.Ordinal);
            string originalAutomaticValue = string.Empty;
            if (existing != null)
            {
                // 多轮自动修复仍需指向最早的自动结果，便于维护人员判断新规则是否真的改善名称。
                originalAutomaticValue = string.IsNullOrWhiteSpace(existing.OriginalAutomaticValue)
                    ? existing.Value
                    : existing.OriginalAutomaticValue;
            }
            return new MetadataValue
            {
                Value = translated,
                Status = isMissing ? ConfidenceStatus.PendingConfirmation : ConfidenceStatus.Guessed,
                ConfidenceScore = isMissing ? 0 : isFullyTranslated ? 72 : 65,
                SourceType = isFullyTranslated && !isMissing ? "名称翻译" : "规则推测",
                SourceSummary = isMissing
                    ? "未形成可靠中文名称"
                    : isFullyTranslated ? "英文标识符拆分翻译" : "英文标识符保守推测",
                OriginalAutomaticValue = originalAutomaticValue,
                Evidence = new List<EvidenceItem>()
            };
        }

        /// <summary>XMZADD 20260831 创建无法可靠推断时统一使用的“其他”模块。</summary>
        private static MetadataValue CreateOtherModuleValue()
        {
            return new MetadataValue
            {
                Value = "其他",
                Status = ConfidenceStatus.Guessed,
                SourceType = "模块归类",
                SourceSummary = "未找到可靠模块证据",
                Evidence = new List<EvidenceItem>()
            };
        }
    }

    /// <summary>XMZADD 20260901 保存标识符的局部翻译结果和仍需知识库或 AI 推理的未知分词。</summary>
    public sealed class IdentifierTranslationResult
    {
        /// <summary>XMZADD 20260901 初始化一次标识符翻译的可显示值与未知分词集合。</summary>
        public IdentifierTranslationResult(string value, IList<string> unknownTokens, bool hasTranslatedToken)
        {
            Value = value;
            UnknownTokens = unknownTokens ?? new List<string>();
            HasTranslatedToken = hasTranslatedToken;
        }

        public string Value { get; private set; }
        public IList<string> UnknownTokens { get; private set; }
        public bool HasTranslatedToken { get; private set; }
    }

    /// <summary>XMZADD 20260901 保存清理后的名称候选和未改写的原始证据文本。</summary>
    public sealed class NameCandidate
    {
        /// <summary>XMZADD 20260901 初始化可展示名称及其完整原始来源。</summary>
        public NameCandidate(string value, string rawEvidence)
        {
            Value = value;
            RawEvidence = rawEvidence;
        }

        public string Value { get; private set; }
        public string RawEvidence { get; private set; }
    }
}
