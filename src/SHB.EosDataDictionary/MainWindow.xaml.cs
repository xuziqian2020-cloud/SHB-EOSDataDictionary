using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Interop;
using System.Windows.Media;
using WinForms = System.Windows.Forms;
using SHB.EosDataDictionary.Models;
using SHB.EosDataDictionary.Services;
using SHB.EosDataDictionary.ViewModels;

namespace SHB.EosDataDictionary
{
    /// <summary>XMZADD 20260828 承载 EOS 数据字典主工作区。</summary>
    public partial class MainWindow : Window
    {
        private const int AutomaticSyncIntervalMinutes = 5;
        private const int WM_GETMINMAXINFO = 0x0024;
        private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

        /// <summary>XMZADD 20260903 表示 Win32 窗口定位所需的物理像素坐标。</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        /// <summary>XMZADD 20260903 承载 Win32 窗口最大化位置及尺寸约束。</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeMinMaxInfo
        {
            public NativePoint Reserved;
            public NativePoint MaxSize;
            public NativePoint MaxPosition;
            public NativePoint MinTrackSize;
            public NativePoint MaxTrackSize;
        }

        /// <summary>XMZADD 20260903 表示 Win32 屏幕区域的物理像素边界。</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        /// <summary>XMZADD 20260903 承载显示器完整边界、工作区和标志信息。</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeMonitorInfo
        {
            public int Size;
            public NativeRect Monitor;
            public NativeRect Work;
            public uint Flags;
        }

        /// <summary>XMZADD 20260903 查找指定窗口当前所在或距离最近的显示器。</summary>
        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr windowHandle, uint flags);

        /// <summary>XMZADD 20260903 读取显示器的物理边界与任务栏扣除后的工作区。</summary>
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr monitorHandle, ref NativeMonitorInfo monitorInfo);

        private readonly MainViewModel _viewModel;
        private readonly ThemeService _themeService;
        private readonly SqlServerConnectionProbe _connectionProbe;
        private readonly ColumnLayoutStore _columnLayoutStore;
        private readonly DictionarySyncCoordinator _syncCoordinator;
        private readonly DictionaryRepositoryOptions _repositoryOptions;
        private readonly GitHubCredentialStore _gitHubCredentialStore;
        private readonly IGitHubDictionaryClient _gitHubDictionaryClient;
        private readonly SemaphoreSlim _synchronizationLock = new SemaphoreSlim(1, 1);
        private CancellationTokenSource _loadCancellation;
        private CancellationTokenSource _startupCancellation;
        private CancellationTokenSource _aiCancellation;
        private Task _startupTask;
        private Task _automaticSyncTask;
        private Task _manualSyncTask;
        private Task _backgroundCleanupTask;
        private bool _isClosed;
        private bool _isRestoringColumnLayout;
        private bool _hasRestoredSnapshot;

        /// <summary>XMZADD 20260828 初始化主界面数据、主题服务和默认连接选择。</summary>
        public MainWindow()
            : this(null, null)
        {
        }

        /// <summary>XMZADD 20260831 使用指定主视图模型初始化窗口，支持生产默认存储和隔离界面测试。</summary>
        public MainWindow(MainViewModel viewModel)
            : this(viewModel, null)
        {
        }

        /// <summary>XMZADD 20260831 使用指定主视图模型和列宽存储初始化窗口，确保界面测试不读取当前用户的列宽配置。</summary>
        public MainWindow(MainViewModel viewModel, ColumnLayoutStore columnLayoutStore)
            : this(viewModel, columnLayoutStore, null, null, null, null)
        {
        }

        /// <summary>XMZADD 20260901 使用应用级 GitHub 同步依赖初始化主窗口，同时保留空依赖的离线界面测试路径。</summary>
        internal MainWindow(
            MainViewModel viewModel,
            ColumnLayoutStore columnLayoutStore,
            DictionarySyncCoordinator syncCoordinator,
            DictionaryRepositoryOptions repositoryOptions,
            GitHubCredentialStore gitHubCredentialStore,
            IGitHubDictionaryClient gitHubDictionaryClient)
        {
            InitializeComponent();
            _viewModel = viewModel ?? new MainViewModel();
            _themeService = ((App)Application.Current).ThemeService;
            _connectionProbe = new SqlServerConnectionProbe();
            // 测试注入独立存储，避免当前 Windows 用户历史列宽影响 XAML 设计初始值。
            _columnLayoutStore = columnLayoutStore ?? new ColumnLayoutStore();
            _syncCoordinator = syncCoordinator;
            _repositoryOptions = repositoryOptions ?? new DictionaryRepositoryOptions();
            _gitHubCredentialStore = gitHubCredentialStore ?? new GitHubCredentialStore(
                Path.Combine(AppPathService.GetLocalDataDirectory(), "github-token.bin"));
            _gitHubDictionaryClient = gitHubDictionaryClient;
            DataContext = _viewModel;
            UpdateThemeButtonToolTip();
            if (ProfileCombo.Items.Count > 0)
            {
                ProfileCombo.SelectedIndex = 0;
            }
        }

        /// <summary>XMZADD 20260903 在主窗口句柄创建后接入 Win32 消息，以约束无边框窗口的最大化范围。</summary>
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            IntPtr windowHandle = new WindowInteropHelper(this).Handle;
            HwndSource windowSource = HwndSource.FromHwnd(windowHandle);
            if (windowSource != null)
            {
                windowSource.AddHook(MainWindowMessageHook);
            }
        }

        /// <summary>XMZADD 20260903 使用当前显示器工作区响应最大化约束，避免无边框阴影覆盖任务栏。</summary>
        private IntPtr MainWindowMessageHook(
            IntPtr windowHandle,
            int message,
            IntPtr wParam,
            IntPtr lParam,
            ref bool handled)
        {
            if (message != WM_GETMINMAXINFO || lParam == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            NativeMonitorInfo monitorInfo;
            if (!TryGetCurrentMonitorInfo(windowHandle, out monitorInfo))
            {
                return IntPtr.Zero;
            }

            Rect maximizedBounds = WindowPlacementCalculator.CalculateMaximizedBounds(
                ToRect(monitorInfo.Monitor),
                ToRect(monitorInfo.Work));
            NativeMinMaxInfo minMaxInfo = (NativeMinMaxInfo)Marshal.PtrToStructure(
                lParam,
                typeof(NativeMinMaxInfo));
            // 最大化坐标必须相对显示器原点，才能正确覆盖负坐标副屏并避开各屏任务栏。
            minMaxInfo.MaxPosition = new NativePoint
            {
                X = (int)maximizedBounds.X,
                Y = (int)maximizedBounds.Y
            };
            minMaxInfo.MaxSize = new NativePoint
            {
                X = (int)maximizedBounds.Width,
                Y = (int)maximizedBounds.Height
            };
            Marshal.StructureToPtr(minMaxInfo, lParam, false);
            handled = true;
            return IntPtr.Zero;
        }

        /// <summary>XMZADD 20260903 将 Win32 物理像素矩形转换为窗口定位计算器使用的矩形。</summary>
        private static Rect ToRect(NativeRect nativeRect)
        {
            return new Rect(
                nativeRect.Left,
                nativeRect.Top,
                nativeRect.Right - nativeRect.Left,
                nativeRect.Bottom - nativeRect.Top);
        }

        /// <summary>XMZADD 20260903 获取主窗口当前显示器信息，统一最大化和标题栏拖动的工作区来源。</summary>
        private static bool TryGetCurrentMonitorInfo(IntPtr windowHandle, out NativeMonitorInfo monitorInfo)
        {
            monitorInfo = new NativeMonitorInfo
            {
                Size = Marshal.SizeOf(typeof(NativeMonitorInfo))
            };
            if (windowHandle == IntPtr.Zero)
            {
                return false;
            }

            IntPtr monitorHandle = MonitorFromWindow(windowHandle, MONITOR_DEFAULTTONEAREST);
            return monitorHandle != IntPtr.Zero && GetMonitorInfo(monitorHandle, ref monitorInfo);
        }

        /// <summary>XMZADD 20260828 在数据绑定完成后恢复设计稿约定的默认表详情，避免控件初始化把选择写回为空。</summary>
        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (_isClosed || _startupCancellation != null)
            {
                return;
            }
            RestoreColumnWidths(TablesGrid);
            RestoreColumnWidths(FieldsGrid);
            RestoreColumnWidths(RelationsGrid);
            RestoreColumnWidths(EvidenceGrid);
            RegisterColumnWidthPersistence(TablesGrid);
            RegisterColumnWidthPersistence(FieldsGrid);
            RegisterColumnWidthPersistence(RelationsGrid);
            RegisterColumnWidthPersistence(EvidenceGrid);
            var startupCancellation = new CancellationTokenSource();
            _startupCancellation = startupCancellation;
            CancellationToken startupToken = startupCancellation.Token;
            _startupTask = InitializeSharedDictionaryAsync(startupToken);
            await _startupTask;
            if (_isClosed || startupToken.IsCancellationRequested)
            {
                return;
            }

            if (_viewModel.SelectedTable == null && _viewModel.TableRows.Count > 0)
            {
                _viewModel.SelectedTable = _viewModel.TableRows[0];
            }
            if (_viewModel.SelectedTable != null)
            {
                TablesGrid.SelectedItem = _viewModel.SelectedTable;
                TablesGrid.ScrollIntoView(_viewModel.SelectedTable);
            }
        }

        /// <summary>XMZADD 20260901 使用启动时捕获的局部取消令牌恢复缓存并同步，关闭后禁止再启动自动循环。</summary>
        private async Task InitializeSharedDictionaryAsync(CancellationToken startupToken)
        {
            if (_syncCoordinator == null)
            {
                await RestoreLocalSnapshotForStartupAsync(startupToken);
                _viewModel.SyncStatusText = "仓库未配置";
                _viewModel.IsOffline = true;
            }
            else
            {
                // 七万表快照先同步再恢复到界面，避免启动时同时保留同步副本和旧界面副本。
                bool restored = await SynchronizeAndRefreshAsync(false, startupToken);
                if (!restored && !_isClosed && !startupToken.IsCancellationRequested)
                {
                    string syncFailureText = _viewModel.SyncStatusText;
                    bool wasOffline = _viewModel.IsOffline;
                    await RestoreLocalSnapshotForStartupAsync(startupToken);
                    _viewModel.SyncStatusText = syncFailureText;
                    _viewModel.IsOffline = wasOffline;
                }
                if (!_isClosed && !startupToken.IsCancellationRequested)
                {
                    _automaticSyncTask = RunAutomaticSyncLoopAsync(startupToken);
                }
            }

            if (!_isClosed && !startupToken.IsCancellationRequested)
            {
                try
                {
                    await RefreshStructureMaintenanceAccessAsync(startupToken);
                }
                catch (OperationCanceledException)
                {
                    if (!startupToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    // 窗口关闭期间取消远端权限核验属于正常生命周期结束，不能冒泡到 async void 界面事件。
                }
            }
        }

        /// <summary>XMZADD 20260903 在远程不可用或未配置时只恢复一次本地规范快照，并把损坏诊断限制为安全状态文本。</summary>
        private async Task<bool> RestoreLocalSnapshotForStartupAsync(CancellationToken cancellationToken)
        {
            try
            {
                bool restored = await _viewModel.RestoreMostRecentSnapshotAsync(cancellationToken);
                if (restored)
                {
                    _hasRestoredSnapshot = true;
                }
                return restored;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception exception)
            {
                if (exception is OutOfMemoryException || exception is StackOverflowException)
                {
                    throw;
                }
                // 本地缓存损坏不能阻断窗口启动，也不能把文件路径等异常详情显示到主界面。
                _viewModel.MarkSnapshotRestoreFailed(
                    exception is InvalidDataException ? "缓存格式无效" : "缓存不可用");
                return false;
            }
        }

        /// <summary>XMZADD 20260901 在启动同步后按远程数字发布者 ID 刷新结构维护入口，不依赖本机连接是否已配置。</summary>
        private async Task RefreshStructureMaintenanceAccessAsync(CancellationToken cancellationToken)
        {
            _viewModel.CanOpenStructureMaintenance = false;
            if (_gitHubDictionaryClient == null)
            {
                return;
            }

            try
            {
                GitHubUserIdentity identity = await _gitHubDictionaryClient.GetCurrentUserAsync(cancellationToken);
                IList<string> publisherIds = await _gitHubDictionaryClient.GetPublisherIdsAsync(cancellationToken);
                if (identity == null || string.IsNullOrWhiteSpace(identity.GitHubUserId) || publisherIds == null)
                {
                    return;
                }
                for (int index = 0; index < publisherIds.Count; index++)
                {
                    if (string.Equals(identity.GitHubUserId, publisherIds[index], StringComparison.Ordinal))
                    {
                        _viewModel.CanOpenStructureMaintenance = true;
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // 远程身份或发布者名单暂时不可用时入口保持隐藏，避免把网络异常误判为授权。
                _viewModel.CanOpenStructureMaintenance = false;
            }
        }

        /// <summary>XMZADD 20260903 支持自绘标题栏拖动、最大化恢复和双击切换，使鼠标抓取位置保持连续。</summary>
        private void AppTitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                ToggleWindowState();
                return;
            }

            if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed)
            {
                return;
            }

            if (WindowState == WindowState.Maximized)
            {
                RestoreWindowForTitleBarDrag(e);
            }

            DragMove();
        }

        /// <summary>XMZADD 20260903 按最大化窗口中的鼠标抓取比例恢复窗口，使标题栏拖动保持连续。</summary>
        private void RestoreWindowForTitleBarDrag(System.Windows.Input.MouseButtonEventArgs e)
        {
            HwndSource windowSource = PresentationSource.FromVisual(this) as HwndSource;
            NativeMonitorInfo monitorInfo;
            if (windowSource == null || windowSource.CompositionTarget == null ||
                !TryGetCurrentMonitorInfo(windowSource.Handle, out monitorInfo))
            {
                WindowState = WindowState.Normal;
                return;
            }

            Point mousePosition = e.GetPosition(this);
            double horizontalRatio = ActualWidth > 0D ? mousePosition.X / ActualWidth : 0.5D;
            double restoredWidth = RestoreBounds.Width;
            double restoredHeight = RestoreBounds.Height;
            if (double.IsNaN(restoredWidth) || double.IsInfinity(restoredWidth) || restoredWidth <= 0D)
            {
                restoredWidth = Width;
            }
            if (double.IsNaN(restoredHeight) || double.IsInfinity(restoredHeight) || restoredHeight <= 0D)
            {
                restoredHeight = Height;
            }
            if (double.IsNaN(restoredWidth) || double.IsInfinity(restoredWidth) || restoredWidth <= 0D)
            {
                restoredWidth = ActualWidth;
            }
            if (double.IsNaN(restoredHeight) || double.IsInfinity(restoredHeight) || restoredHeight <= 0D)
            {
                restoredHeight = ActualHeight;
            }

            // WinForms 光标与 Win32 工作区均为物理像素，必须使用同一窗口 DPI 转换后再参与 WPF 定位。
            System.Drawing.Point physicalCursorPosition = WinForms.Cursor.Position;
            Matrix fromDevice = windowSource.CompositionTarget.TransformFromDevice;
            Point cursorPosition = fromDevice.Transform(
                new Point(physicalCursorPosition.X, physicalCursorPosition.Y));
            Point workAreaTopLeft = fromDevice.Transform(
                new Point(monitorInfo.Work.Left, monitorInfo.Work.Top));
            Point workAreaBottomRight = fromDevice.Transform(
                new Point(monitorInfo.Work.Right, monitorInfo.Work.Bottom));
            var workingArea = new Rect(workAreaTopLeft, workAreaBottomRight);
            Point restoredLocation = WindowPlacementCalculator.CalculateRestoredLocation(
                cursorPosition,
                new Size(restoredWidth, restoredHeight),
                horizontalRatio,
                mousePosition.Y,
                workingArea);

            WindowState = WindowState.Normal;
            Left = restoredLocation.X;
            Top = restoredLocation.Y;
        }

        /// <summary>XMZADD 20260828 最小化自绘标题栏所属的主窗口。</summary>
        private void MinimizeWindowButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        /// <summary>XMZADD 20260828 在普通和最大化状态间切换主窗口。</summary>
        private void MaximizeWindowButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleWindowState();
        }

        /// <summary>XMZADD 20260828 关闭自绘标题栏所属的主窗口。</summary>
        private void CloseWindowButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        /// <summary>XMZADD 20260828 统一标题栏双击和最大化按钮的窗口状态规则。</summary>
        private void ToggleWindowState()
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        /// <summary>XMZADD 20260828 仅验证选中 EOS 数据库连接，不在此步骤扫描结构或项目源码。</summary>
        private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            var profile = ProfileCombo.SelectedItem as ConnectionProfile;
            if (profile == null)
            {
                ManageConnectionsButton_Click(sender, e);
                return;
            }

            try
            {
                string databaseName = await _connectionProbe.TestAsync(profile);
                _viewModel.MarkConnectionValidated(profile, databaseName);
            }
            catch (System.Exception ex)
            {
                _viewModel.MarkConnectionFailed(profile);
                MessageBox.Show(this, "连接 EOS 数据库失败：\r\n" + ex.Message, "连接失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>XMZADD 20260901 手工更新仅同步 GitHub 共享字典并刷新同一本地规范缓存，不访问数据库或源码。</summary>
        private async void UpdateDictionaryButton_Click(object sender, RoutedEventArgs e)
        {
            await StartManualSyncAsync();
        }

        /// <summary>XMZADD 20260901 在窗口生命周期令牌可用后启动唯一的手工同步任务，避免重复点击并发覆盖本地快照。</summary>
        private Task StartManualSyncAsync()
        {
            if (_isClosed)
            {
                return Task.FromResult(0);
            }
            if (_startupCancellation == null)
            {
                _viewModel.SyncStatusText = "正在初始化，请稍后更新";
                return Task.FromResult(0);
            }
            if (_syncCoordinator == null)
            {
                _viewModel.SyncStatusText = "仓库未配置";
                _viewModel.IsOffline = true;
                return Task.FromResult(0);
            }
            if (_manualSyncTask != null && !_manualSyncTask.IsCompleted)
            {
                return _manualSyncTask;
            }

            CancellationToken cancellationToken = _startupCancellation.Token;
            _manualSyncTask = SynchronizeAndRefreshAsync(true, cancellationToken);
            return _manualSyncTask;
        }

        /// <summary>XMZADD 20260901 统一执行启动、手工和定时 GitHub 同步，仅在首次加载或远端快照变化后重新应用规范缓存。</summary>
        private async Task<bool> SynchronizeAndRefreshAsync(bool manual, CancellationToken cancellationToken)
        {
            if (_syncCoordinator == null)
            {
                _viewModel.SyncStatusText = "仓库未配置";
                _viewModel.IsOffline = true;
                return false;
            }

            System.Windows.Threading.Dispatcher uiDispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            Action<DictionarySyncStage> reportStage = delegate(DictionarySyncStage stage)
            {
                string stageText = GetSyncStageText(stage);
                uiDispatcher.BeginInvoke(new Action(delegate
                {
                    _viewModel.SyncStatusText = stageText;
                }));
            };
            bool synchronizationLockTaken = false;
            SnapshotData synchronizedSnapshot = null;
            try
            {
                // 上传、获取、恢复和界面应用必须按完整流水线串行，避免较旧快照在较新快照之后覆盖界面。
                await _synchronizationLock.WaitAsync(cancellationToken);
                synchronizationLockTaken = true;
                // 快照解压、校验与本地持久化放到后台线程，避免完整字典更新期间阻塞界面操作。
                await Task.Run(async delegate
                {
                    if (manual)
                    {
                        synchronizedSnapshot = await _syncCoordinator.SyncNowAsync(cancellationToken, reportStage).ConfigureAwait(false);
                    }
                    else
                    {
                        synchronizedSnapshot = await _syncCoordinator.SyncAsync(cancellationToken, reportStage).ConfigureAwait(false);
                    }
                }, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                bool restored = true;
                if (synchronizedSnapshot != null)
                {
                    // 新修订已落盘但界面应用仍可能失败，先标记未恢复才能让下一轮同修订继续自愈。
                    _hasRestoredSnapshot = false;
                    restored = await _viewModel.ApplySynchronizedSnapshotAsync(synchronizedSnapshot, cancellationToken);
                    if (restored)
                    {
                        _hasRestoredSnapshot = true;
                    }
                }
                else if (!_hasRestoredSnapshot)
                {
                    // 启动时即使远端修订未变化也必须恢复一次；后续五分钟轮询保持当前对象图不动。
                    restored = await _viewModel.RestoreMostRecentSnapshotAsync(cancellationToken);
                    if (restored)
                    {
                        _hasRestoredSnapshot = true;
                    }
                }
                _viewModel.SyncStatusText = _viewModel.PendingApplyCount > 0 ? "等待远端生效" : "获取完成";
                _viewModel.IsOffline = false;
                return restored;
            }
            catch (OperationCanceledException)
            {
                // 关闭窗口取消同步属于正常退出，不覆盖用户最后看到的有效状态。
                return false;
            }
            catch (GitHubDictionaryClientException exception)
            {
                _viewModel.SyncStatusText = GetSafeSyncFailureText(exception.Kind);
                _viewModel.IsOffline = true;
                return false;
            }
            catch (InvalidDataException)
            {
                _viewModel.SyncStatusText = "远程字典校验失败，继续使用本地缓存";
                _viewModel.IsOffline = true;
                return false;
            }
            catch (IOException)
            {
                _viewModel.SyncStatusText = "本地缓存暂时不可用";
                _viewModel.IsOffline = true;
                return false;
            }
            catch (InvalidOperationException)
            {
                _viewModel.SyncStatusText = "同步暂时失败，继续使用本地缓存";
                _viewModel.IsOffline = true;
                return false;
            }
            catch (Exception exception)
            {
                if (exception is OutOfMemoryException || exception is StackOverflowException)
                {
                    throw;
                }
                _viewModel.SyncStatusText = "同步暂时失败，继续使用本地缓存";
                _viewModel.IsOffline = true;
                return false;
            }
            finally
            {
                if (synchronizationLockTaken)
                {
                    try
                    {
                        _viewModel.RefreshLocalSyncStatus();
                    }
                    catch (Exception exception)
                    {
                        if (exception is OutOfMemoryException || exception is StackOverflowException)
                        {
                            throw;
                        }
                        _viewModel.SyncStatusText = "本地缓存暂时不可用";
                        _viewModel.IsOffline = true;
                    }
                    finally
                    {
                        _synchronizationLock.Release();
                    }
                }
            }
        }

        /// <summary>XMZADD 20260903 将同步器阶段转换为用户可直接理解的上传或获取状态。</summary>
        private static string GetSyncStageText(DictionarySyncStage stage)
        {
            return stage == DictionarySyncStage.UploadingChanges
                ? "正在上传修改"
                : "正在获取远程字典";
        }

        /// <summary>XMZADD 20260901 每五分钟仅通过协调器检查远程清单，窗口关闭后由取消令牌立即结束循环。</summary>
        private async Task RunAutomaticSyncLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(AutomaticSyncIntervalMinutes), cancellationToken);
                    await SynchronizeAndRefreshAsync(false, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        /// <summary>XMZADD 20260901 将 GitHub 同步错误分类转换为不包含 Token、URL 或响应正文的中文状态。</summary>
        private static string GetSafeSyncFailureText(GitHubDictionaryErrorKind kind)
        {
            if (kind == GitHubDictionaryErrorKind.Authentication)
            {
                return "GitHub 身份验证失败，请检查同步设置";
            }
            if (kind == GitHubDictionaryErrorKind.RateLimited)
            {
                return "GitHub 请求受限，稍后自动重试";
            }
            if (kind == GitHubDictionaryErrorKind.Configuration)
            {
                return "仓库未配置";
            }
            if (kind == GitHubDictionaryErrorKind.NotFound)
            {
                return "共享字典暂时不可用";
            }
            return "网络不可用，继续使用本地缓存";
        }

        /// <summary>XMZADD 20260828 统一执行完整结构加载并向用户呈现可操作的失败信息。</summary>
        private async System.Threading.Tasks.Task LoadProfile(ConnectionProfile profile)
        {
            if (_viewModel.IsBusy)
            {
                return;
            }

            CancelAiInference();
            _loadCancellation = new CancellationTokenSource();
            CancelLoadButton.IsEnabled = true;
            CancelLoadButton.Content = "取消加载";
            try
            {
                await _viewModel.LoadProfileAsync(profile, _loadCancellation.Token);
                StartAutomaticAiInference();
            }
            catch (System.OperationCanceledException)
            {
                // 用户主动取消属于正常流程，状态栏已经说明上一次结构仍被保留。
            }
            catch (System.Exception ex)
            {
                MessageBox.Show(this, "连接或读取 EOS 结构失败：\r\n" + ex.Message, "连接失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _loadCancellation.Dispose();
                _loadCancellation = null;
            }
        }

        /// <summary>XMZADD 20260831 在真实快照加载完成后启动默认 AI 的后台推测批次，避免用户逐条手工发起分析。</summary>
        private async void StartAutomaticAiInference()
        {
            CancelAiInference();
            _aiCancellation = new CancellationTokenSource();
            try
            {
                int completedCount = await _viewModel.RunAutomaticAiInferenceAsync(_aiCancellation.Token);
                _viewModel.RefreshAfterAiInference(completedCount);
            }
            catch (System.OperationCanceledException)
            {
                // 用户关闭窗口或刷新结构时停止旧 AI 队列，已完成结果仍保存在本机。
            }
            catch (System.Exception)
            {
                // 外部 AI 接口失败不能导致数据字典程序退出，用户可在 AI 设置中单独测试连接。
            }
        }

        /// <summary>XMZADD 20260831 停止当前 AI 队列，避免刷新结构或关闭窗口后旧任务继续占用接口。</summary>
        private void CancelAiInference()
        {
            if (_aiCancellation == null)
            {
                return;
            }
            _aiCancellation.Cancel();
            _aiCancellation.Dispose();
            _aiCancellation = null;
        }

        /// <summary>XMZADD 20260831 在主窗口关闭时取消恢复、加载和 AI 后台任务，避免残留进程继续占用内存。</summary>
        protected override void OnClosed(System.EventArgs e)
        {
            _isClosed = true;
            CancellationTokenSource startupCancellation = _startupCancellation;
            if (startupCancellation != null)
            {
                startupCancellation.Cancel();
                _startupCancellation = null;
                _backgroundCleanupTask = DisposeStartupCancellationAsync(
                    startupCancellation,
                    _startupTask,
                    _automaticSyncTask,
                    _manualSyncTask);
            }
            if (_loadCancellation != null)
            {
                _loadCancellation.Cancel();
            }
            CancelAiInference();
            base.OnClosed(e);
        }

        /// <summary>XMZADD 20260901 等待启动、自动和手工同步任务结束后再释放令牌源，避免关闭过程中访问已释放令牌。</summary>
        private static async Task DisposeStartupCancellationAsync(
            CancellationTokenSource cancellationSource,
            Task startupTask,
            Task automaticSyncTask,
            Task manualSyncTask)
        {
            try
            {
                await ObserveBackgroundTaskAsync(startupTask).ConfigureAwait(false);
                await ObserveBackgroundTaskAsync(automaticSyncTask).ConfigureAwait(false);
                await ObserveBackgroundTaskAsync(manualSyncTask).ConfigureAwait(false);
            }
            finally
            {
                cancellationSource.Dispose();
            }
        }

        /// <summary>XMZADD 20260901 观察关闭时的后台任务并吸收正常取消，防止清理任务产生未观察异常。</summary>
        private static async Task ObserveBackgroundTaskAsync(Task task)
        {
            if (task == null)
            {
                return;
            }
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 窗口关闭主动取消属于正常生命周期，不需要错误状态。
            }
        }

        /// <summary>XMZADD 20260828 请求停止当前全量结构和源码扫描，并防止用户重复提交取消。</summary>
        private void CancelLoadButton_Click(object sender, RoutedEventArgs e)
        {
            if (_loadCancellation == null)
            {
                return;
            }

            CancelLoadButton.IsEnabled = false;
            CancelLoadButton.Content = "正在取消…";
            _loadCancellation.Cancel();
        }

        private void ManageConnectionsButton_Click(object sender, RoutedEventArgs e)
        {
            var window = new ConnectionManagerWindow { Owner = this };
            window.ShowDialog();
            _viewModel.LoadProfiles();
            if (window.SelectedProfile != null)
            {
                for (int i = 0; i < ProfileCombo.Items.Count; i++)
                {
                    var profile = ProfileCombo.Items[i] as ConnectionProfile;
                    if (profile != null && profile.Id == window.SelectedProfile.Id)
                    {
                        ProfileCombo.SelectedIndex = i;
                        break;
                    }
                }
            }
        }

        private void SourceButton_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new WinForms.FolderBrowserDialog())
            {
                dialog.Description = "选择 EOS 项目源码目录";
                dialog.SelectedPath = _viewModel.SourceRoot;
                if (dialog.ShowDialog() == WinForms.DialogResult.OK)
                {
                    _viewModel.SetSourceRoot(dialog.SelectedPath);
                }
            }
        }

        /// <summary>XMZADD 20260831 打开 AI 服务商配置窗口，供用户维护默认模型、服务地址和本机加密密钥。</summary>
        private void AiSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            var window = new AiProviderManagerWindow { Owner = this };
            window.ShowDialog();
        }

        /// <summary>XMZADD 20260831 编辑当前选中字段或表的本地业务字典信息，不修改任何 EOS 数据库结构或数据。</summary>
        private void EditDictionaryButton_Click(object sender, RoutedEventArgs e)
        {
            FieldMetadata field = _viewModel.SelectedField == null
                ? null
                : _viewModel.SelectedField.Source;
            OpenDictionaryEditor(field);
        }

        /// <summary>XMZADD 20260831 双击字段时直接打开本地维护窗口，方便新成员在查看证据后补充业务说明。</summary>
        private void FieldsGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var field = FieldsGrid.SelectedItem as FieldDisplayModel;
            if (field != null)
            {
                OpenDictionaryEditor(field.Metadata);
            }
        }

        /// <summary>XMZADD 20260831 统一打开表或字段人工维护窗口，并在保存后刷新当前列表展示人工优先级结果。</summary>
        private async void OpenDictionaryEditor(FieldMetadata field)
        {
            TableDisplayModel selectedTable = _viewModel.SelectedTable;
            if (selectedTable == null)
            {
                MessageBox.Show(this, "请先选择一个表或字段。", "尚未选择对象", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var window = new DictionaryEditWindow(
                AppPathService.GetLocalDatabasePath(),
                selectedTable.Metadata.ScopeKey,
                selectedTable.Metadata,
                field)
            {
                Owner = this
            };
            if (window.ShowDialog() == true)
            {
                string selectedSchemaName = selectedTable.Metadata.SchemaName;
                string selectedObjectName = selectedTable.ObjectName;
                InvalidateProjectRelatedObjectCache();
                TablesGrid.Items.Refresh();
                FieldsGrid.Items.Refresh();
                ResourceTree.Items.Refresh();
                _viewModel.NavigateToTable(selectedSchemaName, selectedObjectName);
                // 人工保存后立即提交共享事件，五分钟定时任务只负责网络异常时的兜底重试。
                await StartManualSyncAsync();
            }
        }

        /// <summary>XMZADD 20260831 在人工字典保存后通知项目范围缓存失效，保证切回默认对象范围立即应用人工覆盖。</summary>
        private void InvalidateProjectRelatedObjectCache()
        {
            _viewModel.NotifyLocalDictionarySaved();
        }

        /// <summary>XMZADD 20260831 监听指定表格的列宽变化，以便把用户调整后的阅读布局保存到本机。</summary>
        private void RegisterColumnWidthPersistence(DataGrid grid)
        {
            for (int i = 0; i < grid.Columns.Count; i++)
            {
                DependencyPropertyDescriptor descriptor = DependencyPropertyDescriptor.FromProperty(DataGridColumn.WidthProperty, typeof(DataGridColumn));
                descriptor.AddValueChanged(grid.Columns[i], DataGridColumnWidth_Changed);
            }
        }

        /// <summary>XMZADD 20260831 保存用户拖拽后的列宽，避免长文本查看所需的布局在重启后丢失。</summary>
        private void DataGridColumnWidth_Changed(object sender, System.EventArgs e)
        {
            if (_isRestoringColumnLayout)
            {
                return;
            }

            DataGridColumn column = sender as DataGridColumn;
            if (column == null || !column.Width.IsAbsolute || column.Width.Value <= 0D)
            {
                return;
            }

            SaveColumnWidth(TablesGrid, column);
            SaveColumnWidth(FieldsGrid, column);
            SaveColumnWidth(RelationsGrid, column);
            SaveColumnWidth(EvidenceGrid, column);
        }

        /// <summary>XMZADD 20260831 仅为实际所属表格记录列宽，避免同一通知重复写入无关页面的布局。</summary>
        private void SaveColumnWidth(DataGrid grid, DataGridColumn column)
        {
            if (!grid.Columns.Contains(column))
            {
                return;
            }

            _columnLayoutStore.Save(grid.Name, GetColumnKey(grid, column), column.Width.Value);
        }

        /// <summary>XMZADD 20260831 恢复当前数据表已保存的列宽，使用户无需每次重新拖拽查看完整内容。</summary>
        private void RestoreColumnWidths(DataGrid grid)
        {
            _isRestoringColumnLayout = true;
            try
            {
                System.Collections.Generic.IDictionary<string, double> widths = _columnLayoutStore.Load(grid.Name);
                for (int i = 0; i < grid.Columns.Count; i++)
                {
                    double width;
                    if (widths.TryGetValue(GetColumnKey(grid, grid.Columns[i]), out width))
                    {
                        grid.Columns[i].Width = width;
                    }
                }
            }
            finally
            {
                _isRestoringColumnLayout = false;
            }
        }

        /// <summary>XMZADD 20260831 为列宽存储生成稳定键，优先使用绑定属性以抵御列顺序调整。</summary>
        private static string GetColumnKey(DataGrid grid, DataGridColumn column)
        {
            return string.IsNullOrWhiteSpace(column.SortMemberPath)
                ? "Column" + column.DisplayIndex
                : column.SortMemberPath;
        }

        /// <summary>XMZADD 20260831 将资源目录搜索文本同步到当前对象范围，保持显示全部切换后的筛选一致。</summary>
        private void TableSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _viewModel.SearchTables(TableSearchBox.Text);
        }

        /// <summary>XMZADD 20260828 将表目录列头输入转交给对应列的模糊筛选规则。</summary>
        private void TableColumnFilter_TextChanged(object sender, TextChangedEventArgs e)
        {
            TextBox textBox = sender as TextBox;
            if (_viewModel != null && textBox != null)
            {
                _viewModel.SetTableColumnFilter(textBox.Tag as string, textBox.Text);
            }
        }

        /// <summary>XMZADD 20260828 将字段字典列头输入转交给对应列的模糊筛选规则。</summary>
        private void FieldColumnFilter_TextChanged(object sender, TextChangedEventArgs e)
        {
            TextBox textBox = sender as TextBox;
            if (_viewModel != null && textBox != null)
            {
                _viewModel.SetFieldColumnFilter(textBox.Tag as string, textBox.Text);
            }
        }

        /// <summary>XMZADD 20260828 将关联关系列头输入转交给对应列的模糊筛选规则。</summary>
        private void RelationColumnFilter_TextChanged(object sender, TextChangedEventArgs e)
        {
            TextBox textBox = sender as TextBox;
            if (_viewModel != null && textBox != null)
            {
                _viewModel.SetRelationColumnFilter(textBox.Tag as string, textBox.Text);
            }
        }

        /// <summary>XMZADD 20260828 响应用户主题切换并同步更新下一次操作提示。</summary>
        private void ThemeButton_Click(object sender, RoutedEventArgs e)
        {
            _themeService.Toggle();
            UpdateThemeButtonToolTip();
        }

        /// <summary>XMZADD 20260831 从中间对象表格打开详情，确保字段、关系和证据按当前行同步刷新。</summary>
        private void TablesGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var row = TablesGrid.SelectedItem as TableDisplayModel;
            if (row != null)
            {
                _viewModel.SelectedTable = row;
                OpenDictionaryEditor(null);
            }
        }

        /// <summary>XMZADD 20260901 单击资源树中的具体数据表后按架构和表名精确定位，并同步完整详情与整行焦点。</summary>
        private void ResourceTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            TableDisplayModel row = e == null ? null : e.NewValue as TableDisplayModel;
            if (row == null || row.Metadata == null || string.IsNullOrWhiteSpace(row.ObjectName))
            {
                return;
            }

            ClearTableFilterInputs();
            ClearDetailFilterInputs();
            _viewModel.NavigateToTable(row.Metadata.SchemaName, row.ObjectName);
            FieldsGrid.SelectedItem = null;
            if (_viewModel.SelectedTable != null)
            {
                TablesGrid.SelectedItem = _viewModel.SelectedTable;
                TablesGrid.ScrollIntoView(_viewModel.SelectedTable);
                TablesGrid.Focus();
            }
        }

        /// <summary>XMZADD 20260901 打开当前用户的 GitHub 同步设置入口，仓库未配置时仍允许查看安全降级状态。</summary>
        private async void SyncSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            var window = new SyncSettingsWindow(
                _repositoryOptions,
                _gitHubCredentialStore,
                _gitHubDictionaryClient)
            {
                Owner = this
            };
            window.ShowDialog();
            CancellationTokenSource startupCancellation = _startupCancellation;
            if (!_isClosed && startupCancellation != null && !startupCancellation.IsCancellationRequested)
            {
                try
                {
                    await RefreshStructureMaintenanceAccessAsync(startupCancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    // 主窗口关闭时刷新权限取消属于正常退出，不再更新界面。
                }
            }
        }

        /// <summary>XMZADD 20260901 为已授权发布者组合本机只读扫描依赖并打开真实结构维护窗口。</summary>
        private void StructureMaintenanceButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_viewModel.CanOpenStructureMaintenance || _gitHubDictionaryClient == null)
            {
                return;
            }

            string databasePath = AppPathService.GetLocalDatabasePath();
            var snapshotStore = new SnapshotStore(databasePath);
            var metadataReader = new SqlServerMetadataReader();
            var sourceAnalyzer = new EosSourceAnalyzer();
            var publishService = new StructurePublishService(
                _gitHubDictionaryClient,
                delegate(string scopeKey)
                {
                    SnapshotData snapshot;
                    return snapshotStore.TryLoadLatest(scopeKey, out snapshot) ? snapshot : new SnapshotData();
                },
                delegate(ConnectionProfile profile, CancellationToken cancellationToken)
                {
                    return metadataReader.Read(profile, cancellationToken);
                },
                delegate(string sourceRoot, CancellationToken cancellationToken)
                {
                    return sourceAnalyzer.Analyze(sourceRoot, cancellationToken);
                },
                // 结构扫描只聚合本地事实；AI 由主窗口独立后台队列补全，不能阻塞正式库结构预览。
                new StructureNameInferenceService(null, false));
            var window = new StructureMaintenanceWindow(
                publishService,
                new ConnectionProfileStore(databasePath),
                _repositoryOptions.ScopeKey,
                _viewModel.SourceRoot,
                string.Empty)
            {
                Owner = this
            };
            window.ShowDialog();
        }

        /// <summary>XMZADD 20260831 根据关系方向跳转目标对象，必要时由视图模型展开全部对象以显示二跳依赖。</summary>
        private void RelationsGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var row = _viewModel.SelectedRelation;
            if (row == null)
            {
                return;
            }

            TableDisplayModel selectedTable = _viewModel.SelectedTable;
            // 物理表名精确比较可避免子表名称包含父表前缀时跳转到错误方向。
            bool selectedIsParent = selectedTable != null && selectedTable.Metadata != null &&
                string.Equals(selectedTable.Metadata.ObjectName, row.ParentTableName, System.StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(row.Metadata.ParentSchemaName) ||
                 string.Equals(selectedTable.Metadata.SchemaName, row.Metadata.ParentSchemaName, StringComparison.OrdinalIgnoreCase));
            string target = selectedIsParent ? row.ChildTableName : row.ParentTableName;
            string targetSchema = selectedIsParent ? row.Metadata.ChildSchemaName : row.Metadata.ParentSchemaName;
            ClearTableFilterInputs();
            _viewModel.NavigateToTable(targetSchema, target);
            if (_viewModel.SelectedTable != null)
            {
                TablesGrid.ScrollIntoView(_viewModel.SelectedTable);
            }
        }

        /// <summary>XMZADD 20260831 双击列头两侧十像素调整区时按标题和当前可见内容自动适配列宽。</summary>
        private void DataGrid_PreviewMouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            DataGrid grid = sender as DataGrid;
            DataGridColumnHeader header = FindVisualAncestor<DataGridColumnHeader>(e.OriginalSource as DependencyObject);
            if (grid == null || header == null || header.Column == null)
            {
                return;
            }

            System.Windows.Point position = e.GetPosition(header);
            if (position.X > 10D && header.ActualWidth - position.X > 10D)
            {
                return;
            }

            AutoSizeColumnForVisibleContent(grid, header.Column);
            e.Handled = true;
        }

        /// <summary>XMZADD 20260831 依次测量当前视图的单元格和列头，保留两者中较宽的阅读空间。</summary>
        private static void AutoSizeColumnForVisibleContent(DataGrid grid, DataGridColumn column)
        {
            column.Width = DataGridLength.SizeToCells;
            grid.UpdateLayout();
            double cellsWidth = column.ActualWidth;
            column.Width = DataGridLength.SizeToHeader;
            grid.UpdateLayout();
            double headerWidth = column.ActualWidth;
            column.Width = new DataGridLength(System.Math.Max(cellsWidth, headerWidth));
        }

        /// <summary>XMZADD 20260831 右键单元格时同步设置当前单元格，使复制动作始终针对用户刚点击的位置。</summary>
        private void DataGrid_PreviewMouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            DataGrid grid = sender as DataGrid;
            DataGridCell cell = FindVisualAncestor<DataGridCell>(e.OriginalSource as DependencyObject);
            if (grid == null || cell == null)
            {
                return;
            }
            grid.CurrentCell = new DataGridCellInfo(cell.DataContext, cell.Column);
            cell.IsSelected = true;
            cell.Focus();
        }

        /// <summary>XMZADD 20260831 使用 Ctrl+C 复制当前单元格，避免表格默认复制整行或对象文本。</summary>
        private void DataGrid_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            DataGrid grid = sender as DataGrid;
            if (grid != null && e.Key == System.Windows.Input.Key.C &&
                System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control &&
                CopyCurrentCellToClipboard(grid))
            {
                e.Handled = true;
            }
        }

        /// <summary>XMZADD 20260831 将右键菜单所属表格的当前单元格纯文本复制到 Windows 剪贴板。</summary>
        private void CopyCellMenuItem_Click(object sender, RoutedEventArgs e)
        {
            MenuItem item = sender as MenuItem;
            ContextMenu menu = item == null ? null : item.Parent as ContextMenu;
            DataGrid grid = menu == null ? null : menu.PlacementTarget as DataGrid;
            if (grid == null || grid.CurrentCell.Column == null || grid.CurrentCell.Item == null)
            {
                return;
            }

            CopyCurrentCellToClipboard(grid);
        }

        /// <summary>XMZADD 20260831 将当前单元格的显示文本写入剪贴板，保持右键和快捷键复制结果一致。</summary>
        private bool CopyCurrentCellToClipboard(DataGrid grid)
        {
            if (grid == null || grid.CurrentCell.Column == null || grid.CurrentCell.Item == null)
            {
                return false;
            }
            FrameworkElement content = grid.CurrentCell.Column.GetCellContent(grid.CurrentCell.Item);
            string text = FindVisualText(content);
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                // 其他程序暂占剪贴板时不应影响字典浏览和编辑，用户可再次复制。
                return false;
            }
        }

        /// <summary>XMZADD 20260831 从表格单元格模板中读取可见文本，兼容普通文本列和状态徽标列。</summary>
        private static string FindVisualText(DependencyObject parent)
        {
            if (parent == null)
            {
                return string.Empty;
            }
            TextBlock textBlock = parent as TextBlock;
            if (textBlock != null)
            {
                return textBlock.Text ?? string.Empty;
            }
            TextBox textBox = parent as TextBox;
            if (textBox != null)
            {
                return textBox.Text ?? string.Empty;
            }
            int childCount = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < childCount; i++)
            {
                string text = FindVisualText(VisualTreeHelper.GetChild(parent, i));
                if (!string.IsNullOrEmpty(text))
                {
                    return text;
                }
            }
            return string.Empty;
        }

        /// <summary>XMZADD 20260831 沿可视树向上查找指定控件，支持从单元格内部任意元素触发右键选择。</summary>
        private static T FindVisualAncestor<T>(DependencyObject child) where T : DependencyObject
        {
            DependencyObject current = child;
            while (current != null)
            {
                T result = current as T;
                if (result != null)
                {
                    return result;
                }
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        /// <summary>XMZADD 20260831 关联跳转前同步清空可见表筛选，避免筛选文本与结果状态不一致。</summary>
        private void ClearTableFilterInputs()
        {
            TableSearchBox.Clear();
            // 表列会随字典能力扩展，按列头统一清理可避免新增筛选残留并阻断关系跳转。
            ClearColumnFilterTextBoxes(TablesGrid);
        }

        /// <summary>XMZADD 20260901 清空字段和关系表头筛选，使资源树定位后展示目标表的完整字段与关联信息。</summary>
        private void ClearDetailFilterInputs()
        {
            ClearColumnFilterTextBoxes(FieldsGrid);
            ClearColumnFilterTextBoxes(RelationsGrid);
        }

        /// <summary>XMZADD 20260901 清除指定表格列头中的筛选输入，避免旧对象条件隐藏新对象详情。</summary>
        private static void ClearColumnFilterTextBoxes(DataGrid grid)
        {
            if (grid == null)
            {
                return;
            }
            for (int columnIndex = 0; columnIndex < grid.Columns.Count; columnIndex++)
            {
                Panel headerPanel = grid.Columns[columnIndex].Header as Panel;
                if (headerPanel == null)
                {
                    continue;
                }
                for (int childIndex = 0; childIndex < headerPanel.Children.Count; childIndex++)
                {
                    TextBox filter = headerPanel.Children[childIndex] as TextBox;
                    if (filter != null)
                    {
                        filter.Clear();
                    }
                }
            }
        }

        /// <summary>XMZADD 20260828 更新主题按钮说明，让用户明确点击后将切换到哪种主题。</summary>
        private void UpdateThemeButtonToolTip()
        {
            ThemeButton.ToolTip = _themeService.CurrentTheme == AppTheme.Dark ? "切换到浅色主题" : "切换到深色主题";
        }
    }
}
