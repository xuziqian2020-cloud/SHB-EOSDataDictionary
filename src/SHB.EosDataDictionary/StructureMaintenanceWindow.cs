using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SHB.EosDataDictionary.Models;
using SHB.EosDataDictionary.Services;
using WinForms = System.Windows.Forms;

namespace SHB.EosDataDictionary
{
    /// <summary>XMZADD 20260901 为授权发布者提供本机只读配置、结构扫描预览和确认发布窗口。</summary>
    public sealed class StructureMaintenanceWindow : Window
    {
        private readonly StructurePublishService _publishService;
        private readonly ConnectionProfileStore _profileStore;
        private readonly string _scopeKey;
        private readonly TextBox _serverBox;
        private readonly TextBox _databaseBox;
        private readonly TextBox _userNameBox;
        private readonly PasswordBox _passwordBox;
        private readonly ComboBox _authenticationBox;
        private readonly TextBox _sourceRootBox;
        private readonly TextBox _knowledgeBaseRootBox;
        private readonly DataGrid _previewGrid;
        private readonly TextBlock _statusText;
        private readonly Button _scanButton;
        private readonly Button _publishButton;
        private readonly Button _cancelButton;
        private readonly DispatcherTimer _scanElapsedTimer;
        private ConnectionProfile _existingProfile;
        private StructurePublishResult _preview;
        private CancellationTokenSource _operationCancellation;
        private DateTime _scanStartedAtUtc;
        private string _scanStageMessage;
        private bool _isPublishing;
        private bool _isClosing;

        /// <summary>XMZADD 20260901 组合结构发布服务与本机配置存储，且不把已保存密码回填到可见控件。</summary>
        public StructureMaintenanceWindow(
            StructurePublishService publishService,
            ConnectionProfileStore profileStore,
            string scopeKey,
            string defaultSourceRoot,
            string defaultKnowledgeBaseRoot)
        {
            _publishService = publishService ?? throw new ArgumentNullException("publishService");
            _profileStore = profileStore ?? throw new ArgumentNullException("profileStore");
            _scopeKey = scopeKey ?? string.Empty;
            Title = "结构维护";
            Width = 980D;
            Height = 720D;
            MinWidth = 820D;
            MinHeight = 600D;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI");
            Style = Application.Current == null ? null : Application.Current.TryFindResource(typeof(Window)) as Style;

            _serverBox = CreateTextBox();
            _databaseBox = CreateTextBox();
            _userNameBox = CreateTextBox();
            _passwordBox = new PasswordBox
            {
                MinWidth = 220D,
                Padding = new Thickness(10D, 0D, 10D, 0D),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            _authenticationBox = new ComboBox
            {
                MinWidth = 220D,
                Margin = new Thickness(0D, 2D, 0D, 2D),
                Padding = new Thickness(10D, 0D, 30D, 0D),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            _authenticationBox.Items.Add("SQL Server 身份验证");
            _authenticationBox.Items.Add("Windows 身份验证");
            _authenticationBox.SelectedIndex = 0;
            _sourceRootBox = CreateTextBox();
            _knowledgeBaseRootBox = CreateTextBox();
            _previewGrid = CreatePreviewGrid();
            _statusText = new TextBlock { Text = "请先完成本机只读配置并扫描预览。", TextWrapping = TextWrapping.Wrap };
            _scanButton = CreateButton("扫描预览", ScanButton_Click);
            _publishButton = CreateButton("确认发布", PublishButton_Click);
            _publishButton.IsEnabled = false;
            _cancelButton = CreateButton("取消", CancelButton_Click);
            _scanElapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1D) };
            _scanElapsedTimer.Tick += ScanElapsedTimer_Tick;

            Content = BuildContent();
            Closing += Window_Closing;
            LoadLocalConfiguration(defaultSourceRoot, defaultKnowledgeBaseRoot);
        }

        /// <summary>XMZADD 20260901 创建统一的本机配置文本输入框。</summary>
        private static TextBox CreateTextBox()
        {
            return new TextBox
            {
                MinWidth = 220D,
                Padding = new Thickness(10D, 0D, 10D, 0D),
                Margin = new Thickness(0D, 2D, 0D, 2D),
                VerticalContentAlignment = VerticalAlignment.Center
            };
        }

        /// <summary>XMZADD 20260901 创建具有明确业务动作文本的窗口按钮。</summary>
        private static Button CreateButton(string text, RoutedEventHandler clickHandler)
        {
            var button = new Button
            {
                Content = text,
                MinWidth = 92D,
                Padding = new Thickness(14D, 7D, 14D, 7D),
                Margin = new Thickness(8D, 0D, 0D, 0D)
            };
            button.Click += clickHandler;
            return button;
        }

        /// <summary>XMZADD 20260901 创建只展示结构差异摘要的预览表，禁止展示源码正文、密码和业务数据。</summary>
        private static DataGrid CreatePreviewGrid()
        {
            var grid = new DataGrid
            {
                AutoGenerateColumns = false,
                IsReadOnly = true,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                SelectionMode = DataGridSelectionMode.Single,
                SelectionUnit = DataGridSelectionUnit.Cell,
                ClipboardCopyMode = DataGridClipboardCopyMode.ExcludeHeader,
                Margin = new Thickness(0D, 8D, 0D, 8D),
                CellStyle = CreatePreviewCellStyle(),
                ColumnHeaderStyle = CreatePreviewHeaderStyle()
            };
            var copyCellItem = new MenuItem
            {
                Header = "复制单元格",
                InputGestureText = "Ctrl+C",
                Command = ApplicationCommands.Copy,
                CommandTarget = grid
            };
            grid.ContextMenu = new ContextMenu();
            grid.ContextMenu.Items.Add(copyCellItem);
            grid.PreviewMouseRightButtonDown += PreviewGrid_PreviewMouseRightButtonDown;
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = "变化类型",
                Binding = new Binding("ChangeType"),
                Width = new DataGridLength(130D)
            });
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = "表",
                Binding = new Binding("ObjectName"),
                Width = new DataGridLength(180D)
            });
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = "字段",
                Binding = new Binding("FieldName"),
                Width = new DataGridLength(160D)
            });
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = "说明",
                Binding = new Binding("Description"),
                Width = new DataGridLength(1D, DataGridLengthUnitType.Star)
            });
            return grid;
        }

        /// <summary>XMZADD 20260903 右键差异表时先选中鼠标所在单元格，确保复制菜单只复制用户当前看到的值。</summary>
        private static void PreviewGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            var grid = sender as DataGrid;
            var source = e.OriginalSource as DependencyObject;
            DataGridCell cell = FindVisualAncestor<DataGridCell>(source);
            if (grid == null || cell == null)
            {
                return;
            }

            grid.SelectedCells.Clear();
            grid.CurrentCell = new DataGridCellInfo(cell);
            cell.IsSelected = true;
            cell.Focus();
        }

        /// <summary>XMZADD 20260903 从差异表点击位置向上寻找所属控件，供单元格级右键复制定位使用。</summary>
        private static T FindVisualAncestor<T>(DependencyObject source) where T : DependencyObject
        {
            DependencyObject current = source;
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

        /// <summary>XMZADD 20260902 创建居中且四边一致留白的差异内容单元格样式，保证短字段与长说明的视觉对齐。</summary>
        private static Style CreatePreviewCellStyle()
        {
            Style themeStyle = Application.Current == null
                ? null
                : Application.Current.TryFindResource(typeof(DataGridCell)) as Style;
            // 局部对齐规则必须继承当前主题，否则显式 Style 会让暗色表格回退为系统白色外观。
            var style = new Style(typeof(DataGridCell), themeStyle);
            style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
            style.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10D)));
            return style;
        }

        /// <summary>XMZADD 20260902 创建与内容一致的差异表头样式，避免列标题和数据的对齐规则不一致。</summary>
        private static Style CreatePreviewHeaderStyle()
        {
            Style themeStyle = Application.Current == null
                ? null
                : Application.Current.TryFindResource(typeof(DataGridColumnHeader)) as Style;
            // 表头复用同一主题基线，确保背景、前景和边框随浅深色资源一起切换。
            var style = new Style(typeof(DataGridColumnHeader), themeStyle);
            style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
            style.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10D)));
            return style;
        }

        /// <summary>XMZADD 20260901 构建正式库配置、目录配置、差异预览和操作区的企业工具布局。</summary>
        private Grid BuildContent()
        {
            var root = new Grid { Margin = new Thickness(18D) };
            root.SetResourceReference(Panel.BackgroundProperty, "App.WindowBackgroundBrush");
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1D, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var connectionGroup = new GroupBox { Header = "EOS 正式库（仅结构只读）", Padding = new Thickness(12D) };
            ApplyThemeToGroupBox(connectionGroup);
            var connectionGrid = CreateFormGrid(5);
            AddFormRow(connectionGrid, 0, "服务器", _serverBox, null);
            AddFormRow(connectionGrid, 1, "数据库", _databaseBox, null);
            AddFormRow(connectionGrid, 2, "身份验证", _authenticationBox, null);
            AddFormRow(connectionGrid, 3, "账号", _userNameBox, null);
            AddFormRow(connectionGrid, 4, "密码", _passwordBox, null);
            connectionGroup.Content = connectionGrid;
            root.Children.Add(connectionGroup);

            var pathsGroup = new GroupBox { Header = "本机证据目录", Padding = new Thickness(12D), Margin = new Thickness(0D, 10D, 0D, 0D) };
            ApplyThemeToGroupBox(pathsGroup);
            Grid.SetRow(pathsGroup, 1);
            var pathsGrid = CreateFormGrid(2);
            AddFormRow(pathsGrid, 0, "EOS 源码", _sourceRootBox, CreateButton("选择", BrowseSourceButton_Click));
            AddFormRow(pathsGrid, 1, "EOS 知识库（可选）", _knowledgeBaseRootBox, CreateButton("选择", BrowseKnowledgeButton_Click));
            pathsGroup.Content = pathsGrid;
            root.Children.Add(pathsGroup);

            var previewPanel = new DockPanel { Margin = new Thickness(0D, 10D, 0D, 0D) };
            Grid.SetRow(previewPanel, 2);
            var previewTitle = new TextBlock { Text = "结构差异预览", FontWeight = FontWeights.Bold, FontSize = 14D };
            DockPanel.SetDock(previewTitle, Dock.Top);
            previewPanel.Children.Add(previewTitle);
            previewPanel.Children.Add(_previewGrid);
            root.Children.Add(previewPanel);

            var actionGrid = new Grid { Margin = new Thickness(0D, 8D, 0D, 0D) };
            actionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1D, GridUnitType.Star) });
            actionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            actionGrid.Children.Add(_statusText);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            Grid.SetColumn(buttons, 1);
            buttons.Children.Add(_scanButton);
            buttons.Children.Add(_publishButton);
            buttons.Children.Add(_cancelButton);
            actionGrid.Children.Add(buttons);
            Grid.SetRow(actionGrid, 3);
            root.Children.Add(actionGrid);
            return root;
        }

        /// <summary>XMZADD 20260902 为代码创建的分组框绑定动态主题资源，避免二级窗口出现浅色系统底色。</summary>
        private static void ApplyThemeToGroupBox(GroupBox groupBox)
        {
            groupBox.SetResourceReference(Control.BackgroundProperty, "App.PanelBackgroundBrush");
            groupBox.SetResourceReference(Control.ForegroundProperty, "App.TextPrimaryBrush");
            groupBox.SetResourceReference(Control.BorderBrushProperty, "App.BorderBrush");
        }

        /// <summary>XMZADD 20260901 创建固定标签列和可伸缩输入列的配置表格。</summary>
        private static Grid CreateFormGrid(int rowCount)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(145D) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1D, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }
            return grid;
        }

        /// <summary>XMZADD 20260901 把本机配置标签、输入控件和可选选择按钮放入同一业务行。</summary>
        private static void AddFormRow(Grid grid, int rowIndex, string labelText, Control input, Button browseButton)
        {
            var label = new TextBlock { Text = labelText, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(label, rowIndex);
            grid.Children.Add(label);
            Grid.SetRow(input, rowIndex);
            Grid.SetColumn(input, 1);
            grid.Children.Add(input);
            if (browseButton != null)
            {
                Grid.SetRow(browseButton, rowIndex);
                Grid.SetColumn(browseButton, 2);
                grid.Children.Add(browseButton);
            }
        }

        /// <summary>XMZADD 20260901 加载本机正式库和证据目录，已保存密码只保留在内存配置对象中而不回显。</summary>
        private void LoadLocalConfiguration(string defaultSourceRoot, string defaultKnowledgeBaseRoot)
        {
            if (_profileStore.TryLoadDefaultEosProductionProfile(out _existingProfile))
            {
                _serverBox.Text = _existingProfile.Server ?? string.Empty;
                _databaseBox.Text = _existingProfile.Database ?? string.Empty;
                _userNameBox.Text = _existingProfile.UserName ?? string.Empty;
                _authenticationBox.SelectedIndex = _existingProfile.AuthenticationMode == AuthenticationMode.Windows ? 1 : 0;
            }

            string sourceRoot;
            string knowledgeBaseRoot;
            _profileStore.LoadStructurePaths(out sourceRoot, out knowledgeBaseRoot);
            _sourceRootBox.Text = string.IsNullOrWhiteSpace(sourceRoot) ? (defaultSourceRoot ?? string.Empty) : sourceRoot;
            _knowledgeBaseRootBox.Text = string.IsNullOrWhiteSpace(knowledgeBaseRoot)
                ? (defaultKnowledgeBaseRoot ?? string.Empty)
                : knowledgeBaseRoot;
        }

        /// <summary>XMZADD 20260901 从当前输入生成强制只读配置，空密码沿用本机 DPAPI 已保存密码。</summary>
        private ConnectionProfile SaveLocalConfiguration()
        {
            var profile = new ConnectionProfile
            {
                Id = _existingProfile == null ? Guid.NewGuid().ToString("N") : _existingProfile.Id,
                Name = ConnectionProfileStore.DefaultEosProductionProfileName,
                Server = (_serverBox.Text ?? string.Empty).Trim(),
                Port = _existingProfile == null ? 1433 : _existingProfile.Port,
                Database = (_databaseBox.Text ?? string.Empty).Trim(),
                AuthenticationMode = _authenticationBox.SelectedIndex == 1 ? AuthenticationMode.Windows : AuthenticationMode.SqlServer,
                UserName = (_userNameBox.Text ?? string.Empty).Trim(),
                Password = _passwordBox.Password.Length == 0 && _existingProfile != null
                    ? _existingProfile.Password
                    : _passwordBox.Password,
                TrustServerCertificate = _existingProfile == null || _existingProfile.TrustServerCertificate,
                IsReadOnly = true
            };

            if (_existingProfile == null)
            {
                _existingProfile = _profileStore.SaveDefaultEosProductionProfile(profile);
            }
            else
            {
                _profileStore.Save(profile);
                _profileStore.TryLoadDefaultEosProductionProfile(out _existingProfile);
            }
            _profileStore.SaveStructurePaths(_sourceRootBox.Text, _knowledgeBaseRootBox.Text);
            _passwordBox.Clear();
            return _existingProfile;
        }

        /// <summary>XMZADD 20260901 扫描只读元数据和受限证据并显示差异，扫描阶段绝不创建远程 Issue。</summary>
        private async void ScanButton_Click(object sender, RoutedEventArgs e)
        {
            if (_operationCancellation != null)
            {
                return;
            }

            ConnectionProfile profile;
            try
            {
                profile = SaveLocalConfiguration();
            }
            catch (OutOfMemoryException exception)
            {
                _statusText.Text = GetOutOfMemoryFailureText(exception);
                return;
            }
            catch (Exception)
            {
                _statusText.Text = "本机配置无法保存，请检查输入和本地目录权限。";
                return;
            }

            var request = new StructurePublishRequest
            {
                ScopeKey = _scopeKey,
                Profile = profile,
                SourceRoot = (_sourceRootBox.Text ?? string.Empty).Trim(),
                KnowledgeBaseRoot = (_knowledgeBaseRootBox.Text ?? string.Empty).Trim()
            };
            var operationCancellation = new CancellationTokenSource();
            _operationCancellation = operationCancellation;
            StartScanProgress();
            SetBusy(true, null);
            try
            {
                var progress = new Progress<StructureScanProgress>(ShowScanProgress);
                _preview = await _publishService.PreviewAsync(request, operationCancellation.Token, progress);
                if (_isClosing)
                {
                    return;
                }
                ShowPreviewResult(_preview);
            }
            catch (OperationCanceledException)
            {
                if (!_isClosing)
                {
                    _statusText.Text = "扫描已取消。";
                }
            }
            catch (OutOfMemoryException exception)
            {
                if (!_isClosing)
                {
                    _statusText.Text = GetOutOfMemoryFailureText(exception);
                }
            }
            catch (GitHubDictionaryClientException exception)
            {
                if (!_isClosing)
                {
                    // 认证与仓库错误已由客户端脱敏，直接保留可操作原因而不展示远端正文。
                    _statusText.Text = exception.Message;
                }
            }
            catch (Exception)
            {
                if (!_isClosing)
                {
                    _statusText.Text = "扫描失败，未发布任何内容。";
                }
            }
            finally
            {
                StopScanProgress();
                if (ReferenceEquals(_operationCancellation, operationCancellation))
                {
                    _operationCancellation = null;
                }
                operationCancellation.Dispose();
                if (!_isClosing)
                {
                    SetBusy(false, null);
                }
            }
        }

        /// <summary>XMZADD 20260902 启动仅用于界面展示的扫描计时，帮助用户区分正常耗时与需要取消的长任务。</summary>
        private void StartScanProgress()
        {
            _scanStartedAtUtc = DateTime.UtcNow;
            _scanStageMessage = "正在准备只读结构扫描…";
            _scanElapsedTimer.Start();
            UpdateScanProgressText();
        }

        /// <summary>XMZADD 20260902 接收服务报告的安全阶段文本，避免在状态栏展示连接配置或本机目录。</summary>
        private void ShowScanProgress(StructureScanProgress progress)
        {
            if (progress == null || string.IsNullOrWhiteSpace(progress.Message) || _operationCancellation == null)
            {
                return;
            }
            _scanStageMessage = progress.Message;
            UpdateScanProgressText();
        }

        /// <summary>XMZADD 20260902 每秒刷新扫描已耗时，使用户在大型 EOS 源码目录中仍可判断任务是否在推进。</summary>
        private void ScanElapsedTimer_Tick(object sender, EventArgs e)
        {
            UpdateScanProgressText();
        }

        /// <summary>XMZADD 20260902 组合当前阶段与已耗时，且只在活动扫描期间覆盖状态文本。</summary>
        private void UpdateScanProgressText()
        {
            if (_operationCancellation == null || string.IsNullOrWhiteSpace(_scanStageMessage))
            {
                return;
            }
            TimeSpan elapsed = DateTime.UtcNow - _scanStartedAtUtc;
            if (elapsed < TimeSpan.Zero)
            {
                elapsed = TimeSpan.Zero;
            }
            _statusText.Text = _scanStageMessage + "（已耗时 " + FormatElapsed(elapsed) + "）";
        }

        /// <summary>XMZADD 20260902 将扫描耗时格式化为紧凑中文文本，避免状态栏在长任务中产生冗余秒数。</summary>
        private static string FormatElapsed(TimeSpan elapsed)
        {
            if (elapsed.TotalMinutes >= 1D)
            {
                return ((int)elapsed.TotalMinutes).ToString() + "分" + elapsed.Seconds.ToString() + "秒";
            }
            return elapsed.Seconds.ToString() + "秒";
        }

        /// <summary>XMZADD 20260902 停止扫描计时且保留完成、取消或失败的最终状态文本。</summary>
        private void StopScanProgress()
        {
            _scanElapsedTimer.Stop();
            _scanStageMessage = null;
        }

        /// <summary>XMZADD 20260901 把扫描终态转换为可核验的预览表和发布按钮状态。</summary>
        private void ShowPreviewResult(StructurePublishResult result)
        {
            _previewGrid.ItemsSource = result != null && result.Diff != null ? result.Diff.Changes : null;
            _publishButton.IsEnabled = result != null && result.Outcome == StructurePublishOutcome.ReadyToPublish;
            _statusText.Text = GetPreviewStatusText(result);
        }

        /// <summary>XMZADD 20260902 将完整快照、增量和校验终态转换为不泄露本机配置的可执行中文提示。</summary>
        private static string GetPreviewStatusText(StructurePublishResult result)
        {
            if (result == null)
            {
                return "扫描未返回结果。";
            }
            switch (result.Outcome)
            {
                case StructurePublishOutcome.ReadyToPublish:
                    if (result.IsFullSnapshot)
                    {
                        return "扫描完成，将以一个 GitHub Commit 发布完整快照：" +
                            result.FullSnapshotTableCount.ToString() + " 张表、" +
                            result.FullSnapshotFieldCount.ToString() + " 个字段、" +
                            FormatSnapshotSize(result.FullSnapshotCompressedBytes) +
                            "。请核对差异后确认发布。" +
                            (result.KnowledgeBaseDegraded ? " 知识库不可用，本次已降级。" : string.Empty);
                    }
                    return "扫描完成，请核对差异后确认发布。" +
                        (result.KnowledgeBaseDegraded ? " 知识库不可用，本次已降级。" : string.Empty);
                case StructurePublishOutcome.NoChanges:
                    return "扫描完成，未发现需要发布的增量。";
                case StructurePublishOutcome.NotAuthorized:
                    return "当前 GitHub 身份不在远程发布者名单中。";
                case StructurePublishOutcome.NotConfigured:
                    return "请补全正式库和源码目录配置。";
                case StructurePublishOutcome.ScanFailed:
                    return GetSafeScanFailureText(result.FailureStage);
                case StructurePublishOutcome.ValidationFailed:
                    return GetValidationFailureText(result.ValidationReason);
                case StructurePublishOutcome.Published:
                    return result.IsFullSnapshot
                        ? "发布成功，完整快照已通过一个 GitHub Commit 提交。"
                        : "发布成功，Issue #" + result.IssueNumber + " 已创建。";
                default:
                    return "扫描结果未通过公开发布校验。";
            }
        }

        /// <summary>XMZADD 20260902 将结构发布校验原因转换为能够指导重新扫描或收缩范围的安全提示。</summary>
        private static string GetValidationFailureText(StructurePublishValidationReason reason)
        {
            switch (reason)
            {
                case StructurePublishValidationReason.OperationLimitExceeded:
                    return "本次增量超过单个 GitHub Issue 的 100 条操作上限，请先更新字典或缩小本次变更范围后重新扫描。";
                case StructurePublishValidationReason.FullSnapshotSizeExceeded:
                    return "完整公开快照超过 GitHub 单文件 100 MiB 限制，当前不会发布任何内容。";
                case StructurePublishValidationReason.FullSnapshotIncomplete:
                    return "扫描到没有字段的用户表，结构读取可能不完整，已停止发布。";
                case StructurePublishValidationReason.FullSnapshotInvalid:
                    return "完整快照的格式、大小、公开内容安全或表字段稳定标识未通过校验，请检查扫描结果后重新扫描。";
                case StructurePublishValidationReason.FullSnapshotPublisherUnavailable:
                    return "当前程序版本不支持完整快照提交，请更新程序后重新扫描。";
                case StructurePublishValidationReason.RemoteSnapshotChanged:
                    return "远程快照已被其他人更新，本次没有覆盖远程内容；请先更新字典后重新扫描。";
                case StructurePublishValidationReason.PreviewChanged:
                    return "预览内容或发布身份已变化，本次没有发布；请重新扫描后确认。";
                case StructurePublishValidationReason.PublicContentInvalid:
                    return "扫描结果包含不符合公开仓库规则的内容，请检查字段说明和来源证据后重新扫描。";
                default:
                    return "扫描结果未通过公开发布校验，请检查扫描范围后重新扫描。";
            }
        }

        /// <summary>XMZADD 20260902 以紧凑单位展示压缩快照大小，便于发布者在提交前判断完整快照规模。</summary>
        private static string FormatSnapshotSize(long byteCount)
        {
            if (byteCount >= 1024L * 1024L)
            {
                return (byteCount / (1024D * 1024D)).ToString("0.00") + " MB";
            }
            if (byteCount >= 1024L)
            {
                return (byteCount / 1024D).ToString("0.00") + " KB";
            }
            return byteCount.ToString() + " B";
        }

        /// <summary>XMZADD 20260902 返回扫描和发布共用的内存不足安全提示，避免向界面输出运行时异常正文。</summary>
        private static string GetOutOfMemoryFailureText(OutOfMemoryException exception)
        {
            return "本机内存不足，完整快照尚未发布；请关闭其他程序后重试。";
        }

        /// <summary>XMZADD 20260902 将扫描阶段转换为不含路径、密码和连接字符串的用户可执行提示。</summary>
        private static string GetSafeScanFailureText(StructureScanFailureStage failureStage)
        {
            switch (failureStage)
            {
                case StructureScanFailureStage.Database:
                    return "EOS 正式库只读扫描失败，请检查服务器、账号、密码、网络和只读权限；未发布任何内容。";
                case StructureScanFailureStage.Source:
                    return "EOS 源码扫描失败，请检查源码目录范围、文件可读性和扫描边界；未发布任何内容。";
                case StructureScanFailureStage.PreviousSnapshot:
                    return "本地快照读取失败，请先点击更新字典后重新扫描；未发布任何内容。";
                default:
                    return "只读扫描失败，未发布任何内容。";
            }
        }

        /// <summary>XMZADD 20260901 确认发布已预览批次，并在服务端最终授权校验失败时保持未发布状态。</summary>
        private async void PublishButton_Click(object sender, RoutedEventArgs e)
        {
            if (_operationCancellation != null || _preview == null ||
                _preview.Outcome != StructurePublishOutcome.ReadyToPublish)
            {
                return;
            }

            var operationCancellation = new CancellationTokenSource();
            _operationCancellation = operationCancellation;
            _isPublishing = true;
            SetBusy(true, _preview.IsFullSnapshot
                ? "正在重新核验并提交一个完整 GitHub Commit……"
                : "正在重新核验发布资格并提交唯一 Issue……");
            try
            {
                StructurePublishResult result = await _publishService.PublishPreviewAsync(
                    _preview,
                    operationCancellation.Token);
                if (_isClosing)
                {
                    return;
                }
                _preview = result;
                _publishButton.IsEnabled = false;
                _statusText.Text = GetPreviewStatusText(result);
            }
            catch (OperationCanceledException)
            {
                if (!_isClosing)
                {
                    _statusText.Text = "发布已取消。";
                }
            }
            catch (OutOfMemoryException exception)
            {
                if (!_isClosing)
                {
                    _statusText.Text = GetOutOfMemoryFailureText(exception);
                }
            }
            catch (GitHubDictionaryClientException exception)
            {
                if (!_isClosing)
                {
                    // GitHub 客户端仅提供经过脱敏的固定消息，便于区分权限、并发和网络问题。
                    _statusText.Text = exception.Message;
                }
            }
            catch (Exception)
            {
                if (!_isClosing)
                {
                    _statusText.Text = "发布失败，远程快照未被替换。";
                }
            }
            finally
            {
                _isPublishing = false;
                if (ReferenceEquals(_operationCancellation, operationCancellation))
                {
                    _operationCancellation = null;
                }
                operationCancellation.Dispose();
                if (!_isClosing)
                {
                    SetBusy(false, null);
                }
            }
        }

        /// <summary>XMZADD 20260901 切换扫描和发布期间的按钮状态，防止同一窗口并发创建多个批次。</summary>
        private void SetBusy(bool isBusy, string status)
        {
            _scanButton.IsEnabled = !isBusy;
            _publishButton.IsEnabled = !isBusy && _preview != null &&
                _preview.Outcome == StructurePublishOutcome.ReadyToPublish;
            _cancelButton.Content = isBusy
                ? (_isPublishing ? "等待发布" : "取消任务")
                : "关闭";
            if (!string.IsNullOrWhiteSpace(status))
            {
                _statusText.Text = status;
            }
        }

        /// <summary>XMZADD 20260901 选择本机 EOS 源码根目录，不读取或复制目录正文。</summary>
        private void BrowseSourceButton_Click(object sender, RoutedEventArgs e)
        {
            SelectFolder(_sourceRootBox, "选择 EOS 源码根目录");
        }

        /// <summary>XMZADD 20260901 选择本机 EOS 知识库根目录，不把绝对路径带入公开证据。</summary>
        private void BrowseKnowledgeButton_Click(object sender, RoutedEventArgs e)
        {
            SelectFolder(_knowledgeBaseRootBox, "选择 EOS 知识库根目录");
        }

        /// <summary>XMZADD 20260901 使用系统目录选择器更新本机路径输入。</summary>
        private static void SelectFolder(TextBox target, string description)
        {
            using (var dialog = new WinForms.FolderBrowserDialog())
            {
                dialog.Description = description;
                dialog.SelectedPath = Directory.Exists(target.Text) ? target.Text : string.Empty;
                if (dialog.ShowDialog() == WinForms.DialogResult.OK)
                {
                    target.Text = dialog.SelectedPath;
                }
            }
        }

        /// <summary>XMZADD 20260901 扫描可取消，但发布提交期间必须等待远端结果以免误判并重复创建 Issue。</summary>
        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isPublishing)
            {
                _statusText.Text = "发布提交中，请等待 GitHub 返回结果。";
                return;
            }
            if (_operationCancellation != null)
            {
                _operationCancellation.Cancel();
                return;
            }
            Close();
        }

        /// <summary>XMZADD 20260901 发布期间拒绝关闭，扫描期间仍可取消，确保一次 BatchId 只对应一个远端 Issue。</summary>
        private void Window_Closing(object sender, CancelEventArgs e)
        {
            if (_isPublishing)
            {
                e.Cancel = true;
                _statusText.Text = "发布提交中，请等待 GitHub 返回结果。";
                return;
            }
            _isClosing = true;
            StopScanProgress();
            CancellationTokenSource cancellation = _operationCancellation;
            if (cancellation != null)
            {
                cancellation.Cancel();
            }
        }
    }
}
