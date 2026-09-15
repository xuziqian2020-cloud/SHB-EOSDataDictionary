using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using SHB.EosDataDictionary.Models;

namespace SHB.EosDataDictionary.Services
{
    /// <summary>XMZADD 20260915 为名称候选及其证据版本生成稳定指纹以支持可审计否决。</summary>
    public sealed class NameSuggestionFingerprintService
    {
        /// <summary>XMZADD 20260915 使用规范化候选值和排序后的证据位置生成 SHA-256 指纹。</summary>
        public string CreateFingerprint(MetadataValue candidate)
        {
            if (candidate == null)
            {
                return string.Empty;
            }
            return CreateFingerprint(candidate.Value, candidate.Evidence);
        }

        /// <summary>XMZADD 20260915 生成不依赖证据输入顺序和本机绝对目录的候选版本指纹。</summary>
        public string CreateFingerprint(string value, IList<EvidenceItem> evidence)
        {
            NameCandidate normalized = IdentifierTranslationService.NormalizeNameCandidate(value);
            string normalizedValue = (normalized.Value ?? string.Empty).Trim().ToUpperInvariant();
            var descriptors = new List<string>();
            var descriptorKeys = new HashSet<string>(StringComparer.Ordinal);
            if (evidence != null)
            {
                for (int index = 0; index < evidence.Count; index++)
                {
                    EvidenceItem item = evidence[index];
                    if (item == null)
                    {
                        continue;
                    }
                    string descriptor = BuildEvidenceDescriptor(item);
                    if (descriptorKeys.Add(descriptor))
                    {
                        descriptors.Add(descriptor);
                    }
                }
            }
            descriptors.Sort(StringComparer.Ordinal);

            var canonical = new StringBuilder(normalizedValue);
            for (int index = 0; index < descriptors.Count; index++)
            {
                canonical.Append('\n');
                canonical.Append(descriptors[index]);
            }
            using (SHA256 algorithm = SHA256.Create())
            {
                byte[] hash = algorithm.ComputeHash(Encoding.UTF8.GetBytes(canonical.ToString()));
                var hex = new StringBuilder(hash.Length * 2);
                for (int index = 0; index < hash.Length; index++)
                {
                    hex.Append(hash[index].ToString("x2"));
                }
                return hex.ToString();
            }
        }

        /// <summary>XMZADD 20260915 判断候选当前证据版本是否已被维护人员明确否决。</summary>
        public bool IsRejected(IList<string> rejectedFingerprints, MetadataValue candidate)
        {
            if (rejectedFingerprints == null || candidate == null)
            {
                return false;
            }
            string fingerprint = CreateFingerprint(candidate);
            for (int index = 0; index < rejectedFingerprints.Count; index++)
            {
                if (string.Equals(rejectedFingerprints[index], fingerprint, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>XMZADD 20260915 将证据类型、规则、相对路径和行号规范为稳定描述。</summary>
        private static string BuildEvidenceDescriptor(EvidenceItem evidence)
        {
            string sourcePath = (evidence.SourcePath ?? string.Empty).Trim();
            if (Path.IsPathRooted(sourcePath) || sourcePath.IndexOf("..", StringComparison.Ordinal) >= 0)
            {
                sourcePath = Path.GetFileName(sourcePath);
            }
            sourcePath = sourcePath.Replace('\\', '/').ToUpperInvariant();
            return string.Concat(
                (evidence.SourceType ?? string.Empty).Trim().ToUpperInvariant(), "|",
                (evidence.RuleName ?? string.Empty).Trim().ToUpperInvariant(), "|",
                sourcePath, "|", evidence.SourceLine.ToString());
        }
    }
}
