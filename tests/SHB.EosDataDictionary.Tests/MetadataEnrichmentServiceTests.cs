using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHB.EosDataDictionary.Models;
using SHB.EosDataDictionary.Services;
using SHB.EosDataDictionary.ViewModels;

namespace SHB.EosDataDictionary.Tests
{
    [TestClass]
    public sealed class MetadataEnrichmentServiceTests
    {
        /// <summary>XMZADD 20260903 验证 EOS t_ 实体和 f_ 属性使用源码真实名称写入数据字典。</summary>
        [TestMethod]
        public void Enrich_EosGeneratedEntity_UsesEntityAndActualPropertyName()
        {
            SnapshotData snapshot = CreateSingleFieldSnapshot("Item", "Item_ID");
            var evidence = new List<SourceEvidence>
            {
                new SourceEvidence
                {
                    ObjectName = "Item",
                    EntityName = "t_Item",
                    Evidence = new EvidenceItem { RuleName = "TableNameProperty" }
                },
                new SourceEvidence
                {
                    ObjectName = "Item",
                    FieldName = "Item_ID",
                    EntityName = "t_Item",
                    PropertyName = "f_Item_ID",
                    Evidence = new EvidenceItem { RuleName = "EntityProperty" }
                }
            };

            MetadataEnrichmentService.Enrich(snapshot, evidence);

            Assert.AreEqual("t_Item", snapshot.Tables[0].EntityName.Value);
            Assert.AreEqual(ConfidenceStatus.CodeEvidence, snapshot.Tables[0].EntityName.Status);
            Assert.AreEqual("t_Item.f_Item_ID", snapshot.Tables[0].Fields[0].EntityPropertyName.Value);
        }

        /// <summary>XMZADD 20260901 验证源码等号名称只展示右侧文本，同时保留完整原文和相对证据。</summary>
        [TestMethod]
        public void Enrich_SourceEqualsCandidate_UsesRightSideAndKeepsOriginalEvidence()
        {
            SnapshotData snapshot = CreateSingleFieldSnapshot("T_DEMO_ORDER", "FSTATUS");
            var evidence = new List<SourceEvidence>
            {
                new SourceEvidence
                {
                    ObjectName = "T_DEMO_ORDER",
                    FieldName = "FSTATUS",
                    ChineseNameCandidate = "FSTATUS=订单状态",
                    Evidence = new EvidenceItem
                    {
                        SourceType = "EOS源码",
                        SourcePath = "C:\\Private\\EOS\\OrderEntity.vb",
                        SourceLine = 20,
                        RuleName = "EntityProperty",
                        RawValue = "旧原文"
                    }
                }
            };

            MetadataEnrichmentService.Enrich(snapshot, evidence);

            MetadataValue name = snapshot.Tables[0].Fields[0].ChineseName;
            Assert.AreEqual("订单状态", name.Value);
            Assert.AreEqual(ConfidenceStatus.CodeEvidence, name.Status);
            Assert.AreEqual(1, name.Evidence.Count);
            Assert.AreEqual("FSTATUS=订单状态", name.Evidence[0].OriginalText);
            Assert.AreEqual("OrderEntity.vb", name.Evidence[0].SourcePath);
            Assert.IsFalse(name.SourceSummary.Contains("C:\\Private\\EOS"));
        }

        /// <summary>XMZADD 20260901 验证数据库等号名称标准化后仍保持数据库优先级和完整原始依据。</summary>
        [TestMethod]
        public void Enrich_DatabaseEqualsCandidate_UsesRightSideWithoutChangingPriority()
        {
            SnapshotData snapshot = CreateSingleFieldSnapshot("T_BD_MATERIAL", "FNAME");
            snapshot.Tables[0].Fields[0].ChineseName = new MetadataValue
            {
                Value = "FNAME=物料名称",
                Status = ConfidenceStatus.DatabaseEvidence,
                SourceType = "数据库说明",
                SourceSummary = "SQL Server 扩展描述",
                Evidence = new List<EvidenceItem>()
            };

            MetadataEnrichmentService.Enrich(snapshot, new List<SourceEvidence>());

            MetadataValue name = snapshot.Tables[0].Fields[0].ChineseName;
            Assert.AreEqual("物料名称", name.Value);
            Assert.AreEqual(ConfidenceStatus.DatabaseEvidence, name.Status);
            Assert.AreEqual("FNAME=物料名称", name.Evidence[0].OriginalText);
        }

        /// <summary>XMZADD 20260901 验证人工维护内容不作为自动候选清理，锁定语义保持不变。</summary>
        [TestMethod]
        public void Enrich_ManualNameContainingEquals_RemainsUnchanged()
        {
            SnapshotData snapshot = CreateSingleFieldSnapshot("T_BD_MATERIAL", "FNAME");
            snapshot.Tables[0].Fields[0].ChineseName = new MetadataValue
            {
                Value = "人工A=人工B",
                Status = ConfidenceStatus.LocalOverride,
                IsManualOverride = true,
                IsLocked = true,
                Evidence = new List<EvidenceItem>()
            };

            MetadataEnrichmentService.Enrich(snapshot, new List<SourceEvidence>());

            Assert.AreEqual("人工A=人工B", snapshot.Tables[0].Fields[0].ChineseName.Value);
            Assert.IsTrue(snapshot.Tables[0].Fields[0].ChineseName.IsLocked);
        }

        [TestMethod]
        public void Enrich_UsesSourceCommentForFieldAndEntity()
        {
            var snapshot = new SnapshotData
            {
                Tables = new List<TableMetadata>
                {
                    new TableMetadata
                    {
                        ObjectName = "T_DEMO_ORDER",
                        ObjectType = "TABLE",
                        ChineseName = new MetadataValue { Value = "推测：待确认", Status = ConfidenceStatus.PendingConfirmation },
                        ModuleName = new MetadataValue { Value = "推测：待确认", Status = ConfidenceStatus.PendingConfirmation },
                        EntityName = new MetadataValue { Value = "推测：待确认", Status = ConfidenceStatus.PendingConfirmation },
                        Fields = new List<FieldMetadata>
                        {
                            new FieldMetadata
                            {
                                FieldName = "FSTATUS",
                                ChineseName = new MetadataValue { Value = "推测：待确认", Status = ConfidenceStatus.PendingConfirmation },
                                EntityPropertyName = new MetadataValue { Value = "推测：待确认", Status = ConfidenceStatus.PendingConfirmation }
                            }
                        }
                    }
                }
            };
            var evidence = new List<SourceEvidence>
            {
                new SourceEvidence
                {
                    ObjectName = "T_DEMO_ORDER",
                    FieldName = "FSTATUS",
                    EntityName = "Kis_T_DEMO_ORDER",
                    ModulePath = "Purchase",
                    ChineseNameCandidate = "订单状态"
                }
            };

            MetadataEnrichmentService.Enrich(snapshot, evidence);

            Assert.AreEqual("订单状态", snapshot.Tables[0].Fields[0].ChineseName.Value);
            Assert.AreEqual(ConfidenceStatus.CodeEvidence, snapshot.Tables[0].Fields[0].ChineseName.Status);
            Assert.AreEqual("Kis_T_DEMO_ORDER.FSTATUS", snapshot.Tables[0].Fields[0].EntityPropertyName.Value);
            Assert.AreEqual(ConfidenceStatus.CodeEvidence, snapshot.Tables[0].Fields[0].EntityPropertyName.Status);
        }

        /// <summary>XMZADD 20260831 验证过程说明不能覆盖可直接翻译的表名和字段名。</summary>
        [TestMethod]
        public void Enrich_RejectsProceduralCommentAndUsesIdentifierTranslation()
        {
            var snapshot = new SnapshotData
            {
                Tables = new List<TableMetadata>
                {
                    new TableMetadata
                    {
                        ObjectName = "Item_Image",
                        ObjectType = "TABLE",
                        ChineseName = new MetadataValue { Value = "推测：待确认", Status = ConfidenceStatus.PendingConfirmation },
                        ModuleName = new MetadataValue { Value = "推测：待确认", Status = ConfidenceStatus.PendingConfirmation },
                        EntityName = new MetadataValue { Value = "推测：待确认", Status = ConfidenceStatus.PendingConfirmation },
                        Fields = new List<FieldMetadata>
                        {
                            new FieldMetadata
                            {
                                FieldName = "Img_ID",
                                ChineseName = new MetadataValue { Value = "推测：待确认", Status = ConfidenceStatus.PendingConfirmation }
                            }
                        }
                    }
                }
            };
            var evidence = new List<SourceEvidence>
            {
                new SourceEvidence
                {
                    ObjectName = "Item_Image",
                    EntityName = "ucPicView",
                    ModulePath = "ucPicView.vb",
                    ChineseNameCandidate = "Delete_ID(),Delete_N表示删除的图片的ID和总数",
                    Evidence = new EvidenceItem { RuleName = "TableNameProperty" }
                }
            };

            MetadataEnrichmentService.Enrich(snapshot, evidence);

            Assert.AreEqual("物料图片表", snapshot.Tables[0].ChineseName.Value);
            Assert.AreEqual(ConfidenceStatus.Guessed, snapshot.Tables[0].ChineseName.Status);
            Assert.AreEqual("名称翻译", snapshot.Tables[0].ChineseName.SourceType);
            Assert.AreEqual("图片ID", snapshot.Tables[0].Fields[0].ChineseName.Value);
            Assert.AreEqual("名称翻译", snapshot.Tables[0].Fields[0].ChineseName.SourceType);
            Assert.IsFalse(snapshot.Tables[0].ChineseName.Value.Contains("Item_Image"));
        }

        /// <summary>XMZADD 20260831 验证普通界面类不会被误展示为数据库表实体类。</summary>
        [TestMethod]
        public void Enrich_DoesNotUseOrdinaryControlClassAsEntity()
        {
            SnapshotData snapshot = CreateSingleFieldSnapshot("Item_Image", "Img_ID");
            var evidence = new List<SourceEvidence>
            {
                new SourceEvidence
                {
                    ObjectName = "Item_Image",
                    EntityName = "ucPicView",
                    ModulePath = "其他",
                    Evidence = new EvidenceItem { RuleName = "TableNameProperty" }
                }
            };

            MetadataEnrichmentService.Enrich(snapshot, evidence);

            Assert.IsTrue(snapshot.Tables[0].EntityName == null || string.IsNullOrWhiteSpace(snapshot.Tables[0].EntityName.Value));
        }

        /// <summary>XMZADD 20260831 验证源码属性类型关联到真实枚举时显示枚举和值。</summary>
        [TestMethod]
        public void Enrich_MapsCodeEnumToField()
        {
            SnapshotData snapshot = CreateSingleFieldSnapshot("T_DEMO_ORDER", "FSTATUS");
            var evidence = new List<SourceEvidence>
            {
                new SourceEvidence
                {
                    ObjectName = "T_DEMO_ORDER",
                    FieldName = "FSTATUS",
                    EntityName = "Kis_T_DEMO_ORDER",
                    PropertyTypeName = "DemoStatus",
                    Evidence = new EvidenceItem { RuleName = "EntityProperty" }
                },
                new SourceEvidence
                {
                    EnumName = "DemoStatus",
                    EnumValue = "Approved",
                    EnumRawValue = "1",
                    EnumChineseName = "已审核",
                    Strength = SourceEvidenceStrength.DirectBusinessCode,
                    Evidence = new EvidenceItem { RuleName = "EnumMember" }
                }
            };

            MetadataEnrichmentService.Enrich(snapshot, evidence);

            Assert.AreEqual("DemoStatus", snapshot.Tables[0].Fields[0].EnumName.Value);
            Assert.AreEqual(1, snapshot.Tables[0].Fields[0].EnumItems.Count);
            Assert.AreEqual("1", snapshot.Tables[0].Fields[0].EnumItems[0].Value);
            Assert.AreEqual("已审核", snapshot.Tables[0].Fields[0].EnumItems[0].ChineseName.Value);
        }

        /// <summary>XMZADD 20260914 验证生成实体的命名级枚举不能单独进入正式字段枚举。</summary>
        [TestMethod]
        public void Enrich_GeneratedEntityNamingOnlyEnum_DoesNotPublishEnum()
        {
            SnapshotData snapshot = CreateSingleFieldSnapshot("T_DEMO_ORDER", "FSTATUS");
            var evidence = new List<SourceEvidence>
            {
                new SourceEvidence
                {
                    ObjectName = "T_DEMO_ORDER",
                    FieldName = "FSTATUS",
                    EntityName = "t_T_DEMO_ORDER",
                    PropertyTypeName = "DemoStatus",
                    Strength = SourceEvidenceStrength.Authoritative,
                    Evidence = new EvidenceItem
                    {
                        SourceType = "EOS生成实体",
                        RuleName = "EntityProperty"
                    }
                },
                new SourceEvidence
                {
                    EnumName = "DemoStatus",
                    EnumValue = "Approved",
                    EnumRawValue = "1",
                    Strength = SourceEvidenceStrength.NamingOnly,
                    UsageKind = SourceUsageKind.Enumeration,
                    Evidence = new EvidenceItem
                    {
                        SourceType = "EOS生成实体",
                        RuleName = "EnumMember"
                    }
                }
            };

            MetadataEnrichmentService.Enrich(snapshot, evidence);

            Assert.IsNull(snapshot.Tables[0].Fields[0].EnumName);
            Assert.AreEqual(0, snapshot.Tables[0].Fields[0].EnumItems.Count);
        }

        /// <summary>XMZADD 20260914 验证真实业务源码的直接枚举证据仍可进入正式字段枚举。</summary>
        [TestMethod]
        public void Enrich_BusinessCodeEnum_PublishesEnum()
        {
            SnapshotData snapshot = CreateSingleFieldSnapshot("T_DEMO_ORDER", "FSTATUS");
            var evidence = new List<SourceEvidence>
            {
                new SourceEvidence
                {
                    ObjectName = "T_DEMO_ORDER",
                    FieldName = "FSTATUS",
                    EntityName = "t_T_DEMO_ORDER",
                    PropertyTypeName = "DemoStatus",
                    Strength = SourceEvidenceStrength.Authoritative,
                    Evidence = new EvidenceItem
                    {
                        SourceType = "EOS生成实体",
                        RuleName = "EntityProperty"
                    }
                },
                new SourceEvidence
                {
                    EnumName = "DemoStatus",
                    EnumValue = "Approved",
                    EnumRawValue = "1",
                    EnumChineseName = "已审核",
                    Strength = SourceEvidenceStrength.DirectBusinessCode,
                    UsageKind = SourceUsageKind.Enumeration,
                    Evidence = new EvidenceItem
                    {
                        SourceType = "EOS业务源码",
                        RuleName = "EnumMember"
                    }
                }
            };

            MetadataEnrichmentService.Enrich(snapshot, evidence);

            Assert.AreEqual("DemoStatus", snapshot.Tables[0].Fields[0].EnumName.Value);
            Assert.AreEqual(1, snapshot.Tables[0].Fields[0].EnumItems.Count);
            Assert.AreEqual("已审核", snapshot.Tables[0].Fields[0].EnumItems[0].ChineseName.Value);
        }

        /// <summary>XMZADD 20260904 验证真实快照中的只读枚举数组可被源码枚举安全重建。</summary>
        [TestMethod]
        public void Enrich_ReadOnlyEnumItems_ReplacesCollectionBeforePublishingCodeEnum()
        {
            SnapshotData snapshot = CreateSingleFieldSnapshot("T_DEMO_ORDER", "FSTATUS");
            snapshot.Tables[0].Fields[0].EnumItems = new EnumItemMetadata[0];
            var evidence = new List<SourceEvidence>
            {
                new SourceEvidence
                {
                    ObjectName = "T_DEMO_ORDER",
                    FieldName = "FSTATUS",
                    EntityName = "Kis_T_DEMO_ORDER",
                    PropertyTypeName = "DemoStatus",
                    Evidence = new EvidenceItem { RuleName = "EntityProperty" }
                },
                new SourceEvidence
                {
                    EnumName = "DemoStatus",
                    EnumValue = "Approved",
                    EnumRawValue = "1",
                    EnumChineseName = "已审核",
                    Strength = SourceEvidenceStrength.DirectBusinessCode,
                    Evidence = new EvidenceItem { RuleName = "EnumMember" }
                }
            };

            MetadataEnrichmentService.Enrich(snapshot, evidence);

            Assert.AreEqual(1, snapshot.Tables[0].Fields[0].EnumItems.Count);
            Assert.AreEqual("1", snapshot.Tables[0].Fields[0].EnumItems[0].Value);
        }

        /// <summary>XMZADD 20260831 验证源码没有枚举依据时枚举属性保持为空。</summary>
        [TestMethod]
        public void Enrich_LeavesEnumEmptyWhenCodeHasNoEnum()
        {
            SnapshotData snapshot = CreateSingleFieldSnapshot("Item_Image", "Img_ID");

            MetadataEnrichmentService.Enrich(snapshot, new List<SourceEvidence>());

            Assert.IsNull(snapshot.Tables[0].Fields[0].EnumName);
            Assert.AreEqual(0, snapshot.Tables[0].Fields[0].EnumItems.Count);
        }

        /// <summary>XMZADD 20260831 验证数据库扩展描述不会与较低优先级代码候选拼接成冲突名称。</summary>
        [TestMethod]
        public void Enrich_DatabaseDescription_RemainsHigherThanCodeCandidate()
        {
            SnapshotData snapshot = CreateSingleFieldSnapshot("Item_Image", "Img_ID");
            snapshot.Tables[0].ChineseName = new MetadataValue
            {
                Value = "物料图片",
                Status = ConfidenceStatus.DatabaseEvidence,
                SourceSummary = "SQL Server 扩展描述"
            };
            var evidence = new List<SourceEvidence>
            {
                new SourceEvidence
                {
                    ObjectName = "Item_Image",
                    ChineseNameCandidate = "图片资料表",
                    Evidence = new EvidenceItem { RuleName = "KisEntityClass" }
                }
            };

            MetadataEnrichmentService.Enrich(snapshot, evidence);

            Assert.AreEqual("物料图片", snapshot.Tables[0].ChineseName.Value);
            Assert.AreEqual(ConfidenceStatus.DatabaseEvidence, snapshot.Tables[0].ChineseName.Status);
        }

        /// <summary>XMZADD 20260831 验证枚举成员注释只服务于字段枚举，不参与表中文名称投票。</summary>
        [TestMethod]
        public void Enrich_EnumMemberComment_DoesNotBecomeTableName()
        {
            SnapshotData snapshot = CreateSingleFieldSnapshot("T_DEMO_ORDER", "FSTATUS");
            var evidence = new List<SourceEvidence>
            {
                new SourceEvidence
                {
                    ObjectName = "T_DEMO_ORDER",
                    ChineseNameCandidate = "已审核",
                    EnumName = "DemoStatus",
                    EnumValue = "Approved",
                    Evidence = new EvidenceItem { RuleName = "EnumMember" }
                }
            };

            MetadataEnrichmentService.Enrich(snapshot, evidence);

            Assert.AreNotEqual("已审核", snapshot.Tables[0].ChineseName.Value);
        }

        /// <summary>XMZADD 20260904 验证完全未知的表和字段不再显示纯英文并保持待确认状态。</summary>
        [TestMethod]
        public void Enrich_UnknownIdentifiers_UsesRuleGuessedFallback()
        {
            SnapshotData snapshot = CreateSingleFieldSnapshot("AB01", "AB01");

            MetadataEnrichmentService.Enrich(snapshot, new List<SourceEvidence>());

            Assert.AreEqual("暂无可靠中文名称", snapshot.Tables[0].ChineseName.Value);
            Assert.AreEqual(ConfidenceStatus.PendingConfirmation, snapshot.Tables[0].ChineseName.Status);
            Assert.AreEqual("规则推测", snapshot.Tables[0].ChineseName.SourceType);
            Assert.AreEqual("暂无可靠中文名称", snapshot.Tables[0].Fields[0].ChineseName.Value);
            Assert.AreEqual(ConfidenceStatus.PendingConfirmation, snapshot.Tables[0].Fields[0].ChineseName.Status);
            Assert.AreEqual("规则推测", snapshot.Tables[0].Fields[0].ChineseName.SourceType);
        }

        /// <summary>XMZADD 20260831 验证技术目录归并为其他、采购等真实业务模块保留且人工维护模块不被覆盖。</summary>
        [TestMethod]
        public void Enrich_GroupsTechnicalModulesButPreservesBusinessAndManualModules()
        {
            string[] technicalModules = { "表类定义", "表定义", "选择", "界面处理", "基类", "公共", "工具", "测试", "临时", "缓存", "未分类", "Demo.cs", "Demo.vb" };
            for (int i = 0; i < technicalModules.Length; i++)
            {
                SnapshotData technicalSnapshot = CreateSingleFieldSnapshot("T_TECH" + i, "FNAME");
                var technicalEvidence = new List<SourceEvidence>
                {
                    new SourceEvidence
                    {
                        ObjectName = "T_TECH" + i,
                        ModulePath = technicalModules[i],
                        Evidence = new EvidenceItem { RuleName = "TableNameProperty" }
                    }
                };

                MetadataEnrichmentService.Enrich(technicalSnapshot, technicalEvidence);

                Assert.AreEqual("其他", technicalSnapshot.Tables[0].ModuleName.Value);
            }
            SnapshotData businessSnapshot = CreateSingleFieldSnapshot("T_PURCHASE", "FNAME");
            var businessEvidence = new List<SourceEvidence>
            {
                new SourceEvidence
                {
                    ObjectName = "T_PURCHASE",
                    ModulePath = "采购",
                    Evidence = new EvidenceItem { RuleName = "TableNameProperty" }
                }
            };
            SnapshotData manualSnapshot = CreateSingleFieldSnapshot("T_MANUAL", "FNAME");
            manualSnapshot.Tables[0].ModuleName = new MetadataValue
            {
                Value = "采购",
                Status = ConfidenceStatus.LocalOverride,
                IsManualOverride = true
            };

            MetadataEnrichmentService.Enrich(businessSnapshot, businessEvidence);
            MetadataEnrichmentService.Enrich(manualSnapshot, new List<SourceEvidence>());

            Assert.AreEqual("采购", businessSnapshot.Tables[0].ModuleName.Value);
            Assert.AreEqual("采购", manualSnapshot.Tables[0].ModuleName.Value);
        }

        /// <summary>XMZADD 20260831 验证显示模型在中文名称缺失时提供保守文案而不显示待确认推测。</summary>
        [TestMethod]
        public void DisplayModels_UseConservativeTextWhenChineseNameIsMissing()
        {
            var table = new TableDisplayModel(new TableMetadata { ObjectName = "AB01" });
            var field = new FieldDisplayModel(new FieldMetadata { FieldName = "AB01" });
            var blankNameTable = new TableDisplayModel(new TableMetadata
            {
                ObjectName = "AB02",
                ChineseName = new MetadataValue { Value = " " }
            });
            var blankNameField = new FieldDisplayModel(new FieldMetadata
            {
                FieldName = "AB02",
                ChineseName = new MetadataValue { Value = " " }
            });

            Assert.AreEqual("暂无中文名称", table.ChineseName);
            Assert.AreEqual("暂无中文名称", field.ChineseName);
            Assert.AreEqual("暂无中文名称", blankNameTable.ChineseName);
            Assert.AreEqual("暂无中文名称", blankNameField.ChineseName);
        }

        /// <summary>XMZADD 20260831 验证图片名称字段由完整词典翻译时保留推测状态和名称翻译来源。</summary>
        [TestMethod]
        public void Enrich_ImgName_UsesGuessedNameTranslation()
        {
            SnapshotData snapshot = CreateSingleFieldSnapshot("Item_Image", "Img_Name");

            MetadataEnrichmentService.Enrich(snapshot, new List<SourceEvidence>());

            Assert.AreEqual("图片名称", snapshot.Tables[0].Fields[0].ChineseName.Value);
            Assert.AreEqual(ConfidenceStatus.Guessed, snapshot.Tables[0].Fields[0].ChineseName.Status);
            Assert.AreEqual("名称翻译", snapshot.Tables[0].Fields[0].ChineseName.SourceType);
        }

        /// <summary>XMZADD 20260903 验证 ACCOUNT 不再单独等同财务，并按托盘出入库或明确 FINANCE 上下文归类。</summary>
        [TestMethod]
        public void Enrich_AccountRequiresBusinessContextWhileFinanceMapsToFinance()
        {
            SnapshotData accountSnapshot = CreateSingleFieldSnapshot("T_ACCOUNT", "FNAME");
            SnapshotData palletSnapshot = CreateSingleFieldSnapshot("Account_Pallet_FA_Not_IO", "FNAME");
            SnapshotData financeSnapshot = CreateSingleFieldSnapshot("T_FINANCE", "FNAME");
            var evidence = new List<SourceEvidence>
            {
                new SourceEvidence
                {
                    ObjectName = "T_ACCOUNT",
                    ModulePath = "ACCOUNT",
                    Evidence = new EvidenceItem { RuleName = "TableNameProperty" }
                },
                new SourceEvidence
                {
                    ObjectName = "Account_Pallet_FA_Not_IO",
                    ModulePath = "ERP/仓库/ACCOUNT_PALLET_IO",
                    Evidence = new EvidenceItem { RuleName = "TableNameProperty" }
                },
                new SourceEvidence
                {
                    ObjectName = "T_FINANCE",
                    ModulePath = "FINANCE",
                    Evidence = new EvidenceItem { RuleName = "TableNameProperty" }
                }
            };

            MetadataEnrichmentService.Enrich(accountSnapshot, evidence);
            MetadataEnrichmentService.Enrich(palletSnapshot, evidence);
            MetadataEnrichmentService.Enrich(financeSnapshot, evidence);

            Assert.AreEqual("其他", accountSnapshot.Tables[0].ModuleName.Value);
            Assert.AreEqual("仓储", palletSnapshot.Tables[0].ModuleName.Value);
            Assert.AreEqual("财务", financeSnapshot.Tables[0].ModuleName.Value);
        }

        /// <summary>XMZADD 20260831 验证批量快照包含空表、空字段集合和空字段时仍可富化其余有效字段及枚举。</summary>
        [TestMethod]
        public void Enrich_SkipsNullMetadataAndEnrichesRemainingValidField()
        {
            var snapshot = new SnapshotData
            {
                Tables = new List<TableMetadata>
                {
                    null,
                    new TableMetadata
                    {
                        ObjectName = "T_EMPTY",
                        Fields = null
                    },
                    new TableMetadata
                    {
                        ObjectName = "T_VALID",
                        Fields = new List<FieldMetadata>
                        {
                            null,
                            new FieldMetadata
                            {
                                FieldName = "FSTATUS",
                                ChineseName = new MetadataValue { Value = "推测：待确认", Status = ConfidenceStatus.PendingConfirmation },
                                EnumItems = null
                            }
                        }
                    }
                }
            };
            var evidence = new List<SourceEvidence>
            {
                new SourceEvidence
                {
                    ObjectName = "T_VALID",
                    FieldName = "FSTATUS",
                    PropertyTypeName = "DemoStatus",
                    Evidence = new EvidenceItem
                    {
                        RuleName = "EntityProperty",
                        SourcePath = "ERP/表-类定义/t_Valid.vb",
                        SourceLine = 18
                    }
                },
                new SourceEvidence
                {
                    EnumName = "DemoStatus",
                    EnumValue = "Approved",
                    EnumRawValue = "1",
                    EnumChineseName = "已审核",
                    Strength = SourceEvidenceStrength.DirectBusinessCode,
                    Evidence = new EvidenceItem
                    {
                        RuleName = "EnumMember",
                        SourcePath = "ERP/Enums/DemoStatus.vb",
                        SourceLine = 7
                    }
                }
            };

            MetadataEnrichmentService.Enrich(snapshot, evidence);

            Assert.AreEqual("状态", snapshot.Tables[2].Fields[1].ChineseName.Value);
            Assert.AreEqual(1, snapshot.Tables[2].Fields[1].EnumItems.Count);
            Assert.AreEqual("已审核", snapshot.Tables[2].Fields[1].EnumItems[0].ChineseName.Value);
            Assert.AreEqual(1, snapshot.Tables[2].Fields[1].EnumName.Evidence.Count);
            Assert.AreEqual("EntityProperty", snapshot.Tables[2].Fields[1].EnumName.Evidence[0].RuleName);
            Assert.AreEqual(1, snapshot.Tables[2].Fields[1].EnumItems[0].ChineseName.Evidence.Count);
            Assert.AreEqual("EnumMember", snapshot.Tables[2].Fields[1].EnumItems[0].ChineseName.Evidence[0].RuleName);
        }

        /// <summary>XMZADD 20260831 创建只含一个字段的最小快照供富化规则测试复用。</summary>
        private static SnapshotData CreateSingleFieldSnapshot(string objectName, string fieldName)
        {
            return new SnapshotData
            {
                Tables = new List<TableMetadata>
                {
                    new TableMetadata
                    {
                        ObjectName = objectName,
                        ObjectType = "TABLE",
                        ChineseName = new MetadataValue { Value = "推测：待确认", Status = ConfidenceStatus.PendingConfirmation },
                        ModuleName = new MetadataValue { Value = "推测：待确认", Status = ConfidenceStatus.PendingConfirmation },
                        EntityName = new MetadataValue { Value = "推测：待确认", Status = ConfidenceStatus.PendingConfirmation },
                        Fields = new List<FieldMetadata>
                        {
                            new FieldMetadata
                            {
                                FieldName = fieldName,
                                ChineseName = new MetadataValue { Value = "推测：待确认", Status = ConfidenceStatus.PendingConfirmation },
                                EntityPropertyName = new MetadataValue { Value = "推测：待确认", Status = ConfidenceStatus.PendingConfirmation },
                                EnumItems = new List<EnumItemMetadata>()
                            }
                        }
                    }
                }
            };
        }
    }
}
