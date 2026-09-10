using System;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace SHB.EosDataDictionary.Tests
{
    /// <summary>XMZADD 20260901 验证结构维护窗口具备安全凭据输入、扫描预览、确认发布和关闭取消入口。</summary>
    [TestClass]
    public sealed class StructureMaintenanceWindowTests
    {
        /// <summary>XMZADD 20260901 验证数据库密码只能通过 PasswordBox 输入，禁止普通文本框回显 DPAPI 密码。</summary>
        [TestMethod]
        public void Window_UsesPasswordBoxForDatabasePassword()
        {
            FieldInfo passwordField = typeof(StructureMaintenanceWindow).GetField(
                "_passwordBox", BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.IsNotNull(passwordField);
            Assert.AreEqual(typeof(PasswordBox), passwordField.FieldType);
        }

        /// <summary>XMZADD 20260901 验证窗口明确分离扫描预览、确认发布和关闭取消三个业务动作。</summary>
        [TestMethod]
        public void Window_ExposesPreviewPublishAndNonBlockingCloseHandlers()
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;

            Assert.IsNotNull(typeof(StructureMaintenanceWindow).GetMethod("ScanButton_Click", flags));
            Assert.IsNotNull(typeof(StructureMaintenanceWindow).GetMethod("PublishButton_Click", flags));
            Assert.IsNotNull(typeof(StructureMaintenanceWindow).GetMethod("Window_Closing", flags));
        }

        /// <summary>XMZADD 20260901 验证 GitHub 提交结果未确定期间关闭和取消动作只提示等待，不中断可能已落单的请求。</summary>
        [TestMethod]
        public void Window_PublishingCannotBeClosedOrCancelled()
        {
            RunInSta(delegate
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                FieldInfo publishingField = typeof(StructureMaintenanceWindow).GetField("_isPublishing", flags);
                FieldInfo cancellationField = typeof(StructureMaintenanceWindow).GetField("_operationCancellation", flags);
                FieldInfo statusField = typeof(StructureMaintenanceWindow).GetField("_statusText", flags);
                FieldInfo closingField = typeof(StructureMaintenanceWindow).GetField("_isClosing", flags);
                MethodInfo closingMethod = typeof(StructureMaintenanceWindow).GetMethod("Window_Closing", flags);
                MethodInfo cancelMethod = typeof(StructureMaintenanceWindow).GetMethod("CancelButton_Click", flags);

                Assert.IsNotNull(publishingField);
                var cancellation = new CancellationTokenSource();
                var status = new TextBlock();
                var window = (StructureMaintenanceWindow)FormatterServices.GetUninitializedObject(
                    typeof(StructureMaintenanceWindow));
                publishingField.SetValue(window, true);
                cancellationField.SetValue(window, cancellation);
                statusField.SetValue(window, status);

                var closingArguments = new CancelEventArgs();
                closingMethod.Invoke(window, new object[] { null, closingArguments });
                cancelMethod.Invoke(window, new object[] { null, new RoutedEventArgs() });

                Assert.IsTrue(closingArguments.Cancel);
                Assert.IsFalse(cancellation.IsCancellationRequested);
                Assert.IsFalse((bool)closingField.GetValue(window));
                StringAssert.Contains(status.Text, "等待");
                cancellation.Dispose();
            });
        }

        /// <summary>XMZADD 20260901 在 STA 线程执行 WPF 行为校验，使测试不依赖测试运行器的线程模型。</summary>
        private static void RunInSta(Action action)
        {
            Exception failure = null;
            var thread = new Thread(new ThreadStart(delegate
            {
                try
                {
                    action();
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
            }));
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (failure != null)
            {
                throw new AssertFailedException(failure.ToString());
            }
        }
    }
}
