using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHB.EosDataDictionary.Models;
using SHB.EosDataDictionary.Services;

namespace SHB.EosDataDictionary.Tests
{
    /// <summary>XMZADD 20260831 验证数据库标识符可直接转换成适合新手阅读的中文名称。</summary>
    [TestClass]
    public sealed class IdentifierTranslationServiceTests
    {
        /// <summary>XMZADD 20260904 验证未知缩写仅进入证据列表，不再污染最终中文显示名称。</summary>
        [DataTestMethod]
        [DataRow("ABC_Order_Status", "订单状态")]
        [DataRow("Kis_T_SaleOrder2Line", "销售订单2明细")]
        [DataRow("FItem_Image", "物料图片")]
        [DataRow("POI_Plus_EditorName", "采购订单明细扩展编辑人名称")]
        public void Translate_HidesUnknownTokensAndKeepsTranslatedMeaning(string source, string expected)
        {
            IdentifierTranslationResult result = new IdentifierTranslationService().Translate(source);

            Assert.AreEqual(expected, result.Value);
        }

        /// <summary>XMZADD 20260901 验证未知分词按出现顺序进入 AI 后续推理列表且不重复。</summary>
        [TestMethod]
        public void Translate_CollectsUnknownTokensWithoutDuplicates()
        {
            IdentifierTranslationResult result = new IdentifierTranslationService().Translate("Kis_T_ABC_Order_ABC");

            Assert.AreEqual("订单", result.Value);
            Assert.AreEqual(1, result.UnknownTokens.Count);
            Assert.AreEqual("ABC", result.UnknownTokens[0]);
        }

        /// <summary>XMZADD 20260901 验证 F 只有在后续字段词义明确时才作为技术前缀移除。</summary>
        [TestMethod]
        public void Translate_RemovesFieldPrefixOnlyWhenFollowingTokenIsKnown()
        {
            Assert.AreEqual("状态", new IdentifierTranslationService().Translate("FStatus").Value);
            Assert.AreEqual(string.Empty, new IdentifierTranslationService().Translate("FABC").Value);
        }

        /// <summary>XMZADD 20260901 验证名称候选只展示首个等号右侧非空内容并完整保留原始依据。</summary>
        [TestMethod]
        public void NormalizeNameCandidate_UsesRightSideAndKeepsRawEvidence()
        {
            NameCandidate result = IdentifierTranslationService.NormalizeNameCandidate("FNAME=物料名称");
            NameCandidate multiple = IdentifierTranslationService.NormalizeNameCandidate("FNAME= =物料名称=备用");

            Assert.AreEqual("物料名称", result.Value);
            Assert.AreEqual("FNAME=物料名称", result.RawEvidence);
            Assert.AreEqual("物料名称", multiple.Value);
            Assert.AreEqual("FNAME= =物料名称=备用", multiple.RawEvidence);
        }

        /// <summary>XMZADD 20260831 验证明显的图片表名称优先采用标识符拆分翻译。</summary>
        [TestMethod]
        public void TranslateTableName_ItemImage_ReturnsMaterialImageTable()
        {
            Assert.AreEqual("物料图片表", IdentifierTranslationService.TranslateTableName("Item_Image"));
            Assert.AreEqual("采购订单明细表", IdentifierTranslationService.TranslateTableName("Purchase_Order_Item"));
            Assert.AreEqual("仓储事件明细表", IdentifierTranslationService.TranslateTableName("Storage_Event_Item"));
        }

        /// <summary>XMZADD 20260904 验证多义 FA 和 DA 仅在已确认关键表中使用精确业务名称，不污染全局缩写词典。</summary>
        [DataTestMethod]
        [DataRow("Account_Pallet_FA_Not_IO", "非托盘出入库流水账")]
        [DataRow("DA_Account", "财务流水账")]
        public void TranslateTableName_ConfirmedAmbiguousTable_ReturnsExactBusinessName(string source, string expected)
        {
            Assert.AreEqual(expected, IdentifierTranslationService.TranslateTableName(source));
        }

        /// <summary>XMZADD 20260916 验证 Account 在客户和用户主体上下文中表示账户，而不是仓储或财务流水账。</summary>
        [DataTestMethod]
        [DataRow("User_Account", "用户账户表")]
        [DataRow("Customer_Account", "客户账户表")]
        [DataRow("Supplier_Account", "供应商账户表")]
        public void TranslateTableName_IdentityAccount_UsesAccountMeaning(string source, string expected)
        {
            Assert.AreEqual(expected, IdentifierTranslationService.TranslateTableName(source));
        }

        /// <summary>XMZADD 20260831 验证常见图片字段直接显示中文且不重复附加英文字段名。</summary>
        [TestMethod]
        public void TranslateFieldName_ImageFields_ReturnDirectChineseNames()
        {
            Assert.AreEqual("图片ID", IdentifierTranslationService.TranslateFieldName("Img_ID"));
            Assert.AreEqual("图片名称", IdentifierTranslationService.TranslateFieldName("Img_Name"));
            Assert.AreEqual("图片描述", IdentifierTranslationService.TranslateFieldName("Img_Description"));
            Assert.AreEqual("图片", IdentifierTranslationService.TranslateFieldName("Image"));
            Assert.AreEqual("物料ID", IdentifierTranslationService.TranslateFieldName("Item_ID"));
            Assert.AreEqual("是否存在", IdentifierTranslationService.TranslateFieldName("Exist"));
        }

        /// <summary>XMZADD 20260903 验证 KIS 作为字段业务前缀时保留金蝶语义，而不是被当作表技术前缀删除。</summary>
        [TestMethod]
        public void TranslateFieldName_KisNumber_ReturnsKingdeeCode()
        {
            Assert.AreEqual("金蝶编码", IdentifierTranslationService.TranslateFieldName("KisNumber"));
            Assert.AreEqual("金蝶编码", IdentifierTranslationService.TranslateFieldName("KIS_CODE"));
        }

        /// <summary>XMZADD 20260916 验证 Lot 表顺序 GUID 字段显示为批次唯一标识，而不是粘连的 IDUID。</summary>
        [TestMethod]
        public void TranslateFieldName_LotIdUid_ReturnsBatchUniqueIdentifier()
        {
            Assert.AreEqual("批次唯一标识", IdentifierTranslationService.TranslateFieldName("LotID_UID"));
        }

        /// <summary>XMZADD 20260917 验证人工确认的子票号 A/B 业务后缀不会被误报为未翻译英文。</summary>
        [TestMethod]
        public void IsReliableChineseName_ConfirmedTicketVariantSuffixes_AreReliable()
        {
            Assert.IsTrue(IdentifierTranslationService.IsReliableChineseName("子票号A"));
            Assert.IsTrue(IdentifierTranslationService.IsReliableChineseName("子票号B"));
            Assert.IsFalse(IdentifierTranslationService.IsReliableChineseName("子票号X"));
        }

        /// <summary>XMZADD 20260904 验证没有任何可靠中文词义时显示统一待确认名称，而不是纯英文标识符。</summary>
        [TestMethod]
        public void TranslateUnknownIdentifiers_ReturnsReliableChinesePlaceholder()
        {
            Assert.AreEqual("暂无可靠中文名称", IdentifierTranslationService.TranslateTableName("AB01"));
            Assert.AreEqual("暂无可靠中文名称", IdentifierTranslationService.TranslateFieldName("AB01"));
        }

        /// <summary>XMZADD 20260904 验证常用 EOS 业务词根能直接生成完整中文名称。</summary>
        [DataTestMethod]
        [DataRow("Spec", "规格")]
        [DataRow("Currency", "币别")]
        [DataRow("Unit", "单位")]
        [DataRow("Inv", "库存")]
        [DataRow("Program", "项目")]
        [DataRow("Money", "金额")]
        [DataRow("CompanyID", "公司ID")]
        [DataRow("Creator", "创建人")]
        [DataRow("Using", "使用")]
        public void TranslateFieldName_CommonBusinessTerms_ReturnsChinese(string source, string expected)
        {
            Assert.AreEqual(expected, IdentifierTranslationService.TranslateFieldName(source));
        }

        /// <summary>XMZADD 20260904 验证稳定业务词根按修饰、对象、动作状态和值类型顺序保留完整语义。</summary>
        [DataTestMethod]
        [DataRow("Source_Company_ID", "来源公司ID")]
        [DataRow("Target_Warehouse_ID", "目标仓库ID")]
        [DataRow("From_Company_ID", "来源公司ID")]
        [DataRow("To_Warehouse_ID", "目标仓库ID")]
        [DataRow("Last_Update_Time", "最后更新时间")]
        [DataRow("Original_Package_Qty", "原始包装数量")]
        [DataRow("Audit_Result", "审核结果")]
        [DataRow("Audited", "是否已审核")]
        [DataRow("Auditor_ID", "审核人ID")]
        [DataRow("Operator_ID", "操作人ID")]
        [DataRow("Recorder_ID", "记录人ID")]
        [DataRow("Planner_ID", "计划员ID")]
        [DataRow("Fee_Rate", "费用比率")]
        [DataRow("Warehouse_Level", "仓库层级")]
        [DataRow("Report_Insert_Time", "报表插入时间")]
        [DataRow("Commited", "已提交")]
        public void TranslateFieldName_StableBusinessRoots_ReturnsCompleteChineseName(string source, string expected)
        {
            Assert.AreEqual(expected, IdentifierTranslationService.TranslateFieldName(source));
        }

        /// <summary>XMZADD 20260907 验证 EOS 高频复合字段优先采用完整业务短语，避免只翻译尾部词根。</summary>
        [DataTestMethod]
        [DataRow("Insert_Time", "插入时间")]
        [DataRow("CreateTime", "创建时间")]
        [DataRow("EditorName", "编辑人姓名")]
        [DataRow("Audited", "是否已审核")]
        [DataRow("Auditor", "审核人")]
        [DataRow("Planner", "计划员")]
        [DataRow("Recorder", "记录人")]
        [DataRow("File_Lib_ID", "文件库ID")]
        [DataRow("Kis_FStock_BillNo", "金蝶库存单据编号")]
        [DataRow("op_createtime", "操作记录创建时间")]
        public void TranslateFieldName_FrequentCompositeFields_ReturnsCompleteBusinessName(string source, string expected)
        {
            Assert.AreEqual(expected, IdentifierTranslationService.TranslateFieldName(source));
            Assert.IsTrue(IdentifierTranslationService.IsFieldNameFullyTranslated(source));
        }

        /// <summary>XMZADD 20260908 验证知识库和业务代码已经明确的 EOS 核心字段采用完整业务名称。</summary>
        [DataTestMethod]
        [DataRow("Dirty_ByWho", "脏数据标记人ID")]
        [DataRow("Dirty_ByWhoName", "脏数据标记人姓名")]
        [DataRow("op_Done_By", "操作完成人ID")]
        [DataRow("op_Done_Time", "操作完成时间")]
        [DataRow("CheckedByWhoName", "封箱人")]
        [DataRow("LoadForDeliveryByWhoName", "装柜人")]
        [DataRow("LotID", "批次ID")]
        [DataRow("LotNo", "批次号")]
        [DataRow("PS_ID", "产品结构ID")]
        [DataRow("Bu_ID", "事业部ID")]
        [DataRow("PO_ID", "采购订单ID")]
        [DataRow("POI_ID", "采购订单明细ID")]
        [DataRow("POI_Quantity", "采购数量")]
        [DataRow("MPI_ID", "主计划ID")]
        [DataRow("MPIWC_ID", "车间计划ID")]
        [DataRow("WC_ID", "工作中心ID")]
        [DataRow("P_WC_ID", "工序工作中心ID")]
        [DataRow("DPI_ID", "交货计划ID")]
        [DataRow("SID", "仓库ID")]
        [DataRow("CF_ID", "客户工厂及结算主体ID")]
        [DataRow("IST_ID", "存储区域ID")]
        [DataRow("Sub_IST_ID", "存储单元ID")]
        [DataRow("Ac_IO", "出入库标识")]
        [DataRow("Ac_Date", "业务日期")]
        [DataRow("Ac_Title_ID", "业务类型ID")]
        [DataRow("Ac_Entity", "业务实体")]
        [DataRow("Ac_Entity_Name", "业务实体名称")]
        [DataRow("Ac_RecordNo", "流水记录编号")]
        public void TranslateFieldName_VerifiedEosCoreFields_ReturnsBusinessMeaning(string source, string expected)
        {
            Assert.AreEqual(expected, IdentifierTranslationService.TranslateFieldName(source));
            Assert.IsTrue(IdentifierTranslationService.IsFieldNameFullyTranslated(source));
        }

        /// <summary>XMZADD 20260908 验证高频且跨业务域含义稳定的英文词根不会只保留残缺中文。</summary>
        [DataTestMethod]
        [DataRow("TaxRate", "税率")]
        [DataRow("In_Qty", "入库数量")]
        [DataRow("Out_Item_ID", "出库物料ID")]
        [DataRow("Exe_Status", "执行状态")]
        [DataRow("Start_Date", "开始日期")]
        [DataRow("EndDate", "结束日期")]
        [DataRow("ManuLot", "生产批次")]
        [DataRow("Box_Qty", "箱数量")]
        [DataRow("File_Count", "文件数量")]
        [DataRow("PlanDeliverDate", "计划交付日期")]
        [DataRow("WorkTime_Hour", "工作时间小时")]
        [DataRow("Part_Done", "零件完成")]
        [DataRow("Edit_Time", "编辑时间")]
        [DataRow("Contract_No", "合同编号")]
        [DataRow("IsFolder", "是否文件夹")]
        [DataRow("Logis_Price", "物流价格")]
        [DataRow("IQC_Date", "来料检验日期")]
        [DataRow("Invoice_No", "发票编号")]
        [DataRow("Sample_Qty", "样品数量")]
        [DataRow("OtherMoney", "其他金额")]
        [DataRow("PO_Status_ID", "采购订单状态ID")]
        [DataRow("Review_Result", "评审结果")]
        [DataRow("Sup_Price", "供应商价格")]
        [DataRow("DevelopmentClass", "开发类别")]
        [DataRow("DeliveryDays", "交付天数")]
        [DataRow("Stop_Reason", "停止原因")]
        [DataRow("Change_Type", "变更类型")]
        [DataRow("Old_Description", "原描述")]
        [DataRow("ColName", "列名称")]
        [DataRow("LogTime", "日志时间")]
        [DataRow("Row_No", "行编号")]
        [DataRow("Run_ID", "运行ID")]
        [DataRow("TotalPrice", "合计价格")]
        public void TranslateFieldName_StableHighFrequencyVocabulary_ReturnsCompleteChinese(string source, string expected)
        {
            Assert.AreEqual(expected, IdentifierTranslationService.TranslateFieldName(source));
            Assert.IsTrue(IdentifierTranslationService.IsFieldNameFullyTranslated(source));
        }

        /// <summary>XMZADD 20260908 验证知识库已定义的 EOS 核心缩写在带修饰词字段中仍保留完整业务含义。</summary>
        [DataTestMethod]
        [DataRow("PS_Version", "产品结构版本")]
        [DataRow("MPI_Date", "主计划日期")]
        [DataRow("DPI_Status", "交货计划状态")]
        [DataRow("IV_ID", "物料版本ID")]
        [DataRow("CC_ID", "成本中心ID")]
        [DataRow("CF_Name", "客户工厂及结算主体名称")]
        [DataRow("Pallet_Ist_ID", "托盘存储区域ID")]
        [DataRow("Iss_Date", "发货日期")]
        public void TranslateFieldName_KnowledgeBaseAcronyms_ReturnsCompleteChinese(string source, string expected)
        {
            Assert.AreEqual(expected, IdentifierTranslationService.TranslateFieldName(source));
            Assert.IsTrue(IdentifierTranslationService.IsFieldNameFullyTranslated(source));
        }

        /// <summary>XMZADD 20260908 验证 By 和 Who 按 EOS 操作人命名习惯组合，避免生成“由谁”等生硬字面翻译。</summary>
        [DataTestMethod]
        [DataRow("DeletedByName", "删除人姓名")]
        [DataRow("Remark_WhoName", "备注人姓名")]
        [DataRow("ByBox", "按箱")]
        [DataRow("byScan", "按扫码")]
        public void TranslateFieldName_ByWhoPatterns_ReturnNaturalOperatorMeaning(string source, string expected)
        {
            Assert.AreEqual(expected, IdentifierTranslationService.TranslateFieldName(source));
            Assert.IsTrue(IdentifierTranslationService.IsFieldNameFullyTranslated(source));
        }

        /// <summary>XMZADD 20260908 验证第二批高频完整英文词按数据库字段语义输出中文。</summary>
        [DataTestMethod]
        [DataRow("EffectiveStartDate", "生效开始日期")]
        [DataRow("Shift_ID", "班次ID")]
        [DataRow("Task_Node_Count", "任务节点数量")]
        [DataRow("ApplicationStyle", "申请样式")]
        [DataRow("LaborCost", "人工成本")]
        [DataRow("Due_Date", "到期日期")]
        [DataRow("FlowID", "流程ID")]
        [DataRow("MaxInv", "最大库存")]
        [DataRow("Tech_Node", "技术节点")]
        [DataRow("Trade_Terms", "贸易条款")]
        [DataRow("IsActive", "是否有效")]
        [DataRow("File_List", "文件列表")]
        [DataRow("Problem_Date", "问题日期")]
        [DataRow("Color_Tag", "颜色标记")]
        [DataRow("Program_Confirm", "项目确认")]
        [DataRow("Facility_ID", "设施ID")]
        [DataRow("FullPath", "完整路径")]
        [DataRow("PactCompany_ID", "合同公司ID")]
        [DataRow("Client_Version", "客户端版本")]
        [DataRow("Error_Message", "错误消息")]
        [DataRow("MotorType", "电机类型")]
        [DataRow("Actual_Qty", "实际数量")]
        [DataRow("Item_DrawNo", "物料图纸编号")]
        [DataRow("Reject_Qty", "拒收数量")]
        [DataRow("Save_Time", "保存时间")]
        [DataRow("Business_System_ID", "业务系统ID")]
        public void TranslateFieldName_SecondStableVocabularyBatch_ReturnsChinese(string source, string expected)
        {
            Assert.AreEqual(expected, IdentifierTranslationService.TranslateFieldName(source));
            Assert.IsTrue(IdentifierTranslationService.IsFieldNameFullyTranslated(source));
        }

        /// <summary>XMZADD 20260907 验证操作类业务字段名可作为可靠中文，而真正的流程措辞仍被拒绝。</summary>
        [TestMethod]
        public void IsReliableChineseName_OperationBusinessNames_AreNotTreatedAsProceduralText()
        {
            Assert.IsTrue(IdentifierTranslationService.IsReliableChineseName("操作记录创建时间"));
            Assert.IsTrue(IdentifierTranslationService.IsReliableChineseName("操作人"));
            Assert.IsTrue(IdentifierTranslationService.IsReliableChineseName("操作日期"));
            Assert.IsFalse(IdentifierTranslationService.IsReliableChineseName("操作"));
            Assert.IsFalse(IdentifierTranslationService.IsReliableChineseName("进行操作"));
            Assert.IsFalse(IdentifierTranslationService.IsReliableChineseName("点击操作"));
            Assert.IsFalse(IdentifierTranslationService.IsReliableChineseName("MPI_WC"));
            Assert.IsFalse(IdentifierTranslationService.IsReliableChineseName("订单条目ID 属于Purchase_Order_Item"));
        }

        /// <summary>XMZADD 20260916 验证可翻译英文词根不得混入正式中文名，同时保留开发人员通用的技术缩写。</summary>
        [TestMethod]
        public void IsReliableChineseName_UntranslatedLatinFragments_AreRejected()
        {
            Assert.IsFalse(IdentifierTranslationService.IsReliableChineseName("Owner公司ID"));
            Assert.IsFalse(IdentifierTranslationService.IsReliableChineseName("op创建时间"));
            Assert.IsFalse(IdentifierTranslationService.IsReliableChineseName("Spec规格"));
            Assert.IsFalse(IdentifierTranslationService.IsReliableChineseName("Currency币别"));
            Assert.IsFalse(IdentifierTranslationService.IsReliableChineseName("KIS编码"));
            Assert.IsFalse(IdentifierTranslationService.IsReliableChineseName("XYZ名称"));
            Assert.IsTrue(IdentifierTranslationService.IsReliableChineseName("事业部ID"));
            Assert.IsTrue(IdentifierTranslationService.IsReliableChineseName("产品BOM"));
            Assert.IsTrue(IdentifierTranslationService.IsReliableChineseName("OA付款批次"));
            Assert.IsTrue(IdentifierTranslationService.IsReliableChineseName("接口URL"));
        }

        /// <summary>XMZADD 20260907 验证金蝶 F 字段前缀不会成为未知缩写，也不会丢失金蝶来源语义。</summary>
        [DataTestMethod]
        [DataRow("Kis_FNumber", "金蝶编号")]
        [DataRow("Kis_FStock_BillNo", "金蝶库存单据编号")]
        public void Translate_KingdeeFieldPrefix_PreservesSourceAndFullyTranslates(string source, string expected)
        {
            IdentifierTranslationResult result = new IdentifierTranslationService().Translate(source);

            Assert.AreEqual(expected, result.Value);
            Assert.AreEqual(0, result.UnknownTokens.Count);
        }

        /// <summary>XMZADD 20260904 验证多义缩写不进入全局词典，保留给业务上下文规则解释。</summary>
        [DataTestMethod]
        [DataRow("AC")]
        [DataRow("FA")]
        [DataRow("PM")]
        [DataRow("SA")]
        [DataRow("IBS")]
        public void Translate_AmbiguousAbbreviation_RemainsUnknown(string source)
        {
            IdentifierTranslationResult result = new IdentifierTranslationService().Translate(source);

            Assert.AreEqual(string.Empty, result.Value);
            Assert.AreEqual(1, result.UnknownTokens.Count);
            Assert.AreEqual(source, result.UnknownTokens[0]);
        }

        /// <summary>XMZADD 20260831 验证旧快照的技术模块会归并为其他且人工维护模块保持不变。</summary>
        [TestMethod]
        public void RepairWeakMetadata_GroupsTechnicalModulesButPreservesManualModule()
        {
            var snapshot = new SHB.EosDataDictionary.Models.SnapshotData
            {
                Tables = new System.Collections.Generic.List<SHB.EosDataDictionary.Models.TableMetadata>
                {
                    new SHB.EosDataDictionary.Models.TableMetadata
                    {
                        ObjectName = "AB01",
                        ModuleName = new SHB.EosDataDictionary.Models.MetadataValue
                        {
                            Value = "表定义",
                            Status = SHB.EosDataDictionary.Models.ConfidenceStatus.Guessed
                        },
                        Fields = new System.Collections.Generic.List<SHB.EosDataDictionary.Models.FieldMetadata>
                        {
                            new SHB.EosDataDictionary.Models.FieldMetadata
                            {
                                FieldName = "AB01",
                                ChineseName = new SHB.EosDataDictionary.Models.MetadataValue
                                {
                                    Value = "推测：待确认",
                                    Status = SHB.EosDataDictionary.Models.ConfidenceStatus.PendingConfirmation
                                }
                            }
                        }
                    },
                    new SHB.EosDataDictionary.Models.TableMetadata
                    {
                        ObjectName = "AB02",
                        ModuleName = new SHB.EosDataDictionary.Models.MetadataValue
                        {
                            Value = "采购",
                            Status = SHB.EosDataDictionary.Models.ConfidenceStatus.LocalOverride,
                            IsManualOverride = true
                        },
                        Fields = new System.Collections.Generic.List<SHB.EosDataDictionary.Models.FieldMetadata>()
                    }
                }
            };

            IdentifierTranslationService.RepairWeakMetadata(snapshot);

            Assert.AreEqual("其他", snapshot.Tables[0].ModuleName.Value);
            Assert.AreEqual("采购", snapshot.Tables[1].ModuleName.Value);
            Assert.AreEqual("暂无可靠中文名称", snapshot.Tables[0].ChineseName.Value);
            Assert.AreEqual(ConfidenceStatus.PendingConfirmation, snapshot.Tables[0].ChineseName.Status);
            Assert.AreEqual("规则推测", snapshot.Tables[0].ChineseName.SourceType);
            Assert.AreEqual("暂无可靠中文名称", snapshot.Tables[0].Fields[0].ChineseName.Value);
            Assert.AreEqual(ConfidenceStatus.PendingConfirmation, snapshot.Tables[0].Fields[0].ChineseName.Status);
            Assert.AreEqual("规则推测", snapshot.Tables[0].Fields[0].ChineseName.SourceType);
        }

        /// <summary>XMZADD 20260831 验证人工维护模块即使保留待确认状态也绝不被旧快照修复覆盖。</summary>
        [TestMethod]
        public void RepairWeakMetadata_PreservesManualPendingConfirmationModule()
        {
            var snapshot = new SHB.EosDataDictionary.Models.SnapshotData
            {
                Tables = new System.Collections.Generic.List<SHB.EosDataDictionary.Models.TableMetadata>
                {
                    new SHB.EosDataDictionary.Models.TableMetadata
                    {
                        ObjectName = "AB01",
                        ModuleName = new SHB.EosDataDictionary.Models.MetadataValue
                        {
                            Value = "人工采购模块",
                            Status = SHB.EosDataDictionary.Models.ConfidenceStatus.PendingConfirmation,
                            IsManualOverride = true
                        },
                        Fields = new System.Collections.Generic.List<SHB.EosDataDictionary.Models.FieldMetadata>()
                    }
                }
            };

            IdentifierTranslationService.RepairWeakMetadata(snapshot);

            Assert.AreEqual("人工采购模块", snapshot.Tables[0].ModuleName.Value);
            Assert.AreEqual(ConfidenceStatus.PendingConfirmation, snapshot.Tables[0].ModuleName.Status);
        }
    }
}
