using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.Serialization.Json;
using System.Windows;
using System.Windows.Threading;
using SHB.EosDataDictionary.Models;
using SHB.EosDataDictionary.Services;
using SHB.EosDataDictionary.ViewModels;

namespace SHB.EosDataDictionary
{
    /// <summary>XMZADD 20260828 初始化 EOS 数据字典桌面程序。</summary>
    public partial class App : Application
    {
        private const int MaximumRepositoryConfigurationBytes = 16 * 1024;
        private SingleInstanceGuard _singleInstanceGuard;
        private readonly AppDiagnosticLog _diagnosticLog;

        /// <summary>XMZADD 20260828 初始化主题服务，使主窗口和连接管理窗口共享用户主题偏好。</summary>
        public App()
        {
            ThemeService = new ThemeService(new ThemePreferenceStore(AppPathService.GetThemePreferencePath()));
            _diagnosticLog = new AppDiagnosticLog(AppPathService.GetDiagnosticLogPath());
            RepositoryOptions = LoadRepositoryOptions();
            GitHubHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(45D) };
            GitHubCredentialStore = new GitHubCredentialStore(
                Path.Combine(AppPathService.GetLocalDataDirectory(), "github-token.bin"));
            ComposeRemoteDictionaryDependencies();
            DispatcherUnhandledException += App_DispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        }

        public ThemeService ThemeService { get; private set; }
        public DictionaryRepositoryOptions RepositoryOptions { get; private set; }
        public HttpClient GitHubHttpClient { get; private set; }
        public GitHubCredentialStore GitHubCredentialStore { get; private set; }
        public IGitHubDictionaryClient GitHubDictionaryClient { get; private set; }
        public DictionarySyncCoordinator DictionarySyncCoordinator { get; private set; }

        /// <summary>XMZADD 20260828 在创建主窗口前恢复上次主题，避免启动过程出现亮色闪烁。</summary>
        protected override void OnStartup(StartupEventArgs e)
        {
            _singleInstanceGuard = new SingleInstanceGuard("Local\\SHB.EosDataDictionary.Application");
            if (!_singleInstanceGuard.IsPrimaryInstance)
            {
                MessageBox.Show("胜华波 EOS 数据字典已经在运行，请切换到已打开的窗口。", "程序已运行", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }
            LoadThemeWithFallback(delegate { ThemeService.LoadSavedTheme(); }, ApplyDefaultTheme, _diagnosticLog);
            base.OnStartup(e);
            string databasePath = AppPathService.GetLocalDatabasePath();
            MainWindow = new MainWindow(
                new MainViewModel(databasePath, AppPathService.GetDefaultSourceRoot()),
                new ColumnLayoutStore(databasePath),
                DictionarySyncCoordinator,
                RepositoryOptions,
                GitHubCredentialStore,
                GitHubDictionaryClient);
            MainWindow.Show();
        }

        /// <summary>XMZADD 20260901 仅在公开仓库 owner 配置合法时组合 GitHub 客户端和协调器，缺失时保持本地缓存模式。</summary>
        private void ComposeRemoteDictionaryDependencies()
        {
            if (string.IsNullOrWhiteSpace(RepositoryOptions.Owner))
            {
                GitHubDictionaryClient = null;
                DictionarySyncCoordinator = null;
                return;
            }

            try
            {
                RepositoryOptions.Validate();
                var client = new GitHubDictionaryClient(
                    GitHubHttpClient,
                    RepositoryOptions,
                    GitHubCredentialStore);
                string databasePath = AppPathService.GetLocalDatabasePath();
                GitHubDictionaryClient = client;
                DictionarySyncCoordinator = new DictionarySyncCoordinator(
                    new LocalDictionaryStore(databasePath),
                    new SnapshotStore(databasePath),
                    client,
                    RepositoryOptions,
                    new SystemClock());
            }
            catch (ArgumentException)
            {
                // 公开配置损坏时必须禁用远程访问，避免使用不可信仓库坐标发起请求。
                RepositoryOptions = new DictionaryRepositoryOptions();
                GitHubDictionaryClient = null;
                DictionarySyncCoordinator = null;
            }
        }

        /// <summary>XMZADD 20260901 从应用目录读取仅含公开仓库坐标的配置，缺失或损坏时保留未配置状态。</summary>
        private static DictionaryRepositoryOptions LoadRepositoryOptions()
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "GitHubRepository.json");
            return LoadRepositoryOptions(path);
        }

        /// <summary>XMZADD 20260901 从指定公开配置路径读取仓库坐标，使缺失文件降级规则可在隔离目录验证。</summary>
        private static DictionaryRepositoryOptions LoadRepositoryOptions(string path)
        {
            var options = new DictionaryRepositoryOptions();
            if (!File.Exists(path))
            {
                return options;
            }

            try
            {
                var serializer = new DataContractJsonSerializer(
                    typeof(Dictionary<string, object>),
                    new DataContractJsonSerializerSettings
                    {
                        MaxItemsInObjectGraph = 16,
                        UseSimpleDictionaryFormat = true
                    });
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (stream.Length > MaximumRepositoryConfigurationBytes)
                    {
                        return options;
                    }

                    // 配置读取必须保持固定内存上限，并覆盖读取期间文件继续增长的竞态。
                    var buffer = new byte[MaximumRepositoryConfigurationBytes + 1];
                    int totalBytes = 0;
                    while (totalBytes < buffer.Length)
                    {
                        int bytesRead = stream.Read(buffer, totalBytes, buffer.Length - totalBytes);
                        if (bytesRead == 0)
                        {
                            break;
                        }
                        totalBytes += bytesRead;
                    }
                    if (totalBytes > MaximumRepositoryConfigurationBytes)
                    {
                        return options;
                    }

                    using (var configurationStream = new MemoryStream(buffer, 0, totalBytes, false))
                    {
                        var configuration = serializer.ReadObject(configurationStream) as Dictionary<string, object>;
                        if (configuration == null)
                        {
                            return options;
                        }

                        string owner = null;
                        string repositoryName = null;
                        string branch = null;
                        foreach (KeyValuePair<string, object> property in configuration)
                        {
                            // 未声明或非字符串属性可能携带本机凭据，公开配置必须整份拒绝。
                            string value = property.Value as string;
                            if (value == null)
                            {
                                return options;
                            }
                            switch (property.Key)
                            {
                                case "owner":
                                    owner = value;
                                    break;
                                case "repositoryName":
                                    repositoryName = value;
                                    break;
                                case "branch":
                                    branch = value;
                                    break;
                                default:
                                    return options;
                            }
                        }

                        options.Owner = owner;
                        if (!string.IsNullOrWhiteSpace(repositoryName))
                        {
                            options.RepositoryName = repositoryName;
                        }
                        if (!string.IsNullOrWhiteSpace(branch))
                        {
                            options.Branch = branch;
                        }
                    }
                }
                options.Validate();
                return options;
            }
            catch (Exception exception)
            {
                if (exception is OutOfMemoryException || exception is StackOverflowException)
                {
                    throw;
                }
                return new DictionaryRepositoryOptions();
            }
        }

        /// <summary>XMZADD 20260831 在启动主题加载异常时回退默认深色主题，确保偏好文件损坏不阻断数据字典主窗口。</summary>
        private void ApplyDefaultTheme()
        {
            ThemeService.Apply(AppTheme.Dark);
        }

        /// <summary>XMZADD 20260831 执行主题加载并在异常时记录本机诊断后继续使用默认主题，隔离非业务配置故障。</summary>
        private static void LoadThemeWithFallback(Action loadTheme, Action applyDefaultTheme, AppDiagnosticLog diagnosticLog)
        {
            try
            {
                loadTheme();
            }
            catch (Exception exception)
            {
                if (diagnosticLog != null)
                {
                    diagnosticLog.WriteUnhandledException("启动主题加载", exception);
                }
                if (applyDefaultTheme != null)
                {
                    applyDefaultTheme();
                }
            }
        }

        /// <summary>XMZADD 20260831 记录界面线程异常，并仅接管明确可恢复的应用或控件故障。</summary>
        private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            _diagnosticLog.WriteUnhandledException("WPF 界面线程未处理异常", e.Exception);
            if (!IsRecoverableDispatcherException(e.Exception))
            {
                return;
            }

            e.Handled = true;
            MessageBox.Show("程序遇到异常但仍可继续使用，请查看本机诊断日志。", "程序异常", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        /// <summary>XMZADD 20260901 仅允许已知业务、输入、文件和 Win32 界面异常继续运行，避免吞掉损坏进程状态的致命故障。</summary>
        private static bool IsRecoverableDispatcherException(Exception exception)
        {
            return exception is InvalidOperationException ||
                exception is ArgumentException ||
                exception is IOException ||
                exception is UnauthorizedAccessException ||
                exception is FormatException ||
                exception is System.ComponentModel.Win32Exception ||
                exception is GitHubDictionaryClientException;
        }

        /// <summary>XMZADD 20260831 记录不可恢复的应用域异常，为进程退出后的排查保留脱敏依据。</summary>
        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Exception exception = e.ExceptionObject as Exception;
            _diagnosticLog.WriteUnhandledException("应用域未处理异常", exception);
        }

        /// <summary>XMZADD 20260831 在程序退出时释放单实例所有权，保证下次可正常启动。</summary>
        protected override void OnExit(ExitEventArgs e)
        {
            if (_singleInstanceGuard != null)
            {
                _singleInstanceGuard.Dispose();
                _singleInstanceGuard = null;
            }
            if (GitHubHttpClient != null)
            {
                GitHubHttpClient.Dispose();
                GitHubHttpClient = null;
            }
            base.OnExit(e);
        }
    }
}
