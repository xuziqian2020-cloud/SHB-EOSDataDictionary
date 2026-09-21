using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SHB.EosDataDictionary.Models;

namespace SHB.EosDataDictionary.Services
{
    /// <summary>XMZADD 20260916 从 EOS VB.NET/C# 源码收集带文件角色和读写方向的表、实体、字段及枚举证据。</summary>
    public sealed class EosSourceAnalyzer
    {
        private const int MaxSourceFileCount = 10000;
        private const long MaxSourceFileBytes = 4L * 1024L * 1024L;
        private const long MaxTotalSourceBytes = 256L * 1024L * 1024L;
        private const int MaxEvidenceCount = 250000;
        private const int MaxEvidencePerFile = 10000;
        private static readonly SourceTextDecoder SourceDecoder = new SourceTextDecoder();
        private static readonly Regex ClassRegex = new Regex(@"^\s*(?:<[^>]+>\s*)*(?:(?:Public|Private|Friend|Protected|Partial|MustInherit|NotInheritable|static|partial|public|private|internal|abstract|sealed)\s+)*(?:Class|class)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);
        private static readonly Regex TableRegex = new Regex("\\b(?:TableName|mTable)\\s*(?:As\\s+String\\s*)?=\\s*\"(?<name>[^\"]+)\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex EntityPrimaryKeyRegex = new Regex(
            "\\bmIDCol\\s*(?:As\\s+String\\s*)?=\\s*\"(?<field>[A-Za-z_][A-Za-z0-9_]*)\"",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex PropertyRegex = new Regex(@"^\s*(?:Public|Private|Friend|Protected)?\s*(?:Default\s+)?Property\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)(?:\s*\([^)]*\))?\s*(?:As\s+(?:New\s+)?(?<type>[A-Za-z_][A-Za-z0-9_.]*))?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex CSharpPropertyRegex = new Regex(@"^\s*(?:public|private|protected|internal)\s+(?:static\s+)?(?<type>[A-Za-z_][A-Za-z0-9_<>,.\?\[\]]*)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\{", RegexOptions.Compiled);
        private static readonly Regex EnumRegex = new Regex(@"^\s*(?:Public|Private|Friend|Protected|public|private|internal|protected)?\s*(?:Enum|enum)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);
        private static readonly Regex EnumMemberRegex = new Regex(@"^\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)(?:\s*=\s*(?<value>[^,]+))?\s*,?\s*$", RegexOptions.Compiled);
        private static readonly Regex SqlTableRegex = new Regex(@"\b(?<operation>DELETE\s+FROM|CREATE\s+TABLE|FROM|JOIN|UPDATE|INTO)\s+(?:\[?[A-Za-z_][A-Za-z0-9_]*\]?\.)?\[?(?<name>[A-Za-z_][A-Za-z0-9_]*)\]?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex DynamicTableFieldRegex = new Regex(@"\bf_(?<name>Table_[A-Za-z0-9_]+)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>XMZADD 20260828 扫描指定源码目录并输出带文件行号的 EOS 证据。</summary>
        public IList<SourceEvidence> Analyze(string sourceRoot)
        {
            return Analyze(sourceRoot, CancellationToken.None);
        }

        /// <summary>XMZADD 20260828 扫描 EOS 源码并在用户取消时停止继续读取文件和证据。</summary>
        public IList<SourceEvidence> Analyze(string sourceRoot, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = new List<SourceEvidence>();
            if (string.IsNullOrWhiteSpace(sourceRoot) || !Directory.Exists(sourceRoot))
            {
                result.Add(new SourceEvidence
                {
                    Strength = SourceEvidenceStrength.NamingOnly,
                    UsageKind = SourceUsageKind.Unknown,
                    Evidence = new EvidenceItem
                    {
                        SourceType = "源码目录",
                        SourcePath = string.Empty,
                        RuleName = "SourceRootMissing",
                        Explanation = "源码目录不存在，无法进行实体和模块匹配。"
                    }
                });
                return result;
            }

            string[] files = GetSupportedFiles(sourceRoot, cancellationToken);
            var fileResults = new IList<SourceEvidence>[files.Length];
            var scanBudget = new SourceScanBudget();
            var options = new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount)
            };

            Parallel.For(0, files.Length, options, fileIndex =>
            {
                string file = files[fileIndex];
                var fileEvidence = new List<SourceEvidence>();
                // 任一受支持文件不可读都会使结构证据不完整，因此必须阻断整次发布预览。
                AnalyzeFile(sourceRoot, file, fileEvidence, scanBudget, cancellationToken);
                fileResults[fileIndex] = fileEvidence;
            });

            for (int fileIndex = 0; fileIndex < fileResults.Length; fileIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IList<SourceEvidence> fileEvidence = fileResults[fileIndex];
                if (fileEvidence == null)
                {
                    continue;
                }

                for (int evidenceIndex = 0; evidenceIndex < fileEvidence.Count; evidenceIndex++)
                {
                    if (result.Count >= MaxEvidenceCount)
                    {
                        throw new InvalidDataException("源码证据数量超过安全上限，已终止本次结构扫描。");
                    }
                    result.Add(fileEvidence[evidenceIndex]);
                }
            }

            return result;
        }

        /// <summary>XMZADD 20260831 预先固定可分析文件顺序，为并行扫描保持证据发布顺序。</summary>
        private static string[] GetSupportedFiles(string sourceRoot, CancellationToken cancellationToken)
        {
            var files = new List<string>();
            long totalBytes = 0;
            IEnumerable<string> allFiles = Directory.EnumerateFiles(sourceRoot, "*.*", SearchOption.AllDirectories);
            foreach (string file in allFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsSupportedFile(file) && !IsIgnoredPath(sourceRoot, file))
                {
                    var fileInfo = new FileInfo(file);
                    if (fileInfo.Length > MaxSourceFileBytes)
                    {
                        throw new InvalidDataException("源码文件超过 4MB 安全上限，已终止本次结构扫描。");
                    }

                    totalBytes += fileInfo.Length;
                    if (totalBytes > MaxTotalSourceBytes)
                    {
                        throw new InvalidDataException("源码文件总量超过 256MB 安全上限，已终止本次结构扫描。");
                    }

                    files.Add(file);
                    if (files.Count > MaxSourceFileCount)
                    {
                        throw new InvalidDataException("源码文件数量超过安全上限，已终止本次结构扫描。");
                    }
                }
            }

            return files.ToArray();
        }

        /// <summary>XMZADD 20260828 分析单个源码文件并在逐行提取证据时响应取消请求。</summary>
        private static void AnalyzeFile(string sourceRoot, string file, IList<SourceEvidence> result,
            SourceScanBudget scanBudget, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SourceTextDecodeResult decodeResult;
            string[] originalLines = ReadLines(file, scanBudget, cancellationToken, out decodeResult);
            cancellationToken.ThrowIfCancellationRequested();
            string relativePath = MakeRelativePath(sourceRoot, file);
            SourceFileKind sourceFileKind = GetSourceFileKind(file, originalLines);
            bool canUseChineseEvidence = decodeResult.IsReliable && !decodeResult.HasMojibake;
            if (!canUseChineseEvidence)
            {
                AddEncodingWarning(result, relativePath, decodeResult, scanBudget);
            }
            string[] lines = EosBusinessUsageEvidenceExtractor.MaskCSharpBlockComments(relativePath, originalLines);
            string modulePath = GetModulePath(relativePath);
            string currentEntity = null;
            string currentTable = null;
            string currentEnum = null;
            string lastChineseComment = null;
            int lastChineseCommentLine = -100;
            bool isReadingXmlSummary = false;
            var xmlSummary = new StringBuilder();
            string pendingXmlSummary = null;
            bool hasPendingXmlSummary = false;
            int pendingXmlSummaryLine = -100;
            bool currentEnumIsCSharp = false;
            bool currentEnumBraceOpened = false;
            int currentEnumBraceDepth = 0;
            var sqlObjectsInFile = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string line = lines[lineIndex];
                string originalLine = originalLines[lineIndex];
                string completedXmlSummary;
                if (TryReadXmlSummaryComment(line, ref isReadingXmlSummary, xmlSummary, out completedXmlSummary))
                {
                    if (!isReadingXmlSummary)
                    {
                        pendingXmlSummary = completedXmlSummary;
                        hasPendingXmlSummary = true;
                        pendingXmlSummaryLine = lineIndex;
                    }
                    continue;
                }

                string comment = ExtractChineseComment(line);
                if (!string.IsNullOrWhiteSpace(comment))
                {
                    lastChineseComment = comment;
                    lastChineseCommentLine = lineIndex;
                }
                // 注释可以作为下一条声明的中文上下文，但绝不能直接产生表、字段、枚举或 SQL 证据。
                if (IsCommentOnlyLine(line))
                {
                    continue;
                }

                Match classMatch = ClassRegex.Match(line);
                if (classMatch.Success)
                {
                    currentEntity = classMatch.Groups["name"].Value;
                    currentTable = null;
                    currentEnum = null;
                    string xmlChineseName = GetAdjacentXmlSummary(pendingXmlSummary, hasPendingXmlSummary,
                        pendingXmlSummaryLine, lineIndex);
                    string chineseNameCandidate = xmlChineseName
                        ?? GetAdjacentComment(lastChineseComment, lastChineseCommentLine, lineIndex);
                    bool hasAuthoritativeXmlSummary = !string.IsNullOrWhiteSpace(xmlChineseName);
                    if (currentEntity.StartsWith("t_", StringComparison.OrdinalIgnoreCase))
                    {
                        currentTable = currentEntity.Substring(2);
                        AddEvidence(result, currentTable, null, currentEntity, modulePath, chineseNameCandidate,
                            relativePath, lineIndex + 1, "EntityClassConvention", "从 EOS t_ 实体类约定匹配数据库对象。", originalLine,
                            scanBudget, sourceFileKind, canUseChineseEvidence,
                            hasAuthoritativeXmlSummary: hasAuthoritativeXmlSummary);
                    }
                    else if (currentEntity.StartsWith("Kis_", StringComparison.OrdinalIgnoreCase))
                    {
                        currentTable = currentEntity.Substring(4);
                        AddEvidence(result, currentTable, null, currentEntity, modulePath, chineseNameCandidate,
                            relativePath, lineIndex + 1, "KisEntityClass", "从 EOS 实体类名匹配表名。", originalLine,
                            scanBudget, sourceFileKind, canUseChineseEvidence,
                            hasAuthoritativeXmlSummary: hasAuthoritativeXmlSummary);
                    }
                    hasPendingXmlSummary = false;
                    pendingXmlSummary = null;
                    pendingXmlSummaryLine = -100;
                }

                Match tableMatch = TableRegex.Match(line);
                if (tableMatch.Success)
                {
                    currentTable = tableMatch.Groups["name"].Value;
                    AddEvidence(result, currentTable, null, currentEntity, modulePath, GetAdjacentComment(lastChineseComment, lastChineseCommentLine, lineIndex),
                        relativePath, lineIndex + 1, "TableNameProperty", "从源码 TableName 映射匹配数据库对象。", originalLine,
                        scanBudget, sourceFileKind, canUseChineseEvidence);
                }

                Match entityPrimaryKeyMatch = EntityPrimaryKeyRegex.Match(line);
                if (entityPrimaryKeyMatch.Success && !string.IsNullOrWhiteSpace(currentTable))
                {
                    string primaryKeyField = entityPrimaryKeyMatch.Groups["field"].Value;
                    AddEvidence(result, currentTable, primaryKeyField, currentEntity, modulePath, null,
                        relativePath, lineIndex + 1, "EntityPrimaryKey",
                        "EOS 生成实体的 mIDCol 明确声明该字段是对象主键。", originalLine,
                        scanBudget, sourceFileKind, canUseChineseEvidence);
                }

                Match propertyMatch = PropertyRegex.Match(line);
                if (!propertyMatch.Success)
                {
                    propertyMatch = CSharpPropertyRegex.Match(line);
                }
                if (propertyMatch.Success)
                {
                    string propertyName = propertyMatch.Groups["name"].Value;
                    string propertyTypeName = propertyMatch.Groups["type"].Value;
                    if (!string.IsNullOrWhiteSpace(currentTable) &&
                        propertyName.StartsWith("obj", StringComparison.OrdinalIgnoreCase) &&
                        propertyTypeName.StartsWith("t_", StringComparison.OrdinalIgnoreCase))
                    {
                        // EOS 生成实体的 obj 属性代表对象关联，不应误当成同名数据库字段。
                        AddEvidence(result, currentTable, null, currentEntity, modulePath, null,
                            relativePath, lineIndex + 1, "EntityObjectRelation", "从 EOS 实体对象属性识别代码关联。",
                            originalLine, scanBudget, sourceFileKind, canUseChineseEvidence,
                            null, null, null, propertyTypeName, propertyName,
                            propertyName.Substring(3), propertyTypeName);
                    }
                    else if (!string.IsNullOrWhiteSpace(currentTable))
                    {
                        string xmlChineseName = GetAdjacentXmlSummary(pendingXmlSummary, hasPendingXmlSummary,
                            pendingXmlSummaryLine, lineIndex);
                        string chineseNameCandidate = xmlChineseName
                            ?? GetAdjacentComment(lastChineseComment, lastChineseCommentLine, lineIndex);
                        AddEvidence(result, currentTable, NormalizeEntityFieldName(propertyName), currentEntity,
                            modulePath, chineseNameCandidate, relativePath, lineIndex + 1, "EntityProperty", "从实体属性匹配数据库字段候选。",
                            originalLine, scanBudget, sourceFileKind, canUseChineseEvidence,
                            null, null, null, propertyTypeName, propertyName,
                            hasAuthoritativeXmlSummary: !string.IsNullOrWhiteSpace(xmlChineseName));
                    }
                    hasPendingXmlSummary = false;
                    pendingXmlSummary = null;
                    pendingXmlSummaryLine = -100;
                }

                Match enumMatch = EnumRegex.Match(line);
                if (enumMatch.Success)
                {
                    currentEnum = enumMatch.Groups["name"].Value;
                    if (IsGeneratedOrdinalEnum(currentEnum, currentEntity))
                    {
                        // 表类生成器的 en_ 枚举仅表示字段序号，不能发布为业务状态枚举。
                        currentEnum = null;
                        continue;
                    }
                    currentEnumIsCSharp = string.Equals(Path.GetExtension(file), ".cs", StringComparison.OrdinalIgnoreCase);
                    currentEnumBraceOpened = currentEnumIsCSharp && line.IndexOf('{') >= 0;
                    currentEnumBraceDepth = currentEnumIsCSharp ? CountCharacter(line, '{') - CountCharacter(line, '}') : 0;
                    if (currentEnumIsCSharp && currentEnumBraceOpened && currentEnumBraceDepth <= 0)
                    {
                        currentEnum = null;
                    }
                    continue;
                }

                if (currentEnum != null && line.Trim().StartsWith("End Enum", StringComparison.OrdinalIgnoreCase))
                {
                    currentEnum = null;
                    continue;
                }

                if (currentEnum != null && currentEnumIsCSharp)
                {
                    if (line.IndexOf('{') >= 0)
                    {
                        currentEnumBraceOpened = true;
                    }
                    currentEnumBraceDepth += CountCharacter(line, '{') - CountCharacter(line, '}');
                    if (currentEnumBraceOpened && currentEnumBraceDepth <= 0)
                    {
                        currentEnum = null;
                        continue;
                    }
                }

                if (currentEnum != null)
                {
                    Match memberMatch = EnumMemberRegex.Match(line);
                    if (memberMatch.Success && !string.Equals(memberMatch.Groups["name"].Value, "End", StringComparison.OrdinalIgnoreCase))
                    {
                        AddEvidence(result, currentTable, null, currentEntity, modulePath, GetAdjacentComment(lastChineseComment, lastChineseCommentLine, lineIndex),
                            relativePath, lineIndex + 1, "EnumMember", "从源码枚举成员和值收集枚举证据。",
                            originalLine, scanBudget, sourceFileKind, canUseChineseEvidence,
                            currentEnum, memberMatch.Groups["name"].Value,
                            memberMatch.Groups["value"].Success ? memberMatch.Groups["value"].Value.Trim() : null);
                    }
                }

                string upperLine = line.ToUpperInvariant();
                    bool isDynamicTableUsage = upperLine.IndexOf("CREATE TABLE", StringComparison.Ordinal) >= 0 ||
                                            upperLine.IndexOf("INSERT INTO", StringComparison.Ordinal) >= 0 ||
                                           upperLine.IndexOf(" FROM ", StringComparison.Ordinal) >= 0 ||
                                           upperLine.IndexOf(" JOIN ", StringComparison.Ordinal) >= 0 ||
                                           upperLine.IndexOf("UPDATE ", StringComparison.Ordinal) >= 0 ||
                                           upperLine.IndexOf("DELETE FROM", StringComparison.Ordinal) >= 0;
                if (!string.IsNullOrWhiteSpace(currentTable) && isDynamicTableUsage)
                {
                    MatchCollection dynamicFieldMatches = DynamicTableFieldRegex.Matches(line);
                    for (int dynamicIndex = 0; dynamicIndex < dynamicFieldMatches.Count; dynamicIndex++)
                    {
                        string dynamicFieldName = dynamicFieldMatches[dynamicIndex].Groups["name"].Value;
                        bool isDynamicWrite = upperLine.IndexOf("CREATE TABLE", StringComparison.Ordinal) >= 0 ||
                                              upperLine.IndexOf("INSERT INTO", StringComparison.Ordinal) >= 0 ||
                                              upperLine.IndexOf("UPDATE ", StringComparison.Ordinal) >= 0 ||
                                              upperLine.IndexOf("DELETE FROM", StringComparison.Ordinal) >= 0;
                        string dynamicExplanation = isDynamicWrite
                            ? "字段值作为动态建表、写入、更新或删除的目标物理表名。"
                            : "字段值作为业务查询或访问的来源表名。";
                        AddEvidence(result, currentTable, dynamicFieldName, currentEntity, modulePath, null,
                            relativePath, lineIndex + 1, "DynamicTableFieldUsage", dynamicExplanation, originalLine,
                            scanBudget, sourceFileKind, canUseChineseEvidence);
                    }
                }

                MatchCollection sqlMatches = SqlTableRegex.Matches(line);
                for (int sqlIndex = 0; sqlIndex < sqlMatches.Count; sqlIndex++)
                {
                    Match sqlMatch = sqlMatches[sqlIndex];
                    string sqlObjectName = sqlMatch.Groups["name"].Value;
                    string operation = sqlMatch.Groups["operation"].Value.ToUpperInvariant();
                    bool isWriteTarget = operation == "INTO" || operation == "UPDATE" ||
                                         operation.StartsWith("DELETE", StringComparison.Ordinal) ||
                                         operation.StartsWith("CREATE", StringComparison.Ordinal);
                    string usageKey = sqlObjectName + "|" + (isWriteTarget ? "Write" : "Read");
                    if (sqlObjectsInFile.Add(usageKey))
                    {
                        AddEvidence(result, sqlObjectName, null, null, modulePath, null,
                            relativePath, lineIndex + 1, "SqlTableUsage",
                            isWriteTarget
                                ? "SQL 新增、更新、删除或建表目标确认该表参与业务写入。"
                                : "SQL FROM 或 JOIN 来源表确认该表参与业务读取。",
                            originalLine,
                            scanBudget, sourceFileKind, canUseChineseEvidence);
                    }
                }
            }

            if (IsBusinessUsageSourceFile(file) && sourceFileKind != SourceFileKind.GeneratedEntity)
            {
                IList<SourceEvidence> businessEvidence = new EosBusinessUsageEvidenceExtractor().Extract(
                    relativePath, modulePath, originalLines);
                for (int evidenceIndex = 0; evidenceIndex < businessEvidence.Count; evidenceIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    AddExtractedEvidence(result, businessEvidence[evidenceIndex], scanBudget,
                        sourceFileKind, canUseChineseEvidence);
                }
            }
        }

        /// <summary>XMZADD 20260911 将业务用途证据纳入安全预算并按来源文件统一标记可信度。</summary>
        private static void AddExtractedEvidence(IList<SourceEvidence> result, SourceEvidence evidence,
            SourceScanBudget scanBudget, SourceFileKind sourceFileKind, bool canUseChineseEvidence)
        {
            if (result.Count >= MaxEvidencePerFile)
            {
                throw new InvalidDataException("单个源码文件产生的证据过多，已终止本次结构扫描。");
            }
            if (Interlocked.Increment(ref scanBudget.EvidenceCount) > MaxEvidenceCount)
            {
                throw new InvalidDataException("源码证据数量超过安全上限，已终止本次结构扫描。");
            }
            PrepareEvidence(evidence, sourceFileKind, canUseChineseEvidence);
            result.Add(evidence);
        }

        /// <summary>XMZADD 20260916 添加带来源角色的受限源码证据，并隔离生成代码或乱码中的中文候选。</summary>
        private static void AddEvidence(IList<SourceEvidence> result, string objectName, string fieldName,
            string entityName, string modulePath, string chineseName, string file, int line,
            string ruleName, string explanation, string originalText, SourceScanBudget scanBudget,
            SourceFileKind sourceFileKind, bool canUseChineseEvidence,
            string enumName = null, string enumValue = null,
            string enumRawValue = null, string propertyTypeName = null,
            string propertyName = null, string relationFieldName = null,
            string relationTargetEntity = null, bool hasAuthoritativeXmlSummary = false)
        {
            if (result.Count >= MaxEvidencePerFile)
            {
                throw new InvalidDataException("单个源码文件产生的证据过多，已终止本次结构扫描。");
            }
            if (Interlocked.Increment(ref scanBudget.EvidenceCount) > MaxEvidenceCount)
            {
                throw new InvalidDataException("源码证据数量超过安全上限，已终止本次结构扫描。");
            }

            string safeChineseName = canUseChineseEvidence && sourceFileKind != SourceFileKind.GeneratedEntity
                ? chineseName
                : null;
            var evidence = new SourceEvidence
            {
                ObjectName = objectName,
                FieldName = fieldName,
                EntityName = entityName,
                ModulePath = modulePath,
                ChineseNameCandidate = safeChineseName,
                EnumName = enumName,
                EnumValue = enumValue,
                EnumChineseName = safeChineseName,
                EnumRawValue = enumRawValue,
                PropertyTypeName = propertyTypeName,
                PropertyName = propertyName,
                RelationFieldName = relationFieldName,
                RelationTargetEntity = relationTargetEntity,
                Origin = ToSourceEvidenceOrigin(sourceFileKind),
                Evidence = new EvidenceItem
                {
                    SourceType = GetSourceType(sourceFileKind, canUseChineseEvidence),
                    SourcePath = file,
                    SourceLine = line,
                    RuleName = ruleName,
                    RawValue = enumRawValue ?? objectName ?? fieldName ?? enumValue,
                    OriginalText = originalText,
                    Explanation = explanation
                }
            };
            evidence.Strength = GetEvidenceStrength(evidence, sourceFileKind, canUseChineseEvidence,
                hasAuthoritativeXmlSummary);
            evidence.UsageKind = GetUsageKind(evidence);
            result.Add(evidence);
        }

        /// <summary>XMZADD 20260916 为业务用途提取器结果补充来源角色和强度，并清除不可采信的中文候选。</summary>
        private static void PrepareEvidence(SourceEvidence evidence, SourceFileKind sourceFileKind,
            bool canUseChineseEvidence)
        {
            if (evidence == null)
            {
                return;
            }
            if (!canUseChineseEvidence || sourceFileKind == SourceFileKind.GeneratedEntity)
            {
                evidence.ChineseNameCandidate = null;
                evidence.EnumChineseName = null;
            }
            if (evidence.Evidence != null)
            {
                evidence.Evidence.SourceType = GetSourceType(sourceFileKind, canUseChineseEvidence);
            }
            evidence.Origin = ToSourceEvidenceOrigin(sourceFileKind);
            evidence.Strength = GetEvidenceStrength(evidence, sourceFileKind, canUseChineseEvidence, false);
            evidence.UsageKind = GetUsageKind(evidence);
        }

        /// <summary>XMZADD 20260911 按文件来源和规则确定证据强度，生成代码仅对物理映射保持权威。</summary>
        private static SourceEvidenceStrength GetEvidenceStrength(SourceEvidence evidence,
            SourceFileKind sourceFileKind, bool canUseChineseEvidence, bool hasAuthoritativeXmlSummary)
        {
            if (!canUseChineseEvidence)
            {
                return SourceEvidenceStrength.NamingOnly;
            }
            string ruleName = evidence == null || evidence.Evidence == null
                ? string.Empty
                : evidence.Evidence.RuleName ?? string.Empty;
            if (sourceFileKind == SourceFileKind.GeneratedEntity)
            {
                return IsPhysicalMappingRule(ruleName)
                    ? SourceEvidenceStrength.Authoritative
                    : SourceEvidenceStrength.NamingOnly;
            }
            if (sourceFileKind == SourceFileKind.Designer || sourceFileKind == SourceFileKind.Configuration)
            {
                return SourceEvidenceStrength.Contextual;
            }
            if (sourceFileKind == SourceFileKind.AutoGenerated)
            {
                return SourceEvidenceStrength.Contextual;
            }
            if (string.Equals(ruleName, "SqlFieldRelation", StringComparison.Ordinal) ||
                ruleName.EndsWith("Conflict", StringComparison.Ordinal))
            {
                return SourceEvidenceStrength.Contextual;
            }
            if (sourceFileKind == SourceFileKind.BusinessCode && hasAuthoritativeXmlSummary)
            {
                return SourceEvidenceStrength.Authoritative;
            }
            return sourceFileKind == SourceFileKind.BusinessCode
                ? SourceEvidenceStrength.DirectBusinessCode
                : SourceEvidenceStrength.NamingOnly;
        }

        /// <summary>XMZADD 20260911 识别生成实体能够直接证明的表、字段、属性和对象关联规则。</summary>
        private static bool IsPhysicalMappingRule(string ruleName)
        {
            return string.Equals(ruleName, "EntityClassConvention", StringComparison.Ordinal) ||
                   string.Equals(ruleName, "KisEntityClass", StringComparison.Ordinal) ||
                   string.Equals(ruleName, "TableNameProperty", StringComparison.Ordinal) ||
                   string.Equals(ruleName, "EntityPrimaryKey", StringComparison.Ordinal) ||
                   string.Equals(ruleName, "EntityProperty", StringComparison.Ordinal) ||
                   string.Equals(ruleName, "EntityObjectRelation", StringComparison.Ordinal);
        }

        /// <summary>XMZADD 20260911 从证据规则和明确语句方向标记读写、展示、关系或枚举用途。</summary>
        private static SourceUsageKind GetUsageKind(SourceEvidence evidence)
        {
            if (evidence == null || evidence.Evidence == null)
            {
                return SourceUsageKind.Unknown;
            }
            if (evidence.UsageKind != SourceUsageKind.Unknown)
            {
                return evidence.UsageKind;
            }
            string ruleName = evidence.Evidence.RuleName ?? string.Empty;
            if (ruleName.IndexOf("Caption", StringComparison.OrdinalIgnoreCase) >= 0 ||
                string.Equals(ruleName, "SqlColumnAlias", StringComparison.Ordinal) ||
                string.Equals(ruleName, "SqlDerivedColumnAlias", StringComparison.Ordinal))
            {
                return SourceUsageKind.Display;
            }
            if (ruleName.IndexOf("Relation", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return SourceUsageKind.Relation;
            }
            if (ruleName.IndexOf("Enum", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return SourceUsageKind.Enumeration;
            }

            string explanation = evidence.Evidence.Explanation ?? string.Empty;
            if (string.Equals(ruleName, "EntityFieldAssignment", StringComparison.Ordinal))
            {
                if (explanation.IndexOf("写入", StringComparison.Ordinal) >= 0)
                {
                    return SourceUsageKind.Write;
                }
                if (explanation.IndexOf("读取", StringComparison.Ordinal) >= 0)
                {
                    return SourceUsageKind.Read;
                }
            }
            if (string.Equals(ruleName, "SqlFieldUsage", StringComparison.Ordinal) ||
                string.Equals(ruleName, "SqlTableUsage", StringComparison.Ordinal) ||
                string.Equals(ruleName, "DynamicTableFieldUsage", StringComparison.Ordinal))
            {
                if (explanation.IndexOf("写入", StringComparison.Ordinal) >= 0 ||
                    explanation.IndexOf("更新", StringComparison.Ordinal) >= 0 ||
                    explanation.IndexOf("删除", StringComparison.Ordinal) >= 0 ||
                    explanation.IndexOf("建表", StringComparison.Ordinal) >= 0 ||
                    explanation.IndexOf("目标列", StringComparison.Ordinal) >= 0 ||
                    explanation.IndexOf("目标表", StringComparison.Ordinal) >= 0)
                {
                    return SourceUsageKind.Write;
                }
                return SourceUsageKind.Read;
            }
            return SourceUsageKind.Unknown;
        }

        /// <summary>XMZADD 20260911 返回稳定的来源标签，使后续推断能区分生成、设计器和业务源码。</summary>
        private static string GetSourceType(SourceFileKind sourceFileKind, bool canUseChineseEvidence)
        {
            if (!canUseChineseEvidence)
            {
                return "EOS源码（编码不可靠）";
            }
            if (sourceFileKind == SourceFileKind.GeneratedEntity)
            {
                return "EOS生成实体";
            }
            if (sourceFileKind == SourceFileKind.Designer)
            {
                return "EOS设计器";
            }
            if (sourceFileKind == SourceFileKind.AutoGenerated)
            {
                return "EOS自动生成源码";
            }
            if (sourceFileKind == SourceFileKind.BusinessCode)
            {
                return "EOS业务源码";
            }
            return "EOS配置源码";
        }

        /// <summary>XMZADD 20260916 将扫描器内部文件类型转换为模块归属可依赖的公开来源角色。</summary>
        private static SourceEvidenceOrigin ToSourceEvidenceOrigin(SourceFileKind sourceFileKind)
        {
            switch (sourceFileKind)
            {
                case SourceFileKind.GeneratedEntity:
                    return SourceEvidenceOrigin.GeneratedEntity;
                case SourceFileKind.Designer:
                    return SourceEvidenceOrigin.Designer;
                case SourceFileKind.BusinessCode:
                    return SourceEvidenceOrigin.BusinessCode;
                case SourceFileKind.Configuration:
                    return SourceEvidenceOrigin.Configuration;
                case SourceFileKind.AutoGenerated:
                    return SourceEvidenceOrigin.AutoGenerated;
                default:
                    return SourceEvidenceOrigin.Unknown;
            }
        }

        /// <summary>XMZADD 20260911 记录不可可靠解码的文件而不保留乱码正文，供冲突报告解释证据缺失。</summary>
        private static void AddEncodingWarning(IList<SourceEvidence> result, string relativePath,
            SourceTextDecodeResult decodeResult, SourceScanBudget scanBudget)
        {
            var warning = new SourceEvidence
            {
                Strength = SourceEvidenceStrength.NamingOnly,
                UsageKind = SourceUsageKind.Unknown,
                Evidence = new EvidenceItem
                {
                    SourceType = "EOS源码（编码不可靠）",
                    SourcePath = relativePath,
                    SourceLine = 0,
                    RuleName = "SourceEncodingUnreliable",
                    Explanation = "源码编码为 " + (decodeResult == null ? "Unknown" : decodeResult.EncodingName) +
                                  "，存在乱码或无法严格解码，中文内容未进入名称候选。"
                }
            };
            AddExtractedEvidence(result, warning, scanBudget, SourceFileKind.Unknown, false);
        }

        /// <summary>XMZADD 20260903 将 EOS 生成属性的 f_ 前缀还原为数据库物理字段名。</summary>
        private static string NormalizeEntityFieldName(string propertyName)
        {
            string value = propertyName ?? string.Empty;
            return value.StartsWith("f_", StringComparison.OrdinalIgnoreCase) && value.Length > 2
                ? value.Substring(2)
                : value;
        }

        /// <summary>XMZADD 20260903 识别与当前表实体同名的生成字段序号枚举，防止污染业务枚举。</summary>
        private static bool IsGeneratedOrdinalEnum(string enumName, string entityName)
        {
            if (string.IsNullOrWhiteSpace(enumName) || string.IsNullOrWhiteSpace(entityName) ||
                !entityName.StartsWith("t_", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            return string.Equals(enumName, "en_" + entityName.Substring(2), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>XMZADD 20260911 以固定字节上限读取源码并通过确定性解码器返回可审计的编码结论。</summary>
        private static string[] ReadLines(string file, SourceScanBudget scanBudget,
            CancellationToken cancellationToken, out SourceTextDecodeResult decodeResult)
        {
            byte[] bytes;
            using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var output = new MemoryStream())
            {
                var buffer = new byte[8192];
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int bytesRead = input.Read(buffer, 0, buffer.Length);
                    if (bytesRead == 0)
                    {
                        break;
                    }
                    if (output.Length + bytesRead > MaxSourceFileBytes)
                    {
                        throw new InvalidDataException("源码文件在扫描期间超过 4MB 安全上限。");
                    }
                    if (Interlocked.Add(ref scanBudget.TotalBytes, bytesRead) > MaxTotalSourceBytes)
                    {
                        throw new InvalidDataException("源码文件在扫描期间总量超过 256MB 安全上限。");
                    }
                    output.Write(buffer, 0, bytesRead);
                }
                bytes = output.ToArray();
            }
            decodeResult = SourceDecoder.Decode(bytes);
            return decodeResult.Text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        }

        private static bool IsSupportedFile(string file)
        {
            string extension = Path.GetExtension(file);
            return string.Equals(extension, ".vb", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(extension, ".cs", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(extension, ".xml", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(extension, ".config", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>XMZADD 20260905 仅允许真实源码文件进入业务字段用途提取，配置文本继续沿用原有结构扫描。</summary>
        private static bool IsBusinessUsageSourceFile(string file)
        {
            string extension = Path.GetExtension(file);
            return string.Equals(extension, ".vb", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(extension, ".cs", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>XMZADD 20260911 排除依赖、构建、日志和备份目录，阻止历史副本污染当前业务证据。</summary>
        private static bool IsIgnoredPath(string sourceRoot, string file)
        {
            string normalizedRoot = Path.GetFullPath(sourceRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string normalizedFile = Path.GetFullPath(file);
            string relativePath = normalizedFile.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
                ? normalizedFile.Substring(normalizedRoot.Length)
                : normalizedFile;
            string normalized = "\\" + relativePath.Replace('/', '\\');
            return normalized.IndexOf("\\bin\\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   normalized.IndexOf("\\obj\\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   normalized.IndexOf("\\.git\\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   normalized.IndexOf("\\publish\\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   normalized.IndexOf("\\packages\\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   normalized.IndexOf("\\_codex_testdata\\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   normalized.IndexOf("\\.vs\\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   normalized.IndexOf("\\node_modules\\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   normalized.IndexOf("\\logs\\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   normalized.IndexOf("\\log\\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   normalized.IndexOf("\\backup\\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   normalized.IndexOf("\\temp\\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   normalized.IndexOf("\\tmp\\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   normalized.IndexOf("\\history\\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   normalized.IndexOf("\\TestResults\\", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>XMZADD 20260911 根据生成标记、设计器后缀和源码扩展名区分证据来源语境。</summary>
        private static SourceFileKind GetSourceFileKind(string file, string[] lines)
        {
            if (IsGeneratedEntityFile(lines))
            {
                return SourceFileKind.GeneratedEntity;
            }
            string fileName = Path.GetFileName(file) ?? string.Empty;
            if (fileName.EndsWith(".Designer.vb", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase))
            {
                return SourceFileKind.Designer;
            }
            if (IsAutoGeneratedFile(lines))
            {
                return SourceFileKind.AutoGenerated;
            }
            string extension = Path.GetExtension(file);
            if (string.Equals(extension, ".vb", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".cs", StringComparison.OrdinalIgnoreCase))
            {
                return SourceFileKind.BusinessCode;
            }
            return SourceFileKind.Configuration;
        }

        /// <summary>XMZADD 20260911 仅检查源码头部的表类生成标记，避免业务正文中的相同文字误判文件性质。</summary>
        private static bool IsGeneratedEntityFile(string[] lines)
        {
            int maximumLineCount = Math.Min(lines == null ? 0 : lines.Length, 40);
            for (int lineIndex = 0; lineIndex < maximumLineCount; lineIndex++)
            {
                string line = lines[lineIndex] ?? string.Empty;
                if (ClassRegex.IsMatch(line))
                {
                    return false;
                }
                string comment;
                if (!TryGetHeaderComment(line, out comment))
                {
                    continue;
                }
                if (comment.StartsWith("表-类生成代码", StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>XMZADD 20260911 识别通用工具生成标记，使自动报表源码只能提供参考上下文。</summary>
        private static bool IsAutoGeneratedFile(string[] lines)
        {
            int maximumLineCount = Math.Min(lines == null ? 0 : lines.Length, 40);
            for (int lineIndex = 0; lineIndex < maximumLineCount; lineIndex++)
            {
                string line = lines[lineIndex] ?? string.Empty;
                if (ClassRegex.IsMatch(line))
                {
                    return false;
                }
                string comment;
                if (!TryGetHeaderComment(line, out comment))
                {
                    continue;
                }
                string normalized = comment.Replace("-", string.Empty).Replace("_", string.Empty);
                if (normalized.IndexOf("<autogenerated>", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    normalized.IndexOf("autogenerated", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    comment.IndexOf("此代码由工具生成", StringComparison.Ordinal) >= 0 ||
                    comment.IndexOf("自动生成的代码", StringComparison.Ordinal) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>XMZADD 20260911 读取文件开头的 VB、C# 单行或块注释正文以限制生成标记作用域。</summary>
        private static bool TryGetHeaderComment(string line, out string comment)
        {
            string value = (line ?? string.Empty).Trim();
            comment = null;
            if (value.StartsWith("'", StringComparison.Ordinal))
            {
                comment = value.TrimStart('\'').Trim();
                return true;
            }
            if (value.StartsWith("//", StringComparison.Ordinal))
            {
                comment = value.Substring(2).Trim();
                return true;
            }
            if (value.StartsWith("/*", StringComparison.Ordinal) || value.StartsWith("*", StringComparison.Ordinal))
            {
                comment = value.Trim(' ', '/', '*').Trim();
                return true;
            }
            return false;
        }

        /// <summary>XMZADD 20260901 在线程间共享源码实际读取字节和证据数量，限制并行扫描的总资源占用。</summary>
        private sealed class SourceScanBudget
        {
            public long TotalBytes;
            public int EvidenceCount;
        }

        /// <summary>XMZADD 20260911 定义源码文件对业务含义的证明语境。</summary>
        private enum SourceFileKind
        {
            Unknown = 0,
            GeneratedEntity = 1,
            Designer = 2,
            BusinessCode = 3,
            Configuration = 4,
            AutoGenerated = 5
        }

        private static string ExtractChineseComment(string line)
        {
            string trimmedLine = line.TrimStart();
            if (trimmedLine.StartsWith("'''", StringComparison.Ordinal))
            {
                return ExtractChineseText(trimmedLine.Substring(3));
            }
            if (trimmedLine.StartsWith("///", StringComparison.Ordinal))
            {
                return ExtractChineseText(trimmedLine.Substring(3));
            }

            int apostrophe = line.IndexOf('\'');
            int slash = line.IndexOf("//", StringComparison.Ordinal);
            int index = apostrophe >= 0 && slash >= 0 ? Math.Min(apostrophe, slash) : Math.Max(apostrophe, slash);
            if (index < 0 || index + 1 >= line.Length)
            {
                return null;
            }

            string comment = line.Substring(index + (line[index] == '/' ? 2 : 1)).Trim();
            return ExtractChineseText(comment);
        }

        /// <summary>XMZADD 20260831 提取 XML 摘要中的中文业务名称，避免注释标记混入数据字典候选值。</summary>
        private static bool TryReadXmlSummaryComment(string line, ref bool isReadingXmlSummary,
            StringBuilder xmlSummary, out string completedXmlSummary)
        {
            completedXmlSummary = null;
            string trimmedLine = line.TrimStart();
            if (!trimmedLine.StartsWith("'''", StringComparison.Ordinal) && !trimmedLine.StartsWith("///", StringComparison.Ordinal))
            {
                return false;
            }

            string content = trimmedLine.Substring(3).Trim();
            int summaryStart = content.IndexOf("<summary", StringComparison.OrdinalIgnoreCase);
            if (!isReadingXmlSummary && summaryStart < 0)
            {
                return false;
            }

            if (!isReadingXmlSummary)
            {
                int summaryStartEnd = content.IndexOf('>', summaryStart);
                if (summaryStartEnd < 0)
                {
                    return false;
                }
                isReadingXmlSummary = true;
                xmlSummary.Length = 0;
                content = content.Substring(summaryStartEnd + 1);
            }

            int summaryEnd = content.IndexOf("</summary>", StringComparison.OrdinalIgnoreCase);
            if (summaryEnd >= 0)
            {
                AppendXmlSummaryText(xmlSummary, content.Substring(0, summaryEnd));
                isReadingXmlSummary = false;
                completedXmlSummary = ExtractChineseText(xmlSummary.ToString());
                xmlSummary.Length = 0;
                return true;
            }

            AppendXmlSummaryText(xmlSummary, content);
            return true;
        }

        /// <summary>XMZADD 20260831 合并多行 XML 摘要，使换行不会割裂同一个表或字段的业务名称。</summary>
        private static void AppendXmlSummaryText(StringBuilder xmlSummary, string content)
        {
            string text = Regex.Replace(content ?? string.Empty, @"<[^>]+>", string.Empty).Trim();
            if (text.Length == 0)
            {
                return;
            }
            if (xmlSummary.Length > 0)
            {
                xmlSummary.Append(" ");
            }
            xmlSummary.Append(text);
        }

        /// <summary>XMZADD 20260831 仅保留含中文的注释文本，避免代码标记成为业务名称候选值。</summary>
        private static string ExtractChineseText(string text)
        {
            string value = Regex.Replace(text ?? string.Empty, @"<[^>]+>", string.Empty).Trim();
            return Regex.IsMatch(value, "[\\u4e00-\\u9fff]") ? value : null;
        }

        /// <summary>XMZADD 20260901 将证据文件限制为源码根目录内的 POSIX 相对路径，防止公开本机绝对路径。</summary>
        private static string MakeRelativePath(string root, string file)
        {
            string normalizedRoot = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string normalizedFile = Path.GetFullPath(file);
            if (normalizedFile.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return normalizedFile.Substring(normalizedRoot.Length).Replace('\\', '/');
            }

            throw new InvalidDataException("源码文件不在配置的源码根目录内。");
        }

        private static string GetModulePath(string relativePath)
        {
            string normalized = relativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            string[] parts = normalized.Split(new[] { Path.DirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                return "未分类";
            }
            if (string.Equals(parts[0], "ERP", StringComparison.OrdinalIgnoreCase))
            {
                return parts.Length > 2 ? parts[1] : "其他";
            }
            return parts[0].EndsWith(".vb", StringComparison.OrdinalIgnoreCase) || parts[0].EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                ? "其他"
                : parts[0];
        }

        /// <summary>XMZADD 20260831 仅采用紧邻声明的普通中文注释，阻断跨方法复用旧注释造成的错误表名。</summary>
        private static string GetAdjacentComment(string comment, int commentLine, int declarationLine)
        {
            return declarationLine - commentLine <= 1 ? comment : null;
        }

        /// <summary>XMZADD 20260831 仅把紧邻声明的 XML 摘要作为名称证据，避免跨常量或方法沿用旧摘要。</summary>
        private static string GetAdjacentXmlSummary(string summary, bool hasSummary, int summaryLine, int declarationLine)
        {
            return hasSummary && declarationLine - summaryLine <= 1 ? summary : null;
        }

        /// <summary>XMZADD 20260831 统计 C# 枚举大括号层级，使枚举证据在右括号处准确结束。</summary>
        private static int CountCharacter(string value, char character)
        {
            int count = 0;
            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] == character)
                {
                    count++;
                }
            }
            return count;
        }

        /// <summary>XMZADD 20260831 判断当前行是否完全属于注释，避免注释中的旧 SQL 被当作有效代码证据。</summary>
        private static bool IsCommentOnlyLine(string line)
        {
            string value = (line ?? string.Empty).TrimStart();
            return value.StartsWith("'", StringComparison.Ordinal) ||
                   value.StartsWith("//", StringComparison.Ordinal) ||
                   IsVisualBasicRemComment(value);
        }

        /// <summary>XMZADD 20260905 仅把独立 REM 关键字识别为 VB 注释，避免误伤 Remote 和 Remark 代码。</summary>
        private static bool IsVisualBasicRemComment(string value)
        {
            if (!value.StartsWith("REM", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            return value.Length == 3 || char.IsWhiteSpace(value[3]);
        }
    }

    /// <summary>XMZADD 20260911 表示源码证据可支持业务名称或物理映射的强度。</summary>
    public enum SourceEvidenceStrength
    {
        NamingOnly = 0,
        Contextual = 1,
        DirectBusinessCode = 2,
        Authoritative = 3
    }

    /// <summary>XMZADD 20260911 表示业务源码中表或字段被明确用于何种操作。</summary>
    public enum SourceUsageKind
    {
        Unknown = 0,
        Read = 1,
        Write = 2,
        Display = 3,
        Relation = 4,
        Enumeration = 5
    }

    /// <summary>XMZADD 20260916 表示源码证据来自生成实体、Designer、业务代码或其他文件角色。</summary>
    public enum SourceEvidenceOrigin
    {
        Unknown = 0,
        GeneratedEntity = 1,
        Designer = 2,
        BusinessCode = 3,
        Configuration = 4,
        AutoGenerated = 5
    }

    /// <summary>XMZADD 20260916 表示源码分析器发现的一条带来源角色的 EOS 映射、名称或业务用途证据。</summary>
    public sealed class SourceEvidence
    {
        public string ObjectName { get; set; }
        public string FieldName { get; set; }
        public string EntityName { get; set; }
        public string ModulePath { get; set; }
        public string ChineseNameCandidate { get; set; }
        public string EnumName { get; set; }
        public string EnumValue { get; set; }
        public string EnumChineseName { get; set; }
        public string EnumRawValue { get; set; }
        public string PropertyTypeName { get; set; }
        public string PropertyName { get; set; }
        public string RelationFieldName { get; set; }
        public string RelationTargetEntity { get; set; }
        public string RelationTargetObjectName { get; set; }
        public string RelationTargetFieldName { get; set; }
        public string BusinessIdentifierCandidate { get; set; }
        public SourceEvidenceStrength Strength { get; set; }
        public SourceUsageKind UsageKind { get; set; }
        public SourceEvidenceOrigin Origin { get; set; }
        public EvidenceItem Evidence { get; set; }
    }
}
