using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SHB.EosDataDictionary.Services
{
    /// <summary>XMZADD 20260901 提供 GitHub Token 的延迟读取边界，使网络客户端只在需要署名或提交时请求凭据。</summary>
    public interface IGitHubTokenProvider
    {
        /// <summary>XMZADD 20260901 获取当前 Windows 用户已配置的 GitHub Token，未配置时返回空值。</summary>
        string GetToken();
    }

    /// <summary>XMZADD 20260901 使用 Windows 当前用户 DPAPI 保存 GitHub 个人 Token，禁止明文凭据落盘。</summary>
    public sealed class GitHubCredentialStore : IGitHubTokenProvider
    {
        private static readonly byte[] AdditionalEntropy = Encoding.UTF8.GetBytes("SHB.EosDataDictionary.GitHub.Token.v1");
        private readonly string _credentialPath;

        /// <summary>XMZADD 20260901 初始化可注入的本机凭据路径，便于正式环境和隔离测试使用独立文件。</summary>
        public GitHubCredentialStore(string credentialPath)
        {
            if (string.IsNullOrWhiteSpace(credentialPath))
            {
                throw new ArgumentException("GitHub 凭据路径不能为空。", "credentialPath");
            }
            _credentialPath = Path.GetFullPath(credentialPath);
        }

        /// <summary>XMZADD 20260901 用当前 Windows 用户身份加密并原子替换 GitHub Token 文件。</summary>
        public void Save(string token)
        {
            ValidateToken(token);
            byte[] clearBytes = Encoding.UTF8.GetBytes(token);
            byte[] protectedBytes = null;
            string temporaryPath = null;
            try
            {
                protectedBytes = ProtectedData.Protect(clearBytes, AdditionalEntropy, DataProtectionScope.CurrentUser);
                string directory = Path.GetDirectoryName(_credentialPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                temporaryPath = _credentialPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(temporaryPath, protectedBytes);
                if (File.Exists(_credentialPath))
                {
                    File.Replace(temporaryPath, _credentialPath, null);
                }
                else
                {
                    File.Move(temporaryPath, _credentialPath);
                }
            }
            catch (Exception exception)
            {
                if (exception is CryptographicException || exception is IOException || exception is UnauthorizedAccessException)
                {
                    throw new InvalidOperationException("GitHub 凭据无法保存。");
                }
                throw;
            }
            finally
            {
                Array.Clear(clearBytes, 0, clearBytes.Length);
                if (protectedBytes != null)
                {
                    Array.Clear(protectedBytes, 0, protectedBytes.Length);
                }
                if (!string.IsNullOrEmpty(temporaryPath) && File.Exists(temporaryPath))
                {
                    try
                    {
                        File.Delete(temporaryPath);
                    }
                    catch (IOException)
                    {
                    }
                }
            }
        }

        /// <summary>XMZADD 20260901 解密当前 Windows 用户的 GitHub Token，缺少配置时返回空值。</summary>
        public string Load()
        {
            if (!File.Exists(_credentialPath))
            {
                return null;
            }
            byte[] protectedBytes = null;
            byte[] clearBytes = null;
            try
            {
                protectedBytes = File.ReadAllBytes(_credentialPath);
                if (protectedBytes.Length == 0 || protectedBytes.Length > 65536)
                {
                    throw new CryptographicException();
                }
                clearBytes = ProtectedData.Unprotect(protectedBytes, AdditionalEntropy, DataProtectionScope.CurrentUser);
                string token = new UTF8Encoding(false, true).GetString(clearBytes);
                ValidateToken(token);
                return token;
            }
            catch (Exception exception)
            {
                if (exception is CryptographicException || exception is IOException ||
                    exception is UnauthorizedAccessException || exception is DecoderFallbackException ||
                    exception is ArgumentException)
                {
                    throw new InvalidOperationException("GitHub 凭据无法读取，请重新配置个人 Token。");
                }
                throw;
            }
            finally
            {
                if (protectedBytes != null)
                {
                    Array.Clear(protectedBytes, 0, protectedBytes.Length);
                }
                if (clearBytes != null)
                {
                    Array.Clear(clearBytes, 0, clearBytes.Length);
                }
            }
        }

        /// <summary>XMZADD 20260901 为网络客户端提供延迟 Token 读取，保持匿名下载不接触凭据。</summary>
        public string GetToken()
        {
            return Load();
        }

        /// <summary>XMZADD 20260901 清除本机加密 Token，使后续提交必须重新配置身份。</summary>
        public void Clear()
        {
            try
            {
                if (File.Exists(_credentialPath))
                {
                    File.Delete(_credentialPath);
                }
            }
            catch (Exception exception)
            {
                if (exception is IOException || exception is UnauthorizedAccessException)
                {
                    throw new InvalidOperationException("GitHub 凭据无法清除。");
                }
                throw;
            }
        }

        /// <summary>XMZADD 20260901 限制 Token 为无控制字符的合理长度，避免空凭据或异常载荷进入认证头。</summary>
        private static void ValidateToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token) || token.Length > 4096 || token != token.Trim())
            {
                throw new ArgumentException("GitHub Token 格式无效。", "token");
            }
            for (int index = 0; index < token.Length; index++)
            {
                if (char.IsControl(token[index]))
                {
                    throw new ArgumentException("GitHub Token 格式无效。", "token");
                }
            }
        }
    }
}
