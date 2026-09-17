using System;
using System.Collections.Generic;
using System.Globalization;
using SHB.EosDataDictionary.Services;

namespace SHB.EosDataDictionary.Enricher
{
    /// <summary>XMZADD 20260916 提供带金标准质量门禁的 EOS 业务数据字典 V6 离线候选生成入口。</summary>
    internal static class Program
    {
        /// <summary>XMZADD 20260903 解析固定命令行参数并输出可核验的富化覆盖统计。</summary>
        private static int Main(string[] args)
        {
            try
            {
                IDictionary<string, string> values = ParseArguments(args);
                var options = new OfflineDictionaryV1GenerationOptions
                {
                    SourceDatabasePath = ReadRequired(values, "--source-db"),
                    OutputDatabasePath = ReadRequired(values, "--output-db"),
                    ScopeKey = ReadRequired(values, "--scope"),
                    SourceRoot = ReadRequired(values, "--source-root"),
                    KnowledgeBaseRoot = ReadRequired(values, "--knowledge-root"),
                    ReportRoot = ReadRequired(values, "--report-root"),
                    GoldStandardPath = ReadRequired(values, "--gold-standard")
                };
                OfflineDictionaryV1GenerationResult result = new OfflineDictionaryV1Generator().Generate(options);
                BusinessDictionaryV1Report report = result.Report;
                DictionaryV6QualityResult quality = result.Quality;
                Console.WriteLine("EOS 业务数据字典 V6 候选已生成。");
                Console.WriteLine("源库 SHA-256：" + result.SourceSha256);
                Console.WriteLine("输出库 SHA-256：" + result.OutputSha256);
                Console.WriteLine("源码证据：" + result.SourceEvidenceCount);
                Console.WriteLine("表/字段：" + report.TotalTableCount + "/" + report.TotalFieldCount);
                Console.WriteLine("实体表/知识库表：" + report.EntityTableCount + "/" + report.KnowledgeTableCount);
                Console.WriteLine("枚举字段/代码关系：" + report.EnumFieldCount + "/" + report.CodeRelationCount);
                Console.WriteLine("业务/技术/排除/未分类：" + report.BusinessTableCount + "/" +
                                  report.TechnicalTableCount + "/" + report.ExcludedTableCount + "/" +
                                  report.UnclassifiedTableCount);
                Console.WriteLine("实际使用字段：" + report.ActualUsedFieldCount);
                Console.WriteLine("实际使用字段已命名：" + report.ActualUsedNamedFieldCount);
                Console.WriteLine("V6 细化名称：" + report.V4RefinedNameCount);
                Console.WriteLine("实际使用字段待复核：" + report.ActualUsedReviewFieldCount);
                Console.WriteLine("多义冲突：" + report.AmbiguousConflictCount);
                Console.WriteLine("正式名/参考名/关系准确率：" +
                    FormatPercent(quality.OfficialNameAccuracy) + "/" +
                    FormatPercent(quality.SuggestedNameAccuracy) + "/" +
                    FormatPercent(quality.RelationDirectionAccuracy));
                Console.WriteLine("实体表审计：" + quality.AuditedEntityTableCount + "/" +
                                  quality.EntityTableCount);
                if (!quality.CanPromote)
                {
                    Console.Error.WriteLine("V6 候选未通过发布门禁，已保留候选库和审计报告，禁止提升。");
                    for (int index = 0; index < quality.Failures.Count; index++)
                    {
                        Console.Error.WriteLine("- " + quality.Failures[index]);
                    }
                    return 2;
                }
                Console.WriteLine("V6 发布门禁：通过，可进入规范快照提升步骤。");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("V6 生成失败：" + exception);
                return 1;
            }
        }

        /// <summary>XMZADD 20260903 将成对命令行参数解析为大小写不敏感字典，并拒绝残缺输入。</summary>
        private static IDictionary<string, string> ParseArguments(string[] args)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (args == null || args.Length == 0 || args.Length % 2 != 0)
            {
                throw new ArgumentException("参数必须使用 --name value 成对格式。");
            }
            for (int index = 0; index < args.Length; index += 2)
            {
                if (string.IsNullOrWhiteSpace(args[index]) || !args[index].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new ArgumentException("参数名称必须以 -- 开头。");
                }
                result[args[index]] = args[index + 1];
            }
            return result;
        }

        /// <summary>XMZADD 20260903 读取必填参数并为缺失配置提供明确错误。</summary>
        private static string ReadRequired(IDictionary<string, string> values, string name)
        {
            string value;
            if (!values.TryGetValue(name, out value) || string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("缺少必填参数：" + name);
            }
            return value;
        }

        /// <summary>XMZADD 20260916 使用固定两位小数输出质量门禁百分比，便于人工核对。</summary>
        private static string FormatPercent(double value)
        {
            return (value * 100D).ToString("0.00", CultureInfo.InvariantCulture) + "%";
        }
    }
}
