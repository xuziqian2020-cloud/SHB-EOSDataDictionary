using System;

namespace SHB.EosDataDictionary.Services
{
    /// <summary>XMZADD 20260901 集中保存公开共享字典仓库坐标和本地状态键，避免网络路径由用户文本直接拼接。</summary>
    public sealed class DictionaryRepositoryOptions
    {
        /// <summary>XMZADD 20260901 初始化固定仓库名、主分支和 GitHub 官方服务地址，仓库所有者留待部署时明确配置。</summary>
        public DictionaryRepositoryOptions()
        {
            RepositoryName = "SHB-EOSDataDictionary";
            Branch = "main";
            ApiBaseUri = new Uri("https://api.github.com/");
            RawBaseUri = new Uri("https://raw.githubusercontent.com/");
            StateKey = "github-shared-dictionary";
            ScopeKey = "github-shared-dictionary";
        }

        public string Owner { get; set; }
        public string RepositoryName { get; set; }
        public string Branch { get; set; }
        public Uri ApiBaseUri { get; set; }
        public Uri RawBaseUri { get; set; }
        public string StateKey { get; set; }
        public string ScopeKey { get; set; }

        /// <summary>XMZADD 20260901 严格校验仓库坐标和本地键，阻止路径、查询参数或控制字符进入 GitHub 请求。</summary>
        public void Validate()
        {
            ValidateCoordinate(Owner, "仓库所有者");
            ValidateCoordinate(RepositoryName, "仓库名称");
            ValidateCoordinate(Branch, "仓库分支");
            ValidateServiceUri(ApiBaseUri, "api.github.com", "GitHub API 地址");
            ValidateServiceUri(RawBaseUri, "raw.githubusercontent.com", "GitHub 原始文件地址");
            ValidateLocalKey(StateKey, "同步状态键");
            ValidateLocalKey(ScopeKey, "快照作用域键");
        }

        /// <summary>XMZADD 20260901 校验仓库内只含安全路径段的相对路径，拒绝目录逃逸和 URL 参数注入。</summary>
        public static string ValidateRelativePath(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath) || relativePath.Length > 512 ||
                relativePath[0] == '/' || relativePath.IndexOf('\\') >= 0 ||
                relativePath.IndexOf('?') >= 0 || relativePath.IndexOf('#') >= 0 ||
                relativePath.IndexOf(':') >= 0 || relativePath.IndexOf('%') >= 0)
            {
                throw new ArgumentException("仓库相对路径格式无效。", "relativePath");
            }

            string[] segments = relativePath.Split('/');
            if (segments.Length == 0)
            {
                throw new ArgumentException("仓库相对路径格式无效。", "relativePath");
            }
            for (int segmentIndex = 0; segmentIndex < segments.Length; segmentIndex++)
            {
                string segment = segments[segmentIndex];
                if (string.IsNullOrEmpty(segment) || segment == "." || segment == ".." || segment.Length > 128)
                {
                    throw new ArgumentException("仓库相对路径格式无效。", "relativePath");
                }
                for (int characterIndex = 0; characterIndex < segment.Length; characterIndex++)
                {
                    char character = segment[characterIndex];
                    bool safe = (character >= 'a' && character <= 'z') ||
                        (character >= 'A' && character <= 'Z') ||
                        (character >= '0' && character <= '9') ||
                        character == '-' || character == '_' || character == '.';
                    if (!safe)
                    {
                        throw new ArgumentException("仓库相对路径格式无效。", "relativePath");
                    }
                }
            }
            return relativePath;
        }

        /// <summary>XMZADD 20260901 限定仓库坐标为单个 GitHub 安全段，避免所有者、仓库名或分支改变请求路径结构。</summary>
        private static void ValidateCoordinate(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 100 || value != value.Trim())
            {
                throw new ArgumentException(name + "格式无效。", name);
            }
            for (int index = 0; index < value.Length; index++)
            {
                char character = value[index];
                bool safe = (character >= 'a' && character <= 'z') ||
                    (character >= 'A' && character <= 'Z') ||
                    (character >= '0' && character <= '9') ||
                    character == '-' || character == '_' || character == '.';
                if (!safe)
                {
                    throw new ArgumentException(name + "格式无效。", name);
                }
            }
        }

        /// <summary>XMZADD 20260901 要求服务基址使用无凭据、无查询的 HTTPS 根路径，防止 Token 被转发到非预期地址。</summary>
        private static void ValidateServiceUri(Uri value, string expectedHost, string name)
        {
            if (value == null || !value.IsAbsoluteUri ||
                !string.Equals(value.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(value.Host, expectedHost, StringComparison.OrdinalIgnoreCase) ||
                !value.IsDefaultPort || !string.Equals(value.AbsolutePath, "/", StringComparison.Ordinal) ||
                !string.IsNullOrEmpty(value.UserInfo) || !string.IsNullOrEmpty(value.Query) ||
                !string.IsNullOrEmpty(value.Fragment))
            {
                throw new ArgumentException(name + "格式无效。", name);
            }
        }

        /// <summary>XMZADD 20260901 限制本地状态键长度并排除控制字符，保证 SQLite 作用域边界稳定可显示。</summary>
        private static void ValidateLocalKey(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value != value.Trim())
            {
                throw new ArgumentException(name + "格式无效。", name);
            }
            for (int index = 0; index < value.Length; index++)
            {
                if (char.IsControl(value[index]))
                {
                    throw new ArgumentException(name + "格式无效。", name);
                }
            }
        }
    }
}
