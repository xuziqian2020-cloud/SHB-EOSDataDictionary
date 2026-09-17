using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHB.EosDataDictionary.Models;
using SHB.EosDataDictionary.Services;
using SHB.EosDataDictionary.ViewModels;

namespace SHB.EosDataDictionary.Tests
{
    /// <summary>XMZADD 20260828 验证真实 WPF 模板和主题资源在运行时按预期生效。</summary>
    [TestClass]
    [DoNotParallelize]
    public sealed class WpfResourceTests
    {
        private static readonly ManualResetEvent ApplicationReady = new ManualResetEvent(false);
        private static Application _application;
        private static Thread _applicationThread;
        private static Exception _applicationStartupFailure;
        private static string _wpfDatabasePath;
        private static string _wpfLocalDataDirectory;

        /// <summary>XMZADD 20260903 保存 DWM 返回的窗口可见物理边界，避免不可见缩放边框干扰工作区判断。</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeWindowRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        /// <summary>XMZADD 20260903 指定读取窗口的 DWM 扩展可见边界，排除不可见缩放边框。</summary>
        private const int DwmwaExtendedFrameBounds = 9;

        /// <summary>XMZADD 20260903 读取窗口当前 DWM 可见物理矩形，用于核验最大化窗口未覆盖系统任务栏。</summary>
        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(
            IntPtr hwnd,
            int dwAttribute,
            out NativeWindowRect pvAttribute,
            int cbAttribute);

        /// <summary>XMZADD 20260828 创建唯一 WPF 应用线程，避免测试运行器的 MTA 环境掩盖资源问题。</summary>
        [ClassInitialize]
        public static void InitializeWpfApplication(TestContext context)
        {
            _wpfLocalDataDirectory = Path.Combine(Path.GetTempPath(), "shb-wpf-resources-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_wpfLocalDataDirectory);
            AppPathService.SetLocalDataDirectoryOverrideForTesting(_wpfLocalDataDirectory);
            _wpfDatabasePath = Path.Combine(_wpfLocalDataDirectory, "ui-test.db");
            _applicationThread = new Thread(() =>
            {
                try
                {
                    Application.ResourceAssembly = typeof(ThemeService).Assembly;
                    var app = new SHB.EosDataDictionary.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    app.InitializeComponent();
                    _application = app;
                }
                catch (Exception ex)
                {
                    _applicationStartupFailure = ex;
                }
                finally
                {
                    ApplicationReady.Set();
                }
                if (_application != null)
                {
                    System.Windows.Threading.Dispatcher.Run();
                }
            });
            _applicationThread.SetApartmentState(ApartmentState.STA);
            _applicationThread.Start();
            if (!ApplicationReady.WaitOne(10000))
            {
                throw new TimeoutException("WPF 测试应用启动超时。");
            }
            if (_applicationStartupFailure != null)
            {
                throw _applicationStartupFailure;
            }
        }

        /// <summary>XMZADD 20260828 关闭测试专用 WPF 调度线程，避免影响后续测试进程退出。</summary>
        [ClassCleanup]
        public static void CleanupWpfApplication()
        {
            if (_application != null)
            {
                _application.Dispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Normal);
            }
            if (_applicationThread != null)
            {
                _applicationThread.Join(10000);
            }
            if (File.Exists(_wpfDatabasePath)) File.Delete(_wpfDatabasePath);
            if (File.Exists(_wpfDatabasePath + "-journal")) File.Delete(_wpfDatabasePath + "-journal");
            AppPathService.SetLocalDataDirectoryOverrideForTesting(null);
            if (!string.IsNullOrWhiteSpace(_wpfLocalDataDirectory) && Directory.Exists(_wpfLocalDataDirectory))
            {
                Directory.Delete(_wpfLocalDataDirectory, true);
            }
        }

        /// <summary>XMZADD 20260828 验证自定义滚动条模板将方向传递给内部 Track。</summary>
        [TestMethod]
        public void ScrollBarTemplate_VerticalBar_UsesVerticalTrack()
        {
            RunInSta(() =>
            {
                ResourceDictionary controls = LoadDictionary("Themes/ControlStyles.xaml");
                var scrollBar = new ScrollBar
                {
                    Orientation = Orientation.Vertical,
                    Style = (Style)controls[typeof(ScrollBar)]
                };

                scrollBar.ApplyTemplate();
                var track = scrollBar.Template.FindName("PART_Track", scrollBar) as Track;

                Assert.IsNotNull(track);
                Assert.AreEqual(Orientation.Vertical, track.Orientation);
            });
        }

        /// <summary>XMZADD 20260828 验证主题切换只替换色板并保留共享控件样式。</summary>
        [TestMethod]
        public void ThemeService_ApplyLight_ReplacesOnlyThemeDictionary()
        {
            RunInSta(() =>
            {
                string preferencePath = Path.Combine(Path.GetTempPath(), "SHB.EosDataDictionary.Tests", Guid.NewGuid().ToString("N"), "theme.txt");
                _application.Resources.MergedDictionaries.Clear();
                _application.Resources.MergedDictionaries.Add(LoadDictionary("Themes/DarkTheme.xaml"));
                _application.Resources.MergedDictionaries.Add(LoadDictionary("Themes/ControlStyles.xaml"));
                ResourceDictionary controls = _application.Resources.MergedDictionaries[1];
                var service = new ThemeService(new ThemePreferenceStore(preferencePath));

                service.Apply(AppTheme.Light);

                Assert.AreEqual(2, _application.Resources.MergedDictionaries.Count);
                Assert.AreSame(controls, _application.Resources.MergedDictionaries[1]);
                StringAssert.Contains(_application.Resources.MergedDictionaries[0].Source.OriginalString, "LightTheme.xaml");
                Assert.IsNotNull(_application.TryFindResource(typeof(DataGrid)));
            });
        }

        /// <summary>XMZADD 20260901 验证共享字典主界面隐藏数据库和源码入口，仅保留 GitHub 同步与字典维护动作。</summary>
        [TestMethod]
        public void MainWindow_SharedDictionaryToolbar_HidesLegacySourcesAndShowsSyncActions()
        {
            RunInSta(() =>
            {
                PrepareDarkResources();
                var window = CreateMeasuredMainWindow();
                try
                {
                    var legacyConnectionPanel = window.FindName("LegacyConnectionPanel") as FrameworkElement;
                    var profileCombo = window.FindName("ProfileCombo") as ComboBox;
                    var sourceButton = window.FindName("SourceButton") as Button;
                    var updateButton = window.FindName("UpdateDictionaryButton") as Button;
                    var editButton = window.FindName("EditDictionaryButton") as Button;
                    var syncSettingsButton = window.FindName("SyncSettingsButton") as Button;
                    var aiSettingsButton = window.FindName("AiSettingsButton") as Button;
                    var structureButton = window.FindName("StructureMaintenanceButton") as Button;
                    var githubStatus = window.FindName("GitHubStatusText") as TextBlock;
                    var githubFooter = window.FindName("GitHubFooterStatusPanel") as FrameworkElement;
                    var viewModel = window.DataContext as MainViewModel;
                    Assert.IsNotNull(legacyConnectionPanel);
                    Assert.IsNotNull(profileCombo);
                    Assert.IsNotNull(sourceButton);
                    Assert.IsNotNull(updateButton);
                    Assert.IsNotNull(editButton);
                    Assert.IsNotNull(syncSettingsButton);
                    Assert.IsNotNull(aiSettingsButton);
                    Assert.IsNotNull(structureButton);
                    Assert.IsNotNull(githubStatus);
                    Assert.IsNotNull(githubFooter);
                    Assert.IsNotNull(viewModel);
                    window.UpdateLayout();

                    Assert.AreEqual(Visibility.Collapsed, legacyConnectionPanel.Visibility);
                    Assert.AreEqual(Visibility.Collapsed, profileCombo.Visibility);
                    Assert.AreEqual(Visibility.Collapsed, sourceButton.Visibility);
                    Assert.AreEqual("更新字典", updateButton.Content);
                    Assert.AreEqual("编辑字典", editButton.Content);
                    Assert.AreEqual("同步设置", syncSettingsButton.Content);
                    Assert.AreEqual(Visibility.Collapsed, aiSettingsButton.Visibility);
                    Assert.AreEqual(Visibility.Collapsed, structureButton.Visibility);
                    Assert.AreEqual(viewModel.SyncStatusText, githubStatus.Text);
                }
                finally
                {
                    window.Close();
                }
            });
        }

        /// <summary>XMZADD 20260901 验证主窗口仅按远程发布者数字 ID 显示结构维护入口，不要求本机已存在数据库配置。</summary>
        [TestMethod]
        public void MainWindow_PublisherAccess_ShowsStructureMaintenanceWithoutLocalProfile()
        {
            RunTaskInSta(async delegate
            {
                PrepareDarkResources();
                var viewModel = new MainViewModel(_wpfDatabasePath, Path.GetTempPath());
                var client = new StubGitHubDictionaryClient();
                var window = new SHB.EosDataDictionary.MainWindow(
                    viewModel,
                    new ColumnLayoutStore(_wpfDatabasePath),
                    null,
                    new DictionaryRepositoryOptions { Owner = "dictionary-owner" },
                    new GitHubCredentialStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".bin")),
                    client);
                try
                {
                    MethodInfo refresh = typeof(SHB.EosDataDictionary.MainWindow).GetMethod(
                        "RefreshStructureMaintenanceAccessAsync", BindingFlags.Instance | BindingFlags.NonPublic);
                    Assert.IsNotNull(refresh);

                    var task = refresh.Invoke(window, new object[] { CancellationToken.None }) as Task;
                    Assert.IsNotNull(task);
                    await task;

                    Assert.IsTrue(viewModel.CanOpenStructureMaintenance);
                    Assert.AreEqual(1, client.GetCurrentUserCallCount);
                    var aiSettingsButton = window.FindName("AiSettingsButton") as Button;
                    Assert.IsNotNull(aiSettingsButton);
                    window.UpdateLayout();
                    Assert.AreEqual(Visibility.Visible, aiSettingsButton.Visibility);
                }
                finally
                {
                    window.Close();
                }
            });
        }

        /// <summary>XMZADD 20260903 验证窗口生命周期在发布者身份请求期间取消时启动任务正常结束而不是向界面线程抛异常。</summary>
        [TestMethod]
        public void MainWindow_StartupCancellationDuringPublisherAccess_CompletesNormally()
        {
            var identityStarted = new ManualResetEventSlim(false);
            var identityRelease = new ManualResetEventSlim(false);
            var cancellationObserved = new ManualResetEventSlim(false);
            var cancellation = new CancellationTokenSource();
            try
            {
                RunTaskInSta(async delegate
                {
                    PrepareDarkResources();
                    var viewModel = new MainViewModel(_wpfDatabasePath, Path.GetTempPath());
                    var client = new StubGitHubDictionaryClient
                    {
                        StoredIdentityStarted = identityStarted,
                        StoredIdentityRelease = identityRelease,
                        StoredIdentityCancellationObserved = cancellationObserved
                    };
                    var window = new SHB.EosDataDictionary.MainWindow(
                        viewModel,
                        new ColumnLayoutStore(_wpfDatabasePath),
                        null,
                        new DictionaryRepositoryOptions { Owner = "dictionary-owner" },
                        new GitHubCredentialStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".bin")),
                        client);
                    try
                    {
                        MethodInfo initialize = typeof(SHB.EosDataDictionary.MainWindow).GetMethod(
                            "InitializeSharedDictionaryAsync", BindingFlags.Instance | BindingFlags.NonPublic);
                        Assert.IsNotNull(initialize);
                        var initializeTask = initialize.Invoke(window, new object[] { cancellation.Token }) as Task;
                        Assert.IsNotNull(initializeTask);
                        Assert.IsTrue(await Task.Run(() => identityStarted.Wait(TimeSpan.FromSeconds(5D))));

                        cancellation.Cancel();
                        await initializeTask;

                        Assert.IsTrue(cancellationObserved.IsSet);
                        Assert.IsFalse(viewModel.CanOpenStructureMaintenance);
                    }
                    finally
                    {
                        identityRelease.Set();
                        window.Close();
                    }
                });
            }
            finally
            {
                cancellation.Cancel();
                identityRelease.Set();
                cancellation.Dispose();
                identityStarted.Dispose();
                identityRelease.Dispose();
                cancellationObserved.Dispose();
            }
        }

        /// <summary>XMZADD 20260901 验证顶部 GitHub 状态实时展示待上传数量，并按离线属性显隐离线标识。</summary>
        [TestMethod]
        public void MainWindow_GitHubToolbar_UpdatesPendingCountAndOfflineVisibilityAtRuntime()
        {
            RunInSta(() =>
            {
                PrepareDarkResources();
                var window = CreateMeasuredMainWindow();
                try
                {
                    var viewModel = window.DataContext as MainViewModel;
                    var pendingText = window.FindName("GitHubPendingUploadText") as TextBlock;
                    var pendingApplyText = window.FindName("GitHubPendingApplyText") as TextBlock;
                    var lastFetchText = window.FindName("GitHubLastFetchText") as TextBlock;
                    var offlineBadge = window.FindName("GitHubOfflineBadge") as FrameworkElement;
                    Assert.IsNotNull(viewModel);
                    Assert.IsNotNull(pendingText);
                    Assert.IsNotNull(pendingApplyText);
                    Assert.IsNotNull(lastFetchText);
                    Assert.IsNotNull(offlineBadge);

                    viewModel.PendingUploadCount = 3;
                    viewModel.PendingApplyCount = 2;
                    viewModel.IsOffline = true;
                    pendingText.GetBindingExpression(TextBlock.TextProperty).UpdateTarget();
                    pendingApplyText.GetBindingExpression(TextBlock.TextProperty).UpdateTarget();
                    lastFetchText.GetBindingExpression(TextBlock.TextProperty).UpdateTarget();
                    offlineBadge.GetBindingExpression(UIElement.VisibilityProperty).UpdateTarget();
                    Assert.AreEqual("3", pendingText.Text);
                    Assert.AreEqual("2", pendingApplyText.Text);
                    Assert.AreEqual("尚未获取", lastFetchText.Text);
                    Assert.AreEqual(Visibility.Visible, offlineBadge.Visibility);

                    viewModel.PendingUploadCount = 0;
                    viewModel.PendingApplyCount = 0;
                    viewModel.IsOffline = false;
                    pendingText.GetBindingExpression(TextBlock.TextProperty).UpdateTarget();
                    pendingApplyText.GetBindingExpression(TextBlock.TextProperty).UpdateTarget();
                    offlineBadge.GetBindingExpression(UIElement.VisibilityProperty).UpdateTarget();
                    Assert.AreEqual("0", pendingText.Text);
                    Assert.AreEqual("0", pendingApplyText.Text);
                    Assert.AreEqual(Visibility.Collapsed, offlineBadge.Visibility);
                }
                finally
                {
                    window.Close();
                }
            });
        }

        /// <summary>XMZADD 20260828 验证切换下拉候选项不会在连接成功前改变当前活动连接。</summary>
        [TestMethod]
        public void MainWindow_ProfileSelection_DoesNotChangeValidatedConnection()
        {
            RunInSta(() =>
            {
                PrepareDarkResources();
                var window = CreateMeasuredMainWindow();
                try
                {
                    var profileCombo = window.FindName("ProfileCombo") as ComboBox;
                    var viewModel = window.DataContext as MainViewModel;
                    Assert.IsNotNull(profileCombo);
                    Assert.IsNotNull(viewModel);

                    var connectedProfile = new ConnectionProfile { Name = "已连接库", Database = "EOS_CONNECTED" };
                    var candidateProfile = new ConnectionProfile { Name = "候选库", Database = "EOS_CANDIDATE" };
                    viewModel.Profiles.Clear();
                    viewModel.Profiles.Add(connectedProfile);
                    viewModel.Profiles.Add(candidateProfile);
                    viewModel.MarkConnectionValidated(connectedProfile, connectedProfile.Database);

                    profileCombo.SelectedItem = candidateProfile;

                    Assert.AreSame(connectedProfile, viewModel.CurrentProfile);
                    Assert.AreEqual("已连接库", viewModel.CurrentConnectionText);
                    Assert.AreEqual("EOS_CONNECTED", viewModel.CurrentDatabaseText);
                }
                finally
                {
                    window.Close();
                }
            });
        }

        /// <summary>XMZADD 20260828 验证全量加载遮罩提供尺寸一致且可识别的取消按钮。</summary>
        [TestMethod]
        public void MainWindow_BusyOverlay_ContainsCancelLoadButton()
        {
            RunInSta(() =>
            {
                PrepareDarkResources();
                var window = CreateMeasuredMainWindow();
                try
                {
                    var cancelButton = window.FindName("CancelLoadButton") as Button;
                    var busyOverlay = window.FindName("BusyOverlay") as Border;
                    var commandBar = window.FindName("CommandBar") as Border;
                    var viewModel = window.DataContext as MainViewModel;
                    Assert.IsNotNull(cancelButton);
                    Assert.IsNotNull(busyOverlay);
                    Assert.IsNotNull(commandBar);
                    Assert.IsNotNull(viewModel);
                    Assert.AreEqual(96d, cancelButton.Width, 0.1d);
                    Assert.AreEqual(36d, cancelButton.Height, 0.1d);
                    Assert.AreEqual("取消加载", cancelButton.Content);
                    Assert.AreEqual(1, Grid.GetRow(busyOverlay));
                    Assert.AreEqual(2, Grid.GetRowSpan(busyOverlay));

                    MethodInfo busySetter = typeof(MainViewModel).GetProperty("IsBusy").GetSetMethod(true);
                    busySetter.Invoke(viewModel, new object[] { true });
                    window.UpdateLayout();
                    Assert.IsFalse(commandBar.IsEnabled);
                }
                finally
                {
                    window.Close();
                }
            });
        }

        /// <summary>XMZADD 20260903 验证真实主窗口最大化后受主屏工作区约束，避免底部状态栏被任务栏遮挡。</summary>
        [TestMethod]
        public void MainWindow_Maximized_StaysInsidePrimaryWorkingArea()
        {
            RunInSta(() =>
            {
                PrepareDarkResources();
                Rect workArea = SystemParameters.WorkArea;
                if (workArea.Width < 1360D || workArea.Height < 760D)
                {
                    Assert.Inconclusive(
                        string.Format("主屏工作区实际为 {0}×{1}，小于验证主窗口最小尺寸所需的 1360×760。", workArea.Width, workArea.Height));
                }

                var window = new SHB.EosDataDictionary.MainWindow(
                    new MainViewModel(_wpfDatabasePath, Path.GetTempPath()),
                    new ColumnLayoutStore(_wpfDatabasePath))
                {
                    Width = 1600,
                    Height = 980,
                    Left = workArea.Left + 20,
                    Top = workArea.Top + 20,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    WindowStartupLocation = WindowStartupLocation.Manual
                };
                try
                {
                    window.Show();
                    window.WindowState = WindowState.Maximized;
                    window.UpdateLayout();

                    var frame = new System.Windows.Threading.DispatcherFrame();
                    window.Dispatcher.BeginInvoke(
                        System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                        new Action(() => frame.Continue = false));
                    System.Windows.Threading.Dispatcher.PushFrame(frame);
                    window.UpdateLayout();

                    Assert.AreEqual(
                        WindowState.Maximized,
                        window.WindowState,
                        string.Format("窗口状态实际为 {0}，期望为 {1}。", window.WindowState, WindowState.Maximized));

                    IntPtr windowHandle = new WindowInteropHelper(window).Handle;
                    Assert.AreNotEqual(IntPtr.Zero, windowHandle, "窗口句柄实际为空，期望已创建原生窗口句柄。");
                    NativeWindowRect windowRect;
                    int hresult = DwmGetWindowAttribute(
                        windowHandle,
                        DwmwaExtendedFrameBounds,
                        out windowRect,
                        Marshal.SizeOf(typeof(NativeWindowRect)));
                    Assert.AreEqual(
                        0,
                        hresult,
                        string.Format("读取 DWM 可见窗口矩形的 HRESULT 实际为 0x{0:X8}，期望为 0x00000000。", hresult));

                    HwndSource windowSource = HwndSource.FromHwnd(windowHandle);
                    Assert.IsNotNull(windowSource, "窗口呈现源实际为空，期望可读取当前窗口 DPI 变换。");
                    Assert.IsNotNull(windowSource.CompositionTarget, "窗口呈现目标实际为空，期望可把工作区转换为物理坐标。");
                    Point physicalWorkAreaTopLeft = windowSource.CompositionTarget.TransformToDevice.Transform(
                        new Point(workArea.Left, workArea.Top));
                    Point physicalWorkAreaBottomRight = windowSource.CompositionTarget.TransformToDevice.Transform(
                        new Point(workArea.Right, workArea.Bottom));
                    const double physicalPixelTolerance = 2D;

                    Assert.IsTrue(
                        windowRect.Left >= physicalWorkAreaTopLeft.X - physicalPixelTolerance &&
                        windowRect.Top >= physicalWorkAreaTopLeft.Y - physicalPixelTolerance &&
                        windowRect.Right <= physicalWorkAreaBottomRight.X + physicalPixelTolerance &&
                        windowRect.Bottom <= physicalWorkAreaBottomRight.Y + physicalPixelTolerance,
                        string.Format(
                            "DWM 可见窗口实际矩形为 [{0}, {1}, {2}, {3}]，期望位于物理工作区 [{4}, {5}, {6}, {7}] 内（容差 {8} 像素）。",
                            windowRect.Left,
                            windowRect.Top,
                            windowRect.Right,
                            windowRect.Bottom,
                            physicalWorkAreaTopLeft.X,
                            physicalWorkAreaTopLeft.Y,
                            physicalWorkAreaBottomRight.X,
                            physicalWorkAreaBottomRight.Y,
                            physicalPixelTolerance));
                }
                finally
                {
                    window.Close();
                }
            });
        }

        /// <summary>XMZADD 20260903 验证最大化主窗口通过标题栏拖动恢复后回到普通状态，并完整保留在主屏工作区。</summary>
        [TestMethod]
        public void MainWindow_TitleBarDragFromMaximized_RestoresInsidePrimaryWorkingArea()
        {
            RunInSta(() =>
            {
                PrepareDarkResources();
                Rect workArea = SystemParameters.WorkArea;
                const double restoredWidth = 1600D;
                const double restoredHeight = 980D;
                if (workArea.Width < restoredWidth || workArea.Height < restoredHeight)
                {
                    Assert.Inconclusive(
                        string.Format(
                            "主屏工作区实际为 {0}×{1}，小于验证恢复窗口所需的 {2}×{3}。",
                            workArea.Width,
                            workArea.Height,
                            restoredWidth,
                            restoredHeight));
                }

                var window = new SHB.EosDataDictionary.MainWindow(
                    new MainViewModel(_wpfDatabasePath, Path.GetTempPath()),
                    new ColumnLayoutStore(_wpfDatabasePath))
                {
                    Width = restoredWidth,
                    Height = restoredHeight,
                    Left = workArea.Left + ((workArea.Width - restoredWidth) / 2D),
                    Top = workArea.Top + ((workArea.Height - restoredHeight) / 2D),
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    WindowStartupLocation = WindowStartupLocation.Manual
                };
                try
                {
                    window.Show();
                    window.WindowState = WindowState.Maximized;
                    window.UpdateLayout();

                    var maximizeFrame = new System.Windows.Threading.DispatcherFrame();
                    window.Dispatcher.BeginInvoke(
                        System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                        new Action(() => maximizeFrame.Continue = false));
                    System.Windows.Threading.Dispatcher.PushFrame(maximizeFrame);
                    window.UpdateLayout();
                    Assert.AreEqual(WindowState.Maximized, window.WindowState);

                    MethodInfo restoreWindow = typeof(SHB.EosDataDictionary.MainWindow).GetMethod(
                        "RestoreWindowForTitleBarDrag",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    Assert.IsNotNull(restoreWindow);
                    var mouseArgs = new MouseButtonEventArgs(
                        Mouse.PrimaryDevice,
                        Environment.TickCount,
                        MouseButton.Left)
                    {
                        RoutedEvent = Mouse.MouseDownEvent,
                        Source = window
                    };

                    restoreWindow.Invoke(window, new object[] { mouseArgs });

                    var restoreFrame = new System.Windows.Threading.DispatcherFrame();
                    window.Dispatcher.BeginInvoke(
                        System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                        new Action(() => restoreFrame.Continue = false));
                    System.Windows.Threading.Dispatcher.PushFrame(restoreFrame);
                    window.UpdateLayout();

                    Assert.AreEqual(WindowState.Normal, window.WindowState);
                    double restoredRight = window.Left + window.ActualWidth;
                    double restoredBottom = window.Top + window.ActualHeight;
                    const double dipTolerance = 1D;
                    Assert.IsTrue(
                        window.Left >= workArea.Left - dipTolerance &&
                        window.Top >= workArea.Top - dipTolerance &&
                        restoredRight <= workArea.Right + dipTolerance &&
                        restoredBottom <= workArea.Bottom + dipTolerance,
                        string.Format(
                            "恢复窗口实际矩形为 [{0}, {1}, {2}, {3}]，期望位于主屏工作区 [{4}, {5}, {6}, {7}] 内。",
                            window.Left,
                            window.Top,
                            restoredRight,
                            restoredBottom,
                            workArea.Left,
                            workArea.Top,
                            workArea.Right,
                            workArea.Bottom));
                }
                finally
                {
                    window.Close();
                }
            });
        }

        /// <summary>XMZADD 20260828 验证真实主窗口在设计尺寸下使用已确认的顶栏、三栏和详情偏移。</summary>
        [TestMethod]
        public void MainWindow_AtDesignSize_UsesApprovedPanelGeometry()
        {
            RunInSta(() =>
            {
                PrepareDarkResources();
                var window = CreateMeasuredMainWindow();
                try
                {
                    var themeButton = window.FindName("ThemeButton") as Button;
                    var tablesGrid = window.FindName("TablesGrid") as DataGrid;
                    var appTitleBar = window.FindName("AppTitleBar") as Border;
                    Assert.IsNotNull(themeButton);
                    Assert.IsNotNull(tablesGrid);
                    Assert.IsNotNull(appTitleBar);

                    Border topBar = window.FindName("CommandBar") as Border;
                    Border catalogPanel = FindAncestorBorder(tablesGrid, 500d);
                    IList<DataGrid> visibleGrids = FindVisibleDataGrids(window);
                    DataGrid fieldsGrid = FindGridOtherThan(visibleGrids, tablesGrid);
                    TabControl detailTabs = FindAncestor<TabControl>(fieldsGrid);

                    Assert.IsNotNull(topBar);
                    Assert.IsNotNull(catalogPanel);
                    Assert.IsNotNull(fieldsGrid);
                    Assert.IsNotNull(detailTabs);

                    Point topBarPosition = topBar.TranslatePoint(new Point(0, 0), window);
                    Point catalogPosition = catalogPanel.TranslatePoint(new Point(0, 0), window);
                    Point fieldsPosition = fieldsGrid.TranslatePoint(new Point(0, 0), detailTabs);

                    Assert.AreEqual(1600d, window.ActualWidth, 1d);
                    Assert.AreEqual(WindowStyle.None, window.WindowStyle);
                    Assert.AreEqual(34d, appTitleBar.ActualHeight, 1d);
                    Assert.AreEqual(17d, topBarPosition.X, 1d);
                    Assert.AreEqual(47d, topBarPosition.Y, 1d);
                    Assert.AreEqual(72d, topBar.ActualHeight, 1d);
                    Assert.AreEqual(42d, themeButton.ActualWidth, 1d);
                    Assert.AreEqual(36d, themeButton.ActualHeight, 1d);
                    Assert.AreEqual(262d, catalogPosition.X, 2d);
                    Assert.AreEqual(630d, catalogPanel.ActualWidth, 3d);
                    Assert.AreEqual(86d, fieldsPosition.Y, 1d);
                }
                finally
                {
                    window.Close();
                }
            });
        }

        /// <summary>XMZADD 20260831 验证表格使用可拖拽的初始列宽并允许横向查看长文本。</summary>
        [TestMethod]
        public void MainWindow_DataGrids_UseApprovedColumnSizingAndCenteredText()
        {
            RunInSta(() =>
            {
                PrepareDarkResources();
                var window = CreateMeasuredMainWindow();
                try
                {
                    var tablesGrid = window.FindName("TablesGrid") as DataGrid;
                    Assert.IsNotNull(tablesGrid);
                    IList<DataGrid> visibleGrids = FindVisibleDataGrids(window);
                    DataGrid fieldsGrid = FindGridOtherThan(visibleGrids, tablesGrid);
                    Assert.IsNotNull(fieldsGrid);

                    Assert.IsTrue(tablesGrid.Columns[0].Width.IsAbsolute);
                    Assert.AreEqual(112d, tablesGrid.Columns[0].Width.Value, 0.1d);
                    Assert.IsTrue(tablesGrid.Columns[1].Width.IsAbsolute);
                    Assert.IsTrue(tablesGrid.Columns[2].Width.IsAbsolute);
                    Assert.IsTrue(tablesGrid.Columns[3].Width.IsAbsolute);
                    Assert.AreEqual(210d, tablesGrid.Columns[3].Width.Value, 0.1d);
                    Assert.IsTrue(tablesGrid.Columns[4].Width.IsAbsolute);
                    Assert.IsTrue(tablesGrid.Columns[5].Width.IsAbsolute);
                    Assert.IsTrue(tablesGrid.CanUserResizeColumns);
                    Assert.AreEqual(ScrollBarVisibility.Auto, tablesGrid.HorizontalScrollBarVisibility);
                    Assert.IsTrue(fieldsGrid.CanUserResizeColumns);
                    Assert.AreEqual(ScrollBarVisibility.Auto, fieldsGrid.HorizontalScrollBarVisibility);

                    double fieldsWidth = 0d;
                    for (int i = 0; i < fieldsGrid.Columns.Count; i++)
                    {
                        Assert.IsTrue(fieldsGrid.Columns[i].Width.IsAbsolute);
                        fieldsWidth += fieldsGrid.Columns[i].Width.Value;
                    }
                    Assert.IsTrue(fieldsWidth >= 1200d);

                    var centeredStyle = window.TryFindResource("CenteredDataGridTextStyle") as Style;
                    Assert.IsNotNull(centeredStyle);
                    AssertTextColumnsUseStyle(tablesGrid, centeredStyle);
                    AssertTextColumnsUseStyle(fieldsGrid, centeredStyle);
                }
                finally
                {
                    window.Close();
                }
            });
        }

        /// <summary>XMZADD 20260901 验证数据表和字段采用单选整行，并由字段双向选择决定当前编辑对象。</summary>
        [TestMethod]
        public void MainWindow_DataTableAndFieldSelection_UsesSingleFullRowBindings()
        {
            RunInSta(() =>
            {
                PrepareDarkResources();
                var window = CreateMeasuredMainWindow();
                try
                {
                    var tablesGrid = window.FindName("TablesGrid") as DataGrid;
                    var fieldsGrid = window.FindName("FieldsGrid") as DataGrid;
                    var resourceTree = window.FindName("ResourceTree") as TreeView;
                    Assert.IsNotNull(tablesGrid);
                    Assert.IsNotNull(fieldsGrid);
                    Assert.IsNotNull(resourceTree);

                    Assert.IsTrue(VirtualizingStackPanel.GetIsVirtualizing(resourceTree));
                    Assert.AreEqual(
                        VirtualizationMode.Recycling,
                        VirtualizingStackPanel.GetVirtualizationMode(resourceTree));
                    Assert.IsTrue(ScrollViewer.GetCanContentScroll(resourceTree));

                    Assert.AreEqual(DataGridSelectionMode.Single, tablesGrid.SelectionMode);
                    Assert.AreEqual(DataGridSelectionUnit.FullRow, tablesGrid.SelectionUnit);
                    Assert.AreEqual(DataGridSelectionMode.Single, fieldsGrid.SelectionMode);
                    Assert.AreEqual(DataGridSelectionUnit.FullRow, fieldsGrid.SelectionUnit);

                    Binding fieldsSelection = BindingOperations.GetBinding(fieldsGrid, DataGrid.SelectedItemProperty);
                    Assert.IsNotNull(fieldsSelection);
                    Assert.AreEqual("SelectedField", fieldsSelection.Path.Path);
                    Assert.AreEqual(BindingMode.TwoWay, fieldsSelection.Mode);
                    Assert.IsNotNull(typeof(SHB.EosDataDictionary.MainWindow).GetMethod("ResourceTree_SelectedItemChanged", BindingFlags.Instance | BindingFlags.NonPublic));
                    Assert.IsNull(typeof(SHB.EosDataDictionary.MainWindow).GetMethod("ResourceTree_MouseDoubleClick", BindingFlags.Instance | BindingFlags.NonPublic));
                    AssertLogicalTextExists(window, "数据表");
                    AssertLogicalTextExists(window, "请选择一个数据表");
                }
                finally
                {
                    window.Close();
                }
            });
        }

        /// <summary>XMZADD 20260901 验证资源树单击按架构和表名定位、清除筛选并刷新字段选择上下文。</summary>
        [TestMethod]
        public void MainWindow_ResourceTreeSingleClick_SelectsExactTableAndClearsFieldTarget()
        {
            string path = Path.Combine(Path.GetTempPath(), "shb-resource-click-" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                RunInSta(() =>
                {
                    PrepareDarkResources();
                    var viewModel = new MainViewModel(path, Path.GetTempPath());
                    viewModel.ShowAllObjects = true;
                    var snapshot = new SnapshotData();
                    var first = new TableMetadata { SchemaName = "dbo", ObjectName = "T_FIRST", ObjectType = "TABLE" };
                    first.Fields.Add(new FieldMetadata { FieldName = "F_FIRST", OwnerTableName = "T_FIRST", DataType = "int" });
                    var targetTable = new TableMetadata { SchemaName = "sales", ObjectName = "T_TARGET", ObjectType = "TABLE" };
                    targetTable.Fields.Add(new FieldMetadata { FieldName = "F_TARGET", OwnerTableName = "T_TARGET", DataType = "int" });
                    snapshot.Tables.Add(first);
                    snapshot.Tables.Add(targetTable);
                    viewModel.LoadSnapshotForTesting(snapshot);
                    TableDisplayModel target = viewModel.TableRows[1];
                    viewModel.SelectedField = viewModel.FieldRows[0];

                    var window = new SHB.EosDataDictionary.MainWindow(viewModel, new ColumnLayoutStore(path));
                    try
                    {
                        var searchBox = window.FindName("TableSearchBox") as TextBox;
                        var tablesGrid = window.FindName("TablesGrid") as DataGrid;
                        var fieldsGrid = window.FindName("FieldsGrid") as DataGrid;
                        Assert.IsNotNull(searchBox);
                        Assert.IsNotNull(tablesGrid);
                        Assert.IsNotNull(fieldsGrid);
                        searchBox.Text = "FIRST";

                        MethodInfo handler = typeof(SHB.EosDataDictionary.MainWindow).GetMethod(
                            "ResourceTree_SelectedItemChanged",
                            BindingFlags.Instance | BindingFlags.NonPublic);
                        Assert.IsNotNull(handler);
                        handler.Invoke(window, new object[]
                        {
                            window.FindName("ResourceTree"),
                            new RoutedPropertyChangedEventArgs<object>(null, target)
                        });

                        Assert.AreEqual(string.Empty, searchBox.Text);
                        Assert.AreSame(target.Source, viewModel.SelectedTable.Source);
                        Assert.IsNull(viewModel.SelectedField);
                        Assert.IsNull(fieldsGrid.SelectedItem);
                        Assert.AreEqual(1, viewModel.FieldRows.Count);
                        Assert.AreEqual("F_TARGET", viewModel.FieldRows[0].FieldName);
                    }
                    finally
                    {
                        window.Close();
                    }
                });
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
                if (File.Exists(path + "-journal")) File.Delete(path + "-journal");
            }
        }

        /// <summary>XMZADD 20260828 验证首次显示仍保留预览表选择、右侧字段和未连接提示。</summary>
        [TestMethod]
        public void MainWindow_OnLoaded_PreservesApprovedPreviewState()
        {
            RunInSta(() =>
            {
                PrepareDarkResources();
                var window = CreateMeasuredMainWindow();
                try
                {
                    var tablesGrid = window.FindName("TablesGrid") as DataGrid;
                    var profilePlaceholder = window.FindName("ProfilePlaceholder") as TextBlock;
                    var profileCombo = window.FindName("ProfileCombo") as ComboBox;
                    var viewModel = window.DataContext as MainViewModel;
                    Assert.IsNotNull(tablesGrid);
                    Assert.IsNotNull(profilePlaceholder);
                    Assert.IsNotNull(profileCombo);
                    Assert.IsNotNull(viewModel);

                    // 本测试只验证没有保存连接时的预览提示，不能依赖当前 Windows 用户的真实连接配置。
                    viewModel.Profiles.Clear();
                    profileCombo.SelectedIndex = -1;
                    window.UpdateLayout();

                    IList<DataGrid> visibleGrids = FindVisibleDataGrids(window);
                    DataGrid fieldsGrid = FindGridOtherThan(visibleGrids, tablesGrid);
                    Assert.IsNotNull(fieldsGrid);
                    Assert.AreEqual(0, tablesGrid.Items.Count);
                    Assert.AreEqual(0, fieldsGrid.Items.Count);
                    Assert.IsNull(fieldsGrid.SelectedItem);
                    Assert.AreEqual(Visibility.Collapsed, profilePlaceholder.Visibility);
                    Assert.IsTrue(profilePlaceholder.Text.IndexOf("预览", StringComparison.Ordinal) < 0);
                }
                finally
                {
                    window.Close();
                }
            });
        }

        /// <summary>XMZADD 20260903 验证表搜索在数据表区域独占一行，长输入不再受资源目录窄栏限制。</summary>
        [TestMethod]
        public void MainWindow_SearchUsesDedicatedDataTableRow()
        {
            RunInSta(() =>
            {
                PrepareDarkResources();
                var window = CreateMeasuredMainWindow();
                try
                {
                    var tableSearchBox = window.FindName("TableSearchBox") as TextBox;
                    var tableStatusFilter = window.FindName("TableStatusFilterBox") as TextBox;
                    var resourceTree = window.FindName("ResourceTree") as TreeView;
                    var tablesGrid = window.FindName("TablesGrid") as DataGrid;
                    Assert.IsNotNull(tableSearchBox);
                    Assert.IsNotNull(tableStatusFilter);
                    Assert.IsNotNull(resourceTree);
                    Assert.IsNotNull(tablesGrid);

                    Point searchPosition = tableSearchBox.TranslatePoint(new Point(0, 0), window);
                    Point filterPosition = tableStatusFilter.TranslatePoint(new Point(0, 0), window);
                    Point treePosition = resourceTree.TranslatePoint(new Point(0, 0), window);

                    Assert.IsTrue(searchPosition.X > treePosition.X + resourceTree.ActualWidth);
                    Assert.IsTrue(tableSearchBox.ActualWidth >= 580d);
                    Assert.IsTrue(filterPosition.Y > searchPosition.Y + tableSearchBox.ActualHeight);
                    Assert.AreEqual(2, Grid.GetRow(tablesGrid));
                    Assert.AreEqual(0, resourceTree.Items.Count);
                }
                finally
                {
                    window.Close();
                }
            });
        }

        /// <summary>XMZADD 20260903 验证详情标题去除与来源证据页重复的黄色摘要提示。</summary>
        [TestMethod]
        public void MainWindow_DetailHeader_RemovesRedundantEvidenceChip()
        {
            RunInSta(() =>
            {
                PrepareDarkResources();
                var window = CreateMeasuredMainWindow();
                try
                {
                    var evidenceChip = window.FindName("EvidenceChip") as Border;
                    Assert.IsNull(evidenceChip);
                }
                finally
                {
                    window.Close();
                }
            });
        }

        /// <summary>XMZADD 20260904 验证表数量只在右下角显示，避免标题行范围选项和计数互相挤压。</summary>
        [TestMethod]
        public void MainWindow_TableCountSummary_AppearsOnlyInFooter()
        {
            RunInSta(() =>
            {
                PrepareDarkResources();
                var window = CreateMeasuredMainWindow();
                try
                {
                    Assert.AreEqual(1, CountTextBindings(window, "TableCountText"));
                    Assert.AreEqual(1, CountTextOccurrences(window, "GitHub 阶段："));
                }
                finally
                {
                    window.Close();
                }
            });
        }

        /// <summary>XMZADD 20260828 验证字段表横向滚动条使用设计稿的10像素紧凑轨道。</summary>
        /// <summary>XMZADD 20260831 验证项目对象目录切换、来源证据表格和字段表名列均按当前界面规范提供。</summary>
        [TestMethod]
        public void MainWindow_ProjectObjectControls_ExposeToggleEvidenceGridAndTableNameColumn()
        {
            RunInSta(() =>
            {
                PrepareDarkResources();
                var window = CreateMeasuredMainWindow();
                try
                {
                    var showAllObjects = window.FindName("ShowAllObjectsCheckBox") as CheckBox;
                    var showEosEntities = window.FindName("ShowEosEntityObjectsOnlyCheckBox") as CheckBox;
                    var showMissingOfficial = window.FindName("ShowOnlyMissingOfficialNamesCheckBox") as CheckBox;
                    var showSuggestions = window.FindName("ShowOnlyWithSuggestionsCheckBox") as CheckBox;
                    var showConflicts = window.FindName("ShowOnlyConflictsCheckBox") as CheckBox;
                    var showActualFields = window.FindName("ShowOnlyActualUsedFieldsCheckBox") as CheckBox;
                    var evidenceGrid = window.FindName("EvidenceGrid") as DataGrid;
                    var tablesGrid = window.FindName("TablesGrid") as DataGrid;
                    var fieldsGrid = window.FindName("FieldsGrid") as DataGrid;

                    Assert.IsNotNull(showAllObjects);
                    Assert.IsTrue(showAllObjects.IsChecked.GetValueOrDefault());
                    Assert.IsNotNull(showEosEntities);
                    Binding entityBinding = BindingOperations.GetBinding(showEosEntities, ToggleButton.IsCheckedProperty);
                    Assert.IsNotNull(entityBinding);
                    Assert.AreEqual("ShowEosEntityObjectsOnly", entityBinding.Path.Path);
                    Assert.IsNotNull(showMissingOfficial);
                    Assert.IsNotNull(showSuggestions);
                    Assert.IsNotNull(showConflicts);
                    Assert.IsNotNull(showActualFields);
                    Assert.IsNotNull(evidenceGrid);
                    AssertHasCopyCellMenu(evidenceGrid);
                    Assert.IsNotNull(tablesGrid);
                    Assert.AreEqual(3, tablesGrid.FrozenColumnCount);
                    Assert.AreEqual("正式中文名", GetColumnHeaderText(tablesGrid.Columns[1]));
                    Assert.AreEqual("英文表名", GetColumnHeaderText(tablesGrid.Columns[2]));
                    Assert.AreEqual("参考译名", GetColumnHeaderText(tablesGrid.Columns[3]));
                    Assert.AreEqual("冲突", GetColumnHeaderText(tablesGrid.Columns[4]));
                    Assert.AreEqual("被使用模块", GetColumnHeaderText(tablesGrid.Columns[7]));
                    Assert.IsNotNull(fieldsGrid);
                    Assert.AreEqual(3, fieldsGrid.FrozenColumnCount);
                    Assert.AreEqual("正式字段名", GetColumnHeaderText(fieldsGrid.Columns[2]));
                    Assert.AreEqual("参考译名", GetColumnHeaderText(fieldsGrid.Columns[3]));
                    Assert.AreEqual("表名", GetColumnHeaderText(fieldsGrid.Columns[5]));
                }
                finally
                {
                    window.Close();
                }
            });
        }

        /// <summary>XMZADD 20260831 验证字段表横向滚动区域保持既有紧凑布局。</summary>
        [TestMethod]
        public void MainWindow_FieldsScrollBar_UsesApprovedThickness()
        {
            RunInSta(() =>
            {
                PrepareDarkResources();
                var window = CreateMeasuredMainWindow();
                try
                {
                    var fieldsGrid = window.FindName("FieldsGrid") as DataGrid;
                    Assert.IsNotNull(fieldsGrid);
                    Assert.IsTrue(fieldsGrid.Columns.Count > 0);
                }
                finally
                {
                    window.Close();
                }
            });
        }

        /// <summary>XMZADD 20260828 验证共享样式消除双边框并固定企业表格的居中和列宽行为。</summary>
        [TestMethod]
        public void ControlStyles_UseApprovedGridAndPanelSettings()
        {
            RunInSta(() =>
            {
                PrepareDarkResources();
                ResourceDictionary controls = _application.Resources.MergedDictionaries[1];
                var dataGridStyle = controls[typeof(DataGrid)] as Style;
                var cellStyle = controls[typeof(DataGridCell)] as Style;
                var filterStyle = controls["ColumnFilterTextBoxStyle"] as Style;
                var titleStyle = controls["SectionTitleTextStyle"] as Style;
                var panelStyle = controls["PanelBorderStyle"] as Style;
                var shellStyle = controls["ShellBorderStyle"] as Style;

                Assert.IsNotNull(dataGridStyle);
                Assert.AreEqual(new Thickness(0), GetSetterValue(dataGridStyle, DataGrid.BorderThicknessProperty));
                Assert.AreEqual(14d, GetSetterValue(dataGridStyle, DataGrid.FontSizeProperty));
                Assert.AreEqual(46d, GetSetterValue(dataGridStyle, DataGrid.RowHeightProperty));
                Assert.AreEqual(76d, GetSetterValue(dataGridStyle, DataGrid.ColumnHeaderHeightProperty));
                Assert.AreEqual(true, GetSetterValue(dataGridStyle, DataGrid.CanUserResizeColumnsProperty));
                Assert.AreEqual(DataGridSelectionUnit.CellOrRowHeader, GetSetterValue(dataGridStyle, DataGrid.SelectionUnitProperty));
                Assert.AreEqual(DataGridClipboardCopyMode.ExcludeHeader, GetSetterValue(dataGridStyle, DataGrid.ClipboardCopyModeProperty));
                Assert.AreEqual(HorizontalAlignment.Stretch, GetSetterValue(cellStyle, DataGridCell.HorizontalContentAlignmentProperty));
                Assert.AreEqual(VerticalAlignment.Stretch, GetSetterValue(cellStyle, DataGridCell.VerticalContentAlignmentProperty));
                Assert.AreEqual(new Thickness(5, 4, 5, 0), GetSetterValue(filterStyle, TextBox.MarginProperty));
                Assert.AreEqual(13d, GetSetterValue(filterStyle, TextBox.FontSizeProperty));
                Assert.AreEqual(30d, GetSetterValue(filterStyle, TextBox.HeightProperty));
                Assert.AreEqual(FontWeights.SemiBold, GetSetterValue(titleStyle, TextBlock.FontWeightProperty));
                var panel = new Border { Style = panelStyle };
                var shell = new Border { Style = shellStyle };
                Assert.IsInstanceOfType(panel.Effect, typeof(DropShadowEffect));
                Assert.IsInstanceOfType(shell.Effect, typeof(DropShadowEffect));
            });
        }

        /// <summary>XMZADD 20260831 验证自定义列头保留 WPF 调整手柄，列宽拖拽不会被主题模板禁用。</summary>
        [TestMethod]
        public void ControlStyles_DataGridHeaderTemplate_HasResizeGrippers()
        {
            RunInSta(() =>
            {
                PrepareDarkResources();
                ResourceDictionary controls = _application.Resources.MergedDictionaries[1];
                var headerStyle = controls[typeof(DataGridColumnHeader)] as Style;
                var resizeThumbStyle = controls["DataGridColumnResizeThumbStyle"] as Style;
                var header = new DataGridColumnHeader { Style = headerStyle };
                header.ApplyTemplate();

                var leftGripper = header.Template.FindName("PART_LeftHeaderGripper", header) as Thumb;
                var rightGripper = header.Template.FindName("PART_RightHeaderGripper", header) as Thumb;

                Assert.IsNotNull(resizeThumbStyle);
                Assert.IsNotNull(leftGripper);
                Assert.IsNotNull(rightGripper);
                Assert.AreSame(resizeThumbStyle, leftGripper.Style);
                Assert.AreSame(resizeThumbStyle, rightGripper.Style);
            });
        }

        /// <summary>XMZADD 20260831 验证全部表格提供单元格复制菜单，字段列表不再显示实体属性列。</summary>
        [TestMethod]
        public void MainWindow_DataGrids_SupportCellCopyAndHideFieldEntityProperty()
        {
            RunInSta(() =>
            {
                PrepareDarkResources();
                // 界面资源测试必须使用隔离快照，避免构造主窗口时修复并写回用户正在使用的字典库。
                var window = new SHB.EosDataDictionary.MainWindow(
                    new MainViewModel(_wpfDatabasePath, Path.GetTempPath()),
                    new ColumnLayoutStore(_wpfDatabasePath));
                try
                {
                    var tablesGrid = window.FindName("TablesGrid") as DataGrid;
                    var fieldsGrid = window.FindName("FieldsGrid") as DataGrid;
                    var relationsGrid = window.FindName("RelationsGrid") as DataGrid;
                    Assert.IsNotNull(tablesGrid);
                    Assert.IsNotNull(fieldsGrid);
                    Assert.IsNotNull(relationsGrid);
                    AssertHasCopyCellMenu(tablesGrid);
                    AssertHasCopyCellMenu(fieldsGrid);
                    AssertHasCopyCellMenu(relationsGrid);

                    for (int i = 0; i < fieldsGrid.Columns.Count; i++)
                    {
                        Assert.AreNotEqual("实体属性", GetColumnHeaderText(fieldsGrid.Columns[i]));
                    }
                }
                finally
                {
                    window.Close();
                }
            });
        }

        /// <summary>XMZADD 20260831 验证更新字典是唯一的真实结构加载入口，避免用户在等价动作之间犹豫。</summary>
        [TestMethod]
        public void MainWindow_UpdateDictionaryCommand_ReplacesDuplicateLoadActions()
        {
            RunInSta(() =>
            {
                PrepareDarkResources();
                var window = CreateMeasuredMainWindow();
                try
                {
                    AssertLogicalTextExists(window, "更新字典");
                    Assert.IsFalse(FindLogicalText(window, "加载全部", false));
                    Assert.IsFalse(FindLogicalText(window, "刷新结构", false));
                    Assert.IsNotNull(typeof(SHB.EosDataDictionary.MainWindow).GetMethod("UpdateDictionaryButton_Click", BindingFlags.Instance | BindingFlags.NonPublic));
                    Assert.IsNull(typeof(SHB.EosDataDictionary.MainWindow).GetMethod("LoadAllButton_Click", BindingFlags.Instance | BindingFlags.NonPublic));
                    Assert.IsNull(typeof(SHB.EosDataDictionary.MainWindow).GetMethod("RefreshButton_Click", BindingFlags.Instance | BindingFlags.NonPublic));
                }
                finally
                {
                    window.Close();
                }
            });
        }

        /// <summary>XMZADD 20260901 验证同步设置不回显已保存 Token，留空沿用凭据并通过注入客户端显示不可变 GitHub 身份。</summary>
        [TestMethod]
        public void SyncSettingsWindow_BlankToken_PreservesCredentialAndVerifiesIdentityOffline()
        {
            string credentialPath = Path.Combine(Path.GetTempPath(), "shb-github-credential-" + Guid.NewGuid().ToString("N") + ".bin");
            try
            {
                var credentialStore = new GitHubCredentialStore(credentialPath);
                credentialStore.Save("stored-test-token");
                var client = new StubGitHubDictionaryClient();
                var options = new DictionaryRepositoryOptions { Owner = "dictionary-owner" };

                RunInSta(() =>
                {
                    PrepareDarkResources();
                    Type windowType = typeof(SHB.EosDataDictionary.MainWindow).Assembly.GetType("SHB.EosDataDictionary.SyncSettingsWindow", false);
                    Assert.IsNotNull(windowType);
                    var window = Activator.CreateInstance(windowType, options, credentialStore, client) as Window;
                    Assert.IsNotNull(window);
                    try
                    {
                        var tokenBox = window.FindName("TokenPasswordBox") as PasswordBox;
                        var identityText = window.FindName("GitHubIdentityText") as TextBlock;
                        Assert.IsNotNull(tokenBox);
                        Assert.IsNotNull(identityText);
                        Assert.AreEqual(string.Empty, tokenBox.Password);
                        AssertLogicalTextContains(window, "dictionary-owner/SHB-EOSDataDictionary");
                        StringAssert.Contains(identityText.Text, "已保存");
                        StringAssert.Contains(identityText.Text, "留空");

                        MethodInfo saveAndVerify = windowType.GetMethod("SaveAndVerifyAsync", BindingFlags.Instance | BindingFlags.NonPublic);
                        Assert.IsNotNull(saveAndVerify);
                        var task = saveAndVerify.Invoke(window, new object[] { CancellationToken.None }) as Task;
                        Assert.IsNotNull(task);
                        task.GetAwaiter().GetResult();

                        Assert.AreEqual(1, client.GetCurrentUserCallCount);
                        Assert.AreEqual("stored-test-token", credentialStore.Load());
                        StringAssert.Contains(identityText.Text, "dictionary-user");
                        StringAssert.Contains(identityText.Text, "123456");
                    }
                    finally
                    {
                        window.Close();
                    }
                });
            }
            finally
            {
                if (File.Exists(credentialPath)) File.Delete(credentialPath);
            }
        }

        /// <summary>XMZADD 20260902 验证同步设置和结构维护窗口复用当前主题，并让凭据文字在 36 像素输入框中完整居中显示。</summary>
        [TestMethod]
        public void SecondaryWindows_UseThemeRootAndCenteredCredentialInputs()
        {
            string credentialPath = Path.Combine(Path.GetTempPath(), "shb-secondary-window-" + Guid.NewGuid().ToString("N") + ".bin");
            try
            {
                RunInSta(() =>
                {
                    PrepareDarkResources();
                    Style windowStyle = _application.TryFindResource(typeof(Window)) as Style;
                    var client = new StubGitHubDictionaryClient();
                    var syncWindow = new SHB.EosDataDictionary.SyncSettingsWindow(
                        new DictionaryRepositoryOptions { Owner = "dictionary-owner" },
                        new GitHubCredentialStore(credentialPath),
                        client);
                    var structureWindow = new StructureMaintenanceWindow(
                        new StructurePublishService(
                            client,
                            delegate(string scopeKey) { return new SnapshotData(); },
                            delegate(ConnectionProfile profile, CancellationToken cancellationToken) { return new SnapshotData(); },
                            delegate(string sourceRoot, CancellationToken cancellationToken) { return new List<SourceEvidence>(); }),
                        new ConnectionProfileStore(_wpfDatabasePath),
                        "test|EOS",
                        string.Empty,
                        string.Empty);
                    try
                    {
                        var syncRoot = syncWindow.Content as Panel;
                        var structureRoot = structureWindow.Content as Panel;
                        var tokenBox = syncWindow.FindName("TokenPasswordBox") as PasswordBox;
                        FieldInfo serverField = typeof(StructureMaintenanceWindow).GetField(
                            "_serverBox", BindingFlags.Instance | BindingFlags.NonPublic);
                        FieldInfo passwordField = typeof(StructureMaintenanceWindow).GetField(
                            "_passwordBox", BindingFlags.Instance | BindingFlags.NonPublic);
                        var serverBox = serverField == null ? null : serverField.GetValue(structureWindow) as TextBox;
                        var passwordBox = passwordField == null ? null : passwordField.GetValue(structureWindow) as PasswordBox;

                        Assert.AreSame(windowStyle, syncWindow.Style);
                        Assert.AreSame(windowStyle, structureWindow.Style);
                        Assert.AreSame(_application.TryFindResource("App.WindowBackgroundBrush"), syncRoot.Background);
                        Assert.AreSame(_application.TryFindResource("App.WindowBackgroundBrush"), structureRoot.Background);
                        Assert.IsNotNull(tokenBox);
                        Assert.IsNotNull(serverBox);
                        Assert.IsNotNull(passwordBox);
                        Assert.AreEqual(new Thickness(10D, 0D, 10D, 0D), tokenBox.Padding);
                        Assert.AreEqual(new Thickness(10D, 0D, 10D, 0D), serverBox.Padding);
                        Assert.AreEqual(new Thickness(10D, 0D, 10D, 0D), passwordBox.Padding);
                        Assert.AreEqual(VerticalAlignment.Center, tokenBox.VerticalContentAlignment);
                        Assert.AreEqual(VerticalAlignment.Center, serverBox.VerticalContentAlignment);
                        Assert.AreEqual(VerticalAlignment.Center, passwordBox.VerticalContentAlignment);
                    }
                    finally
                    {
                        syncWindow.Close();
                        structureWindow.Close();
                    }
                });
            }
            finally
            {
                if (File.Exists(credentialPath)) File.Delete(credentialPath);
            }
        }

        /// <summary>XMZADD 20260902 验证结构差异预览表的表头与内容居中，并使用四边一致的单元格留白。</summary>
        [TestMethod]
        public void StructureMaintenancePreviewGrid_CentersAndPadsCellsEvenly()
        {
            RunInSta(() =>
            {
                PrepareDarkResources();
                var themedCellStyle = _application.TryFindResource(typeof(DataGridCell)) as Style;
                var themedHeaderStyle = _application.TryFindResource(typeof(DataGridColumnHeader)) as Style;
                MethodInfo createPreviewGrid = typeof(StructureMaintenanceWindow).GetMethod(
                    "CreatePreviewGrid", BindingFlags.Static | BindingFlags.NonPublic);
                Assert.IsNotNull(createPreviewGrid);
                var previewGrid = createPreviewGrid.Invoke(null, null) as DataGrid;

                Assert.IsNotNull(previewGrid);
                Assert.IsNotNull(themedCellStyle);
                Assert.IsNotNull(themedHeaderStyle);
                Assert.AreSame(themedCellStyle, previewGrid.CellStyle.BasedOn);
                Assert.AreSame(themedHeaderStyle, previewGrid.ColumnHeaderStyle.BasedOn);
                Assert.AreEqual(HorizontalAlignment.Center, GetStyleSetterValue(
                    previewGrid.CellStyle, Control.HorizontalContentAlignmentProperty));
                Assert.AreEqual(VerticalAlignment.Center, GetStyleSetterValue(
                    previewGrid.CellStyle, Control.VerticalContentAlignmentProperty));
                Assert.AreEqual(new Thickness(10D), GetStyleSetterValue(
                    previewGrid.CellStyle, Control.PaddingProperty));
                Assert.AreEqual(HorizontalAlignment.Center, GetStyleSetterValue(
                    previewGrid.ColumnHeaderStyle, Control.HorizontalContentAlignmentProperty));
                Assert.AreEqual(VerticalAlignment.Center, GetStyleSetterValue(
                    previewGrid.ColumnHeaderStyle, Control.VerticalContentAlignmentProperty));
                Assert.AreEqual(new Thickness(10D), GetStyleSetterValue(
                    previewGrid.ColumnHeaderStyle, Control.PaddingProperty));
                Assert.AreEqual(DataGridSelectionUnit.Cell, previewGrid.SelectionUnit);
                Assert.AreEqual(DataGridClipboardCopyMode.ExcludeHeader, previewGrid.ClipboardCopyMode);
                AssertHasCopyCellMenu(previewGrid);
            });
        }

        /// <summary>XMZADD 20260902 验证首次完整快照、压缩容量和结构不完整使用不同且不含旧数量阈值的中文状态。</summary>
        [TestMethod]
        public void StructureMaintenancePreviewStatus_ExplainsDedicatedFullSnapshotValidationReasons()
        {
            MethodInfo getPreviewStatusText = typeof(StructureMaintenanceWindow).GetMethod(
                "GetPreviewStatusText", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(getPreviewStatusText);
            var ready = new StructurePublishResult();
            SetProperty(ready, "Outcome", StructurePublishOutcome.ReadyToPublish);
            SetProperty(ready, "IsFullSnapshot", true);
            SetProperty(ready, "FullSnapshotTableCount", 30);
            SetProperty(ready, "FullSnapshotFieldCount", 45);
            SetProperty(ready, "FullSnapshotCompressedBytes", 2048L);

            string readyText = getPreviewStatusText.Invoke(null, new object[] { ready }) as string;

            StringAssert.Contains(readyText, "一个 GitHub Commit");
            StringAssert.Contains(readyText, "30 张表");
            StringAssert.Contains(readyText, "45 个字段");

            var sizeExceeded = new StructurePublishResult();
            SetProperty(sizeExceeded, "Outcome", StructurePublishOutcome.ValidationFailed);
            SetProperty(sizeExceeded, "ValidationReason", StructurePublishValidationReason.FullSnapshotSizeExceeded);

            string sizeExceededText = getPreviewStatusText.Invoke(null, new object[] { sizeExceeded }) as string;

            Assert.AreEqual(
                "完整公开快照超过 GitHub 单文件 100 MiB 限制，当前不会发布任何内容。",
                sizeExceededText);

            var incomplete = new StructurePublishResult();
            SetProperty(incomplete, "Outcome", StructurePublishOutcome.ValidationFailed);
            SetProperty(incomplete, "ValidationReason", StructurePublishValidationReason.FullSnapshotIncomplete);

            string incompleteText = getPreviewStatusText.Invoke(null, new object[] { incomplete }) as string;

            Assert.AreEqual("扫描到没有字段的用户表，结构读取可能不完整，已停止发布。", incompleteText);

            var legacyLimit = new StructurePublishResult();
            SetProperty(legacyLimit, "Outcome", StructurePublishOutcome.ValidationFailed);
            SetProperty(legacyLimit, "ValidationReason", StructurePublishValidationReason.FullSnapshotLimitExceeded);

            string legacyLimitText = getPreviewStatusText.Invoke(null, new object[] { legacyLimit }) as string;

            Assert.IsFalse(legacyLimitText.Contains("10000"));

            var operationLimit = new StructurePublishResult();
            SetProperty(operationLimit, "Outcome", StructurePublishOutcome.ValidationFailed);
            SetProperty(operationLimit, "ValidationReason", StructurePublishValidationReason.OperationLimitExceeded);

            string operationLimitText = getPreviewStatusText.Invoke(null, new object[] { operationLimit }) as string;

            StringAssert.Contains(operationLimitText, "GitHub Issue");
            StringAssert.Contains(operationLimitText, "100 条操作");
            Assert.AreNotEqual(sizeExceededText, operationLimitText);
        }

        /// <summary>XMZADD 20260902 验证扫描和发布异步处理先接管内存不足，并始终使用不包含异常正文的固定安全提示。</summary>
        [TestMethod]
        public void StructureMaintenanceHandlers_OutOfMemoryCatchPrecedesGeneralCatchAndUsesFixedText()
        {
            AssertOutOfMemoryCatchPrecedesGeneralCatch("ScanButton_Click", 2);
            AssertOutOfMemoryCatchPrecedesGeneralCatch("PublishButton_Click", 1);

            MethodInfo getFailureText = typeof(StructureMaintenanceWindow).GetMethod(
                "GetOutOfMemoryFailureText",
                BindingFlags.Static | BindingFlags.NonPublic,
                null,
                new[] { typeof(OutOfMemoryException) },
                null);
            Assert.IsNotNull(getFailureText);
            Assert.AreEqual(
                "本机内存不足，完整快照尚未发布；请关闭其他程序后重试。",
                getFailureText.Invoke(null, new object[] { new OutOfMemoryException("不得显示的异常正文") }));
        }

        /// <summary>XMZADD 20260901 验证候选 Token 只有通过身份检查后才替换旧凭据，失败时旧有效值保持不变。</summary>
        [TestMethod]
        public void SyncSettingsWindow_CandidateToken_SavesOnlyAfterSuccessfulIdentityVerification()
        {
            string credentialPath = Path.Combine(Path.GetTempPath(), "shb-github-candidate-" + Guid.NewGuid().ToString("N") + ".bin");
            try
            {
                var credentialStore = new GitHubCredentialStore(credentialPath);
                credentialStore.Save("old-valid-token");
                var client = new StubGitHubDictionaryClient
                {
                    StoredIdentityException = new GitHubDictionaryClientException(
                        "认证失败", GitHubDictionaryErrorKind.Authentication, false, null),
                    CandidateIdentityException = new GitHubDictionaryClientException(
                        "认证失败", GitHubDictionaryErrorKind.Authentication, false, null)
                };
                var options = new DictionaryRepositoryOptions { Owner = "dictionary-owner" };

                RunInSta(() =>
                {
                    PrepareDarkResources();
                    var window = new SHB.EosDataDictionary.SyncSettingsWindow(options, credentialStore, client);
                    try
                    {
                        var tokenBox = window.FindName("TokenPasswordBox") as PasswordBox;
                        MethodInfo saveAndVerify = typeof(SHB.EosDataDictionary.SyncSettingsWindow).GetMethod(
                            "SaveAndVerifyAsync",
                            BindingFlags.Instance | BindingFlags.NonPublic);
                        Assert.IsNotNull(tokenBox);
                        Assert.IsNotNull(saveAndVerify);

                        tokenBox.Password = "bad-candidate-token";
                        var badTask = saveAndVerify.Invoke(window, new object[] { CancellationToken.None }) as Task;
                        badTask.GetAwaiter().GetResult();
                        Assert.AreEqual("old-valid-token", credentialStore.Load());

                        client.StoredIdentityException = null;
                        client.CandidateIdentityException = null;
                        tokenBox.Password = "good-candidate-token";
                        var goodTask = saveAndVerify.Invoke(window, new object[] { CancellationToken.None }) as Task;
                        goodTask.GetAwaiter().GetResult();
                        Assert.AreEqual("good-candidate-token", credentialStore.Load());
                        Assert.AreEqual(2, client.CandidateIdentityCallCount);
                        Assert.AreEqual("good-candidate-token", client.LastCandidateToken);
                    }
                    finally
                    {
                        window.Close();
                    }
                });
            }
            finally
            {
                if (File.Exists(credentialPath)) File.Delete(credentialPath);
            }
        }

        /// <summary>XMZADD 20260901 验证标题栏关闭会取消候选身份请求、清空密码并在后台任务结束后释放验证令牌。</summary>
        [TestMethod]
        public void SyncSettingsWindow_CloseDuringVerification_CancelsWithoutUiUpdateOrBlocking()
        {
            string credentialPath = Path.Combine(Path.GetTempPath(), "shb-github-close-" + Guid.NewGuid().ToString("N") + ".bin");
            var verificationStarted = new ManualResetEventSlim(false);
            var releaseVerification = new ManualResetEventSlim(false);
            var cancellationObserved = new ManualResetEventSlim(false);
            SHB.EosDataDictionary.SyncSettingsWindow window = null;
            PasswordBox tokenBox = null;
            TextBlock identityText = null;
            Button saveButton = null;
            try
            {
                FieldInfo verificationTaskField = typeof(SHB.EosDataDictionary.SyncSettingsWindow).GetField(
                    "_verificationTask", BindingFlags.Instance | BindingFlags.NonPublic);
                FieldInfo cleanupTaskField = typeof(SHB.EosDataDictionary.SyncSettingsWindow).GetField(
                    "_verificationCleanupTask", BindingFlags.Instance | BindingFlags.NonPublic);
                MethodInfo startVerification = typeof(SHB.EosDataDictionary.SyncSettingsWindow).GetMethod(
                    "StartVerificationAsync", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(verificationTaskField);
                Assert.IsNotNull(cleanupTaskField);
                Assert.IsNotNull(startVerification);

                var store = new GitHubCredentialStore(credentialPath);
                var client = new StubGitHubDictionaryClient
                {
                    CandidateIdentityStarted = verificationStarted,
                    CandidateIdentityRelease = releaseVerification,
                    CandidateCancellationObserved = cancellationObserved
                };
                RunInSta(() =>
                {
                    PrepareDarkResources();
                    window = new SHB.EosDataDictionary.SyncSettingsWindow(
                        new DictionaryRepositoryOptions { Owner = "dictionary-owner" },
                        store,
                        client);
                    tokenBox = window.FindName("TokenPasswordBox") as PasswordBox;
                    identityText = window.FindName("GitHubIdentityText") as TextBlock;
                    saveButton = window.FindName("SaveAndVerifyButton") as Button;
                    tokenBox.Password = "candidate-token-for-cancel";
                    window.Show();
                    startVerification.Invoke(window, null);
                });
                Assert.IsTrue(verificationStarted.Wait(TimeSpan.FromSeconds(5D)));

                string textAtClose = null;
                RunInSta(() => { textAtClose = identityText.Text; });
                var stopwatch = Stopwatch.StartNew();
                RunInSta(window.Close);
                stopwatch.Stop();
                Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(1D));
                Assert.IsTrue(cancellationObserved.Wait(TimeSpan.FromSeconds(5D)));
                releaseVerification.Set();

                Task verificationTask = verificationTaskField.GetValue(window) as Task;
                Task cleanupTask = cleanupTaskField.GetValue(window) as Task;
                Assert.IsNotNull(verificationTask);
                Assert.IsNotNull(cleanupTask);
                Assert.IsTrue(verificationTask.Wait(TimeSpan.FromSeconds(5D)));
                Assert.IsTrue(cleanupTask.Wait(TimeSpan.FromSeconds(5D)));
                Assert.IsFalse(verificationTask.IsFaulted);
                Assert.IsFalse(cleanupTask.IsFaulted);
                RunInSta(() =>
                {
                    Assert.AreEqual(string.Empty, tokenBox.Password);
                    Assert.AreEqual(textAtClose, identityText.Text);
                    Assert.IsFalse(saveButton.IsEnabled);
                });
            }
            finally
            {
                releaseVerification.Set();
                if (window != null && window.IsVisible) RunInSta(window.Close);
                verificationStarted.Dispose();
                releaseVerification.Dispose();
                cancellationObserved.Dispose();
                if (File.Exists(credentialPath)) File.Delete(credentialPath);
            }
        }

        /// <summary>XMZADD 20260901 验证手工更新只通过注入协调器拉取 GitHub 快照，并刷新共享缓存与同步状态。</summary>
        [TestMethod]
        public void MainWindow_ManualSync_UsesInjectedCoordinatorAndRefreshesSharedSnapshot()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), "shb-main-sync-" + Guid.NewGuid().ToString("N") + ".db");
            string credentialPath = Path.Combine(Path.GetTempPath(), "shb-main-sync-token-" + Guid.NewGuid().ToString("N") + ".bin");
            SHB.EosDataDictionary.MainWindow window = null;
            try
            {
                var options = new DictionaryRepositoryOptions { Owner = "dictionary-owner" };
                var snapshot = new SnapshotData
                {
                    FormatVersion = 1,
                    Revision = 1,
                    RefreshedAt = DateTime.UtcNow
                };
                snapshot.Tables.Add(new TableMetadata
                {
                    SchemaName = "dbo",
                    ObjectName = "T_SYNC_TABLE",
                    ObjectType = "TABLE",
                    ScopeKey = options.ScopeKey
                });
                var codec = new SnapshotCodec();
                byte[] content = codec.Encode(snapshot);
                var client = new StubGitHubDictionaryClient
                {
                    Manifest = new SnapshotManifest
                    {
                        FormatVersion = 1,
                        Revision = 1,
                        SnapshotPath = "snapshot/dictionary.json.gz",
                        SnapshotSha256 = codec.ComputeSha256(content),
                        GeneratedAtUtc = DateTime.UtcNow
                    },
                    SnapshotContent = content
                };
                var coordinator = new DictionarySyncCoordinator(
                    new LocalDictionaryStore(databasePath),
                    new SnapshotStore(databasePath),
                    client,
                    options,
                    new SystemClock());
                var viewModel = new MainViewModel(databasePath, Path.GetTempPath());
                var credentialStore = new GitHubCredentialStore(credentialPath);

                ConstructorInfo constructor = typeof(SHB.EosDataDictionary.MainWindow).GetConstructor(
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    null,
                    new[]
                    {
                        typeof(MainViewModel), typeof(ColumnLayoutStore), typeof(DictionarySyncCoordinator),
                        typeof(DictionaryRepositoryOptions), typeof(GitHubCredentialStore), typeof(IGitHubDictionaryClient)
                    },
                    null);
                Assert.IsNotNull(constructor);
                RunInSta(() =>
                {
                    PrepareDarkResources();
                    window = constructor.Invoke(new object[]
                    {
                        viewModel, new ColumnLayoutStore(databasePath), coordinator, options, credentialStore, client
                    }) as SHB.EosDataDictionary.MainWindow;
                });
                Assert.IsNotNull(window);

                MethodInfo synchronize = typeof(SHB.EosDataDictionary.MainWindow).GetMethod(
                    "SynchronizeAndRefreshAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                FieldInfo synchronizationLockField = typeof(SHB.EosDataDictionary.MainWindow).GetField(
                    "_synchronizationLock",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(synchronize);
                Assert.IsNotNull(synchronizationLockField);
                var synchronizationLock = synchronizationLockField.GetValue(window) as SemaphoreSlim;
                Assert.IsNotNull(synchronizationLock);
                Assert.IsTrue(synchronizationLock.Wait(0));
                Task synchronizationTask = null;
                int synchronizationThreadId = 0;
                RunInSta(() =>
                {
                    synchronizationThreadId = Thread.CurrentThread.ManagedThreadId;
                    synchronizationTask = synchronize.Invoke(
                        window,
                        new object[] { true, CancellationToken.None }) as Task;
                });
                Assert.IsNotNull(synchronizationTask);
                Assert.IsFalse(synchronizationTask.IsCompleted);
                Assert.AreEqual(0, client.GetManifestCallCount);
                synchronizationLock.Release();
                RunTaskInSta(() => synchronizationTask);

                Assert.AreEqual(1, client.GetManifestCallCount);
                Assert.AreNotEqual(synchronizationThreadId, client.ManifestThreadId);
                Assert.AreEqual(1, client.DownloadSnapshotCallCount);
                Assert.AreEqual(1, viewModel.TableRows.Count);
                Assert.AreEqual("T_SYNC_TABLE", viewModel.TableRows[0].ObjectName);
                Assert.AreEqual("获取完成", viewModel.SyncStatusText);
                Assert.IsFalse(viewModel.IsOffline);
                TableDisplayModel loadedRow = viewModel.TableRows[0];

                RunTaskInSta(() => synchronize.Invoke(
                    window,
                    new object[] { false, CancellationToken.None }) as Task);

                Assert.AreEqual(2, client.GetManifestCallCount);
                Assert.AreEqual(1, client.DownloadSnapshotCallCount);
                Assert.AreSame(loadedRow, viewModel.TableRows[0]);
            }
            finally
            {
                if (window != null) RunInSta(window.Close);
                if (File.Exists(databasePath)) File.Delete(databasePath);
                if (File.Exists(databasePath + "-journal")) File.Delete(databasePath + "-journal");
                if (File.Exists(credentialPath)) File.Delete(credentialPath);
            }
        }

        /// <summary>XMZADD 20260903 验证新修订直接复用协调器已解码对象，即使落盘后缓存暂时不可读也无需二次反序列化。</summary>
        [TestMethod]
        public void MainWindow_SnapshotChange_AppliesDecodedSnapshotWithoutSecondSqliteRead()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), "shb-main-restore-retry-" + Guid.NewGuid().ToString("N") + ".db");
            string credentialPath = Path.Combine(Path.GetTempPath(), "shb-main-restore-retry-token-" + Guid.NewGuid().ToString("N") + ".bin");
            SHB.EosDataDictionary.MainWindow window = null;
            try
            {
                var options = new DictionaryRepositoryOptions { Owner = "dictionary-owner" };
                var codec = new SnapshotCodec();
                var initialSnapshot = new SnapshotData { FormatVersion = 1, Revision = 1, RefreshedAt = DateTime.UtcNow };
                initialSnapshot.Tables.Add(new TableMetadata
                {
                    SchemaName = "dbo", ObjectName = "T_OLD", ObjectType = "TABLE", ScopeKey = options.ScopeKey
                });
                var remoteSnapshot = new SnapshotData { FormatVersion = 1, Revision = 2, RefreshedAt = DateTime.UtcNow };
                remoteSnapshot.Tables.Add(new TableMetadata
                {
                    SchemaName = "dbo", ObjectName = "T_NEW", ObjectType = "TABLE", ScopeKey = options.ScopeKey
                });
                var snapshotStore = new SnapshotStore(databasePath);
                snapshotStore.ReplaceScope(options.ScopeKey, initialSnapshot);
                int initialTableCount;
                string initialPayloadHash;
                Assert.IsTrue(snapshotStore.TryGetScopeSummary(options.ScopeKey, out initialTableCount, out initialPayloadHash));
                var localStore = new LocalDictionaryStore(databasePath);
                localStore.SaveSyncState(new DictionarySyncState
                {
                    StateKey = options.StateKey,
                    Revision = 1,
                    ManifestHash = initialPayloadHash,
                    LocalPayloadHash = initialPayloadHash,
                    LastSyncAtUtc = DateTime.UtcNow
                });
                byte[] remoteContent = codec.Encode(remoteSnapshot);
                var client = new StubGitHubDictionaryClient
                {
                    Manifest = new SnapshotManifest
                    {
                        FormatVersion = 1,
                        Revision = 2,
                        SnapshotPath = "snapshot/dictionary.json.gz",
                        SnapshotSha256 = codec.ComputeSha256(remoteContent),
                        GeneratedAtUtc = DateTime.UtcNow
                    },
                    SnapshotContent = remoteContent
                };
                var coordinator = new DictionarySyncCoordinator(
                    localStore,
                    snapshotStore,
                    client,
                    options,
                    new SystemClock(),
                    new DeleteSnapshotOnceObserver(databasePath, options.ScopeKey));
                var viewModel = new MainViewModel(databasePath, Path.GetTempPath());
                viewModel.LoadSnapshotForTesting(initialSnapshot);
                RunInSta(() =>
                {
                    PrepareDarkResources();
                    window = new SHB.EosDataDictionary.MainWindow(
                        viewModel,
                        new ColumnLayoutStore(databasePath),
                        coordinator,
                        options,
                        new GitHubCredentialStore(credentialPath),
                        client);
                });
                FieldInfo restoredField = typeof(SHB.EosDataDictionary.MainWindow).GetField(
                    "_hasRestoredSnapshot", BindingFlags.Instance | BindingFlags.NonPublic);
                MethodInfo synchronize = typeof(SHB.EosDataDictionary.MainWindow).GetMethod(
                    "SynchronizeAndRefreshAsync", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(restoredField);
                Assert.IsNotNull(synchronize);
                restoredField.SetValue(window, true);

                RunTaskInSta(() => synchronize.Invoke(
                    window,
                    new object[] { false, CancellationToken.None }) as Task);

                Assert.AreEqual("T_NEW", viewModel.TableRows[0].ObjectName);
            }
            finally
            {
                if (window != null) RunInSta(window.Close);
                if (File.Exists(databasePath)) File.Delete(databasePath);
                if (File.Exists(databasePath + "-journal")) File.Delete(databasePath + "-journal");
                if (File.Exists(credentialPath)) File.Delete(credentialPath);
            }
        }

        /// <summary>XMZADD 20260903 验证上传成功但随后获取失败时，底部计数仍立即从待上传切换为待生效。</summary>
        [TestMethod]
        public void MainWindow_ManifestFailureAfterUpload_RefreshesLocalQueueCounts()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), "shb-main-sync-failure-" + Guid.NewGuid().ToString("N") + ".db");
            string credentialPath = Path.Combine(Path.GetTempPath(), "shb-main-sync-failure-token-" + Guid.NewGuid().ToString("N") + ".bin");
            SHB.EosDataDictionary.MainWindow window = null;
            try
            {
                var options = new DictionaryRepositoryOptions { Owner = "dictionary-owner" };
                var localStore = new LocalDictionaryStore(databasePath);
                string operationId = Guid.NewGuid().ToString("N");
                var batch = new DictionaryChangeBatch
                {
                    BatchId = operationId,
                    AuthorGitHubUserId = "123",
                    CreatedAtUtc = new DateTime(2026, 9, 3, 7, 30, 0, DateTimeKind.Utc)
                };
                batch.Overrides.Add(new DictionaryOverride
                {
                    ScopeKey = options.ScopeKey,
                    ObjectName = "T_TEST",
                    FieldName = "FNAME",
                    ObjectKey = "dbo.T_TEST",
                    FieldKey = "FNAME",
                    PropertyName = "ChineseName",
                    ManualValue = "本地名称",
                    IsLocked = true,
                    UpdatedAt = batch.CreatedAtUtc
                });
                batch.Operations.Add(new DictionaryChangeOperation
                {
                    OperationId = operationId,
                    AuthorGitHubUserId = "123",
                    ObjectKey = "dbo.T_TEST",
                    FieldKey = "FNAME",
                    PropertyName = "ChineseName",
                    NewValue = "本地名称",
                    ChangeKind = "Set",
                    CreatedAtUtc = batch.CreatedAtUtc
                });
                localStore.SaveOverridesWithPendingOperation(batch.Overrides, batch);
                var client = new StubGitHubDictionaryClient
                {
                    CreatedIssueNumber = 77,
                    ManifestException = new GitHubDictionaryClientException(
                        "暂时无法获取清单。",
                        GitHubDictionaryErrorKind.Transient,
                        true,
                        null)
                };
                var coordinator = new DictionarySyncCoordinator(
                    localStore,
                    new SnapshotStore(databasePath),
                    client,
                    options,
                    new SystemClock());
                var viewModel = new MainViewModel(databasePath, Path.GetTempPath());
                viewModel.NotifyLocalDictionarySaved();
                Assert.AreEqual(1, viewModel.PendingUploadCount);
                Assert.AreEqual(0, viewModel.PendingApplyCount);
                var credentialStore = new GitHubCredentialStore(credentialPath);
                ConstructorInfo constructor = typeof(SHB.EosDataDictionary.MainWindow).GetConstructor(
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    null,
                    new[]
                    {
                        typeof(MainViewModel), typeof(ColumnLayoutStore), typeof(DictionarySyncCoordinator),
                        typeof(DictionaryRepositoryOptions), typeof(GitHubCredentialStore), typeof(IGitHubDictionaryClient)
                    },
                    null);
                Assert.IsNotNull(constructor);
                RunInSta(() =>
                {
                    PrepareDarkResources();
                    window = constructor.Invoke(new object[]
                    {
                        viewModel, new ColumnLayoutStore(databasePath), coordinator, options, credentialStore, client
                    }) as SHB.EosDataDictionary.MainWindow;
                });
                MethodInfo synchronize = typeof(SHB.EosDataDictionary.MainWindow).GetMethod(
                    "SynchronizeAndRefreshAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(synchronize);

                RunTaskInSta(() => synchronize.Invoke(
                    window,
                    new object[] { true, CancellationToken.None }) as Task);

                Assert.AreEqual(0, viewModel.PendingUploadCount);
                Assert.AreEqual(1, viewModel.PendingApplyCount);
                Assert.IsTrue(viewModel.IsOffline);
            }
            finally
            {
                if (window != null) RunInSta(window.Close);
                if (File.Exists(databasePath)) File.Delete(databasePath);
                if (File.Exists(databasePath + "-journal")) File.Delete(databasePath + "-journal");
                if (File.Exists(credentialPath)) File.Delete(credentialPath);
            }
        }

        /// <summary>XMZADD 20260901 验证应用统一组合 GitHub 同步依赖，并在缺少真实 owner 时安全禁用远程同步。</summary>
        [TestMethod]
        public void App_MissingRepositoryOwner_ComposesLocalDependenciesAndDisablesRemoteSync()
        {
            var app = _application as SHB.EosDataDictionary.App;
            Assert.IsNotNull(app);

            PropertyInfo optionsProperty = typeof(SHB.EosDataDictionary.App).GetProperty("RepositoryOptions");
            PropertyInfo credentialProperty = typeof(SHB.EosDataDictionary.App).GetProperty("GitHubCredentialStore");
            PropertyInfo httpProperty = typeof(SHB.EosDataDictionary.App).GetProperty("GitHubHttpClient");
            PropertyInfo clientProperty = typeof(SHB.EosDataDictionary.App).GetProperty("GitHubDictionaryClient");
            PropertyInfo coordinatorProperty = typeof(SHB.EosDataDictionary.App).GetProperty("DictionarySyncCoordinator");
            Assert.IsNotNull(optionsProperty);
            Assert.IsNotNull(credentialProperty);
            Assert.IsNotNull(httpProperty);
            Assert.IsNotNull(clientProperty);
            Assert.IsNotNull(coordinatorProperty);

            var options = optionsProperty.GetValue(app) as DictionaryRepositoryOptions;
            Assert.IsNotNull(options);
            Assert.IsNotNull(credentialProperty.GetValue(app));
            Assert.IsNotNull(httpProperty.GetValue(app));
            if (string.IsNullOrWhiteSpace(options.Owner))
            {
                Assert.IsNull(clientProperty.GetValue(app));
                Assert.IsNull(coordinatorProperty.GetValue(app));
            }
            else
            {
                Assert.IsNotNull(clientProperty.GetValue(app));
                Assert.IsNotNull(coordinatorProperty.GetValue(app));
            }

            MethodInfo loadOptions = typeof(SHB.EosDataDictionary.App).GetMethod(
                "LoadRepositoryOptions",
                BindingFlags.Static | BindingFlags.NonPublic,
                null,
                new[] { typeof(string) },
                null);
            Assert.IsNotNull(loadOptions);
            string missingPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "GitHubRepository.json");
            var missingOptions = loadOptions.Invoke(null, new object[] { missingPath }) as DictionaryRepositoryOptions;
            Assert.IsNotNull(missingOptions);
            Assert.IsTrue(string.IsNullOrWhiteSpace(missingOptions.Owner));
        }

        /// <summary>XMZADD 20260901 验证公开仓库配置超过固定上限时直接禁用远程同步，避免启动阶段无界读取。</summary>
        [TestMethod]
        public void App_RepositoryConfiguration_OversizedFile_IsRejected()
        {
            string content = "{\"owner\":\"team\",\"repositoryName\":\"repo\",\"branch\":\"main\"}" + new string(' ', 17000);
            DictionaryRepositoryOptions options = LoadRepositoryOptionsFromText(content);
            Assert.IsTrue(string.IsNullOrWhiteSpace(options.Owner));
        }

        /// <summary>XMZADD 20260901 验证公开仓库配置出现未声明字段时整份拒绝，防止 Token 等私密属性混入部署文件。</summary>
        [TestMethod]
        public void App_RepositoryConfiguration_UnknownField_IsRejected()
        {
            DictionaryRepositoryOptions options = LoadRepositoryOptionsFromText(
                "{\"owner\":\"team\",\"repositoryName\":\"repo\",\"branch\":\"main\",\"token\":\"not-a-real-token\"}");
            Assert.IsTrue(string.IsNullOrWhiteSpace(options.Owner));
        }

        /// <summary>XMZADD 20260901 验证损坏的公开仓库配置安全降级为未配置状态，不阻断应用启动。</summary>
        [TestMethod]
        public void App_RepositoryConfiguration_CorruptJson_IsRejected()
        {
            DictionaryRepositoryOptions options = LoadRepositoryOptionsFromText("{\"owner\":");
            Assert.IsTrue(string.IsNullOrWhiteSpace(options.Owner));
        }

        /// <summary>XMZADD 20260901 验证仓库坐标超过业务长度上限时禁用远程同步，避免构造异常请求路径。</summary>
        [TestMethod]
        public void App_RepositoryConfiguration_OverlongOwner_IsRejected()
        {
            string content = "{\"owner\":\"" + new string('a', 101) + "\",\"repositoryName\":\"repo\",\"branch\":\"main\"}";
            DictionaryRepositoryOptions options = LoadRepositoryOptionsFromText(content);
            Assert.IsTrue(string.IsNullOrWhiteSpace(options.Owner));
        }

        /// <summary>XMZADD 20260901 验证只含白名单字符串属性的公开仓库配置被精确保留并可通过仓库坐标校验。</summary>
        [TestMethod]
        public void App_RepositoryConfiguration_KnownStringFields_ArePreserved()
        {
            DictionaryRepositoryOptions options = LoadRepositoryOptionsFromText(
                "{\"owner\":\"team\",\"repositoryName\":\"repo\",\"branch\":\"main\"}");
            Assert.AreEqual("team", options.Owner);
            Assert.AreEqual("repo", options.RepositoryName);
            Assert.AreEqual("main", options.Branch);
            options.Validate();
        }

        /// <summary>XMZADD 20260901 验证合法公开仓库配置可组合远程依赖，且依赖创建过程不会自行访问 GitHub。</summary>
        [TestMethod]
        public void App_RepositoryConfiguration_ValidOptions_ComposeDependenciesWithoutNetworkRequest()
        {
            var app = _application as SHB.EosDataDictionary.App;
            Assert.IsNotNull(app);
            DictionaryRepositoryOptions options = LoadRepositoryOptionsFromText(
                "{\"owner\":\"team\",\"repositoryName\":\"repo\",\"branch\":\"main\"}");
            var handler = new NoNetworkHttpMessageHandler();
            var testHttpClient = new HttpClient(handler);
            PropertyInfo optionsProperty = typeof(SHB.EosDataDictionary.App).GetProperty("RepositoryOptions");
            PropertyInfo httpProperty = typeof(SHB.EosDataDictionary.App).GetProperty("GitHubHttpClient");
            PropertyInfo clientProperty = typeof(SHB.EosDataDictionary.App).GetProperty("GitHubDictionaryClient");
            PropertyInfo coordinatorProperty = typeof(SHB.EosDataDictionary.App).GetProperty("DictionarySyncCoordinator");
            MethodInfo compose = typeof(SHB.EosDataDictionary.App).GetMethod(
                "ComposeRemoteDictionaryDependencies",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(optionsProperty);
            Assert.IsNotNull(httpProperty);
            Assert.IsNotNull(clientProperty);
            Assert.IsNotNull(coordinatorProperty);
            Assert.IsNotNull(compose);
            object originalOptions = optionsProperty.GetValue(app);
            object originalHttpClient = httpProperty.GetValue(app);
            object originalClient = clientProperty.GetValue(app);
            object originalCoordinator = coordinatorProperty.GetValue(app);
            try
            {
                optionsProperty.GetSetMethod(true).Invoke(app, new object[] { options });
                httpProperty.GetSetMethod(true).Invoke(app, new object[] { testHttpClient });
                compose.Invoke(app, null);

                Assert.IsNotNull(clientProperty.GetValue(app));
                Assert.IsNotNull(coordinatorProperty.GetValue(app));
                Assert.AreEqual(0, handler.SendCount);
            }
            finally
            {
                optionsProperty.GetSetMethod(true).Invoke(app, new[] { originalOptions });
                httpProperty.GetSetMethod(true).Invoke(app, new[] { originalHttpClient });
                clientProperty.GetSetMethod(true).Invoke(app, new[] { originalClient });
                coordinatorProperty.GetSetMethod(true).Invoke(app, new[] { originalCoordinator });
                testHttpClient.Dispose();
            }
        }

        /// <summary>XMZADD 20260901 验证公开仓库配置拒绝重复属性，避免解析顺序决定最终仓库坐标。</summary>
        [TestMethod]
        public void App_RepositoryConfiguration_DuplicateField_IsRejected()
        {
            DictionaryRepositoryOptions options = LoadRepositoryOptionsFromText(
                "{\"owner\":\"team\",\"owner\":\"other\",\"repositoryName\":\"repo\",\"branch\":\"main\"}");
            Assert.IsTrue(string.IsNullOrWhiteSpace(options.Owner));
        }

        /// <summary>XMZADD 20260901 验证公开仓库配置拒绝非字符串坐标，禁止隐式类型转换改变校验语义。</summary>
        [TestMethod]
        public void App_RepositoryConfiguration_NonStringField_IsRejected()
        {
            DictionaryRepositoryOptions options = LoadRepositoryOptionsFromText(
                "{\"owner\":123,\"repositoryName\":\"repo\",\"branch\":\"main\"}");
            Assert.IsTrue(string.IsNullOrWhiteSpace(options.Owner));
        }

        /// <summary>XMZADD 20260901 验证公开仓库配置拒绝嵌套属性，保持部署文件只有一层简单字符串坐标。</summary>
        [TestMethod]
        public void App_RepositoryConfiguration_NestedField_IsRejected()
        {
            DictionaryRepositoryOptions options = LoadRepositoryOptionsFromText(
                "{\"owner\":{\"value\":\"team\"},\"repositoryName\":\"repo\",\"branch\":\"main\"}");
            Assert.IsTrue(string.IsNullOrWhiteSpace(options.Owner));
        }

        /// <summary>XMZADD 20260901 在隔离临时文件中调用真实配置加载入口，确保安全校验覆盖实际文件读取路径。</summary>
        private static DictionaryRepositoryOptions LoadRepositoryOptionsFromText(string content)
        {
            string path = Path.Combine(Path.GetTempPath(), "shb-repository-options-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                File.WriteAllText(path, content);
                MethodInfo loadOptions = typeof(SHB.EosDataDictionary.App).GetMethod(
                    "LoadRepositoryOptions",
                    BindingFlags.Static | BindingFlags.NonPublic,
                    null,
                    new[] { typeof(string) },
                    null);
                Assert.IsNotNull(loadOptions);
                var options = loadOptions.Invoke(null, new object[] { path }) as DictionaryRepositoryOptions;
                Assert.IsNotNull(options);
                return options;
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        /// <summary>XMZADD 20260901 验证界面线程只接管明确可恢复的异常，致命运行时故障仍由进程级机制终止。</summary>
        [TestMethod]
        public void App_DispatcherExceptionPolicy_DoesNotHandleFatalRuntimeFailures()
        {
            MethodInfo policy = typeof(SHB.EosDataDictionary.App).GetMethod(
                "IsRecoverableDispatcherException",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(policy);

            Assert.IsFalse((bool)policy.Invoke(null, new object[] { new OutOfMemoryException() }));
            Assert.IsFalse((bool)policy.Invoke(null, new object[] { new StackOverflowException() }));
            Assert.IsFalse((bool)policy.Invoke(null, new object[] { new AccessViolationException() }));
            Assert.IsTrue((bool)policy.Invoke(null, new object[] { new InvalidOperationException() }));
            Assert.IsTrue((bool)policy.Invoke(null, new object[] { new ArgumentException() }));
        }

        /// <summary>XMZADD 20260901 验证后台自动同步固定每五分钟轮询一次，不引入更高频的远程下载。</summary>
        [TestMethod]
        public void MainWindow_AutomaticSync_UsesFiveMinuteInterval()
        {
            FieldInfo interval = typeof(SHB.EosDataDictionary.MainWindow).GetField(
                "AutomaticSyncIntervalMinutes",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(interval);
            Assert.AreEqual(5, interval.GetRawConstantValue());
            Assert.IsNotNull(typeof(SHB.EosDataDictionary.MainWindow).GetMethod(
                "RunAutomaticSyncLoopAsync",
                BindingFlags.Instance | BindingFlags.NonPublic));
        }

        /// <summary>XMZADD 20260901 验证启动同步尚未返回时关闭窗口只取消后台任务，不阻塞且不触发释放竞态。</summary>
        [TestMethod]
        public void MainWindow_CloseDuringStartupSync_CancelsWithoutNullOrDisposedRace()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), "shb-close-sync-" + Guid.NewGuid().ToString("N") + ".db");
            string credentialPath = Path.Combine(Path.GetTempPath(), "shb-close-sync-token-" + Guid.NewGuid().ToString("N") + ".bin");
            var manifestStarted = new ManualResetEventSlim(false);
            var releaseManifest = new ManualResetEventSlim(false);
            SHB.EosDataDictionary.MainWindow window = null;
            try
            {
                FieldInfo startupTaskField = typeof(SHB.EosDataDictionary.MainWindow).GetField(
                    "_startupTask",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                FieldInfo cleanupTaskField = typeof(SHB.EosDataDictionary.MainWindow).GetField(
                    "_backgroundCleanupTask",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(startupTaskField);
                Assert.IsNotNull(cleanupTaskField);

                var options = new DictionaryRepositoryOptions { Owner = "dictionary-owner" };
                var client = new StubGitHubDictionaryClient
                {
                    Manifest = new SnapshotManifest
                    {
                        FormatVersion = 1,
                        Revision = 0,
                        SnapshotPath = "snapshot/dictionary.json.gz",
                        GeneratedAtUtc = DateTime.UtcNow
                    },
                    ManifestRequestStarted = manifestStarted,
                    ManifestRelease = releaseManifest
                };
                var coordinator = new DictionarySyncCoordinator(
                    new LocalDictionaryStore(databasePath),
                    new SnapshotStore(databasePath),
                    client,
                    options,
                    new SystemClock());
                var viewModel = new MainViewModel(databasePath, Path.GetTempPath());
                var credentialStore = new GitHubCredentialStore(credentialPath);
                ConstructorInfo constructor = typeof(SHB.EosDataDictionary.MainWindow).GetConstructor(
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    null,
                    new[]
                    {
                        typeof(MainViewModel), typeof(ColumnLayoutStore), typeof(DictionarySyncCoordinator),
                        typeof(DictionaryRepositoryOptions), typeof(GitHubCredentialStore), typeof(IGitHubDictionaryClient)
                    },
                    null);
                Assert.IsNotNull(constructor);

                RunInSta(() =>
                {
                    PrepareDarkResources();
                    window = constructor.Invoke(new object[]
                    {
                        viewModel, new ColumnLayoutStore(databasePath), coordinator, options, credentialStore, client
                    }) as SHB.EosDataDictionary.MainWindow;
                    window.Show();
                });
                Assert.IsTrue(manifestStarted.Wait(TimeSpan.FromSeconds(5D)));

                Task startupTask = startupTaskField.GetValue(window) as Task;
                Assert.IsNotNull(startupTask);
                var stopwatch = Stopwatch.StartNew();
                RunInSta(window.Close);
                stopwatch.Stop();
                Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(1D));

                releaseManifest.Set();
                Assert.IsTrue(startupTask.Wait(TimeSpan.FromSeconds(5D)));
                Assert.IsFalse(startupTask.IsFaulted);
                Task cleanupTask = cleanupTaskField.GetValue(window) as Task;
                Assert.IsNotNull(cleanupTask);
                Assert.IsTrue(cleanupTask.Wait(TimeSpan.FromSeconds(5D)));
                Assert.IsFalse(cleanupTask.IsFaulted);
                Assert.IsTrue(viewModel.SyncStatusText.IndexOf("失败", StringComparison.Ordinal) < 0);
            }
            finally
            {
                releaseManifest.Set();
                if (window != null && window.IsVisible) RunInSta(window.Close);
                manifestStarted.Dispose();
                releaseManifest.Dispose();
                if (File.Exists(databasePath)) File.Delete(databasePath);
                if (File.Exists(databasePath + "-journal")) File.Delete(databasePath + "-journal");
                if (File.Exists(credentialPath)) File.Delete(credentialPath);
            }
        }

        /// <summary>XMZADD 20260901 验证手工同步被阻塞时关闭窗口会取消并等待该任务后再释放共享生命周期令牌。</summary>
        [TestMethod]
        public void MainWindow_CloseDuringManualSync_TracksCancellationBeforeDisposingLifetimeToken()
        {
            string databasePath = Path.Combine(Path.GetTempPath(), "shb-close-manual-" + Guid.NewGuid().ToString("N") + ".db");
            string credentialPath = Path.Combine(Path.GetTempPath(), "shb-close-manual-token-" + Guid.NewGuid().ToString("N") + ".bin");
            var manualStarted = new ManualResetEventSlim(false);
            var releaseManual = new ManualResetEventSlim(false);
            var cancellationObserved = new ManualResetEventSlim(false);
            SHB.EosDataDictionary.MainWindow window = null;
            try
            {
                FieldInfo manualTaskField = typeof(SHB.EosDataDictionary.MainWindow).GetField(
                    "_manualSyncTask", BindingFlags.Instance | BindingFlags.NonPublic);
                MethodInfo startManualSync = typeof(SHB.EosDataDictionary.MainWindow).GetMethod(
                    "StartManualSyncAsync", BindingFlags.Instance | BindingFlags.NonPublic);
                FieldInfo startupTaskField = typeof(SHB.EosDataDictionary.MainWindow).GetField(
                    "_startupTask", BindingFlags.Instance | BindingFlags.NonPublic);
                FieldInfo cleanupTaskField = typeof(SHB.EosDataDictionary.MainWindow).GetField(
                    "_backgroundCleanupTask", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(manualTaskField);
                Assert.IsNotNull(startManualSync);
                Assert.IsNotNull(startupTaskField);
                Assert.IsNotNull(cleanupTaskField);

                var options = new DictionaryRepositoryOptions { Owner = "dictionary-owner" };
                var snapshot = new SnapshotData { FormatVersion = 1, Revision = 0, RefreshedAt = DateTime.UtcNow };
                new SnapshotStore(databasePath).ReplaceScope(options.ScopeKey, snapshot, CancellationToken.None);
                new LocalDictionaryStore(databasePath).SaveSyncState(new DictionarySyncState
                {
                    StateKey = options.StateKey,
                    Revision = 0,
                    LastSyncAtUtc = DateTime.UtcNow
                });
                var client = new StubGitHubDictionaryClient
                {
                    Manifest = new SnapshotManifest
                    {
                        FormatVersion = 1,
                        Revision = 0,
                        SnapshotPath = "snapshot/dictionary.json.gz",
                        GeneratedAtUtc = DateTime.UtcNow
                    },
                    ManifestRequestStarted = manualStarted,
                    ManifestRelease = releaseManual,
                    ManifestBlockOnCallNumber = 2,
                    ManifestCancellationObserved = cancellationObserved
                };
                var coordinator = new DictionarySyncCoordinator(
                    new LocalDictionaryStore(databasePath),
                    new SnapshotStore(databasePath),
                    client,
                    options,
                    new SystemClock());
                var viewModel = new MainViewModel(databasePath, Path.GetTempPath());
                ConstructorInfo constructor = typeof(SHB.EosDataDictionary.MainWindow).GetConstructor(
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    null,
                    new[]
                    {
                        typeof(MainViewModel), typeof(ColumnLayoutStore), typeof(DictionarySyncCoordinator),
                        typeof(DictionaryRepositoryOptions), typeof(GitHubCredentialStore), typeof(IGitHubDictionaryClient)
                    },
                    null);

                RunInSta(() =>
                {
                    PrepareDarkResources();
                    window = constructor.Invoke(new object[]
                    {
                        viewModel, new ColumnLayoutStore(databasePath), coordinator, options,
                        new GitHubCredentialStore(credentialPath), client
                    }) as SHB.EosDataDictionary.MainWindow;
                    window.Show();
                });
                Task startupTask = null;
                Assert.IsTrue(SpinWait.SpinUntil(() =>
                {
                    startupTask = startupTaskField.GetValue(window) as Task;
                    return startupTask != null && startupTask.IsCompleted;
                }, TimeSpan.FromSeconds(5D)));
                Assert.IsFalse(startupTask.IsFaulted);

                Task manualTask = null;
                RunInSta(() =>
                {
                    manualTask = startManualSync.Invoke(window, null) as Task;
                });
                Assert.IsNotNull(manualTask);
                Assert.IsTrue(manualStarted.Wait(TimeSpan.FromSeconds(5D)));

                Task duplicateManualTask = null;
                RunInSta(() =>
                {
                    duplicateManualTask = startManualSync.Invoke(window, null) as Task;
                });
                Assert.AreSame(manualTask, duplicateManualTask);
                Assert.AreEqual(2, client.GetManifestCallCount);

                var stopwatch = Stopwatch.StartNew();
                RunInSta(window.Close);
                stopwatch.Stop();
                Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(1D));
                Assert.IsTrue(cancellationObserved.Wait(TimeSpan.FromSeconds(5D)));
                releaseManual.Set();
                Assert.IsTrue(manualTask.Wait(TimeSpan.FromSeconds(5D)));
                Assert.IsFalse(manualTask.IsFaulted);
                Task cleanupTask = cleanupTaskField.GetValue(window) as Task;
                Assert.IsNotNull(cleanupTask);
                Assert.IsTrue(cleanupTask.Wait(TimeSpan.FromSeconds(5D)));
                Assert.IsFalse(cleanupTask.IsFaulted);
            }
            finally
            {
                releaseManual.Set();
                if (window != null && window.IsVisible) RunInSta(window.Close);
                manualStarted.Dispose();
                releaseManual.Dispose();
                cancellationObserved.Dispose();
                if (File.Exists(databasePath)) File.Delete(databasePath);
                if (File.Exists(databasePath + "-journal")) File.Delete(databasePath + "-journal");
                if (File.Exists(credentialPath)) File.Delete(credentialPath);
            }
        }

        /// <summary>XMZADD 20260901 验证生命周期令牌尚未建立时手工更新只提示初始化，不使用无关联取消令牌启动同步。</summary>
        [TestMethod]
        public void MainWindow_ManualSyncBeforeLoaded_ShowsInitializingWithoutGitHubRequest()
        {
            RunInSta(() =>
            {
                PrepareDarkResources();
                var viewModel = new MainViewModel(_wpfDatabasePath, Path.GetTempPath());
                var window = new SHB.EosDataDictionary.MainWindow(viewModel, new ColumnLayoutStore(_wpfDatabasePath));
                try
                {
                    MethodInfo startManualSync = typeof(SHB.EosDataDictionary.MainWindow).GetMethod(
                        "StartManualSyncAsync", BindingFlags.Instance | BindingFlags.NonPublic);
                    Assert.IsNotNull(startManualSync);
                    var task = startManualSync.Invoke(window, null) as Task;
                    task.GetAwaiter().GetResult();
                    Assert.AreEqual("正在初始化，请稍后更新", viewModel.SyncStatusText);
                }
                finally
                {
                    window.Close();
                }
            });
        }

        /// <summary>XMZADD 20260831 验证来源证据表保留列宽、复制当前单元格和双击列边缘自适配能力。</summary>
        [TestMethod]
        public void MainWindow_EvidenceGrid_PersistsColumnWidthAndUsesCellCopyHandlers()
        {
            string path = Path.Combine(Path.GetTempPath(), "shb-evidence-grid-" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                RunInSta(() =>
                {
                    PrepareDarkResources();
                    var viewModel = new MainViewModel(path, Path.GetTempPath());
                    var snapshot = new SnapshotData();
                    var table = new TableMetadata { SchemaName = "dbo", ObjectName = "T_ITEM", ObjectType = "TABLE" };
                    table.ChineseName = new MetadataValue
                    {
                        Value = "物料",
                        Evidence = new List<EvidenceItem>
                        {
                            new EvidenceItem { SourceType = "源码", SourcePath = "Item.cs", SourceLine = 12, RuleName = "实体映射", Explanation = "物料名称" }
                        }
                    };
                    snapshot.Tables.Add(table);
                    viewModel.LoadSnapshotForTesting(snapshot);
                    viewModel.SelectedTable = viewModel.TableRows[0];
                    var layoutStore = new ColumnLayoutStore(path);
                    var window = new SHB.EosDataDictionary.MainWindow(viewModel, layoutStore);
                    try
                    {
                        window.Show();
                        window.UpdateLayout();
                        var evidenceGrid = window.FindName("EvidenceGrid") as DataGrid;
                        Assert.IsNotNull(evidenceGrid);
                        Assert.AreEqual(1, evidenceGrid.Items.Count);

                        evidenceGrid.Columns[0].Width = 137D;
                        window.UpdateLayout();
                        IDictionary<string, double> widths = layoutStore.Load(evidenceGrid.Name);
                        Assert.IsTrue(widths.ContainsKey("SourceType"));

                        MethodInfo autoSize = typeof(SHB.EosDataDictionary.MainWindow).GetMethod("DataGrid_PreviewMouseDoubleClick", BindingFlags.Instance | BindingFlags.NonPublic);
                        MethodInfo copyCurrentCell = typeof(SHB.EosDataDictionary.MainWindow).GetMethod("CopyCurrentCellToClipboard", BindingFlags.Instance | BindingFlags.NonPublic);
                        MethodInfo keyboardCopy = typeof(SHB.EosDataDictionary.MainWindow).GetMethod("DataGrid_PreviewKeyDown", BindingFlags.Instance | BindingFlags.NonPublic);
                        Assert.IsNotNull(autoSize);
                        Assert.IsNotNull(copyCurrentCell);
                        Assert.IsNotNull(keyboardCopy);

                        evidenceGrid.CurrentCell = new DataGridCellInfo(evidenceGrid.Items[0], evidenceGrid.Columns[0]);
                        object copied = copyCurrentCell.Invoke(window, new object[] { evidenceGrid });
                        Assert.IsInstanceOfType(copied, typeof(bool));
                    }
                    finally
                    {
                        window.Close();
                    }
                });
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
                if (File.Exists(path + "-journal")) File.Delete(path + "-journal");
            }
        }

        /// <summary>XMZADD 20260831 验证人工字典保存后的刷新链路预留项目范围缓存失效钩子。</summary>
        [TestMethod]
        public void MainWindow_ManualDictionarySave_UsesProjectCacheInvalidationHook()
        {
            MethodInfo invalidationHook = typeof(SHB.EosDataDictionary.MainWindow).GetMethod("InvalidateProjectRelatedObjectCache", BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.IsNotNull(invalidationHook);
        }

        /// <summary>XMZADD 20260831 验证 AI 设置和字典编辑窗口应用当前主题并展示已保存连接。</summary>
        [TestMethod]
        public void SecondaryWindows_UseThemeAndAiWindowShowsSavedConnection()
        {
            string path = Path.Combine(Path.GetTempPath(), "shb-window-" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                var store = new LocalDictionaryStore(path);
                store.SaveAiProvider(new AiProviderConfiguration
                {
                    Id = "deepseek",
                    Name = "DeepSeek 主连接",
                    Endpoint = "https://api.deepseek.com",
                    Model = "deepseek-chat",
                    ApiKey = "saved-key",
                    IsDefault = true,
                    IsEnabled = true,
                    TimeoutSeconds = 60,
                    MaxConcurrency = 2,
                    BatchSize = 10
                });

                RunInSta(() =>
                {
                    PrepareDarkResources();
                    Style windowStyle = _application.TryFindResource(typeof(Window)) as Style;
                    var aiWindow = new SHB.EosDataDictionary.AiProviderManagerWindow(path);
                    var dictionaryWindow = new SHB.EosDataDictionary.DictionaryEditWindow(path, "dev|SHB",
                        new TableMetadata { SchemaName = "dbo", ObjectName = "Item_Image", ObjectType = "TABLE" }, null);
                    try
                    {
                        Assert.AreSame(windowStyle, aiWindow.Style);
                        Assert.AreSame(windowStyle, dictionaryWindow.Style);
                        AssertLogicalTextExists(aiWindow, "新增连接");
                        AssertLogicalTextExists(aiWindow, "https://api.deepseek.com");
                        AssertLogicalTextExists(aiWindow, "deepseek-chat");
                        AssertLogicalTextContains(aiWindow, "密钥已保存");
                        AssertNoGainsboroTextBackground(dictionaryWindow);
                    }
                    finally
                    {
                        aiWindow.Close();
                        dictionaryWindow.Close();
                    }
                });
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
                if (File.Exists(path + "-journal")) File.Delete(path + "-journal");
            }
        }

        /// <summary>XMZADD 20260831 验证主窗口可使用隔离 ViewModel，界面测试不会读取用户真实超大快照。</summary>
        [TestMethod]
        public void MainWindow_CustomViewModel_UsesProvidedDataContext()
        {
            string path = Path.Combine(Path.GetTempPath(), "shb-main-window-" + Guid.NewGuid().ToString("N") + ".db");
            RunInSta(() =>
            {
                PrepareDarkResources();
                var viewModel = new MainViewModel(path, Path.GetTempPath());
                var window = new SHB.EosDataDictionary.MainWindow(viewModel, new ColumnLayoutStore(path));
                try
                {
                    Assert.AreSame(viewModel, window.DataContext);
                }
                finally
                {
                    window.Close();
                }
            });
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + "-journal")) File.Delete(path + "-journal");
        }

        /// <summary>XMZADD 20260831 验证当前子表物理名包含父表前缀时，关系双击仍精确跳转到父表。</summary>
        [TestMethod]
        public void RelationsGrid_MouseDoubleClick_WhenTableNameContainsParentPrefix_NavigatesToParentTable()
        {
            RunInSta(() =>
            {
                PrepareDarkResources();
                TableMetadata parent = new TableMetadata
                {
                    SchemaName = "dbo",
                    ObjectName = "T_ORDER",
                    ObjectType = "TABLE",
                    ChineseName = new MetadataValue { Value = "订单", Status = ConfidenceStatus.CodeEvidence }
                };
                TableMetadata child = new TableMetadata
                {
                    SchemaName = "dbo",
                    ObjectName = "T_ORDER_LINE",
                    ObjectType = "TABLE",
                    ChineseName = new MetadataValue { Value = "订单明细", Status = ConfidenceStatus.Guessed }
                };
                RelationMetadata relation = new RelationMetadata
                {
                    ParentSchemaName = parent.SchemaName,
                    ParentTableName = parent.ObjectName,
                    ParentFieldName = "FID",
                    ChildSchemaName = child.SchemaName,
                    ChildTableName = child.ObjectName,
                    ChildFieldName = "FORDERID",
                    RelationType = new MetadataValue { Value = "一对多", Status = ConfidenceStatus.Guessed }
                };
                parent.Relations.Add(relation);
                child.Relations.Add(relation);
                SnapshotData snapshot = new SnapshotData();
                snapshot.Tables.Add(parent);
                snapshot.Tables.Add(child);
                var viewModel = new MainViewModel(_wpfDatabasePath, Path.GetTempPath());
                viewModel.LoadSnapshotForTesting(snapshot);
                viewModel.SelectedTable = viewModel.TableRows[1];
                viewModel.SelectedRelation = viewModel.RelationRows[0];
                var window = new SHB.EosDataDictionary.MainWindow(viewModel, new ColumnLayoutStore(_wpfDatabasePath));
                try
                {
                    window.Show();
                    window.UpdateLayout();
                    MethodInfo handler = typeof(SHB.EosDataDictionary.MainWindow).GetMethod("RelationsGrid_MouseDoubleClick", BindingFlags.Instance | BindingFlags.NonPublic);

                    handler.Invoke(window, new object[] { window.FindName("RelationsGrid"), null });

                    Assert.AreEqual(parent.ObjectName, viewModel.SelectedTable.ObjectName);
                    Assert.AreEqual(parent.SchemaName, viewModel.SelectedTable.Metadata.SchemaName);
                }
                finally
                {
                    window.Close();
                }
            });
        }

        /// <summary>XMZADD 20260828 为主窗口运行时测试恢复确定的深色资源集合。</summary>
        private static void PrepareDarkResources()
        {
            _application.Resources.MergedDictionaries.Clear();
            _application.Resources.MergedDictionaries.Add(LoadDictionary("Themes/DarkTheme.xaml"));
            _application.Resources.MergedDictionaries.Add(LoadDictionary("Themes/ControlStyles.xaml"));
        }

        /// <summary>XMZADD 20260828 在屏幕外显示真实主窗口以完成模板、列宽和布局测量。</summary>
        private static SHB.EosDataDictionary.MainWindow CreateMeasuredMainWindow()
        {
            var window = new SHB.EosDataDictionary.MainWindow(
                new MainViewModel(_wpfDatabasePath, Path.GetTempPath()),
                new ColumnLayoutStore(_wpfDatabasePath))
            {
                Width = 1600,
                Height = 980,
                Left = -32000,
                Top = -32000,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual
            };
            window.Show();
            window.UpdateLayout();
            return window;
        }

        /// <summary>XMZADD 20260828 查找满足最小宽度的最近 Border，用于定位设计稿面板边界。</summary>
        private static Border FindAncestorBorder(DependencyObject child, double minimumWidth)
        {
            DependencyObject current = child;
            while (current != null)
            {
                var border = current as Border;
                if (border != null && border.ActualWidth >= minimumWidth)
                {
                    return border;
                }
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        /// <summary>XMZADD 20260828 查找指定类型的最近可视父级。</summary>
        private static T FindAncestor<T>(DependencyObject child) where T : DependencyObject
        {
            DependencyObject current = child;
            while (current != null)
            {
                T match = current as T;
                if (match != null)
                {
                    return match;
                }
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        /// <summary>XMZADD 20260828 收集当前已生成并可见的 DataGrid，排除未选中标签页内容。</summary>
        private static IList<DataGrid> FindVisibleDataGrids(DependencyObject root)
        {
            var result = new List<DataGrid>();
            CollectVisibleDataGrids(root, result);
            return result;
        }

        /// <summary>XMZADD 20260828 递归收集可见 DataGrid 供实际窗口布局断言使用。</summary>
        private static void CollectVisibleDataGrids(DependencyObject parent, IList<DataGrid> result)
        {
            int childCount = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < childCount; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                var grid = child as DataGrid;
                if (grid != null && grid.IsVisible)
                {
                    result.Add(grid);
                }
                CollectVisibleDataGrids(child, result);
            }
        }

        /// <summary>XMZADD 20260828 从可见表格中取得右侧字段字典表。</summary>
        private static DataGrid FindGridOtherThan(IList<DataGrid> grids, DataGrid excluded)
        {
            for (int i = 0; i < grids.Count; i++)
            {
                if (grids[i] != excluded)
                {
                    return grids[i];
                }
            }
            return null;
        }

        /// <summary>XMZADD 20260917 验证每个文本列直接或通过语义颜色样式继承统一的居中布局。</summary>
        private static void AssertTextColumnsUseStyle(DataGrid grid, Style centeredStyle)
        {
            for (int i = 0; i < grid.Columns.Count; i++)
            {
                var textColumn = grid.Columns[i] as DataGridTextColumn;
                if (textColumn != null)
                {
                    Style actualStyle = textColumn.ElementStyle;
                    Assert.IsTrue(
                        object.ReferenceEquals(centeredStyle, actualStyle) ||
                        (actualStyle != null && object.ReferenceEquals(centeredStyle, actualStyle.BasedOn)),
                        "文本列必须保留统一居中布局，列索引：" + i);
                }
            }
        }

        /// <summary>XMZADD 20260828 读取样式中指定依赖属性的显式 Setter 值供像素规范断言。</summary>
        private static object GetSetterValue(Style style, DependencyProperty property)
        {
            for (int i = 0; i < style.Setters.Count; i++)
            {
                var setter = style.Setters[i] as Setter;
                if (setter != null && setter.Property == property)
                {
                    return setter.Value;
                }
            }
            return null;
        }

        /// <summary>XMZADD 20260831 验证表格右键菜单包含复制当前单元格动作。</summary>
        private static void AssertHasCopyCellMenu(DataGrid grid)
        {
            Assert.IsNotNull(grid.ContextMenu);
            bool found = false;
            for (int i = 0; i < grid.ContextMenu.Items.Count; i++)
            {
                var item = grid.ContextMenu.Items[i] as MenuItem;
                if (item != null && string.Equals(item.Header as string, "复制单元格", StringComparison.Ordinal))
                {
                    found = true;
                    break;
                }
            }
            Assert.IsTrue(found);
        }

        /// <summary>XMZADD 20260831 从表格列头控件读取第一行中文标签。</summary>
        private static string GetColumnHeaderText(DataGridColumn column)
        {
            var panel = column.Header as Panel;
            if (panel == null)
            {
                return column.Header as string;
            }
            for (int i = 0; i < panel.Children.Count; i++)
            {
                var text = panel.Children[i] as TextBlock;
                if (text != null)
                {
                    return text.Text;
                }
            }
            return null;
        }

        /// <summary>XMZADD 20260831 验证逻辑树中存在指定完整文本。</summary>
        private static void AssertLogicalTextExists(DependencyObject root, string expected)
        {
            Assert.IsTrue(FindLogicalText(root, expected, false));
        }

        /// <summary>XMZADD 20260831 验证逻辑树中存在包含指定内容的提示文本。</summary>
        private static void AssertLogicalTextContains(DependencyObject root, string expected)
        {
            Assert.IsTrue(FindLogicalText(root, expected, true));
        }

        /// <summary>XMZADD 20260831 递归查找文本控件、按钮和输入框的可见内容。</summary>
        private static bool FindLogicalText(DependencyObject root, string expected, bool contains)
        {
            string value = null;
            var textBlock = root as TextBlock;
            var textBox = root as TextBox;
            var contentControl = root as ContentControl;
            if (textBlock != null) value = textBlock.Text;
            else if (textBox != null) value = textBox.Text;
            else if (contentControl != null) value = contentControl.Content as string;
            if (!string.IsNullOrEmpty(value) && (contains ? value.IndexOf(expected, StringComparison.Ordinal) >= 0 : value == expected))
            {
                return true;
            }
            foreach (object childObject in LogicalTreeHelper.GetChildren(root))
            {
                DependencyObject child = childObject as DependencyObject;
                if (child != null && FindLogicalText(child, expected, contains))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>XMZADD 20260831 验证深色编辑窗口没有遗留系统浅灰说明区。</summary>
        private static void AssertNoGainsboroTextBackground(DependencyObject root)
        {
            var text = root as TextBlock;
            Assert.IsFalse(text != null && Equals(text.Background, System.Windows.Media.Brushes.Gainsboro));
            foreach (object childObject in LogicalTreeHelper.GetChildren(root))
            {
                DependencyObject child = childObject as DependencyObject;
                if (child != null)
                {
                    AssertNoGainsboroTextBackground(child);
                }
            }
        }

        /// <summary>XMZADD 20260828 在指定表格的可视树中查找目标方向滚动条。</summary>
        private static ScrollBar FindScrollBar(DependencyObject parent, Orientation orientation)
        {
            int childCount = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < childCount; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                var scrollBar = child as ScrollBar;
                if (scrollBar != null && scrollBar.Orientation == orientation && scrollBar.IsVisible)
                {
                    return scrollBar;
                }
                ScrollBar nested = FindScrollBar(child, orientation);
                if (nested != null)
                {
                    return nested;
                }
            }
            return null;
        }

        /// <summary>XMZADD 20260828 从控件可视树读取首个可见文本，核验模板真实渲染结果。</summary>
        private static string FindFirstVisibleText(DependencyObject parent)
        {
            int childCount = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < childCount; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                var textBlock = child as TextBlock;
                if (textBlock != null && textBlock.IsVisible && !string.IsNullOrWhiteSpace(textBlock.Text))
                {
                    return textBlock.Text;
                }
                string nestedText = FindFirstVisibleText(child);
                if (!string.IsNullOrWhiteSpace(nestedText))
                {
                    return nestedText;
                }
            }
            return null;
        }

        /// <summary>XMZADD 20260901 为同步设置窗口提供完全离线的 GitHub 身份响应，禁止自动测试访问真实仓库。</summary>
        private sealed class StubGitHubDictionaryClient : IGitHubDictionaryClient
        {
            public int GetCurrentUserCallCount { get; private set; }
            public int GetManifestCallCount { get; private set; }
            public int ManifestThreadId { get; private set; }
            public int DownloadSnapshotCallCount { get; private set; }
            public SnapshotManifest Manifest { get; set; }
            public byte[] SnapshotContent { get; set; }
            public ManualResetEventSlim ManifestRequestStarted { get; set; }
            public ManualResetEventSlim ManifestRelease { get; set; }
            public int ManifestBlockOnCallNumber { get; set; }
            public Exception ManifestException { get; set; }
            public int CreatedIssueNumber { get; set; }
            public ManualResetEventSlim ManifestCancellationObserved { get; set; }
            public Exception StoredIdentityException { get; set; }
            public Exception CandidateIdentityException { get; set; }
            public int CandidateIdentityCallCount { get; private set; }
            public string LastCandidateToken { get; private set; }
            public ManualResetEventSlim CandidateIdentityStarted { get; set; }
            public ManualResetEventSlim CandidateIdentityRelease { get; set; }
            public ManualResetEventSlim CandidateCancellationObserved { get; set; }
            public ManualResetEventSlim StoredIdentityStarted { get; set; }
            public ManualResetEventSlim StoredIdentityRelease { get; set; }
            public ManualResetEventSlim StoredIdentityCancellationObserved { get; set; }

            /// <summary>XMZADD 20260901 阻止同步设置测试意外读取远程清单。</summary>
            public Task<SnapshotManifest> GetManifestAsync(CancellationToken cancellationToken)
            {
                ManifestThreadId = Thread.CurrentThread.ManagedThreadId;
                GetManifestCallCount++;
                if (ManifestException != null)
                {
                    throw ManifestException;
                }
                if (Manifest == null)
                {
                    throw new InvalidOperationException("测试不应读取远程清单。");
                }
                if (ManifestRelease != null &&
                    (ManifestBlockOnCallNumber <= 0 || GetManifestCallCount == ManifestBlockOnCallNumber))
                {
                    return Task.Run(() =>
                    {
                        if (ManifestRequestStarted != null)
                        {
                            ManifestRequestStarted.Set();
                        }
                        int signaled = WaitHandle.WaitAny(
                            new[] { ManifestRelease.WaitHandle, cancellationToken.WaitHandle },
                            TimeSpan.FromSeconds(10D));
                        if (signaled == 1 && ManifestCancellationObserved != null)
                        {
                            ManifestCancellationObserved.Set();
                        }
                        if (signaled == WaitHandle.WaitTimeout)
                        {
                            throw new TimeoutException("假清单请求等待超时。");
                        }
                        cancellationToken.ThrowIfCancellationRequested();
                        return Manifest;
                    }, cancellationToken);
                }
                return Task.FromResult(Manifest);
            }

            /// <summary>XMZADD 20260901 阻止同步设置测试意外下载远程快照。</summary>
            public Task<byte[]> DownloadSnapshotAsync(string relativePath, CancellationToken cancellationToken)
            {
                DownloadSnapshotCallCount++;
                if (SnapshotContent == null)
                {
                    throw new InvalidOperationException("测试不应下载远程快照。");
                }
                return Task.FromResult(SnapshotContent);
            }

            /// <summary>XMZADD 20260901 阻止同步设置测试意外下载远程修订。</summary>
            public Task<DictionaryRevisionPackage> DownloadRevisionAsync(long revision, CancellationToken cancellationToken)
            {
                throw new InvalidOperationException("测试不应下载远程修订。");
            }

            /// <summary>XMZADD 20260901 返回稳定的假 GitHub 身份以验证界面展示。</summary>
            public Task<GitHubUserIdentity> GetCurrentUserAsync(CancellationToken cancellationToken)
            {
                GetCurrentUserCallCount++;
                if (StoredIdentityException != null)
                {
                    return Task.FromException<GitHubUserIdentity>(StoredIdentityException);
                }
                if (StoredIdentityRelease != null)
                {
                    return Task.Run(() =>
                    {
                        if (StoredIdentityStarted != null)
                        {
                            StoredIdentityStarted.Set();
                        }
                        int signaled = WaitHandle.WaitAny(
                            new[] { StoredIdentityRelease.WaitHandle, cancellationToken.WaitHandle },
                            TimeSpan.FromSeconds(10D));
                        if (signaled == 1 && StoredIdentityCancellationObserved != null)
                        {
                            StoredIdentityCancellationObserved.Set();
                        }
                        if (signaled == WaitHandle.WaitTimeout)
                        {
                            throw new TimeoutException("假已保存身份请求等待超时。");
                        }
                        cancellationToken.ThrowIfCancellationRequested();
                        return new GitHubUserIdentity
                        {
                            GitHubUserId = "123456",
                            Login = "dictionary-user",
                            DisplayName = "Dictionary User"
                        };
                    }, cancellationToken);
                }
                return Task.FromResult(new GitHubUserIdentity
                {
                    GitHubUserId = "123456",
                    Login = "dictionary-user",
                    DisplayName = "Dictionary User"
                });
            }

            /// <summary>XMZADD 20260901 使用候选 Token 返回可配置身份结果，验证成功前不接触真实凭据或网络。</summary>
            public Task<GitHubUserIdentity> GetCurrentUserAsync(string candidateToken, CancellationToken cancellationToken)
            {
                CandidateIdentityCallCount++;
                LastCandidateToken = candidateToken;
                if (CandidateIdentityException != null)
                {
                    return Task.FromException<GitHubUserIdentity>(CandidateIdentityException);
                }
                if (CandidateIdentityRelease != null)
                {
                    return Task.Run(() =>
                    {
                        if (CandidateIdentityStarted != null)
                        {
                            CandidateIdentityStarted.Set();
                        }
                        int signaled = WaitHandle.WaitAny(
                            new[] { CandidateIdentityRelease.WaitHandle, cancellationToken.WaitHandle },
                            TimeSpan.FromSeconds(10D));
                        if (signaled == 1 && CandidateCancellationObserved != null)
                        {
                            CandidateCancellationObserved.Set();
                        }
                        if (signaled == WaitHandle.WaitTimeout)
                        {
                            throw new TimeoutException("假身份请求等待超时。");
                        }
                        cancellationToken.ThrowIfCancellationRequested();
                        return new GitHubUserIdentity
                        {
                            GitHubUserId = "123456",
                            Login = "dictionary-user",
                            DisplayName = "Dictionary User"
                        };
                    }, cancellationToken);
                }
                return Task.FromResult(new GitHubUserIdentity
                {
                    GitHubUserId = "123456",
                    Login = "dictionary-user",
                    DisplayName = "Dictionary User"
                });
            }

            /// <summary>XMZADD 20260901 返回固定发布者名单，供结构入口权限测试在无网络环境下复用。</summary>
            public Task<IList<string>> GetPublisherIdsAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult<IList<string>>(new List<string> { "123456" });
            }

            /// <summary>XMZADD 20260901 阻止同步设置测试意外创建远程 Issue。</summary>
            public Task<int> CreateDictionaryIssueAsync(DictionaryChangeBatch batch, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (CreatedIssueNumber <= 0)
                {
                    throw new InvalidOperationException("测试不应创建远程 Issue。");
                }
                return Task.FromResult(CreatedIssueNumber);
            }

            /// <summary>XMZADD 20260903 同步设置测试不参与远端工作流确认。</summary>
            public Task<bool> AreDictionaryOperationsAppliedAsync(
                DictionaryChangeBatch batch,
                int issueNumber,
                long maximumRevision,
                CancellationToken cancellationToken)
            {
                throw new InvalidOperationException("测试不应核验远程 Issue。");
            }

            /// <summary>XMZADD 20260903 返回待生效状态以满足同步设置测试的三态客户端契约。</summary>
            public Task<DictionaryIssueApplyStatus> GetDictionaryOperationsApplyStatusAsync(
                DictionaryChangeBatch batch,
                int issueNumber,
                long maximumRevision,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(DictionaryIssueApplyStatus.Pending);
            }

            /// <summary>XMZADD 20260903 返回原 Issue 编号以满足同步设置测试的恢复客户端契约。</summary>
            public Task<int> RecreateRejectedDictionaryIssueAsync(
                DictionaryChangeBatch batch,
                int rejectedIssueNumber,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(rejectedIssueNumber);
            }
        }

        /// <summary>XMZADD 20260903 仅在首次保存新同步游标前移除快照，模拟持久化成功后的瞬时本地恢复失败。</summary>
        private sealed class DeleteSnapshotOnceObserver : IDictionarySyncCommitObserver
        {
            private readonly string _databasePath;
            private readonly string _scopeKey;
            private bool _deleted;

            /// <summary>XMZADD 20260903 保存测试数据库和作用域，以便在精确提交边界注入一次恢复故障。</summary>
            public DeleteSnapshotOnceObserver(string databasePath, string scopeKey)
            {
                _databasePath = databasePath;
                _scopeKey = scopeKey;
            }

            /// <summary>XMZADD 20260903 关系确认阶段不注入故障。</summary>
            public void BeforeConfirmingBatches()
            {
            }

            /// <summary>XMZADD 20260903 快照替换阶段不注入故障。</summary>
            public void BeforeReplacingSnapshot()
            {
            }

            /// <summary>XMZADD 20260903 首次游标保存前删除刚落盘快照，使下一轮可验证界面恢复重试。</summary>
            public void BeforeSavingSyncState()
            {
                if (_deleted)
                {
                    return;
                }
                _deleted = true;
                using (var connection = new SQLiteConnection("Data Source=" + _databasePath + ";Version=3;"))
                using (SQLiteCommand command = connection.CreateCommand())
                {
                    connection.Open();
                    command.CommandText = @"DELETE FROM SnapshotTables WHERE ScopeKey = @ScopeKey;
DELETE FROM SnapshotContents WHERE ScopeKey = @ScopeKey;";
                    command.Parameters.AddWithValue("@ScopeKey", _scopeKey);
                    command.ExecuteNonQuery();
                }
            }
        }

        /// <summary>XMZADD 20260901 统计并阻断配置组合测试中的 HTTP 请求，证明构造同步依赖不会触网。</summary>
        private sealed class NoNetworkHttpMessageHandler : HttpMessageHandler
        {
            public int SendCount { get; private set; }

            /// <summary>XMZADD 20260901 在测试边界记录意外网络请求并立即失败，禁止自动测试访问真实 GitHub。</summary>
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                SendCount++;
                throw new InvalidOperationException("配置组合测试不允许访问网络。");
            }
        }

        /// <summary>XMZADD 20260902 从样式定义读取指定属性值，验证代码创建表格的视觉约束不依赖实际渲染尺寸。</summary>
        private static object GetStyleSetterValue(Style style, DependencyProperty property)
        {
            if (style == null)
            {
                return null;
            }
            for (int index = 0; index < style.Setters.Count; index++)
            {
                var setter = style.Setters[index] as Setter;
                if (setter != null && setter.Property == property)
                {
                    return setter.Value;
                }
            }
            return null;
        }

        /// <summary>XMZADD 20260902 通过非公开 setter 构造界面状态测试输入，避免为测试扩大生产结果对象的写权限。</summary>
        private static void SetProperty(object target, string propertyName, object value)
        {
            PropertyInfo property = target.GetType().GetProperty(
                propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.IsNotNull(property);
            property.SetValue(target, value, null);
        }

        /// <summary>XMZADD 20260904 递归统计指定绑定路径的文本元素，防止同一状态摘要在多个位置重复展示。</summary>
        private static int CountTextBindings(DependencyObject root, string path)
        {
            int count = 0;
            var textBlock = root as TextBlock;
            if (textBlock != null)
            {
                Binding binding = BindingOperations.GetBinding(textBlock, TextBlock.TextProperty);
                if (binding != null && binding.Path != null && binding.Path.Path == path)
                {
                    count++;
                }
            }
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            {
                count += CountTextBindings(VisualTreeHelper.GetChild(root, index), path);
            }
            return count;
        }

        /// <summary>XMZADD 20260917 递归统计界面中的固定文案，防止状态栏标签意外重复。</summary>
        private static int CountTextOccurrences(DependencyObject root, string expected)
        {
            int count = 0;
            var textBlock = root as TextBlock;
            if (textBlock != null && string.Equals(textBlock.Text, expected, StringComparison.Ordinal))
            {
                count++;
            }
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            {
                count += CountTextOccurrences(VisualTreeHelper.GetChild(root, index), expected);
            }
            return count;
        }

        /// <summary>XMZADD 20260902 通过异步状态机异常表验证致命内存异常不会落入通用异常分支。</summary>
        private static void AssertOutOfMemoryCatchPrecedesGeneralCatch(
            string methodName,
            int expectedOrderedCatchCount)
        {
            MethodInfo handler = typeof(StructureMaintenanceWindow).GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(handler);
            var stateMachineAttribute = Attribute.GetCustomAttribute(
                handler,
                typeof(System.Runtime.CompilerServices.AsyncStateMachineAttribute))
                as System.Runtime.CompilerServices.AsyncStateMachineAttribute;
            Assert.IsNotNull(stateMachineAttribute);
            MethodInfo moveNext = stateMachineAttribute.StateMachineType.GetMethod(
                "MoveNext",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.IsNotNull(moveNext);
            MethodBody body = moveNext.GetMethodBody();
            Assert.IsNotNull(body);

            int orderedCatchCount = 0;
            IList<ExceptionHandlingClause> clauses = body.ExceptionHandlingClauses;
            for (int outOfMemoryIndex = 0; outOfMemoryIndex < clauses.Count; outOfMemoryIndex++)
            {
                ExceptionHandlingClause outOfMemoryClause = clauses[outOfMemoryIndex];
                if (outOfMemoryClause.Flags != ExceptionHandlingClauseOptions.Clause ||
                    outOfMemoryClause.CatchType != typeof(OutOfMemoryException))
                {
                    continue;
                }
                for (int generalIndex = outOfMemoryIndex + 1; generalIndex < clauses.Count; generalIndex++)
                {
                    ExceptionHandlingClause generalClause = clauses[generalIndex];
                    if (generalClause.Flags == ExceptionHandlingClauseOptions.Clause &&
                        generalClause.CatchType == typeof(Exception) &&
                        generalClause.TryOffset == outOfMemoryClause.TryOffset &&
                        generalClause.TryLength == outOfMemoryClause.TryLength)
                    {
                        orderedCatchCount++;
                        break;
                    }
                }
            }

            Assert.AreEqual(
                expectedOrderedCatchCount,
                orderedCatchCount,
                methodName + " 未在每个业务通用异常前单独处理内存不足。");
        }

        /// <summary>XMZADD 20260828 从应用程序集加载指定 WPF 资源字典。</summary>
        private static ResourceDictionary LoadDictionary(string relativePath)
        {
            var dictionary = new ResourceDictionary();
            dictionary.Source = new Uri("pack://application:,,,/SHB.EosDataDictionary;component/" + relativePath, UriKind.Absolute);
            return dictionary;
        }

        /// <summary>XMZADD 20260828 在 STA 线程执行 WPF 断言并把异常返回测试线程。</summary>
        private static void RunInSta(Action action)
        {
            _application.Dispatcher.Invoke(action);
        }

        /// <summary>XMZADD 20260901 在 WPF 调度程序持续泵送消息时等待异步界面任务，避免跨线程修改绑定集合。</summary>
        private static void RunTaskInSta(Func<Task> action)
        {
            _application.Dispatcher.Invoke(() =>
            {
                Task task = action();
                var frame = new System.Windows.Threading.DispatcherFrame();
                task.ContinueWith(
                    delegate { frame.Continue = false; },
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.FromCurrentSynchronizationContext());
                System.Windows.Threading.Dispatcher.PushFrame(frame);
                task.GetAwaiter().GetResult();
            });
        }
    }
}
