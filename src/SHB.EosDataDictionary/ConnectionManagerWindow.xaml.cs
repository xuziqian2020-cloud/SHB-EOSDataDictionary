using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Windows;
using System.Windows.Controls;
using SHB.EosDataDictionary.Models;
using SHB.EosDataDictionary.Services;

namespace SHB.EosDataDictionary
{
    /// <summary>XMZADD 20260828 提供 EOS SQL Server 连接配置的增删改查和连通性测试。</summary>
    public partial class ConnectionManagerWindow : Window
    {
        private readonly ConnectionProfileStore _store;
        private IList<ConnectionProfile> _profiles;
        private ConnectionProfile _editingProfile;

        public ConnectionManagerWindow()
        {
            InitializeComponent();
            _store = new ConnectionProfileStore(AppPathService.GetLocalDatabasePath());
            ReloadProfiles();
            if (_profiles.Count == 0)
            {
                NewProfile();
            }
            else
            {
                ProfileList.SelectedIndex = 0;
            }
        }

        public ConnectionProfile SelectedProfile { get; private set; }

        private void ReloadProfiles()
        {
            _profiles = _store.LoadAll();
            ProfileList.ItemsSource = _profiles;
        }

        private void ProfileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var profile = ProfileList.SelectedItem as ConnectionProfile;
            if (profile != null)
            {
                _editingProfile = Clone(profile);
                SetEditor(_editingProfile);
            }
        }

        private void NewButton_Click(object sender, RoutedEventArgs e)
        {
            NewProfile();
        }

        private void NewProfile()
        {
            _editingProfile = new ConnectionProfile { Name = "新 EOS 连接" };
            SetEditor(_editingProfile);
            ProfileList.SelectedIndex = -1;
            MessageText.Text = "正在编辑新连接";
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            var profile = ProfileList.SelectedItem as ConnectionProfile;
            if (profile == null)
            {
                return;
            }
            if (MessageBox.Show(this, "确定删除本机连接配置“" + profile.Name + "”吗？\r\n不会删除 EOS 数据库。", "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return;
            }
            _store.Delete(profile.Id);
            ReloadProfiles();
            if (_profiles.Count > 0)
            {
                ProfileList.SelectedIndex = 0;
            }
            else
            {
                NewProfile();
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ReadEditor();
                _store.Save(_editingProfile);
                SelectedProfile = Clone(_editingProfile);
                ReloadProfiles();
                SelectProfile(_editingProfile.Id);
                MessageText.Text = "已保存到本机，密码已 DPAPI 加密";
            }
            catch (Exception ex)
            {
                MessageText.Text = ex.Message;
            }
        }

        private void TestButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ReadEditor();
                using (var connection = new SqlConnection(_editingProfile.BuildConnectionString()))
                {
                    connection.Open();
                    const string sql = "SELECT DB_NAME();";
                    ReadOnlySqlGuard.Validate(sql);
                    using (SqlCommand command = new SqlCommand(sql, connection))
                    {
                        string databaseName = Convert.ToString(command.ExecuteScalar());
                        MessageText.Text = "连接成功：" + databaseName;
                    }
                }
            }
            catch (Exception ex)
            {
                MessageText.Text = "连接失败：" + ex.Message;
            }
        }

        private void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ReadEditor();
                _store.Save(_editingProfile);
                SelectedProfile = Clone(_editingProfile);
                DialogResult = true;
            }
            catch (Exception ex)
            {
                MessageText.Text = ex.Message;
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void ReadEditor()
        {
            int port;
            if (!int.TryParse(PortBox.Text, out port) || port <= 0 || port > 65535)
            {
                throw new InvalidOperationException("端口必须是 1 到 65535 的数字。");
            }
            if (string.IsNullOrWhiteSpace(NameBox.Text) || string.IsNullOrWhiteSpace(ServerBox.Text) || string.IsNullOrWhiteSpace(DatabaseBox.Text))
            {
                throw new InvalidOperationException("连接名称、服务器和数据库不能为空。");
            }

            _editingProfile.Name = NameBox.Text.Trim();
            _editingProfile.Server = ServerBox.Text.Trim();
            _editingProfile.Port = port;
            _editingProfile.Database = DatabaseBox.Text.Trim();
            _editingProfile.AuthenticationMode = AuthModeBox.SelectedIndex == 1 ? AuthenticationMode.SqlServer : AuthenticationMode.Windows;
            _editingProfile.UserName = UserNameBox.Text.Trim();
            _editingProfile.Password = PasswordBox.Password;
            _editingProfile.TrustServerCertificate = TrustCertificateBox.IsChecked == true;
            // 数据字典只提供结构查询，连接配置不得绕过只读访问边界。
            _editingProfile.IsReadOnly = true;
        }

        private void SetEditor(ConnectionProfile profile)
        {
            NameBox.Text = profile.Name ?? string.Empty;
            ServerBox.Text = profile.Server ?? string.Empty;
            PortBox.Text = profile.Port.ToString();
            DatabaseBox.Text = profile.Database ?? string.Empty;
            AuthModeBox.SelectedIndex = profile.AuthenticationMode == AuthenticationMode.SqlServer ? 1 : 0;
            UserNameBox.Text = profile.UserName ?? string.Empty;
            PasswordBox.Password = profile.Password ?? string.Empty;
            TrustCertificateBox.IsChecked = profile.TrustServerCertificate;
            ReadOnlyBox.IsChecked = true;
        }

        private void SelectProfile(string profileId)
        {
            for (int i = 0; i < _profiles.Count; i++)
            {
                if (_profiles[i].Id == profileId)
                {
                    ProfileList.SelectedIndex = i;
                    return;
                }
            }
        }

        private static ConnectionProfile Clone(ConnectionProfile profile)
        {
            return new ConnectionProfile
            {
                Id = profile.Id,
                Name = profile.Name,
                Server = profile.Server,
                Port = profile.Port,
                Database = profile.Database,
                AuthenticationMode = profile.AuthenticationMode,
                UserName = profile.UserName,
                Password = profile.Password,
                TrustServerCertificate = profile.TrustServerCertificate,
                IsReadOnly = profile.IsReadOnly
            };
        }
    }
}
