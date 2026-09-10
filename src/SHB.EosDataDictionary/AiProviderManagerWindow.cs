using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using SHB.EosDataDictionary.Models;
using SHB.EosDataDictionary.Services;

namespace SHB.EosDataDictionary
{
    /// <summary>XMZADD 20260831 提供 AI 服务商的本机配置界面，确保自动推测始终只使用一个默认配置。</summary>
    public sealed class AiProviderManagerWindow : Window
    {
        private readonly LocalDictionaryStore _store;
        private readonly ListBox _providerList;
        private readonly TextBox _idBox;
        private readonly TextBox _nameBox;
        private readonly TextBox _endpointBox;
        private readonly TextBox _modelBox;
        private readonly PasswordBox _apiKeyBox;
        private readonly TextBox _timeoutBox;
        private readonly TextBox _concurrencyBox;
        private readonly TextBox _batchSizeBox;
        private readonly CheckBox _enabledBox;
        private readonly CheckBox _defaultBox;
        private readonly TextBlock _apiKeyStatusText;

        /// <summary>XMZADD 20260831 初始化 AI 配置窗口，所有保存行为仅进入当前用户的本机 SQLite 与 DPAPI 密钥保护。</summary>
        public AiProviderManagerWindow()
            : this(null)
        {
        }

        /// <summary>XMZADD 20260831 使用指定本机数据库初始化 AI 配置窗口，支持生产界面和隔离测试共用完整流程。</summary>
        public AiProviderManagerWindow(string databasePath)
        {
            _store = new LocalDictionaryStore(databasePath);
            Title = "AI 服务商配置";
            Style = Application.Current == null ? null : Application.Current.TryFindResource(typeof(Window)) as Style;
            Width = 900D;
            Height = 620D;
            MinWidth = 780D;
            MinHeight = 520D;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var root = new Grid { Margin = new Thickness(20D) };
            root.SetResourceReference(Panel.BackgroundProperty, "App.WindowBackgroundBrush");
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250D) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20D) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1D, GridUnitType.Star) });

            var providerPanel = new Grid();
            providerPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48D) });
            providerPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1D, GridUnitType.Star) });
            var providerTitle = new TextBlock { Text = "已保存的 AI 连接", FontSize = 15D, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14D, 0D, 14D, 0D) };
            providerPanel.Children.Add(providerTitle);

            _providerList = new ListBox { DisplayMemberPath = "Name", Margin = new Thickness(8D, 0D, 8D, 8D) };
            _providerList.SelectionChanged += ProviderList_SelectionChanged;
            Grid.SetRow(_providerList, 1);
            providerPanel.Children.Add(_providerList);
            var providerBorder = new Border { Child = providerPanel };
            providerBorder.Style = TryFindResource("PanelBorderStyle") as Style;
            Grid.SetColumn(providerBorder, 0);
            root.Children.Add(providerBorder);

            var editor = new Grid();
            editor.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112D) });
            editor.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1D, GridUnitType.Star) });
            for (int i = 0; i < 12; i++)
            {
                editor.RowDefinitions.Add(new RowDefinition { Height = new GridLength(i == 11 ? 1D : 42D, i == 11 ? GridUnitType.Star : GridUnitType.Auto) });
            }

            _idBox = AddTextBox(editor, 0, "配置标识");
            _nameBox = AddTextBox(editor, 1, "显示名称");
            _endpointBox = AddTextBox(editor, 2, "服务地址");
            _modelBox = AddTextBox(editor, 3, "模型名称");
            _apiKeyBox = AddPasswordBox(editor, 4, "API Key");
            _apiKeyStatusText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 11D };
            _apiKeyStatusText.SetResourceReference(TextBlock.ForegroundProperty, "App.TextMutedBrush");
            Grid.SetColumn(_apiKeyStatusText, 1);
            Grid.SetRow(_apiKeyStatusText, 5);
            editor.Children.Add(_apiKeyStatusText);
            _timeoutBox = AddTextBox(editor, 6, "超时（秒）");
            _concurrencyBox = AddTextBox(editor, 7, "并发数");
            _batchSizeBox = AddTextBox(editor, 8, "每批字段");
            _enabledBox = AddCheckBox(editor, 9, "启用自动推测");
            _defaultBox = AddCheckBox(editor, 10, "设为默认配置");

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0D, 16D, 0D, 0D) };
            buttons.Children.Add(CreateButton("新增连接", NewConnectionButton_Click));
            buttons.Children.Add(CreateButton("测试连接", TestButton_Click));
            buttons.Children.Add(CreateButton("保存", SaveButton_Click));
            buttons.Children.Add(CreateButton("删除", DeleteButton_Click));
            buttons.Children.Add(CreateButton("关闭", CloseButton_Click));
            Grid.SetColumnSpan(buttons, 2);
            Grid.SetRow(buttons, 11);
            editor.Children.Add(buttons);

            var editorBorder = new Border { Child = editor, Padding = new Thickness(22D, 18D, 22D, 18D) };
            editorBorder.Style = TryFindResource("PanelBorderStyle") as Style;
            Grid.SetColumn(editorBorder, 2);
            root.Children.Add(editorBorder);

            Content = root;
            RefreshProviders(null);
            if (_providerList.Items.Count == 0)
            {
                PrepareNewConnection();
            }
        }

        /// <summary>XMZADD 20260831 刷新本机服务商列表，使默认设置、删除和保存立即反馈到配置界面。</summary>
        private void RefreshProviders(string selectedId)
        {
            IList<AiProviderConfiguration> providers = _store.LoadAiProviders();
            _providerList.ItemsSource = providers;
            for (int i = 0; i < providers.Count; i++)
            {
                if (string.Equals(providers[i].Id, selectedId, StringComparison.Ordinal))
                {
                    _providerList.SelectedItem = providers[i];
                    return;
                }
            }
            if (providers.Count > 0)
            {
                _providerList.SelectedItem = providers[0];
            }
        }

        /// <summary>XMZADD 20260831 将选中配置载入编辑区，密钥不回显以免在界面中意外暴露。</summary>
        private void ProviderList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            AiProviderConfiguration provider = _providerList.SelectedItem as AiProviderConfiguration;
            if (provider == null)
            {
                return;
            }

            _idBox.Text = provider.Id;
            _nameBox.Text = provider.Name;
            _endpointBox.Text = provider.Endpoint;
            _modelBox.Text = provider.Model;
            _apiKeyBox.Clear();
            _apiKeyStatusText.Text = string.IsNullOrWhiteSpace(provider.ApiKey) ? "尚未保存 API Key" : "密钥已保存，留空表示不修改";
            _timeoutBox.Text = provider.TimeoutSeconds.ToString();
            _concurrencyBox.Text = provider.MaxConcurrency.ToString();
            _batchSizeBox.Text = provider.BatchSize.ToString();
            _enabledBox.IsChecked = provider.IsEnabled;
            _defaultBox.IsChecked = provider.IsDefault;
        }

        /// <summary>XMZADD 20260831 填入 DeepSeek 兼容接口默认值，减少首次配置的手工输入。</summary>
        private void NewConnectionButton_Click(object sender, RoutedEventArgs e)
        {
            PrepareNewConnection();
        }

        /// <summary>XMZADD 20260831 测试当前 AI 接口连通性，不读取或修改任何 EOS 数据库内容。</summary>
        private void TestButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                AiProviderConfiguration provider = BuildProvider();
                string reply = new OpenAiCompatibleChatClient().Complete(provider, "你是连接测试助手。", "仅回复：连接成功");
                MessageBox.Show(this, "AI 服务连接成功。返回内容：\r\n" + reply, "测试成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, "AI 服务连接失败：\r\n" + exception.Message, "测试失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>XMZADD 20260831 保存当前配置，并让本机存储维护唯一默认项和 API Key 加密。</summary>
        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                AiProviderConfiguration provider = BuildProvider();
                _store.SaveAiProvider(provider);
                RefreshProviders(provider.Id);
                MessageBox.Show(this, "AI 配置已加密保存到本机。自动推测会使用默认配置。", "保存成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, exception.Message, "无法保存 AI 配置", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>XMZADD 20260831 删除选中本地 AI 配置，剩余配置的默认项由本机存储自动维护。</summary>
        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            AiProviderConfiguration provider = _providerList.SelectedItem as AiProviderConfiguration;
            if (provider == null)
            {
                return;
            }
            if (MessageBox.Show(this, "删除本地 AI 配置“" + provider.Name + "”？", "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }

            _store.DeleteAiProvider(provider.Id);
            RefreshProviders(null);
            if (_providerList.Items.Count == 0)
            {
                PrepareNewConnection();
            }
        }

        /// <summary>XMZADD 20260831 关闭配置窗口，不改变尚未保存的编辑内容。</summary>
        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        /// <summary>XMZADD 20260831 从界面构建可调用的配置，编辑已有项时空密钥会继续使用本机已加密的原密钥。</summary>
        private AiProviderConfiguration BuildProvider()
        {
            string id = (_idBox.Text ?? string.Empty).Trim();
            string name = (_nameBox.Text ?? string.Empty).Trim();
            string endpoint = (_endpointBox.Text ?? string.Empty).Trim();
            string model = (_modelBox.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(model))
            {
                throw new InvalidOperationException("配置标识、显示名称、服务地址和模型名称不能为空。");
            }

            AiProviderConfiguration existing = FindProvider(id);
            string apiKey = string.IsNullOrEmpty(_apiKeyBox.Password) && existing != null ? existing.ApiKey : _apiKeyBox.Password;
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException("请输入 API Key。API Key 只会以当前 Windows 用户的加密形式保存到本机。");
            }

            return new AiProviderConfiguration
            {
                Id = id,
                Name = name,
                Protocol = AiProviderProtocol.OpenAiCompatible,
                Endpoint = endpoint,
                Model = model,
                ApiKey = apiKey,
                IsEnabled = _enabledBox.IsChecked == true,
                IsDefault = _defaultBox.IsChecked == true,
                TimeoutSeconds = ReadPositiveNumber(_timeoutBox, 60),
                MaxConcurrency = ReadPositiveNumber(_concurrencyBox, 2),
                // 配置界面只保存服务能够一次安全处理的最大批量。
                BatchSize = Math.Min(ReadPositiveNumber(_batchSizeBox, 10), AiInferenceService.MaximumBatchTargetCount),
                ConfigurationVersion = "v1",
                UpdatedAt = DateTime.Now
            };
        }

        /// <summary>XMZADD 20260831 查找同标识的既有配置，支持更新模型参数时不必再次暴露密钥。</summary>
        private AiProviderConfiguration FindProvider(string id)
        {
            IList<AiProviderConfiguration> providers = _store.LoadAiProviders();
            for (int i = 0; i < providers.Count; i++)
            {
                if (string.Equals(providers[i].Id, id, StringComparison.Ordinal))
                {
                    return providers[i];
                }
            }
            return null;
        }

        /// <summary>XMZADD 20260831 填入 DeepSeek 默认参数，并允许用户替换为公司内部兼容接口。</summary>
        private void PrepareNewConnection()
        {
            _providerList.SelectedItem = null;
            _idBox.Text = "deepseek";
            _nameBox.Text = "DeepSeek";
            _endpointBox.Text = "https://api.deepseek.com";
            _modelBox.Text = "deepseek-chat";
            _apiKeyBox.Clear();
            _apiKeyStatusText.Text = "请输入 API Key；保存后只显示已保存状态";
            _timeoutBox.Text = "60";
            _concurrencyBox.Text = "2";
            _batchSizeBox.Text = "10";
            _enabledBox.IsChecked = true;
            _defaultBox.IsChecked = true;
        }

        /// <summary>XMZADD 20260831 将正整数配置转换为可控默认值，避免异常参数阻塞自动推测。</summary>
        private static int ReadPositiveNumber(TextBox textBox, int defaultValue)
        {
            int value;
            return Int32.TryParse(textBox.Text, out value) && value > 0 ? value : defaultValue;
        }

        /// <summary>XMZADD 20260831 添加成对标签和文本输入，使配置项含义可直接被用户理解。</summary>
        private static TextBox AddTextBox(Grid grid, int row, string label)
        {
            AddLabel(grid, row, label);
            var textBox = new TextBox { Margin = new Thickness(0D, 3D, 0D, 3D) };
            Grid.SetColumn(textBox, 1);
            Grid.SetRow(textBox, row);
            grid.Children.Add(textBox);
            return textBox;
        }

        /// <summary>XMZADD 20260831 添加不回显 API Key 的密码输入框，减少本地配置过程中的敏感信息暴露。</summary>
        private static PasswordBox AddPasswordBox(Grid grid, int row, string label)
        {
            AddLabel(grid, row, label);
            var passwordBox = new PasswordBox { Margin = new Thickness(0D, 3D, 0D, 3D) };
            Grid.SetColumn(passwordBox, 1);
            Grid.SetRow(passwordBox, row);
            grid.Children.Add(passwordBox);
            return passwordBox;
        }

        /// <summary>XMZADD 20260831 添加布尔配置项，避免服务商启用和默认资格由隐含规则决定。</summary>
        private static CheckBox AddCheckBox(Grid grid, int row, string label)
        {
            AddLabel(grid, row, label);
            var checkBox = new CheckBox { VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(checkBox, 1);
            Grid.SetRow(checkBox, row);
            grid.Children.Add(checkBox);
            return checkBox;
        }

        /// <summary>XMZADD 20260831 添加配置标签，使字段含义无需依赖外部说明文档。</summary>
        private static void AddLabel(Grid grid, int row, string text)
        {
            var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0D, 0D, 8D, 0D) };
            Grid.SetRow(label, row);
            grid.Children.Add(label);
        }

        /// <summary>XMZADD 20260831 创建统一操作按钮，减少配置窗口的重复布局代码。</summary>
        private static Button CreateButton(string content, RoutedEventHandler handler)
        {
            var button = new Button { Content = content, Margin = new Thickness(0D, 0D, 8D, 0D), MinWidth = 78D, Height = 30D };
            button.Click += handler;
            return button;
        }
    }
}
