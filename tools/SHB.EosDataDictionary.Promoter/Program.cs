using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using SHB.EosDataDictionary.Models;
using SHB.EosDataDictionary.Services;

namespace SHB.EosDataDictionary.Promoter
{
    /// <summary>XMZADD 20260909 提供不执行 Git 提交、推送或 GitHub API 的规范快照提升命令行入口。</summary>
    internal static class Program
    {
        /// <summary>XMZADD 20260909 核对隔离仓库和候选数据库后生成下一规范修订及同载荷本地数据库。</summary>
        private static int Main(string[] args)
        {
            string temporaryOutputPath = null;
            try
            {
                IDictionary<string, string> values = ParseArguments(args);
                string sourceDatabasePath = Path.GetFullPath(ReadRequired(values, "--source-db"));
                string outputDatabasePath = Path.GetFullPath(ReadRequired(values, "--output-db"));
                string scopeKey = ReadRequired(values, "--scope");
                string repositoryRoot = Path.GetFullPath(ReadRequired(values, "--repository-root"));
                string expectedHead = ReadRequired(values, "--expected-head");
                long expectedRevision = ParseRevision(ReadRequired(values, "--expected-revision"));
                string expectedSha256 = ReadRequired(values, "--expected-sha256");
                ValidatePaths(sourceDatabasePath, outputDatabasePath, repositoryRoot);
                ValidateGitHead(repositoryRoot, expectedHead);

                SnapshotData candidate;
                if (!new SnapshotStore(sourceDatabasePath).TryLoadLatest(scopeKey, out candidate) || candidate == null)
                {
                    throw new InvalidDataException("源数据库缺少指定作用域的候选快照。");
                }
                if (candidate.Revision != expectedRevision)
                {
                    throw new InvalidDataException("候选数据库修订号与预期基线不一致。");
                }

                string outputDirectory = Path.GetDirectoryName(outputDatabasePath);
                if (!string.IsNullOrWhiteSpace(outputDirectory))
                {
                    Directory.CreateDirectory(outputDirectory);
                }
                temporaryOutputPath = outputDatabasePath + ".promotion-" + Guid.NewGuid().ToString("N") + ".tmp";
                File.Copy(sourceDatabasePath, temporaryOutputPath, false);
                // 提前初始化并读回副本，降低仓库文件生成后才发现 SQLite 副本不可写的风险。
                SnapshotData copiedCandidate;
                if (!new SnapshotStore(temporaryOutputPath).TryLoadLatest(scopeKey, out copiedCandidate) || copiedCandidate == null)
                {
                    throw new InvalidDataException("候选数据库副本无法读取。");
                }

                CanonicalSnapshotPromotionResult result = new CanonicalSnapshotPromotionService().Prepare(
                    repositoryRoot,
                    candidate,
                    expectedRevision,
                    expectedSha256,
                    DateTime.UtcNow);
                byte[] promotedPayload = File.ReadAllBytes(result.LatestSnapshotPath);
                new SnapshotStore(temporaryOutputPath).ReplaceScopeFromRemote(
                    scopeKey,
                    promotedPayload,
                    result.SnapshotSha256,
                    result.Manifest.FormatVersion,
                    result.Manifest.Revision,
                    System.Threading.CancellationToken.None);
                File.Move(temporaryOutputPath, outputDatabasePath);
                temporaryOutputPath = null;

                Console.WriteLine("EOS 规范数据字典 Revision " +
                    result.Manifest.Revision.ToString(CultureInfo.InvariantCulture) + " 已生成。");
                Console.WriteLine("快照 SHA-256：" + result.SnapshotSha256);
                Console.WriteLine("表/字段：" + result.TableCount.ToString(CultureInfo.InvariantCulture) + "/" +
                    result.FieldCount.ToString(CultureInfo.InvariantCulture));
                Console.WriteLine("实体/枚举项/关系：" + result.EntityCount.ToString(CultureInfo.InvariantCulture) + "/" +
                    result.EnumItemCount.ToString(CultureInfo.InvariantCulture) + "/" +
                    result.RelationCount.ToString(CultureInfo.InvariantCulture));
                Console.WriteLine("输出数据库：" + outputDatabasePath);
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("规范快照提升失败：" + exception.Message);
                return 1;
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(temporaryOutputPath) && File.Exists(temporaryOutputPath))
                {
                    try
                    {
                        // 临时文件只属于本次未完成输出，失败时清除以便安全重试。
                        File.Delete(temporaryOutputPath);
                    }
                    catch (Exception cleanupException)
                    {
                        // 清理失败不应掩盖规范快照提升的原始失败原因。
                        Console.Error.WriteLine("临时数据库清理失败：" + cleanupException.Message);
                    }
                }
            }
        }

        /// <summary>XMZADD 20260909 将成对命令行参数解析为大小写不敏感字典并拒绝重复或残缺输入。</summary>
        private static IDictionary<string, string> ParseArguments(string[] args)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (args == null || args.Length == 0 || args.Length % 2 != 0)
            {
                throw new ArgumentException("参数必须使用 --name value 成对格式。");
            }
            for (int index = 0; index < args.Length; index += 2)
            {
                string name = args[index];
                if (string.IsNullOrWhiteSpace(name) || !name.StartsWith("--", StringComparison.Ordinal) ||
                    result.ContainsKey(name))
                {
                    throw new ArgumentException("参数名称无效或重复。");
                }
                result.Add(name, args[index + 1]);
            }
            return result;
        }

        /// <summary>XMZADD 20260909 读取必填参数并为缺失配置提供明确错误。</summary>
        private static string ReadRequired(IDictionary<string, string> values, string name)
        {
            string value;
            if (!values.TryGetValue(name, out value) || string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("缺少必填参数：" + name);
            }
            return value.Trim();
        }

        /// <summary>XMZADD 20260909 解析非负规范修订号，拒绝溢出和非数字基线。</summary>
        private static long ParseRevision(string text)
        {
            long revision;
            if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out revision) || revision < 0)
            {
                throw new ArgumentException("预期修订号必须是非负整数。");
            }
            return revision;
        }

        /// <summary>XMZADD 20260909 拒绝覆盖既有输出或把源库、输出库和仓库目录混用。</summary>
        private static void ValidatePaths(string sourceDatabasePath, string outputDatabasePath, string repositoryRoot)
        {
            if (!File.Exists(sourceDatabasePath))
            {
                throw new FileNotFoundException("源数据库不存在。", sourceDatabasePath);
            }
            if (File.Exists(outputDatabasePath))
            {
                throw new IOException("输出数据库已经存在，禁止覆盖。");
            }
            if (string.Equals(sourceDatabasePath, outputDatabasePath, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("源数据库与输出数据库不能相同。");
            }
            if (!Directory.Exists(Path.Combine(repositoryRoot, ".git")) ||
                !File.Exists(Path.Combine(repositoryRoot, "snapshot", "manifest.json")))
            {
                throw new DirectoryNotFoundException("仓库目录缺少 .git 或 snapshot/manifest.json。");
            }
        }

        /// <summary>XMZADD 20260909 绑定本地工作树精确 HEAD，防止在计划外代码或数据基线上生成修订。</summary>
        private static void ValidateGitHead(string repositoryRoot, string expectedHead)
        {
            if (!IsGitSha(expectedHead))
            {
                throw new ArgumentException("预期 Git HEAD 格式无效。");
            }
            var startInfo = new ProcessStartInfo
            {
                FileName = "git.exe",
                Arguments = "rev-parse HEAD",
                WorkingDirectory = repositoryRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            using (Process process = Process.Start(startInfo))
            {
                string actualHead = process.StandardOutput.ReadToEnd().Trim();
                process.StandardError.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0 || !string.Equals(actualHead, expectedHead, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("本地工作树 HEAD 已偏离预期基线。");
                }
            }
        }

        /// <summary>XMZADD 20260909 验证完整 Git 提交哈希，避免缩写哈希绕过精确并发保护。</summary>
        private static bool IsGitSha(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length != 40)
            {
                return false;
            }
            for (int index = 0; index < value.Length; index++)
            {
                char character = value[index];
                if (!((character >= '0' && character <= '9') ||
                      (character >= 'a' && character <= 'f') ||
                      (character >= 'A' && character <= 'F')))
                {
                    return false;
                }
            }
            return true;
        }
    }
}
