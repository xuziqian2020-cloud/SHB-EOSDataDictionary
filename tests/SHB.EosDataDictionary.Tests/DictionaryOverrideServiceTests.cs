using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHB.EosDataDictionary.Models;
using SHB.EosDataDictionary.Services;

namespace SHB.EosDataDictionary.Tests
{
    /// <summary>XMZADD 20260831 验证本机人工字典维护会覆盖自动推测且在刷新后保持最高优先级。</summary>
    [TestClass]
    public sealed class DictionaryOverrideServiceTests
    {
        /// <summary>XMZADD 20260831 验证表和字段人工中文名覆盖自动结果并保留自动值作为追溯依据。</summary>
        [TestMethod]
        public void ApplyOverrides_PreservesHumanValuesOverAutomaticResults()
        {
            string path = Path.Combine(Path.GetTempPath(), "shb-overrides-" + Guid.NewGuid().ToString("N") + ".db");
            const string scope = "dev|SHB";
            try
            {
                var store = new LocalDictionaryStore(path);
                store.SaveOverride(new DictionaryOverride
                {
                    ScopeKey = scope,
                    ObjectName = "T_ORDER",
                    FieldName = string.Empty,
                    PropertyName = "ChineseName",
                    ManualValue = "采购订单",
                    IsLocked = true
                });
                store.SaveOverride(new DictionaryOverride
                {
                    ScopeKey = scope,
                    ObjectName = "T_ORDER",
                    FieldName = "FBILLNO",
                    PropertyName = "ChineseName",
                    ManualValue = "采购单号",
                    IsLocked = true
                });
                var snapshot = new SnapshotData
                {
                    Tables = new List<TableMetadata>
                    {
                        new TableMetadata
                        {
                            ScopeKey = scope,
                            ObjectName = "T_ORDER",
                            ChineseName = new MetadataValue { Value = "推测：订单", Status = ConfidenceStatus.AiGuessed },
                            Fields = new List<FieldMetadata>
                            {
                                new FieldMetadata
                                {
                                    FieldName = "FBILLNO",
                                    ChineseName = new MetadataValue { Value = "推测：编号", Status = ConfidenceStatus.AiGuessed }
                                }
                            }
                        }
                    }
                };

                DictionaryOverrideService.ApplyOverrides(snapshot, store, scope);

                Assert.AreEqual("采购订单", snapshot.Tables[0].ChineseName.Value);
                Assert.AreEqual(ConfidenceStatus.LocalOverride, snapshot.Tables[0].ChineseName.Status);
                Assert.AreEqual("推测：订单", snapshot.Tables[0].ChineseName.OriginalAutomaticValue);
                Assert.AreEqual("采购单号", snapshot.Tables[0].Fields[0].ChineseName.Value);
                Assert.AreEqual(ConfidenceStatus.LocalOverride, snapshot.Tables[0].Fields[0].ChineseName.Status);
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        /// <summary>XMZADD 20260831 验证已从数据库结构删除的字段人工维护记录仍被保留并标记出来。</summary>
        [TestMethod]
        public void FindOrphanedOverrides_MarksDeletedFieldRecords()
        {
            string path = Path.Combine(Path.GetTempPath(), "shb-overrides-" + Guid.NewGuid().ToString("N") + ".db");
            const string scope = "dev|SHB";
            try
            {
                var store = new LocalDictionaryStore(path);
                store.SaveOverride(new DictionaryOverride
                {
                    ScopeKey = scope,
                    ObjectName = "T_ORDER",
                    FieldName = "FREMOVED",
                    PropertyName = "ChineseName",
                    ManualValue = "已删除字段的人工说明"
                });
                var snapshot = new SnapshotData
                {
                    Tables = new List<TableMetadata>
                    {
                        new TableMetadata { ObjectName = "T_ORDER", Fields = new List<FieldMetadata>() }
                    }
                };

                IList<DictionaryOverride> orphaned = DictionaryOverrideService.FindOrphanedOverrides(snapshot, store, scope);

                Assert.AreEqual(1, orphaned.Count);
                Assert.IsTrue(orphaned[0].IsOrphaned);
                Assert.AreEqual("FREMOVED", orphaned[0].FieldName);
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        /// <summary>XMZADD 20260901 验证表分类和空表保留规则可从本地字符串覆盖中恢复。</summary>
        [TestMethod]
        public void ApplyOverrides_RestoresCategoryAndKeepWhenEmpty()
        {
            string path = Path.Combine(Path.GetTempPath(), "shb-overrides-" + Guid.NewGuid().ToString("N") + ".db");
            const string scope = "dev|SHB";
            try
            {
                var store = new LocalDictionaryStore(path);
                store.SaveOverride(new DictionaryOverride
                {
                    ScopeKey = scope,
                    ObjectName = "T_ORDER",
                    FieldName = string.Empty,
                    PropertyName = "Category",
                    ManualValue = "Business"
                });
                store.SaveOverride(new DictionaryOverride
                {
                    ScopeKey = scope,
                    ObjectName = "T_ORDER",
                    FieldName = string.Empty,
                    PropertyName = "KeepWhenEmpty",
                    ManualValue = "True"
                });
                var snapshot = new SnapshotData();
                snapshot.Tables.Add(new TableMetadata
                {
                    ObjectName = "T_ORDER",
                    Category = DictionaryTableCategory.Technical,
                    KeepWhenEmpty = false
                });

                DictionaryOverrideService.ApplyOverrides(snapshot, store, scope);

                Assert.AreEqual(DictionaryTableCategory.Business, snapshot.Tables[0].Category);
                Assert.IsTrue(snapshot.Tables[0].KeepWhenEmpty);
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        /// <summary>XMZADD 20260901 验证未分类和非法布尔文本不会覆盖刷新后已有的表规则。</summary>
        [TestMethod]
        public void ApplyOverrides_InvalidCategoryAndKeepWhenEmpty_KeepCurrentValues()
        {
            string path = Path.Combine(Path.GetTempPath(), "shb-overrides-" + Guid.NewGuid().ToString("N") + ".db");
            const string scope = "dev|SHB";
            try
            {
                var store = new LocalDictionaryStore(path);
                store.SaveOverride(new DictionaryOverride
                {
                    ScopeKey = scope,
                    ObjectName = "T_ORDER",
                    FieldName = string.Empty,
                    PropertyName = "Category",
                    ManualValue = "Unclassified"
                });
                store.SaveOverride(new DictionaryOverride
                {
                    ScopeKey = scope,
                    ObjectName = "T_ORDER",
                    FieldName = string.Empty,
                    PropertyName = "KeepWhenEmpty",
                    ManualValue = "maybe"
                });
                var snapshot = new SnapshotData();
                snapshot.Tables.Add(new TableMetadata
                {
                    ObjectName = "T_ORDER",
                    Category = DictionaryTableCategory.BaseData,
                    KeepWhenEmpty = true
                });

                DictionaryOverrideService.ApplyOverrides(snapshot, store, scope);

                Assert.AreEqual(DictionaryTableCategory.BaseData, snapshot.Tables[0].Category);
                Assert.IsTrue(snapshot.Tables[0].KeepWhenEmpty);
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        /// <summary>XMZADD 20260901 验证数字、未分类和未知分类字符串都不会覆盖当前明确表分类。</summary>
        [TestMethod]
        public void ApplyOverrides_NumericUnclassifiedAndUnknownCategories_KeepCurrentValues()
        {
            string path = Path.Combine(Path.GetTempPath(), "shb-overrides-" + Guid.NewGuid().ToString("N") + ".db");
            const string scope = "dev|SHB";
            try
            {
                var store = new LocalDictionaryStore(path);
                string[] objectNames = { "T_NUMERIC", "T_UNCLASSIFIED", "T_UNKNOWN" };
                string[] manualValues = { "1", "Unclassified", "FutureCategory" };
                var snapshot = new SnapshotData();
                for (int i = 0; i < objectNames.Length; i++)
                {
                    store.SaveOverride(new DictionaryOverride
                    {
                        ScopeKey = scope,
                        ObjectName = objectNames[i],
                        FieldName = string.Empty,
                        PropertyName = "Category",
                        ManualValue = manualValues[i]
                    });
                    snapshot.Tables.Add(new TableMetadata
                    {
                        ObjectName = objectNames[i],
                        Category = DictionaryTableCategory.Technical
                    });
                }

                DictionaryOverrideService.ApplyOverrides(snapshot, store, scope);

                for (int i = 0; i < snapshot.Tables.Count; i++)
                {
                    Assert.AreEqual(DictionaryTableCategory.Technical, snapshot.Tables[i].Category);
                }
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        /// <summary>XMZADD 20260901 验证旧枚举名称覆盖不再属于字段编辑白名单并被标记为孤立记录。</summary>
        [TestMethod]
        public void FindOrphanedOverrides_EnumName_IsNoLongerSupported()
        {
            string path = Path.Combine(Path.GetTempPath(), "shb-overrides-" + Guid.NewGuid().ToString("N") + ".db");
            const string scope = "dev|SHB";
            try
            {
                var store = new LocalDictionaryStore(path);
                store.SaveOverride(new DictionaryOverride
                {
                    ScopeKey = scope,
                    ObjectName = "T_ORDER",
                    FieldName = "FSTATUS",
                    PropertyName = "EnumName",
                    ManualValue = "订单状态"
                });
                var snapshot = new SnapshotData();
                var table = new TableMetadata { ObjectName = "T_ORDER" };
                table.Fields.Add(new FieldMetadata { FieldName = "FSTATUS" });
                snapshot.Tables.Add(table);

                IList<DictionaryOverride> orphaned = DictionaryOverrideService.FindOrphanedOverrides(snapshot, store, scope);

                Assert.AreEqual(1, orphaned.Count);
                Assert.AreEqual("EnumName", orphaned[0].PropertyName);
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        /// <summary>XMZADD 20260917 验证旧快照缺少否决集合时仍能恢复本地决定并隐藏完全匹配的参考译名。</summary>
        [TestMethod]
        public void ApplyOverrides_MissingRejectedCollection_InitializesAndAppliesFingerprint()
        {
            string path = Path.Combine(Path.GetTempPath(), "shb-overrides-rejected-" + Guid.NewGuid().ToString("N") + ".db");
            const string scope = "dev|SHB";
            try
            {
                var suggestion = new MetadataValue { Value = "仓储区定义" };
                string fingerprint = new NameSuggestionFingerprintService().CreateFingerprint(suggestion);
                var store = new LocalDictionaryStore(path);
                store.SaveOverride(new DictionaryOverride
                {
                    ScopeKey = scope,
                    ObjectName = "T_STORAGE",
                    FieldName = string.Empty,
                    PropertyName = "RejectedSuggestionFingerprint",
                    ManualValue = fingerprint
                });
                var table = new TableMetadata
                {
                    ObjectName = "T_STORAGE",
                    SuggestedChineseName = suggestion,
                    RejectedSuggestionFingerprints = null
                };
                var snapshot = new SnapshotData();
                snapshot.Tables.Add(table);

                DictionaryOverrideService.ApplyOverrides(snapshot, store, scope);

                Assert.IsNotNull(table.RejectedSuggestionFingerprints);
                Assert.AreEqual(fingerprint, table.RejectedSuggestionFingerprints[0]);
                Assert.IsNull(table.SuggestedChineseName);
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }
}
