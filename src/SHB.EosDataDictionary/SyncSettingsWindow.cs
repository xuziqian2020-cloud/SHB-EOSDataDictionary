using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using SHB.EosDataDictionary.Models;
using SHB.EosDataDictionary.Services;

namespace SHB.EosDataDictionary
{
    /// <summary>XMZADD 20260901 维护当前 Windows 用户的 GitHub Token，并显示公开字典仓库与不可变用户身份。</summary>
    public sealed class SyncSettingsWindow : Window
    {
        private readonly DictionaryRepositoryOptions _options;
        private readonly GitHubCredentialStore _credentialStore;
        private readonly IGitHubDictionaryClient _client;
        private PasswordBox _tokenPasswordBox;
        private TextBlock _githubIdentityText;
        private Button _saveButton;
        private CancellationTokenSource _verificationCancellation;
        private Task _verificationTask;
        private Task _verificationCleanupTask;
        private bool _isClosed;

        /// <summary>XMZADD 20260901 使用固定公开仓库、当前用户凭据存储和可替换客户端初始化同步设置窗口。</summary>
        public SyncSettingsWindow(
            DictionaryRepositoryOptions options,
            GitHubCredentialStore credentialStore,
            IGitHubDictionaryClient client)
        {
            if (options == null)
            {
                throw new ArgumentNullException("options");
            }
            if (credentialStore == null)
            {
                throw new ArgumentNullException("credentialStore");
            }

            _options = options;
            _credentialStore = credentialStore;
            _client = client;
            Style = Application.Current == null ? null : Application.Current.TryFindResource(typeof(Window)) as Style;
            BuildContent();
        }

        /// <summary>XMZADD 20260901 构建只包含公开仓库状态、个人 Token 和身份结果的最小设置界面。</summary>
        private void BuildContent()
        {
            Title = "GitHub 同步设置";
            Width = 540D;
            Height = 330D;
            MinWidth = 500D;
            MinHeight = 300D;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;

            NameScope.SetNameScope(this, new NameScope());
            var root = new Grid { Margin = new Thickness(24D) };
            root.SetResourceReference(Panel.BackgroundProperty, "App.WindowBackgroundBrush");
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(18D) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10D) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(18D) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1D, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var repositoryPanel = new StackPanel();
            repositoryPanel.Children.Add(new TextBlock
            {
                Text = "公开字典仓库",
                FontWeight = FontWeights.Bold,
                FontSize = 14D
            });
            repositoryPanel.Children.Add(new TextBlock
            {
                Text = GetRepositoryStatusText(),
                Margin = new Thickness(0D, 6D, 0D, 0D),
                TextWrapping = TextWrapping.Wrap
            });
            Grid.SetRow(repositoryPanel, 0);
            root.Children.Add(repositoryPanel);

            bool hasSavedToken = HasSavedToken();
            var tokenLabel = new TextBlock
            {
                Text = "新的 GitHub Token（仅更换时输入）",
                FontWeight = FontWeights.Bold
            };
            Grid.SetRow(tokenLabel, 2);
            root.Children.Add(tokenLabel);

            _tokenPasswordBox = new PasswordBox
            {
                Name = "TokenPasswordBox",
                Height = 36D,
                Padding = new Thickness(10D, 0D, 10D, 0D),
                VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = "留空并保存验证，会继续使用当前 Windows 用户已保存的 Token"
            };
            RegisterName(_tokenPasswordBox.Name, _tokenPasswordBox);
            Grid.SetRow(_tokenPasswordBox, 4);
            root.Children.Add(_tokenPasswordBox);

            _githubIdentityText = new TextBlock
            {
                Name = "GitHubIdentityText",
                Text = GetInitialIdentityText(hasSavedToken),
                TextWrapping = TextWrapping.Wrap
            };
            RegisterName(_githubIdentityText.Name, _githubIdentityText);
            Grid.SetRow(_githubIdentityText, 6);
            root.Children.Add(_githubIdentityText);

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            _saveButton = new Button
            {
                Name = "SaveAndVerifyButton",
                Content = "保存并验证",
                Width = 110D,
                Height = 34D,
                IsEnabled = _client != null,
                Margin = new Thickness(0D, 0D, 8D, 0D)
            };
            RegisterName(_saveButton.Name, _saveButton);
            _saveButton.Click += SaveButton_Click;
            var closeButton = new Button
            {
                Content = "关闭",
                Width = 80D,
                Height = 34D
            };
            closeButton.Click += CloseButton_Click;
            actions.Children.Add(_saveButton);
            actions.Children.Add(closeButton);
            Grid.SetRow(actions, 8);
            root.Children.Add(actions);

            Content = root;
        }

        /// <summary>XMZADD 20260902 检查当前 Windows 用户是否已有可复用凭据，仅用于界面提示且绝不回显 Token。</summary>
        private bool HasSavedToken()
        {
            try
            {
                return !string.IsNullOrWhiteSpace(_credentialStore.Load());
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        /// <summary>XMZADD 20260902 根据本机凭据状态说明留空保存的行为，避免用户误以为每次同步都要重新输入 Token。</summary>
        private string GetInitialIdentityText(bool hasSavedToken)
        {
            if (_client == null)
            {
                return "仓库未配置，暂时无法验证 GitHub 身份。";
            }
            if (hasSavedToken)
            {
                return "当前 Windows 用户已保存 GitHub Token；留空点击保存并验证将继续使用，输入新 Token 后才会替换。";
            }
            return "尚未保存 GitHub Token；首次输入并保存验证后会长期复用。";
        }

        /// <summary>XMZADD 20260901 生成不含 Token 的公开仓库状态，缺少真实 owner 时明确安全降级。</summary>
        private string GetRepositoryStatusText()
        {
            if (string.IsNullOrWhiteSpace(_options.Owner))
            {
                return "仓库未配置 · " + _options.RepositoryName;
            }
            return _options.Owner + "/" + _options.RepositoryName + " · public · " + _options.Branch;
        }

        /// <summary>XMZADD 20260901 先验证非空候选 Token 再替换凭据，留空时沿用当前用户已加密保存的 Token。</summary>
        private async Task SaveAndVerifyAsync(CancellationToken cancellationToken)
        {
            if (_isClosed)
            {
                return;
            }
            if (_client == null)
            {
                _githubIdentityText.Text = "仓库未配置，暂时无法验证 GitHub 身份。";
                return;
            }

            _saveButton.IsEnabled = false;
            try
            {
                _githubIdentityText.Text = "正在验证当前 GitHub 身份…";
                string candidateToken = _tokenPasswordBox.Password;
                GitHubUserIdentity identity;
                if (candidateToken.Length > 0)
                {
                    identity = await _client.GetCurrentUserAsync(candidateToken, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    // 候选身份通过后才原子替换旧凭据，验证失败或进程中断不会破坏旧有效 Token。
                    _credentialStore.Save(candidateToken);
                    _tokenPasswordBox.Clear();
                }
                else
                {
                    identity = await _client.GetCurrentUserAsync(cancellationToken);
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (!_isClosed)
                {
                    _githubIdentityText.Text = "已验证：" + identity.Login + "（不可变 ID：" + identity.GitHubUserId + "）";
                }
            }
            catch (OperationCanceledException)
            {
                // 窗口关闭或用户取消验证属于正常结束，不展示失败状态。
            }
            catch (GitHubDictionaryClientException exception)
            {
                if (!_isClosed)
                {
                    _githubIdentityText.Text = GetSafeIdentityFailureText(exception.Kind);
                }
            }
            catch (ArgumentException)
            {
                if (!_isClosed)
                {
                    _githubIdentityText.Text = "Token 格式无效，请重新输入。";
                }
            }
            catch (InvalidOperationException)
            {
                if (!_isClosed)
                {
                    _githubIdentityText.Text = "个人凭据无法使用，请重新配置 Token。";
                }
            }
            finally
            {
                if (!_isClosed && _verificationCancellation != null &&
                    _verificationCancellation.Token == cancellationToken)
                {
                    _saveButton.IsEnabled = true;
                }
            }
        }

        /// <summary>XMZADD 20260901 取消上一轮验证并启动唯一候选身份任务，使重复点击不会并发覆盖凭据或界面状态。</summary>
        private Task StartVerificationAsync()
        {
            if (_isClosed)
            {
                return Task.CompletedTask;
            }
            CancellationTokenSource previousCancellation = _verificationCancellation;
            Task previousTask = _verificationTask;
            if (previousCancellation != null)
            {
                previousCancellation.Cancel();
                _verificationCleanupTask = DisposeVerificationCancellationAsync(previousCancellation, previousTask);
            }

            var verificationCancellation = new CancellationTokenSource();
            _verificationCancellation = verificationCancellation;
            _verificationTask = SaveAndVerifyAsync(verificationCancellation.Token);
            return _verificationTask;
        }

        /// <summary>XMZADD 20260901 把 GitHub 错误类别转换为不包含 URL、响应正文或凭据的中文提示。</summary>
        private static string GetSafeIdentityFailureText(GitHubDictionaryErrorKind kind)
        {
            if (kind == GitHubDictionaryErrorKind.Authentication)
            {
                return "GitHub 身份验证失败，请检查个人 Token。";
            }
            if (kind == GitHubDictionaryErrorKind.RateLimited)
            {
                return "GitHub 请求暂时受限，请稍后重试。";
            }
            if (kind == GitHubDictionaryErrorKind.Configuration)
            {
                return "GitHub 仓库尚未正确配置。";
            }
            return "GitHub 身份暂时无法验证，请稍后重试。";
        }

        /// <summary>XMZADD 20260901 从按钮事件执行个人身份验证，异常均在窗口内转换为安全状态。</summary>
        private async void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            await StartVerificationAsync();
        }

        /// <summary>XMZADD 20260901 关闭同步设置窗口，不保留密码框中的个人 Token 文本。</summary>
        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        /// <summary>XMZADD 20260901 关闭同步设置时取消身份验证并异步释放令牌源，标题栏关闭与按钮关闭遵循同一规则。</summary>
        protected override void OnClosed(EventArgs e)
        {
            _isClosed = true;
            _tokenPasswordBox.Clear();
            CancellationTokenSource verificationCancellation = _verificationCancellation;
            Task verificationTask = _verificationTask;
            if (verificationCancellation != null)
            {
                verificationCancellation.Cancel();
                _verificationCancellation = null;
                _verificationCleanupTask = DisposeVerificationCancellationAsync(
                    verificationCancellation,
                    verificationTask);
            }
            base.OnClosed(e);
        }

        /// <summary>XMZADD 20260901 等待候选身份任务退出后释放验证令牌源，避免关闭时发生已释放令牌竞态。</summary>
        private static async Task DisposeVerificationCancellationAsync(
            CancellationTokenSource cancellationSource,
            Task verificationTask)
        {
            try
            {
                if (verificationTask != null)
                {
                    try
                    {
                        await verificationTask.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // 用户重复验证或关闭窗口主动取消属于正常流程。
                    }
                }
            }
            finally
            {
                cancellationSource.Dispose();
            }
        }
    }
}
