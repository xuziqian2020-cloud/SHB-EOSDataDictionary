using System;
using System.Collections;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHB.EosDataDictionary.Models;
using SHB.EosDataDictionary.Services;

namespace SHB.EosDataDictionary.Tests
{
    /// <summary>XMZADD 20260901 验证字典编辑批次只保存有效变化并可安全进入本地待上传队列。</summary>
    [TestClass]
    public sealed class DictionaryEditPersistenceTests
    {
        /// <summary>XMZADD 20260901 验证待上传批次的幂等编号、覆盖项和逐项变更信息可完整往返。</summary>
        [TestMethod]
        public void DictionaryJsonSerializer_Batch_RoundTripsAllPersistenceFields()
        {
            DateTime createdAtUtc = new DateTime(2026, 9, 1, 1, 2, 3, DateTimeKind.Utc);
            var batch = new DictionaryChangeBatch
            {
                BatchId = "11111111111111111111111111111111",
                AuthorGitHubUserId = "github-user-1",
                CreatedAtUtc = createdAtUtc
            };
            batch.Overrides.Add(new DictionaryOverride
            {
                ScopeKey = "dev|SHB",
                ObjectName = "T_ORDER",
                FieldName = "FBILLNO",
                ObjectKey = "dbo.T_ORDER",
                FieldKey = "FBILLNO",
                PropertyName = "ChineseName",
                ManualValue = "单据编号"
            });
            batch.Operations.Add(new DictionaryChangeOperation
            {
                OperationId = "22222222222222222222222222222222",
                AuthorGitHubUserId = "github-user-1",
                ObjectKey = "dbo.T_ORDER",
                FieldKey = "FBILLNO",
                PropertyName = "ChineseName",
                OldValue = "编号",
                NewValue = "单据编号",
                ChangeKind = "Set",
                CreatedAtUtc = createdAtUtc
            });

            string json = DictionaryJsonSerializer.SerializeBatch(batch);
            DictionaryChangeBatch restored = DictionaryJsonSerializer.DeserializeBatch(json);

            Assert.AreEqual(batch.BatchId, restored.BatchId);
            Assert.AreEqual(batch.AuthorGitHubUserId, restored.AuthorGitHubUserId);
            Assert.AreEqual(createdAtUtc, restored.CreatedAtUtc.ToUniversalTime());
            Assert.AreEqual(1, restored.Overrides.Count);
            Assert.AreEqual("dev|SHB", restored.Overrides[0].ScopeKey);
            Assert.AreEqual("dbo.T_ORDER", restored.Overrides[0].ObjectKey);
            Assert.AreEqual(1, restored.Operations.Count);
            Assert.AreEqual("22222222222222222222222222222222", restored.Operations[0].OperationId);
            Assert.AreEqual("编号", restored.Operations[0].OldValue);
            Assert.AreEqual("单据编号", restored.Operations[0].NewValue);
        }

        /// <summary>XMZADD 20260901 验证损坏的本地载荷只返回安全异常且不会把原始内容写入错误消息。</summary>
        [TestMethod]
        public void DictionaryJsonSerializer_InvalidJson_ThrowsSafeExceptionWithoutPayload()
        {
            const string invalidPayload = "{credential-like-private-payload";

            Exception exception = Assert.ThrowsException<SerializationException>(
                () => DictionaryJsonSerializer.DeserializeBatch(invalidPayload));

            Assert.IsFalse(exception.Message.Contains(invalidPayload));
            Assert.IsFalse(exception.InnerException != null && exception.InnerException.Message.Contains(invalidPayload));
        }

        /// <summary>XMZADD 20260901 验证编辑值未变化时不创建待上传批次。</summary>
        [TestMethod]
        public void CreateChangeBatch_NoActualChanges_ReturnsNull()
        {
            var table = new TableMetadata
            {
                SchemaName = "dbo",
                ObjectName = "T_ORDER",
                ChineseName = new MetadataValue { Value = "订单" }
            };
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "ChineseName", "订单" }
            };

            DictionaryChangeBatch batch = InvokeCreateChangeBatch("dev|SHB", table, null, values);

            Assert.IsNull(batch);
        }

        /// <summary>XMZADD 20260901 验证多个字段属性变化合并为一个批次并使用稳定键和逐项幂等编号。</summary>
        [TestMethod]
        public void CreateChangeBatch_MultipleFieldChanges_CreatesOneStableBatch()
        {
            var table = new TableMetadata { SchemaName = "dbo", ObjectName = "T_ORDER" };
            var field = new FieldMetadata
            {
                FieldName = "FBILLNO",
                ChineseName = new MetadataValue { Value = "编号" },
                BusinessMeaning = new MetadataValue { Value = "旧含义" },
                EnumName = new MetadataValue { Value = "旧枚举名" }
            };
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "ChineseName", "单据编号" },
                { "BusinessMeaning", "业务单据的唯一编号" },
                { "EnumName", "不允许发布的枚举名" }
            };

            DictionaryChangeBatch batch = InvokeCreateChangeBatch("dev|SHB", table, field, values);

            Assert.IsNotNull(batch);
            Guid parsedId;
            Assert.IsTrue(Guid.TryParseExact(batch.BatchId, "N", out parsedId));
            Assert.AreEqual(DateTimeKind.Utc, batch.CreatedAtUtc.Kind);
            Assert.AreEqual(2, batch.Overrides.Count);
            Assert.AreEqual(2, batch.Operations.Count);
            for (int i = 0; i < batch.Operations.Count; i++)
            {
                DictionaryChangeOperation operation = batch.Operations[i];
                Assert.IsTrue(Guid.TryParseExact(operation.OperationId, "N", out parsedId));
                Assert.AreEqual("dbo.T_ORDER", operation.ObjectKey);
                Assert.AreEqual("FBILLNO", operation.FieldKey);
                Assert.AreEqual(DateTimeKind.Utc, operation.CreatedAtUtc.Kind);
                Assert.AreNotEqual(operation.OldValue, operation.NewValue);
                Assert.AreNotEqual("EnumName", operation.PropertyName);
            }
            Assert.AreEqual("编号", batch.Operations[0].OldValue);
            Assert.AreEqual("单据编号", batch.Operations[0].NewValue);
        }

        /// <summary>XMZADD 20260901 验证缺失或空白的原自动值回退到首次人工编辑前的当前展示值。</summary>
        [TestMethod]
        public void CreateChangeBatch_BlankOriginalAutomaticValue_FallsBackToCurrentValue()
        {
            string[] blankValues = { null, string.Empty, "   " };
            for (int i = 0; i < blankValues.Length; i++)
            {
                var table = new TableMetadata
                {
                    SchemaName = "dbo",
                    ObjectName = "T_ORDER",
                    ChineseName = new MetadataValue
                    {
                        Value = "自动名称",
                        OriginalAutomaticValue = blankValues[i]
                    }
                };
                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "ChineseName", "人工名称" }
                };

                DictionaryChangeBatch batch = InvokeCreateChangeBatch("dev|SHB", table, null, values);

                Assert.AreEqual("自动名称", batch.Overrides[0].OriginalAutomaticValue, "空白场景索引：" + i);
            }
        }

        /// <summary>XMZADD 20260901 验证已有非空原自动值在后续人工编辑批次中继续保留。</summary>
        [TestMethod]
        public void CreateChangeBatch_ExistingOriginalAutomaticValue_IsPreserved()
        {
            var table = new TableMetadata
            {
                SchemaName = "dbo",
                ObjectName = "T_ORDER",
                ChineseName = new MetadataValue
                {
                    Value = "当前人工名称",
                    OriginalAutomaticValue = "首次自动名称"
                }
            };
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "ChineseName", "修改后的人工名称" }
            };

            DictionaryChangeBatch batch = InvokeCreateChangeBatch("dev|SHB", table, null, values);

            Assert.AreEqual("首次自动名称", batch.Overrides[0].OriginalAutomaticValue);
        }

        /// <summary>XMZADD 20260901 验证数字、未分类、未知分类和非法布尔值不能进入表规则发布批次。</summary>
        [TestMethod]
        public void CreateChangeBatch_NumericUnclassifiedUnknownAndInvalidBoolean_AreNotPublished()
        {
            var table = new TableMetadata
            {
                SchemaName = "dbo",
                ObjectName = "T_ORDER",
                Category = DictionaryTableCategory.Technical,
                KeepWhenEmpty = true
            };
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Category", "1" },
                { "KeepWhenEmpty", "maybe" }
            };

            DictionaryChangeBatch numericBatch = InvokeCreateChangeBatch("dev|SHB", table, null, values);
            values.Remove("KeepWhenEmpty");
            values["Category"] = "Unclassified";
            DictionaryChangeBatch unclassifiedBatch = InvokeCreateChangeBatch("dev|SHB", table, null, values);
            values["Category"] = "FutureCategory";
            DictionaryChangeBatch unknownBatch = InvokeCreateChangeBatch("dev|SHB", table, null, values);

            Assert.IsNull(numericBatch);
            Assert.IsNull(unclassifiedBatch);
            Assert.IsNull(unknownBatch);
        }

        /// <summary>XMZADD 20260901 验证保存按钮在所有值未变化时不会写入覆盖或待上传队列。</summary>
        [TestMethod]
        public void SaveChanges_NoActualChanges_DoesNotEnqueueBatch()
        {
            string path = Path.Combine(Path.GetTempPath(), "shb-edit-persistence-" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                RunInSta(() =>
                {
                    var table = new TableMetadata
                    {
                        SchemaName = "dbo",
                        ObjectName = "T_ORDER",
                        ChineseName = new MetadataValue { Value = "订单" }
                    };
                    var window = new SHB.EosDataDictionary.DictionaryEditWindow(path, "dev|SHB", table, null);
                    try
                    {
                        bool saved = InvokeSaveChanges(window);

                        Assert.IsFalse(saved);
                        Assert.AreEqual(0, new LocalDictionaryStore(path).LoadPendingOperations().Count);
                        Assert.AreEqual(0, new LocalDictionaryStore(path).LoadOverrides("dev|SHB").Count);
                    }
                    finally
                    {
                        window.Close();
                    }
                });
            }
            finally
            {
                DeletePath(path);
            }
        }

        /// <summary>XMZADD 20260901 验证多个实际变化只写入一个待上传批次并在事务成功后刷新字段内存值。</summary>
        [TestMethod]
        public void SaveChanges_MultipleChanges_EnqueuesOneBatchAndUpdatesMemory()
        {
            string path = Path.Combine(Path.GetTempPath(), "shb-edit-persistence-" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                RunInSta(() =>
                {
                    var table = new TableMetadata { SchemaName = "dbo", ObjectName = "T_ORDER" };
                    var field = new FieldMetadata
                    {
                        FieldName = "FBILLNO",
                        ChineseName = new MetadataValue { Value = "编号" },
                        BusinessMeaning = new MetadataValue { Value = "旧含义" }
                    };
                    var window = new SHB.EosDataDictionary.DictionaryEditWindow(path, "dev|SHB", table, field);
                    try
                    {
                        SetEditorValue(window, "ChineseName", "单据编号");
                        SetEditorValue(window, "BusinessMeaning", "业务单据唯一编号");

                        Assert.IsTrue(InvokeSaveChanges(window));

                        IList<PendingDictionaryOperation> pending = new LocalDictionaryStore(path).LoadPendingOperations();
                        Assert.AreEqual(1, pending.Count);
                        Assert.AreEqual(2, pending[0].Batch.Operations.Count);
                        Assert.AreEqual("单据编号", field.ChineseName.Value);
                        Assert.AreEqual("业务单据唯一编号", field.BusinessMeaning.Value);
                    }
                    finally
                    {
                        window.Close();
                    }
                });
            }
            finally
            {
                DeletePath(path);
            }
        }

        /// <summary>XMZADD 20260901 验证 SQLite 事务失败时字段内存值仍保持保存前状态。</summary>
        [TestMethod]
        public void SaveChanges_TransactionFails_DoesNotUpdateMemory()
        {
            string path = Path.Combine(Path.GetTempPath(), "shb-edit-persistence-" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                new LocalDictionaryStore(path);
                using (var connection = new SQLiteConnection("Data Source=" + path + ";Version=3;"))
                {
                    connection.Open();
                    using (SQLiteCommand command = connection.CreateCommand())
                    {
                        command.CommandText = @"CREATE TRIGGER RejectEditBatch
BEFORE INSERT ON PendingDictionaryOperations
BEGIN
    SELECT RAISE(ABORT, 'forced edit failure');
END;";
                        command.ExecuteNonQuery();
                    }
                }
                RunInSta(() =>
                {
                    var table = new TableMetadata { SchemaName = "dbo", ObjectName = "T_ORDER" };
                    var field = new FieldMetadata
                    {
                        FieldName = "FBILLNO",
                        ChineseName = new MetadataValue { Value = "原名称" }
                    };
                    var window = new SHB.EosDataDictionary.DictionaryEditWindow(path, "dev|SHB", table, field);
                    try
                    {
                        SetEditorValue(window, "ChineseName", "新名称");

                        Assert.ThrowsException<TargetInvocationException>(() => InvokeSaveChanges(window));

                        Assert.AreEqual("原名称", field.ChineseName.Value);
                        Assert.AreEqual(0, new LocalDictionaryStore(path).LoadOverrides("dev|SHB").Count);
                    }
                    finally
                    {
                        window.Close();
                    }
                });
            }
            finally
            {
                DeletePath(path);
            }
        }

        /// <summary>XMZADD 20260901 验证表分类使用枚举选择、空表规则使用布尔控件且字段不再显示枚举名称入口。</summary>
        [TestMethod]
        public void Editors_UseConfirmedWhitelistsAndTypedTableControls()
        {
            string path = Path.Combine(Path.GetTempPath(), "shb-edit-persistence-" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                RunInSta(() =>
                {
                    var table = new TableMetadata { SchemaName = "dbo", ObjectName = "T_ORDER" };
                    var tableWindow = new SHB.EosDataDictionary.DictionaryEditWindow(path, "dev|SHB", table, null);
                    var fieldWindow = new SHB.EosDataDictionary.DictionaryEditWindow(
                        path,
                        "dev|SHB",
                        table,
                        new FieldMetadata { FieldName = "FBILLNO" });
                    try
                    {
                        IDictionary tableEditors = GetEditors(tableWindow);
                        IDictionary fieldEditors = GetEditors(fieldWindow);

                        Assert.AreEqual(7, tableEditors.Count);
                        Assert.IsTrue(tableEditors["Category"] is ComboBox);
                        Assert.IsTrue(tableEditors["KeepWhenEmpty"] is CheckBox);
                        Assert.AreEqual(6, fieldEditors.Count);
                        Assert.IsFalse(fieldEditors.Contains("EnumName"));
                    }
                    finally
                    {
                        fieldWindow.Close();
                        tableWindow.Close();
                    }
                });
            }
            finally
            {
                DeletePath(path);
            }
        }

        /// <summary>XMZADD 20260901 调用编辑窗口实际使用的私有纯函数以验证持久化批次契约。</summary>
        private static DictionaryChangeBatch InvokeCreateChangeBatch(
            string scopeKey,
            TableMetadata table,
            FieldMetadata field,
            IDictionary<string, string> values)
        {
            MethodInfo method = typeof(SHB.EosDataDictionary.DictionaryEditWindow).GetMethod(
                "CreateChangeBatch",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(method);
            return (DictionaryChangeBatch)method.Invoke(
                null,
                new object[] { scopeKey, table, field, values, "github-user-1" });
        }

        /// <summary>XMZADD 20260901 调用保存按钮使用的私有保存流程以验证真实 SQLite 副作用。</summary>
        private static bool InvokeSaveChanges(SHB.EosDataDictionary.DictionaryEditWindow window)
        {
            MethodInfo method = typeof(SHB.EosDataDictionary.DictionaryEditWindow).GetMethod(
                "SaveChanges",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(method);
            return (bool)method.Invoke(window, null);
        }

        /// <summary>XMZADD 20260901 读取窗口实际编辑器集合以验证控件类型和设置测试输入。</summary>
        private static IDictionary GetEditors(SHB.EosDataDictionary.DictionaryEditWindow window)
        {
            FieldInfo field = typeof(SHB.EosDataDictionary.DictionaryEditWindow).GetField(
                "_editors",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field);
            return (IDictionary)field.GetValue(window);
        }

        /// <summary>XMZADD 20260901 为文本、枚举或布尔编辑器设置测试值。</summary>
        private static void SetEditorValue(
            SHB.EosDataDictionary.DictionaryEditWindow window,
            string propertyName,
            object value)
        {
            FrameworkElement editor = (FrameworkElement)GetEditors(window)[propertyName];
            var textBox = editor as TextBox;
            if (textBox != null)
            {
                textBox.Text = value == null ? string.Empty : value.ToString();
                return;
            }
            var comboBox = editor as ComboBox;
            if (comboBox != null)
            {
                comboBox.SelectedItem = value;
                return;
            }
            var checkBox = editor as CheckBox;
            if (checkBox != null)
            {
                checkBox.IsChecked = Convert.ToBoolean(value);
            }
        }

        /// <summary>XMZADD 20260901 在 STA 线程执行 WPF 窗口测试并把原始异常返回测试线程。</summary>
        private static void RunInSta(Action action)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    action();
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (failure != null)
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }
        }

        /// <summary>XMZADD 20260901 删除编辑持久化测试产生的 SQLite 文件和事务日志。</summary>
        private static void DeletePath(string path)
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + "-journal")) File.Delete(path + "-journal");
        }
    }
}
