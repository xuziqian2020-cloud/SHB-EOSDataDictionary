using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using SHB.EosDataDictionary.Models;

namespace SHB.EosDataDictionary.Services
{
    /// <summary>XMZADD 20260904 从已读取的 EOS 源码行中提取 SQL 字段、界面标题和实体字段赋值证据。</summary>
    public sealed class EosBusinessUsageEvidenceExtractor
    {
        private const int MaximumSqlContinuationLines = 32;
        private const string UnknownSqlObjectPlaceholder = "__EOS_DYNAMIC_OBJECT__";
        private static readonly Regex EntityVariableRegex = new Regex(
            @"\bDim\s+(?<variable>[A-Za-z_][A-Za-z0-9_]*)\s+As\s+(?:New\s+)?t_(?<table>[A-Za-z_][A-Za-z0-9_]*)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SqlKeywordRegex = new Regex(
            @"\b(?:SELECT|FROM|JOIN|WHERE|GROUP\s+BY|ORDER\s+BY|INSERT\s+INTO|UPDATE)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SqlTableReferenceRegex = new Regex(
            @"\b(?<clause>FROM|JOIN|UPDATE|INTO)\s+(?:(?:\[[^\]]+\]|[A-Za-z_][A-Za-z0-9_]*)\s*\.\s*)?(?<table>\[[^\]]+\]|[A-Za-z_][A-Za-z0-9_]*)(?:\s+(?:AS\s+)?(?<alias>\[[^\]]+\]|[A-Za-z_][A-Za-z0-9_]*))?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex QualifiedFieldRegex = new Regex(
            @"(?<alias>\[[^\]]+\]|[A-Za-z_][A-Za-z0-9_]*)\s*\.\s*(?<field>\[[^\]]+\]|[A-Za-z_][A-Za-z0-9_]*)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SqlRelationRegex = new Regex(
            @"(?<leftAlias>\[[^\]]+\]|[A-Za-z_][A-Za-z0-9_]*)\s*\.\s*(?<leftField>\[[^\]]+\]|[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?<rightAlias>\[[^\]]+\]|[A-Za-z_][A-Za-z0-9_]*)\s*\.\s*(?<rightField>\[[^\]]+\]|[A-Za-z_][A-Za-z0-9_]*)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex InsertColumnsRegex = new Regex(
            @"\bINSERT\s+INTO\s+(?:(?:\[[^\]]+\]|[A-Za-z_][A-Za-z0-9_]*)\s*\.\s*)?(?<table>\[[^\]]+\]|[A-Za-z_][A-Za-z0-9_]*)\s*\((?<columns>[^)]*)\)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex UpdateSetRegex = new Regex(
            @"\bUPDATE\s+(?:(?:\[[^\]]+\]|[A-Za-z_][A-Za-z0-9_]*)\s*\.\s*)?(?<table>\[[^\]]+\]|[A-Za-z_][A-Za-z0-9_]*)(?:\s+(?:AS\s+)?(?<alias>\[[^\]]+\]|[A-Za-z_][A-Za-z0-9_]*))?\s+SET\s+(?<values>.*?)(?:\bWHERE\b|\bGROUP\s+BY\b|\bORDER\s+BY\b|$)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex UpdateFieldRegex = new Regex(
            @"(?:\b(?:\[[^\]]+\]|[A-Za-z_][A-Za-z0-9_]*)\s*\.\s*)?(?<field>\[[^\]]+\]|[A-Za-z_][A-Za-z0-9_]*)\s*=",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex InsertColumnRegex = new Regex(
            @"(?:^|,)\s*(?<field>\[[^\]]+\]|[A-Za-z_][A-Za-z0-9_]*)\s*(?=,|$)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SqlOutputAliasRegex = new Regex(
            @"\bAS\s+(?:\[[^\]]+\]|[A-Za-z_][A-Za-z0-9_]*)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SqlVariableAssignmentRegex = new Regex(
            @"^\s*(?:Dim\s+)?(?<target>[A-Za-z_][A-Za-z0-9_]*)(?:\s+As\s+(?:New\s+)?[A-Za-z_][A-Za-z0-9_.]*(?:\s*\([^)]*\))?)?\s*=\s*(?<expression>.*)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SqlAppendExpressionRegex = new Regex(
            @"^\s*(?<source>[A-Za-z_][A-Za-z0-9_]*)\s*(?:&|\+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SqlCompoundAmpersandAssignmentRegex = new Regex(
            @"^\s*(?<target>[A-Za-z_][A-Za-z0-9_]*)\s*&=\s*(?<expression>.*)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SelectModifierOnlyRegex = new Regex(
            @"^(?:(?:DISTINCT|ALL)(?:\s+|$))?(?:TOP\s*(?:\(\s*[^)]+\s*\)|[0-9]+)\s*(?:PERCENT\s+)?(?:WITH\s+TIES\s*)?)?$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SelectModifierPrefixRegex = new Regex(
            @"^\s*(?:(?:DISTINCT|ALL)\s+)?(?:TOP\s*(?:\(\s*[^)]+\s*\)|[0-9]+)\s*(?:PERCENT\s+)?(?:WITH\s+TIES\s*)?)?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SelectKeywordOnlyRegex = new Regex(
            @"\bSELECT\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex FromKeywordOnlyRegex = new Regex(
            @"\bFROM\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex DirectSelectFieldRegex = new Regex(
            @"^(?:(?<alias>\[[^\]]+\]|[A-Za-z_][A-Za-z0-9_]*)\s*\.\s*)?(?<field>\[[^\]]+\]|[A-Za-z_][A-Za-z0-9_]*)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SqlCteNameRegex = new Regex(
            @"(?:\bWITH|,)\s*(?<name>\[[^\]]+\]|[A-Za-z_][A-Za-z0-9_]*)\s*(?:\([^)]*\)\s*)?AS\s*\(",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ForwardEntityAssignmentRegex = new Regex(
            @"\b(?<variable>[A-Za-z_][A-Za-z0-9_]*)\s*\.\s*f_(?<field>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?<candidate>[^'\r\n]+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ReverseEntityAssignmentRegex = new Regex(
            @"\b(?<candidate>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?<variable>[A-Za-z_][A-Za-z0-9_]*)\s*\.\s*f_(?<field>[A-Za-z_][A-Za-z0-9_]*)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex DirectCaptionRegex = new Regex(
            @"\.\s*(?<collection>Cols|Columns)\s*\(\s*""(?<field>[^""]+)""\s*\)\s*\.\s*Caption\s*=\s*""(?<caption>[^""]+)""",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex DataColumnVariableRegex = new Regex(
            @"\bDim\s+(?<variable>[A-Za-z_][A-Za-z0-9_]*)\s+As\s+New\s+DataColumn\s*\(\s*""(?<field>[^""]+)""",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex VariableCaptionRegex = new Regex(
            @"\b(?<variable>[A-Za-z_][A-Za-z0-9_]*)\s*\.\s*Caption\s*=\s*""(?<caption>[^""]+)""",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ResourceFieldCaptionRegex = new Regex(
            @"\.\s*(?<collection>Cols|Columns)\s*\(\s*""(?<field>[^""]+)""\s*\)\s*\.\s*Caption\s*=\s*(?<expression>GetResourceText\s*\([^\r\n]*\))",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ResourceVariableCaptionRegex = new Regex(
            @"\b(?<variable>[A-Za-z_][A-Za-z0-9_]*)\s*\.\s*Caption\s*=\s*(?<expression>GetResourceText\s*\([^\r\n]*\))",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ResourceFallbackRegex = new Regex(
            @"GetResourceText\s*\(\s*[^,\r\n]+,\s*(?:""(?<doubleCaption>[^""]*[\u4e00-\u9fff][^""]*)""|'(?<singleCaption>[^']*[\u4e00-\u9fff][^']*)')\s*\)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex DataMapAssignmentRegex = new Regex(
            @"\.\s*(?:Cols|Columns)\s*\(\s*""(?<field>[^""]+)""\s*\)\s*\.\s*DataMap\s*=\s*(?<source>.+)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex MapVariableDeclarationRegex = new Regex(
            @"^\s*Dim\s+(?<variable>[A-Za-z_][A-Za-z0-9_]*)\s+(?:As\s+(?:New\s+)?(?:System\.Collections\.Specialized\.)?(?:ListDictionary|Hashtable|SortedList|Dictionary\s*\([^)]*\))\b|=\s*New\s+(?:System\.Collections\.Specialized\.)?(?:ListDictionary|Hashtable|SortedList|Dictionary\s*\([^)]*\))\b)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex MapAddCallRegex = new Regex(
            @"\b(?<variable>[A-Za-z_][A-Za-z0-9_]*)\s*\.\s*Add\s*\(",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ConstantMapAddRegex = new Regex(
            @"\b(?<variable>[A-Za-z_][A-Za-z0-9_]*)\s*\.\s*Add\s*\(\s*(?<value>-?\d+(?:\.\d+)?|True|False|""(?:[^""]|"""")*"")\s*,\s*""(?<caption>(?:[^""]|"""")*)""\s*\)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex InlineMapInitializerRegex = new Regex(
            @"^\s*New\s+(?:System\.Collections\.Specialized\.)?(?:ListDictionary|Hashtable|SortedList|Dictionary\s*\([^)]*\))\s*(?:\(\s*\))?\s+From\s+(?<items>\{.*\})\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex InlineMapPairRegex = new Regex(
            @"\{\s*(?<value>-?\d+(?:\.\d+)?|True|False|""(?:[^""]|"""")*"")\s*,\s*""(?<caption>(?:[^""]|"""")*)""\s*\}",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SqlSimpleCaseRegex = new Regex(
            @"^\s*CASE\s+(?<fieldRef>(?:(?:\[[^\]]+\]|[A-Za-z_][A-Za-z0-9_]*)\s*\.\s*)?(?:\[[^\]]+\]|[A-Za-z_][A-Za-z0-9_]*))\s+(?:WHEN\s+(?<value>-?\d+(?:\.\d+)?|N?'(?:''|[^'])*')\s+THEN\s+(?<caption>N?'(?:''|[^'])+')\s*)+(?:ELSE\s+N?'(?:''|[^'])*'\s*)?END\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex SqlSearchedCaseRegex = new Regex(
            @"^\s*CASE\s+(?:WHEN\s+(?<fieldRef>(?:(?:\[[^\]]+\]|[A-Za-z_][A-Za-z0-9_]*)\s*\.\s*)?(?:\[[^\]]+\]|[A-Za-z_][A-Za-z0-9_]*))\s*=\s*(?<value>-?\d+(?:\.\d+)?|N?'(?:''|[^'])*')\s+THEN\s+(?<caption>N?'(?:''|[^'])+')\s*)+(?:ELSE\s+N?'(?:''|[^'])*'\s*)?END\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex VbSelectCaseEntityFieldRegex = new Regex(
            @"^\s*Select\s+Case\s+(?:CType\s*\(\s*)?(?<variable>[A-Za-z_][A-Za-z0-9_]*)\s*\.\s*f_(?<field>[A-Za-z_][A-Za-z0-9_]*)(?:\s*,\s*[A-Za-z_][A-Za-z0-9_.]*)?\s*\)?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex VbCaseConstantRegex = new Regex(
            @"^\s*Case\s+(?<value>-?\d+(?:\.\d+)?|True|False|""(?:[^""]|"""")*"")\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex VbCaseElseRegex = new Regex(
            @"^\s*Case\s+Else\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex VbReturnCaptionRegex = new Regex(
            @"^\s*Return\s+""(?<caption>(?:[^""]|"""")+)""\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex VbAssignmentCaptionRegex = new Regex(
            @"^\s*(?<target>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*""(?<caption>(?:[^""]|"""")+)""\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex IdentifierRegex = new Regex(
            @"\[?(?<identifier>[A-Za-z_][A-Za-z0-9_]*)\]?",
            RegexOptions.Compiled);

        /// <summary>XMZADD 20260904 在不重新读取文件或执行 SQL 的前提下提取并去重真实业务字段用途。</summary>
        public IList<SourceEvidence> Extract(string relativePath, string modulePath, string[] lines)
        {
            var result = new List<SourceEvidence>();
            if (lines == null || lines.Length == 0)
            {
                return result;
            }

            string sourcePath = relativePath ?? string.Empty;
            string sourceModule = modulePath ?? string.Empty;
            string[] codeLines = MaskCSharpBlockComments(sourcePath, lines);
            var deduplicationKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var entityTables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var dataColumnFields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var fieldOwners = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            CollectDeclarations(codeLines, entityTables, dataColumnFields);
            ExtractEntityAssignments(sourcePath, sourceModule, codeLines, lines, entityTables, fieldOwners,
                result, deduplicationKeys);

            IList<SqlBlock> sqlBlocks = BuildSqlBlocks(codeLines);
            for (int blockIndex = 0; blockIndex < sqlBlocks.Count; blockIndex++)
            {
                ExtractSqlBlock(sourcePath, sourceModule, lines, sqlBlocks[blockIndex], fieldOwners,
                    result, deduplicationKeys);
            }

            ExtractDataMapEnumerations(sourcePath, sourceModule, codeLines, lines, fieldOwners,
                result, deduplicationKeys);
            ExtractVbSelectCaseEnumerations(sourcePath, sourceModule, codeLines, lines, entityTables,
                result, deduplicationKeys);
            ExtractCaptions(sourcePath, sourceModule, codeLines, lines, dataColumnFields, fieldOwners,
                result, deduplicationKeys);
            return result;
        }

        /// <summary>XMZADD 20260904 预先收集实体变量和 DataColumn 变量，使声明顺序不影响同文件字段归属。</summary>
        private static void CollectDeclarations(string[] lines, IDictionary<string, string> entityTables,
            IDictionary<string, string> dataColumnFields)
        {
            for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                string line = lines[lineIndex] ?? string.Empty;
                if (IsCommentOnlyLine(line))
                {
                    continue;
                }

                MatchCollection entityMatches = EntityVariableRegex.Matches(line);
                for (int matchIndex = 0; matchIndex < entityMatches.Count; matchIndex++)
                {
                    entityTables[entityMatches[matchIndex].Groups["variable"].Value] =
                        entityMatches[matchIndex].Groups["table"].Value;
                }

                MatchCollection dataColumnMatches = DataColumnVariableRegex.Matches(line);
                for (int matchIndex = 0; matchIndex < dataColumnMatches.Count; matchIndex++)
                {
                    dataColumnFields[dataColumnMatches[matchIndex].Groups["variable"].Value] =
                        NormalizeIdentifier(dataColumnMatches[matchIndex].Groups["field"].Value);
                }
            }
        }

        /// <summary>XMZADD 20260904 从实体字段正向及反向赋值中确认字段所属物理表和业务值线索。</summary>
        private static void ExtractEntityAssignments(string sourcePath, string modulePath, string[] codeLines,
            string[] originalLines,
            IDictionary<string, string> entityTables, IDictionary<string, HashSet<string>> fieldOwners,
            IList<SourceEvidence> result, ISet<string> deduplicationKeys)
        {
            for (int lineIndex = 0; lineIndex < codeLines.Length; lineIndex++)
            {
                string line = codeLines[lineIndex] ?? string.Empty;
                if (IsCommentOnlyLine(line))
                {
                    continue;
                }

                MatchCollection forwardMatches = ForwardEntityAssignmentRegex.Matches(line);
                for (int matchIndex = 0; matchIndex < forwardMatches.Count; matchIndex++)
                {
                    Match match = forwardMatches[matchIndex];
                    string tableName;
                    if (!entityTables.TryGetValue(match.Groups["variable"].Value, out tableName))
                    {
                        continue;
                    }

                    string fieldName = NormalizeIdentifier(match.Groups["field"].Value);
                    string candidate = ExtractBusinessIdentifier(match.Groups["candidate"].Value);
                    AddFieldOwner(fieldOwners, fieldName, tableName);
                    AddEvidence(result, deduplicationKeys, tableName, fieldName, "t_" + tableName,
                        modulePath, null, candidate, sourcePath, lineIndex + 1, "EntityFieldAssignment",
                        match.Value, GetOriginalLine(originalLines, lineIndex + 1),
                        "实体变量赋值确认该字段在实际业务代码中被写入。",
                        usageKind: SourceUsageKind.Write);
                }

                MatchCollection reverseMatches = ReverseEntityAssignmentRegex.Matches(line);
                for (int matchIndex = 0; matchIndex < reverseMatches.Count; matchIndex++)
                {
                    Match match = reverseMatches[matchIndex];
                    string tableName;
                    if (!entityTables.TryGetValue(match.Groups["variable"].Value, out tableName))
                    {
                        continue;
                    }

                    string fieldName = NormalizeIdentifier(match.Groups["field"].Value);
                    string candidate = ExtractBusinessIdentifier(match.Groups["candidate"].Value);
                    AddFieldOwner(fieldOwners, fieldName, tableName);
                    AddEvidence(result, deduplicationKeys, tableName, fieldName, "t_" + tableName,
                        modulePath, null, candidate, sourcePath, lineIndex + 1, "EntityFieldAssignment",
                        match.Value, GetOriginalLine(originalLines, lineIndex + 1),
                        "实体变量反向赋值确认该字段在实际业务代码中被读取。",
                        usageKind: SourceUsageKind.Read);
                }
            }
        }

        /// <summary>XMZADD 20260904 把 VB 连续字符串限制在三十二行窗口内，防止远距离别名被错误关联。</summary>
        private static IList<SqlBlock> BuildSqlBlocks(string[] lines)
        {
            var result = new List<SqlBlock>();
            int lineIndex = 0;
            while (lineIndex < lines.Length)
            {
                string firstLine = lines[lineIndex] ?? string.Empty;
                if (IsCommentOnlyLine(firstLine))
                {
                    lineIndex++;
                    continue;
                }

                int startLineIndex = lineIndex;
                int endLineIndex = lineIndex;
                var text = new StringBuilder(firstLine);
                // 旧 VB 界面常把一条 SQL 拆为多行，固定窗口可阻断远处同名别名污染当前语句。
                while (endLineIndex + 1 < lines.Length &&
                       endLineIndex - startLineIndex + 1 < MaximumSqlContinuationLines &&
                       HasVisualBasicContinuation(lines[endLineIndex]))
                {
                    if (IsCommentOnlyLine(lines[endLineIndex + 1]))
                    {
                        break;
                    }
                    endLineIndex++;
                    text.Append('\n');
                    text.Append(lines[endLineIndex] ?? string.Empty);
                }

                string assignedVariable;
                bool hasAssignedVariable = TryGetSqlAssignedVariable(firstLine, out assignedVariable);
                while (hasAssignedVariable && endLineIndex + 1 < lines.Length &&
                       endLineIndex - startLineIndex + 1 < MaximumSqlContinuationLines &&
                       IsSameVariableSqlAppend(lines[endLineIndex + 1], assignedVariable))
                {
                    endLineIndex++;
                    text.Append('\n');
                    text.Append(lines[endLineIndex] ?? string.Empty);
                    // 同变量追加自身也可能继续拆行，仍受同一个三十二行物理窗口约束。
                    while (endLineIndex + 1 < lines.Length &&
                           endLineIndex - startLineIndex + 1 < MaximumSqlContinuationLines &&
                           HasVisualBasicContinuation(lines[endLineIndex]))
                    {
                        if (IsCommentOnlyLine(lines[endLineIndex + 1]))
                        {
                            break;
                        }
                        endLineIndex++;
                        text.Append('\n');
                        text.Append(lines[endLineIndex] ?? string.Empty);
                    }
                }

                string aliasText = NormalizeVisualBasicSqlText(text.ToString());
                // SQL 常量只是筛选值，屏蔽后可避免状态文本和模糊查询内容伪造成物理字段。
                string blockText = MaskSqlSingleQuotedLiterals(aliasText);
                bool[] commentCharacters = BuildSqlCommentCharacterMap(blockText);
                // SQL 注释不参与物理表归属，但必须等长屏蔽以保持源码证据行号准确。
                blockText = MaskCharactersPreservingLines(blockText, commentCharacters);
                aliasText = MaskCharactersPreservingLines(aliasText, commentCharacters);
                if (SqlKeywordRegex.IsMatch(blockText))
                {
                    result.Add(new SqlBlock(startLineIndex, endLineIndex, blockText, aliasText));
                }
                lineIndex = endLineIndex + 1;
            }
            return result;
        }

        /// <summary>XMZADD 20260911 解析单个受限 SQL 窗口并区分目标写入、来源读取及等值关联证据。</summary>
        private static void ExtractSqlBlock(string sourcePath, string modulePath, string[] lines,
            SqlBlock block, IDictionary<string, HashSet<string>> fieldOwners,
            IList<SourceEvidence> result, ISet<string> deduplicationKeys)
        {
            var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var cteNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectSqlCteNames(block.Text, cteNames);
            CollectSqlTables(block.Text, aliases, tables, cteNames);
            if (tables.Count == 0)
            {
                return;
            }

            ExtractSqlColumnAliases(sourcePath, modulePath, lines, block, cteNames, fieldOwners,
                result, deduplicationKeys);

            // 写目标必须先于通用字段引用进入去重集合，避免 UPDATE 左值被同一语句中的读取证据覆盖。
            ExtractInsertFields(sourcePath, modulePath, lines, block, fieldOwners, result, deduplicationKeys);
            ExtractUpdateFields(sourcePath, modulePath, lines, block, aliases, fieldOwners,
                result, deduplicationKeys);

            MatchCollection fieldMatches = QualifiedFieldRegex.Matches(block.Text);
            for (int matchIndex = 0; matchIndex < fieldMatches.Count; matchIndex++)
            {
                Match match = fieldMatches[matchIndex];
                string tableName;
                if (!aliases.TryGetValue(NormalizeIdentifier(match.Groups["alias"].Value), out tableName))
                {
                    continue;
                }

                string fieldName = NormalizeIdentifier(match.Groups["field"].Value);
                AddFieldOwner(fieldOwners, fieldName, tableName);
                int sourceLine = GetSourceLine(block, match.Groups["field"].Index);
                AddEvidence(result, deduplicationKeys, tableName, fieldName, null, modulePath,
                    null, fieldName, sourcePath, sourceLine, "SqlFieldUsage", match.Value,
                    GetOriginalLine(lines, sourceLine), "SQL 字段引用确认该物理字段参与业务读取。",
                    usageKind: SourceUsageKind.Read);
            }

            MatchCollection relationMatches = SqlRelationRegex.Matches(block.Text);
            for (int matchIndex = 0; matchIndex < relationMatches.Count; matchIndex++)
            {
                Match match = relationMatches[matchIndex];
                string leftTable;
                string rightTable;
                if (!aliases.TryGetValue(NormalizeIdentifier(match.Groups["leftAlias"].Value), out leftTable) ||
                    !aliases.TryGetValue(NormalizeIdentifier(match.Groups["rightAlias"].Value), out rightTable))
                {
                    continue;
                }

                string leftField = NormalizeIdentifier(match.Groups["leftField"].Value);
                string rightField = NormalizeIdentifier(match.Groups["rightField"].Value);
                // 数据字典需要从连接两侧追溯字段关系，因此同一等值条件保留双向端点。
                int leftSourceLine = GetSourceLine(block, match.Groups["leftField"].Index);
                AddEvidence(result, deduplicationKeys, leftTable, leftField, null, modulePath,
                    null, leftField, sourcePath, leftSourceLine, "SqlFieldRelation", match.Value,
                    GetOriginalLine(lines, leftSourceLine),
                    "SQL 等值连接确认两个物理字段之间的业务关系。", rightTable, rightField,
                    SourceUsageKind.Relation);
                int rightSourceLine = GetSourceLine(block, match.Groups["rightField"].Index);
                AddEvidence(result, deduplicationKeys, rightTable, rightField, null, modulePath,
                    null, rightField, sourcePath, rightSourceLine, "SqlFieldRelation", match.Value,
                    GetOriginalLine(lines, rightSourceLine),
                    "SQL 等值连接确认两个物理字段之间的反向业务关系。", leftTable, leftField,
                    SourceUsageKind.Relation);
            }

            if (tables.Count == 1)
            {
                string onlyTable = GetOnlyValue(tables);
                ExtractSingleTableClauseFields(sourcePath, modulePath, lines, block, onlyTable,
                    aliases, cteNames, fieldOwners, result, deduplicationKeys);
            }
        }

        /// <summary>XMZADD 20260904 建立 SQL 表别名到物理表的映射并排除被误捕获的关键字。</summary>
        private static void CollectSqlTables(string sqlText, IDictionary<string, string> aliases,
            ISet<string> tables, ISet<string> cteNames)
        {
            MatchCollection matches = SqlTableReferenceRegex.Matches(sqlText);
            for (int matchIndex = 0; matchIndex < matches.Count; matchIndex++)
            {
                Match match = matches[matchIndex];
                string tableName = NormalizeIdentifier(match.Groups["table"].Value);
                if (string.IsNullOrWhiteSpace(tableName) || IsSqlKeyword(tableName) ||
                    IsUnknownSqlObject(tableName) || cteNames.Contains(tableName))
                {
                    continue;
                }

                tables.Add(tableName);
                aliases[tableName] = tableName;
                string alias = NormalizeIdentifier(match.Groups["alias"].Value);
                if (!string.IsNullOrWhiteSpace(alias) && !IsSqlKeyword(alias))
                {
                    if (!string.Equals(alias, tableName, StringComparison.OrdinalIgnoreCase))
                    {
                        // UPDATE 别名会先被表正则暂存，FROM 的真实表出现后应移除该伪物理对象。
                        tables.Remove(alias);
                    }
                    aliases[alias] = tableName;
                }
            }
        }

        /// <summary>XMZADD 20260905 收集当前 SQL 的 CTE 名称，避免把逻辑结果集发布为物理数据库表。</summary>
        private static void CollectSqlCteNames(string sqlText, ISet<string> cteNames)
        {
            MatchCollection matches = SqlCteNameRegex.Matches(sqlText ?? string.Empty);
            for (int matchIndex = 0; matchIndex < matches.Count; matchIndex++)
            {
                string cteName = NormalizeIdentifier(matches[matchIndex].Groups["name"].Value);
                if (!string.IsNullOrWhiteSpace(cteName))
                {
                    cteNames.Add(cteName);
                }
            }
        }

        /// <summary>XMZADD 20260914 按每个 SELECT 独立作用域把直接列中文别名映射到唯一物理字段。</summary>
        private static void ExtractSqlColumnAliases(string sourcePath, string modulePath, string[] lines,
            SqlBlock block, ISet<string> cteNames, IDictionary<string, HashSet<string>> fieldOwners,
            IList<SourceEvidence> result, ISet<string> deduplicationKeys)
        {
            bool[] identifierCharacters = BuildSqlIdentifierCharacterMap(block.Text);
            int[] parenthesisDepths = BuildParenthesisDepthMap(block.Text, identifierCharacters);
            MatchCollection selectMatches = SelectKeywordOnlyRegex.Matches(block.Text);
            MatchCollection fromMatches = FromKeywordOnlyRegex.Matches(block.Text);
            for (int selectIndex = 0; selectIndex < selectMatches.Count; selectIndex++)
            {
                Match selectMatch = selectMatches[selectIndex];
                if (identifierCharacters[selectMatch.Index])
                {
                    continue;
                }
                int selectDepth = parenthesisDepths[selectMatch.Index];
                int fromIndex = FindFollowingFromAtDepth(
                    fromMatches, selectMatch.Index + selectMatch.Length, selectDepth, parenthesisDepths,
                    identifierCharacters);
                if (fromIndex < 0)
                {
                    continue;
                }

                int scopeEnd = FindSelectSourceEnd(block.Text, fromIndex + 4, selectDepth,
                    parenthesisDepths, identifierCharacters);
                SelectSourceContext sourceContext = BuildSelectSourceContext(
                    block.Text, fromIndex, scopeEnd, selectDepth, parenthesisDepths,
                    identifierCharacters, cteNames);
                IList<ProjectionSegment> segments = SplitSelectProjection(
                    block.AliasText, selectMatch.Index + selectMatch.Length, fromIndex,
                    selectDepth, parenthesisDepths);
                for (int segmentIndex = 0; segmentIndex < segments.Count; segmentIndex++)
                {
                    ExtractSqlCaseEnumeration(sourcePath, modulePath, lines, block, segments[segmentIndex],
                        sourceContext, fieldOwners, result, deduplicationKeys);
                    ExtractSqlColumnAlias(sourcePath, modulePath, lines, block, segments[segmentIndex],
                        sourceContext, fieldOwners, result, deduplicationKeys);
                }
            }
        }

        /// <summary>XMZADD 20260915 仅从唯一物理字段和字面常量组成的 SQL CASE 中提取枚举键值。</summary>
        private static void ExtractSqlCaseEnumeration(string sourcePath, string modulePath, string[] lines,
            SqlBlock block, ProjectionSegment segment, SelectSourceContext sourceContext,
            IDictionary<string, HashSet<string>> fieldOwners, IList<SourceEvidence> result,
            ISet<string> deduplicationKeys)
        {
            string expression;
            string chineseAlias;
            int expressionOffset;
            int aliasOffset;
            if (!TrySplitChineseSelectAlias(segment.Text, out expression, out chineseAlias,
                    out expressionOffset, out aliasOffset))
            {
                return;
            }

            Match caseMatch = SqlSimpleCaseRegex.Match(expression);
            bool isSearchedCase = false;
            if (!caseMatch.Success)
            {
                caseMatch = SqlSearchedCaseRegex.Match(expression);
                isSearchedCase = true;
            }
            if (!caseMatch.Success)
            {
                return;
            }

            CaptureCollection fieldReferences = caseMatch.Groups["fieldRef"].Captures;
            CaptureCollection values = caseMatch.Groups["value"].Captures;
            CaptureCollection captions = caseMatch.Groups["caption"].Captures;
            if (fieldReferences.Count == 0 || values.Count == 0 || values.Count != captions.Count ||
                (isSearchedCase && fieldReferences.Count != values.Count))
            {
                return;
            }

            string tableName;
            string fieldName;
            if (!TryResolveSqlFieldReference(sourceContext, fieldReferences[0].Value,
                    out tableName, out fieldName))
            {
                return;
            }
            if (isSearchedCase)
            {
                for (int fieldIndex = 1; fieldIndex < fieldReferences.Count; fieldIndex++)
                {
                    string otherTable;
                    string otherField;
                    if (!TryResolveSqlFieldReference(sourceContext, fieldReferences[fieldIndex].Value,
                            out otherTable, out otherField) ||
                        !string.Equals(tableName, otherTable, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(fieldName, otherField, StringComparison.OrdinalIgnoreCase))
                    {
                        // 搜索 CASE 混用多个字段时，任何单个分支都不足以证明完整字段枚举。
                        return;
                    }
                }
            }

            var pending = new List<EnumerationSourceItem>();
            for (int itemIndex = 0; itemIndex < values.Count; itemIndex++)
            {
                string value = NormalizeSqlLiteral(values[itemIndex].Value);
                string caption = NormalizeSqlLiteral(captions[itemIndex].Value);
                if (string.IsNullOrWhiteSpace(value) || !ContainsChineseCharacter(caption))
                {
                    return;
                }
                int sourceLine = GetSourceLine(block,
                    segment.StartIndex + expressionOffset + values[itemIndex].Index);
                pending.Add(new EnumerationSourceItem(value, caption, sourceLine,
                    caseMatch.Value, GetOriginalLine(lines, sourceLine)));
            }

            AddFieldOwner(fieldOwners, fieldName, tableName);
            for (int itemIndex = 0; itemIndex < pending.Count; itemIndex++)
            {
                EnumerationSourceItem item = pending[itemIndex];
                AddEnumerationEvidence(result, deduplicationKeys, tableName, fieldName, modulePath,
                    item.Value, item.ChineseName, sourcePath, item.SourceLine, "SqlCaseEnum",
                    item.RawValue, item.OriginalText,
                    "SQL CASE 将唯一物理字段的常量值直接映射为中文业务含义。");
            }
        }

        /// <summary>XMZADD 20260915 将当前 SELECT 作用域内的直接字段引用解析为唯一物理表字段。</summary>
        private static bool TryResolveSqlFieldReference(SelectSourceContext sourceContext, string fieldReference,
            out string tableName, out string fieldName)
        {
            tableName = null;
            fieldName = null;
            Match fieldMatch = DirectSelectFieldRegex.Match((fieldReference ?? string.Empty).Trim());
            if (!fieldMatch.Success)
            {
                return false;
            }

            fieldName = NormalizeIdentifier(fieldMatch.Groups["field"].Value);
            string qualifier = NormalizeIdentifier(fieldMatch.Groups["alias"].Value);
            if (!string.IsNullOrWhiteSpace(qualifier))
            {
                if (sourceContext.AmbiguousAliases.Contains(qualifier) ||
                    !sourceContext.Aliases.TryGetValue(qualifier, out tableName))
                {
                    return false;
                }
            }
            else if (!sourceContext.HasAmbiguousUnqualifiedSource && sourceContext.Tables.Count == 1)
            {
                tableName = GetOnlyValue(sourceContext.Tables);
            }
            return !string.IsNullOrWhiteSpace(tableName) && !string.IsNullOrWhiteSpace(fieldName);
        }

        /// <summary>XMZADD 20260915 规范化 SQL 数字或字符串字面量且不接受变量和计算表达式。</summary>
        private static string NormalizeSqlLiteral(string literal)
        {
            string value = (literal ?? string.Empty).Trim();
            if (value.Length >= 3 && (value[0] == 'N' || value[0] == 'n') && value[1] == '\'' &&
                value[value.Length - 1] == '\'')
            {
                return value.Substring(2, value.Length - 3).Replace("''", "'").Trim();
            }
            if (value.Length >= 2 && value[0] == '\'' && value[value.Length - 1] == '\'')
            {
                return value.Substring(1, value.Length - 2).Replace("''", "'").Trim();
            }
            return value;
        }

        /// <summary>XMZADD 20260914 解析一个投影项且只让完整单列表达式形成物理字段命名证据。</summary>
        private static void ExtractSqlColumnAlias(string sourcePath, string modulePath, string[] lines,
            SqlBlock block, ProjectionSegment segment, SelectSourceContext sourceContext,
            IDictionary<string, HashSet<string>> fieldOwners, IList<SourceEvidence> result,
            ISet<string> deduplicationKeys)
        {
            string expression;
            string chineseAlias;
            int expressionOffset;
            int aliasOffset;
            if (!TrySplitChineseSelectAlias(segment.Text, out expression, out chineseAlias,
                    out expressionOffset, out aliasOffset))
            {
                return;
            }

            Match fieldMatch = DirectSelectFieldRegex.Match(expression);
            string tableName = null;
            string fieldName = null;
            if (fieldMatch.Success)
            {
                fieldName = NormalizeIdentifier(fieldMatch.Groups["field"].Value);
                string qualifier = NormalizeIdentifier(fieldMatch.Groups["alias"].Value);
                if (!string.IsNullOrWhiteSpace(qualifier))
                {
                    if (!sourceContext.AmbiguousAliases.Contains(qualifier))
                    {
                        sourceContext.Aliases.TryGetValue(qualifier, out tableName);
                    }
                }
                else if (!sourceContext.HasAmbiguousUnqualifiedSource && sourceContext.Tables.Count == 1)
                {
                    tableName = GetOnlyValue(sourceContext.Tables);
                }
            }

            if (!string.IsNullOrWhiteSpace(tableName) && !string.IsNullOrWhiteSpace(fieldName))
            {
                AddFieldOwner(fieldOwners, fieldName, tableName);
                int fieldCharacterIndex = segment.StartIndex + expressionOffset +
                                          fieldMatch.Groups["field"].Index;
                int sourceLine = GetSourceLine(block, fieldCharacterIndex);
                AddEvidence(result, deduplicationKeys, tableName, fieldName, null, modulePath,
                    chineseAlias, fieldName, sourcePath, sourceLine, "SqlColumnAlias", segment.Text,
                    GetOriginalLine(lines, sourceLine),
                    "SELECT 直接列与中文输出别名在同一作用域唯一绑定，可作为物理字段业务名称证据。",
                    usageKind: SourceUsageKind.Display,
                    strength: SourceEvidenceStrength.DirectBusinessCode);
                return;
            }

            // 派生或无法唯一归属的显示列只留审计，不得把标题回写到参与表达式的任一物理字段。
            int aliasSourceLine = GetSourceLine(block, segment.StartIndex + aliasOffset);
            AddEvidence(result, deduplicationKeys, null, null, null, modulePath, null,
                chineseAlias, sourcePath, aliasSourceLine, "SqlDerivedColumnAlias", segment.Text,
                GetOriginalLine(lines, aliasSourceLine),
                "SELECT 输出别名来自计算表达式、逻辑结果集或歧义作用域，仅保留派生显示列审计。",
                usageKind: SourceUsageKind.Display,
                strength: SourceEvidenceStrength.Contextual);
        }

        /// <summary>XMZADD 20260914 建立 SQL 每个字符前的括号深度以隔离 CTE、子查询和外层查询。</summary>
        private static int[] BuildParenthesisDepthMap(string sqlText, bool[] identifierCharacters)
        {
            string value = sqlText ?? string.Empty;
            var result = new int[value.Length + 1];
            int depth = 0;
            for (int index = 0; index < value.Length; index++)
            {
                result[index] = depth;
                if (identifierCharacters[index])
                {
                    continue;
                }
                char character = value[index];
                if (character == '(')
                {
                    depth++;
                }
                else if (character == ')' && depth > 0)
                {
                    depth--;
                }
            }
            result[value.Length] = depth;
            return result;
        }

        /// <summary>XMZADD 20260914 标记方括号和双引号标识符字符，防止其中的关键字改变查询作用域。</summary>
        private static bool[] BuildSqlIdentifierCharacterMap(string sqlText)
        {
            string value = sqlText ?? string.Empty;
            var result = new bool[value.Length];
            bool insideBracket = false;
            bool insideDoubleQuote = false;
            for (int index = 0; index < value.Length; index++)
            {
                char character = value[index];
                if (insideBracket)
                {
                    result[index] = true;
                    if (character == ']' && index + 1 < value.Length && value[index + 1] == ']')
                    {
                        result[index + 1] = true;
                        index++;
                    }
                    else if (character == ']')
                    {
                        insideBracket = false;
                    }
                    continue;
                }
                if (insideDoubleQuote)
                {
                    result[index] = true;
                    if (character == '"' && index + 1 < value.Length && value[index + 1] == '"')
                    {
                        result[index + 1] = true;
                        index++;
                    }
                    else if (character == '"')
                    {
                        insideDoubleQuote = false;
                    }
                    continue;
                }
                if (character == '[')
                {
                    result[index] = true;
                    insideBracket = true;
                }
                else if (character == '"')
                {
                    result[index] = true;
                    insideDoubleQuote = true;
                }
            }
            return result;
        }

        /// <summary>XMZADD 20260914 查找当前 SELECT 同括号层级的 FROM，避免读取嵌套查询来源。</summary>
        private static int FindFollowingFromAtDepth(MatchCollection fromMatches, int startIndex,
            int selectDepth, int[] parenthesisDepths, bool[] identifierCharacters)
        {
            for (int index = 0; index < fromMatches.Count; index++)
            {
                Match match = fromMatches[index];
                if (match.Index >= startIndex && !identifierCharacters[match.Index] &&
                    parenthesisDepths[match.Index] == selectDepth)
                {
                    return match.Index;
                }
            }
            return -1;
        }

        /// <summary>XMZADD 20260914 定位当前 SELECT 来源子句边界，使表别名只在自身查询作用域生效。</summary>
        private static int FindSelectSourceEnd(string sqlText, int startIndex, int selectDepth,
            int[] parenthesisDepths, bool[] identifierCharacters)
        {
            string value = sqlText ?? string.Empty;
            string[] boundaryKeywords =
            {
                "WHERE", "GROUP", "HAVING", "ORDER", "UNION", "EXCEPT", "INTERSECT", "OPTION", "FOR"
            };
            for (int index = startIndex; index < value.Length; index++)
            {
                if (identifierCharacters[index] || parenthesisDepths[index] != selectDepth)
                {
                    continue;
                }
                if (value[index] == ')' || value[index] == ';')
                {
                    return index;
                }
                for (int keywordIndex = 0; keywordIndex < boundaryKeywords.Length; keywordIndex++)
                {
                    if (IsSqlKeywordAt(value, index, boundaryKeywords[keywordIndex]))
                    {
                        return index;
                    }
                }
            }
            return value.Length;
        }

        /// <summary>XMZADD 20260914 收集单个 SELECT 来源范围内的物理表与无歧义别名映射。</summary>
        private static SelectSourceContext BuildSelectSourceContext(string sqlText, int fromIndex,
            int scopeEnd, int selectDepth, int[] parenthesisDepths, bool[] identifierCharacters,
            ISet<string> cteNames)
        {
            var context = new SelectSourceContext();
            int length = Math.Max(0, scopeEnd - fromIndex);
            string sourceText = (sqlText ?? string.Empty).Substring(fromIndex, length);
            MatchCollection tableMatches = SqlTableReferenceRegex.Matches(sourceText);
            for (int matchIndex = 0; matchIndex < tableMatches.Count; matchIndex++)
            {
                Match match = tableMatches[matchIndex];
                int absoluteIndex = fromIndex + match.Index;
                int clauseIndex = fromIndex + match.Groups["clause"].Index;
                if (identifierCharacters[clauseIndex] || parenthesisDepths[absoluteIndex] != selectDepth)
                {
                    continue;
                }

                string tableName = NormalizeIdentifier(match.Groups["table"].Value);
                if (string.IsNullOrWhiteSpace(tableName) || IsSqlKeyword(tableName) ||
                    IsUnknownSqlObject(tableName) || cteNames.Contains(tableName))
                {
                    context.HasAmbiguousUnqualifiedSource = true;
                    continue;
                }

                context.Tables.Add(tableName);
                AddSelectScopeAlias(context, tableName, tableName);
                string alias = NormalizeIdentifier(match.Groups["alias"].Value);
                if (!string.IsNullOrWhiteSpace(alias) && !IsSqlKeyword(alias))
                {
                    AddSelectScopeAlias(context, alias, tableName);
                }
            }

            if (ContainsCommaAtDepth(sqlText, fromIndex, scopeEnd, selectDepth, parenthesisDepths))
            {
                // 旧式逗号联表的完整来源不一定能被表正则还原，裸字段在该作用域必须保持保守。
                context.HasAmbiguousUnqualifiedSource = true;
            }
            return context;
        }

        /// <summary>XMZADD 20260914 添加作用域别名并在同层别名指向多表时标记不可采信。</summary>
        private static void AddSelectScopeAlias(SelectSourceContext context, string alias, string tableName)
        {
            if (context.AmbiguousAliases.Contains(alias))
            {
                return;
            }
            string existing;
            if (context.Aliases.TryGetValue(alias, out existing) &&
                !string.Equals(existing, tableName, StringComparison.OrdinalIgnoreCase))
            {
                context.Aliases.Remove(alias);
                context.AmbiguousAliases.Add(alias);
                return;
            }
            context.Aliases[alias] = tableName;
        }

        /// <summary>XMZADD 20260914 判断来源子句同层是否含旧式逗号联表，阻止裸字段误归属。</summary>
        private static bool ContainsCommaAtDepth(string sqlText, int startIndex, int endIndex,
            int selectDepth, int[] parenthesisDepths)
        {
            string value = sqlText ?? string.Empty;
            int maximum = Math.Min(value.Length, endIndex);
            for (int index = Math.Max(0, startIndex); index < maximum; index++)
            {
                if (value[index] == ',' && parenthesisDepths[index] == selectDepth)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>XMZADD 20260914 按顶层逗号拆分 SELECT 投影且保留每项在逻辑 SQL 中的绝对位置。</summary>
        private static IList<ProjectionSegment> SplitSelectProjection(string sqlText, int startIndex,
            int endIndex, int selectDepth, int[] parenthesisDepths)
        {
            var result = new List<ProjectionSegment>();
            string value = sqlText ?? string.Empty;
            int segmentStart = startIndex;
            bool insideBracket = false;
            bool insideSingleQuote = false;
            bool insideDoubleQuote = false;
            for (int index = startIndex; index <= endIndex; index++)
            {
                bool atEnd = index == endIndex;
                if (!atEnd)
                {
                    char character = value[index];
                    if (insideSingleQuote)
                    {
                        if (character == '\'' && index + 1 < endIndex && value[index + 1] == '\'')
                        {
                            index++;
                            continue;
                        }
                        if (character == '\'')
                        {
                            insideSingleQuote = false;
                        }
                        continue;
                    }
                    if (insideDoubleQuote)
                    {
                        if (character == '"')
                        {
                            insideDoubleQuote = false;
                        }
                        continue;
                    }
                    if (insideBracket)
                    {
                        if (character == ']')
                        {
                            insideBracket = false;
                        }
                        continue;
                    }
                    if (character == '\'')
                    {
                        insideSingleQuote = true;
                        continue;
                    }
                    if (character == '"')
                    {
                        insideDoubleQuote = true;
                        continue;
                    }
                    if (character == '[')
                    {
                        insideBracket = true;
                        continue;
                    }
                }

                if (atEnd || (value[index] == ',' && parenthesisDepths[index] == selectDepth))
                {
                    result.Add(new ProjectionSegment(segmentStart,
                        value.Substring(segmentStart, index - segmentStart)));
                    segmentStart = index + 1;
                }
            }
            return result;
        }

        /// <summary>XMZADD 20260914 从投影项尾部解析显式或隐式中文别名并保留源表达式偏移。</summary>
        private static bool TrySplitChineseSelectAlias(string segment, out string expression,
            out string chineseAlias, out int expressionOffset, out int aliasOffset)
        {
            expression = null;
            chineseAlias = null;
            expressionOffset = 0;
            aliasOffset = 0;
            string value = segment ?? string.Empty;
            int aliasEnd = value.Length - 1;
            while (aliasEnd >= 0 && char.IsWhiteSpace(value[aliasEnd]))
            {
                aliasEnd--;
            }
            if (aliasEnd < 0)
            {
                return false;
            }

            int aliasStart = FindSelectAliasStart(value, aliasEnd);
            if (aliasStart < 0)
            {
                return false;
            }
            string aliasValue = NormalizeSelectAlias(value.Substring(aliasStart, aliasEnd - aliasStart + 1));
            if (!ContainsChineseCharacter(aliasValue))
            {
                return false;
            }

            int cursor = aliasStart - 1;
            bool separatedByWhitespace = cursor >= 0 && char.IsWhiteSpace(value[cursor]);
            while (cursor >= 0 && char.IsWhiteSpace(value[cursor]))
            {
                cursor--;
            }
            if (!separatedByWhitespace || cursor < 0)
            {
                return false;
            }

            int expressionEnd = cursor;
            if (cursor >= 1 && (value[cursor] == 'S' || value[cursor] == 's') &&
                (value[cursor - 1] == 'A' || value[cursor - 1] == 'a') &&
                (cursor - 2 < 0 || !IsSqlIdentifierCharacter(value[cursor - 2])))
            {
                expressionEnd = cursor - 2;
                while (expressionEnd >= 0 && char.IsWhiteSpace(value[expressionEnd]))
                {
                    expressionEnd--;
                }
            }
            if (expressionEnd < 0)
            {
                return false;
            }

            int expressionStart = 0;
            while (expressionStart <= expressionEnd && char.IsWhiteSpace(value[expressionStart]))
            {
                expressionStart++;
            }
            string rawExpression = value.Substring(expressionStart, expressionEnd - expressionStart + 1);
            Match modifier = SelectModifierPrefixRegex.Match(rawExpression);
            expressionStart += modifier.Length;
            while (expressionStart <= expressionEnd && char.IsWhiteSpace(value[expressionStart]))
            {
                expressionStart++;
            }
            if (expressionStart > expressionEnd)
            {
                return false;
            }

            expression = value.Substring(expressionStart, expressionEnd - expressionStart + 1).TrimEnd();
            chineseAlias = aliasValue;
            expressionOffset = expressionStart;
            aliasOffset = aliasStart;
            return expression.Length > 0;
        }

        /// <summary>XMZADD 20260914 定位方括号、单双引号或普通 Unicode 输出别名的起点。</summary>
        private static int FindSelectAliasStart(string value, int aliasEnd)
        {
            char endCharacter = value[aliasEnd];
            char openingCharacter = '\0';
            if (endCharacter == ']') openingCharacter = '[';
            else if (endCharacter == '\'') openingCharacter = '\'';
            else if (endCharacter == '"') openingCharacter = '"';
            if (openingCharacter != '\0')
            {
                for (int index = aliasEnd - 1; index >= 0; index--)
                {
                    if (value[index] == openingCharacter)
                    {
                        return index;
                    }
                }
                return -1;
            }

            if (!IsSelectAliasCharacter(endCharacter))
            {
                return -1;
            }
            int aliasStart = aliasEnd;
            while (aliasStart > 0 && IsSelectAliasCharacter(value[aliasStart - 1]))
            {
                aliasStart--;
            }
            return aliasStart;
        }

        /// <summary>XMZADD 20260914 清除 SQL 输出别名包裹符并恢复常见引号转义。</summary>
        private static string NormalizeSelectAlias(string value)
        {
            string result = (value ?? string.Empty).Trim();
            if (result.Length >= 2 &&
                ((result[0] == '[' && result[result.Length - 1] == ']') ||
                 (result[0] == '\'' && result[result.Length - 1] == '\'') ||
                 (result[0] == '"' && result[result.Length - 1] == '"')))
            {
                char wrapper = result[0];
                result = result.Substring(1, result.Length - 2);
                if (wrapper == '\'') result = result.Replace("''", "'");
                if (wrapper == '"') result = result.Replace("\"\"", "\"");
            }
            return result.Trim();
        }

        /// <summary>XMZADD 20260914 判断未包裹输出别名可包含的 Unicode 字母、数字和下划线。</summary>
        private static bool IsSelectAliasCharacter(char character)
        {
            return char.IsLetterOrDigit(character) || character == '_';
        }

        /// <summary>XMZADD 20260914 确认输出别名包含实际中文字符而非纯英文技术别名。</summary>
        private static bool ContainsChineseCharacter(string value)
        {
            string text = value ?? string.Empty;
            for (int index = 0; index < text.Length; index++)
            {
                if (text[index] >= '\u4e00' && text[index] <= '\u9fff')
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>XMZADD 20260914 按完整关键字边界判断当前字符位置，避免字段名片段误触发作用域结束。</summary>
        private static bool IsSqlKeywordAt(string value, int index, string keyword)
        {
            if (index < 0 || index + keyword.Length > value.Length ||
                string.Compare(value, index, keyword, 0, keyword.Length, StringComparison.OrdinalIgnoreCase) != 0)
            {
                return false;
            }
            bool validBefore = index == 0 || !IsSqlIdentifierCharacter(value[index - 1]);
            int following = index + keyword.Length;
            bool validAfter = following >= value.Length || !IsSqlIdentifierCharacter(value[following]);
            return validBefore && validAfter;
        }

        /// <summary>XMZADD 20260904 提取 INSERT 目标列，使无别名写入字段仍保留实际表归属。</summary>
        private static void ExtractInsertFields(string sourcePath, string modulePath, string[] lines,
            SqlBlock block, IDictionary<string, HashSet<string>> fieldOwners,
            IList<SourceEvidence> result, ISet<string> deduplicationKeys)
        {
            MatchCollection matches = InsertColumnsRegex.Matches(block.Text);
            for (int matchIndex = 0; matchIndex < matches.Count; matchIndex++)
            {
                Match match = matches[matchIndex];
                string tableName = NormalizeIdentifier(match.Groups["table"].Value);
                Group columnsGroup = match.Groups["columns"];
                MatchCollection columnMatches = InsertColumnRegex.Matches(columnsGroup.Value);
                for (int columnIndex = 0; columnIndex < columnMatches.Count; columnIndex++)
                {
                    Match columnMatch = columnMatches[columnIndex];
                    string fieldName = NormalizeIdentifier(columnMatch.Groups["field"].Value);
                    if (!IsSimpleIdentifier(fieldName))
                    {
                        continue;
                    }
                    AddFieldOwner(fieldOwners, fieldName, tableName);
                    int sourceLine = GetSourceLine(block,
                        columnsGroup.Index + columnMatch.Groups["field"].Index);
                    AddEvidence(result, deduplicationKeys, tableName, fieldName, null, modulePath,
                        null, fieldName, sourcePath, sourceLine, "SqlFieldUsage",
                        columnMatch.Groups["field"].Value,
                        GetOriginalLine(lines, sourceLine), "INSERT 目标列确认该字段参与业务写入。",
                        usageKind: SourceUsageKind.Write);
                }
            }
        }

        /// <summary>XMZADD 20260904 提取 UPDATE SET 左侧字段，使无别名更新字段仍保留实际表归属。</summary>
        private static void ExtractUpdateFields(string sourcePath, string modulePath, string[] lines,
            SqlBlock block, IDictionary<string, string> aliases,
            IDictionary<string, HashSet<string>> fieldOwners,
            IList<SourceEvidence> result, ISet<string> deduplicationKeys)
        {
            MatchCollection matches = UpdateSetRegex.Matches(block.Text);
            for (int matchIndex = 0; matchIndex < matches.Count; matchIndex++)
            {
                Match match = matches[matchIndex];
                string tableName = NormalizeIdentifier(match.Groups["table"].Value);
                string physicalTableName;
                if (aliases.TryGetValue(tableName, out physicalTableName))
                {
                    tableName = physicalTableName;
                }
                MatchCollection fieldMatches = UpdateFieldRegex.Matches(match.Groups["values"].Value);
                for (int fieldIndex = 0; fieldIndex < fieldMatches.Count; fieldIndex++)
                {
                    Match fieldMatch = fieldMatches[fieldIndex];
                    string fieldName = NormalizeIdentifier(fieldMatch.Groups["field"].Value);
                    if (!IsSimpleIdentifier(fieldName))
                    {
                        continue;
                    }
                    AddFieldOwner(fieldOwners, fieldName, tableName);
                    int sourceLine = GetSourceLine(block,
                        match.Groups["values"].Index + fieldMatch.Groups["field"].Index);
                    AddEvidence(result, deduplicationKeys, tableName, fieldName, null, modulePath,
                        null, fieldName, sourcePath, sourceLine, "SqlFieldUsage",
                        fieldMatch.Groups["field"].Value,
                        GetOriginalLine(lines, sourceLine), "UPDATE 目标列确认该字段参与业务更新。",
                        usageKind: SourceUsageKind.Write);
                }
            }
        }

        /// <summary>XMZADD 20260904 在单表 SQL 中补充 SELECT、WHERE、GROUP BY 和 ORDER BY 的无别名字段使用。</summary>
        private static void ExtractSingleTableClauseFields(string sourcePath, string modulePath, string[] lines,
            SqlBlock block, string tableName, IDictionary<string, string> aliases,
            ISet<string> cteNames,
            IDictionary<string, HashSet<string>> fieldOwners, IList<SourceEvidence> result,
            ISet<string> deduplicationKeys)
        {
            string[] clausePatterns =
            {
                @"\bSELECT\s+(?<body>.*?)(?=\bFROM\b)",
                @"\bWHERE\s+(?<body>.*?)(?=\bGROUP\s+BY\b|\bORDER\s+BY\b|$)",
                @"\bGROUP\s+BY\s+(?<body>.*?)(?=\bORDER\s+BY\b|$)",
                @"\bORDER\s+BY\s+(?<body>.*?)(?=\bOFFSET\b|\bFETCH\b|\bFOR\b|\bOPTION\b|\bUNION\b|\bEXCEPT\b|\bINTERSECT\b|;|$)"
            };
            string topLevelSql = MaskParenthesizedSqlPreservingLayout(block.Text);
            var outputAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int patternIndex = 0; patternIndex < clausePatterns.Length; patternIndex++)
            {
                string clauseSource = patternIndex == 3 ? topLevelSql : block.Text;
                MatchCollection clauses = Regex.Matches(clauseSource, clausePatterns[patternIndex],
                    RegexOptions.IgnoreCase | RegexOptions.Singleline);
                for (int clauseIndex = 0; clauseIndex < clauses.Count; clauseIndex++)
                {
                    Match clause = clauses[clauseIndex];
                    Group bodyGroup = clause.Groups["body"];
                    string body = bodyGroup.Value;
                    if (patternIndex == 0)
                    {
                        body = MaskSelectOutputAliasesPreservingLayout(body, outputAliases);
                    }
                    body = MaskRegexMatchesPreservingLayout(body, QualifiedFieldRegex);
                    body = MaskRegexMatchesPreservingLayout(body, SqlOutputAliasRegex);
                    MatchCollection identifiers = IdentifierRegex.Matches(body);
                    for (int identifierIndex = 0; identifierIndex < identifiers.Count; identifierIndex++)
                    {
                        Match identifierMatch = identifiers[identifierIndex];
                        string fieldName = identifierMatch.Groups["identifier"].Value;
                        if (!CanBeUnqualifiedField(body, identifierMatch, fieldName, tableName, aliases,
                            cteNames, outputAliases))
                        {
                            continue;
                        }
                        AddFieldOwner(fieldOwners, fieldName, tableName);
                        int sourceLine = GetSourceLine(block, bodyGroup.Index + identifierMatch.Index);
                        AddEvidence(result, deduplicationKeys, tableName, fieldName, null, modulePath,
                            null, fieldName, sourcePath, sourceLine, "SqlFieldUsage", fieldName,
                            GetOriginalLine(lines, sourceLine), "单表 SQL 子句确认该无别名字段被业务代码读取。",
                            usageKind: SourceUsageKind.Read);
                    }
                }
            }
        }

        /// <summary>XMZADD 20260904 排除 SQL 关键字、函数、表名和代码拼接标识符，降低单表字段误判。</summary>
        private static bool CanBeUnqualifiedField(string body, Match match, string identifier,
            string tableName, IDictionary<string, string> aliases, ISet<string> cteNames,
            ISet<string> outputAliases)
        {
            if (string.IsNullOrWhiteSpace(identifier) || IsSqlKeyword(identifier) ||
                IsUnknownSqlObject(identifier) ||
                string.Equals(identifier, tableName, StringComparison.OrdinalIgnoreCase) ||
                aliases.ContainsKey(identifier) || cteNames.Contains(identifier) ||
                outputAliases.Contains(identifier))
            {
                return false;
            }

            int followingIndex = match.Index + match.Length;
            while (followingIndex < body.Length && char.IsWhiteSpace(body[followingIndex]))
            {
                followingIndex++;
            }
            if (followingIndex < body.Length && body[followingIndex] == '(')
            {
                return false;
            }

            int precedingIndex = match.Index - 1;
            while (precedingIndex >= 0 && char.IsWhiteSpace(body[precedingIndex]))
            {
                precedingIndex--;
            }
            return precedingIndex < 0 || (body[precedingIndex] != '@' && body[precedingIndex] != '#');
        }

        /// <summary>XMZADD 20260905 等长屏蔽 SELECT 投影项的显式或隐式输出别名，防止别名伪装成物理字段。</summary>
        private static string MaskSelectOutputAliasesPreservingLayout(string selectBody,
            ISet<string> outputAliases)
        {
            string value = selectBody ?? string.Empty;
            char[] characters = value.ToCharArray();
            int segmentStart = 0;
            int parenthesisDepth = 0;
            bool insideBracket = false;
            for (int characterIndex = 0; characterIndex <= value.Length; characterIndex++)
            {
                bool atEnd = characterIndex == value.Length;
                if (!atEnd)
                {
                    char character = value[characterIndex];
                    if (character == '[')
                    {
                        insideBracket = true;
                    }
                    else if (character == ']')
                    {
                        insideBracket = false;
                    }
                    else if (!insideBracket && character == '(')
                    {
                        parenthesisDepth++;
                    }
                    else if (!insideBracket && character == ')' && parenthesisDepth > 0)
                    {
                        parenthesisDepth--;
                    }
                }

                if (atEnd || (!insideBracket && parenthesisDepth == 0 && value[characterIndex] == ','))
                {
                    MaskTrailingSelectAlias(value, characters, segmentStart, characterIndex, outputAliases);
                    segmentStart = characterIndex + 1;
                }
            }
            return new string(characters);
        }

        /// <summary>XMZADD 20260905 等长屏蔽括号范围，使弱字段补取只把深度零的 ORDER BY 当作顶层排序子句。</summary>
        private static string MaskParenthesizedSqlPreservingLayout(string sqlText)
        {
            string value = sqlText ?? string.Empty;
            char[] characters = value.ToCharArray();
            int parenthesisDepth = 0;
            bool insideBracket = false;
            for (int characterIndex = 0; characterIndex < value.Length; characterIndex++)
            {
                char character = value[characterIndex];
                if (parenthesisDepth == 0 && character == '[')
                {
                    insideBracket = true;
                }
                else if (parenthesisDepth == 0 && character == ']')
                {
                    insideBracket = false;
                }

                if (!insideBracket && character == '(')
                {
                    parenthesisDepth++;
                }
                if (parenthesisDepth > 0)
                {
                    if (character != '\r' && character != '\n')
                    {
                        characters[characterIndex] = ' ';
                    }
                    if (!insideBracket && character == ')')
                    {
                        parenthesisDepth--;
                    }
                }
            }
            return new string(characters);
        }

        /// <summary>XMZADD 20260905 在一个投影项内仅屏蔽与前方表达式由空白分隔的末尾别名。</summary>
        private static void MaskTrailingSelectAlias(string value, char[] characters,
            int segmentStart, int segmentEnd, ISet<string> outputAliases)
        {
            int aliasEnd = segmentEnd - 1;
            while (aliasEnd >= segmentStart && char.IsWhiteSpace(value[aliasEnd]))
            {
                aliasEnd--;
            }
            if (aliasEnd < segmentStart)
            {
                return;
            }

            int aliasStart;
            if (value[aliasEnd] == ']')
            {
                aliasStart = aliasEnd;
                while (aliasStart >= segmentStart && value[aliasStart] != '[')
                {
                    aliasStart--;
                }
                if (aliasStart < segmentStart)
                {
                    return;
                }
            }
            else
            {
                if (!IsSqlIdentifierCharacter(value[aliasEnd]))
                {
                    return;
                }
                aliasStart = aliasEnd;
                while (aliasStart > segmentStart && IsSqlIdentifierCharacter(value[aliasStart - 1]))
                {
                    aliasStart--;
                }
            }

            if (aliasStart <= segmentStart || !char.IsWhiteSpace(value[aliasStart - 1]))
            {
                return;
            }
            int expressionEnd = aliasStart - 1;
            while (expressionEnd >= segmentStart && char.IsWhiteSpace(value[expressionEnd]))
            {
                expressionEnd--;
            }
            if (expressionEnd < segmentStart || IsSqlExpressionOperator(value[expressionEnd]))
            {
                return;
            }

            string expression = value.Substring(segmentStart, expressionEnd - segmentStart + 1).Trim();
            if (expression.Length == 0 || SelectModifierOnlyRegex.IsMatch(expression))
            {
                return;
            }
            string outputAlias = NormalizeIdentifier(value.Substring(aliasStart, aliasEnd - aliasStart + 1));
            if (!string.IsNullOrWhiteSpace(outputAlias))
            {
                outputAliases.Add(outputAlias);
            }
            for (int characterIndex = aliasStart; characterIndex <= aliasEnd; characterIndex++)
            {
                if (characters[characterIndex] != '\r' && characters[characterIndex] != '\n')
                {
                    characters[characterIndex] = ' ';
                }
            }
        }

        /// <summary>XMZADD 20260905 判断投影项末尾是否仍是运算符，避免把运算右操作数当成输出别名。</summary>
        private static bool IsSqlExpressionOperator(char character)
        {
            return character == '+' || character == '-' || character == '*' || character == '/' ||
                   character == '%' || character == '=' || character == '<' || character == '>' ||
                   character == '|' || character == '&' || character == '^' || character == '.';
        }

        /// <summary>XMZADD 20260915 仅发布静态常量字典绑定且排除数据库行或变量动态装载的 DataMap。</summary>
        private static void ExtractDataMapEnumerations(string sourcePath, string modulePath, string[] codeLines,
            string[] originalLines, IDictionary<string, HashSet<string>> fieldOwners,
            IList<SourceEvidence> result, ISet<string> deduplicationKeys)
        {
            var maps = new Dictionary<string, MapVariableState>(StringComparer.OrdinalIgnoreCase);
            for (int lineIndex = 0; lineIndex < codeLines.Length; lineIndex++)
            {
                string code = GetCodeBeforeTrailingComment(codeLines[lineIndex] ?? string.Empty).Trim();
                if (code.Length == 0)
                {
                    continue;
                }
                if (IsVbMemberBoundary(code))
                {
                    maps.Clear();
                    continue;
                }

                Match declaration = MapVariableDeclarationRegex.Match(code);
                if (declaration.Success)
                {
                    maps[declaration.Groups["variable"].Value] = new MapVariableState();
                }

                MatchCollection addCalls = MapAddCallRegex.Matches(code);
                for (int addIndex = 0; addIndex < addCalls.Count; addIndex++)
                {
                    string variableName = addCalls[addIndex].Groups["variable"].Value;
                    MapVariableState state;
                    if (!maps.TryGetValue(variableName, out state))
                    {
                        continue;
                    }
                    Match constantAdd = ConstantMapAddRegex.Match(code, addCalls[addIndex].Index);
                    if (!constantAdd.Success ||
                        constantAdd.Index != addCalls[addIndex].Index ||
                        !string.Equals(constantAdd.Groups["variable"].Value, variableName,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        // 混入数据库行、变量或计算表达式后无法证明字典在运行时的固定键值集合。
                        state.HasDynamicEntry = true;
                        continue;
                    }

                    string value = NormalizeVisualBasicLiteral(constantAdd.Groups["value"].Value);
                    string caption = NormalizeVisualBasicLiteral(
                        "\"" + constantAdd.Groups["caption"].Value + "\"");
                    if (!string.IsNullOrWhiteSpace(value) && ContainsChineseCharacter(caption))
                    {
                        state.Items.Add(new EnumerationSourceItem(value, caption, lineIndex + 1,
                            constantAdd.Value, GetOriginalLine(originalLines, lineIndex + 1)));
                    }
                }

                MatchCollection assignments = DataMapAssignmentRegex.Matches(code);
                for (int assignmentIndex = 0; assignmentIndex < assignments.Count; assignmentIndex++)
                {
                    Match assignment = assignments[assignmentIndex];
                    string fieldName = NormalizeIdentifier(assignment.Groups["field"].Value);
                    string sourceExpression = assignment.Groups["source"].Value.Trim();
                    IList<EnumerationSourceItem> items = ParseInlineMapItems(
                        sourceExpression, lineIndex + 1, GetOriginalLine(originalLines, lineIndex + 1));
                    if (items == null)
                    {
                        MapVariableState state;
                        if (!IsSimpleIdentifier(sourceExpression) ||
                            !maps.TryGetValue(sourceExpression, out state) || state.HasDynamicEntry)
                        {
                            continue;
                        }
                        items = state.Items;
                    }
                    AddFieldEnumerationItems(result, deduplicationKeys, fieldOwners, fieldName,
                        modulePath, sourcePath, "GridColumnDataMap", items,
                        "网格 DataMap 的静态常量字典把字段值直接映射为中文业务含义。");
                }
            }
        }

        /// <summary>XMZADD 20260915 解析 VB 集合初始化器并在存在任何动态元素时拒绝整组枚举。</summary>
        private static IList<EnumerationSourceItem> ParseInlineMapItems(string expression, int sourceLine,
            string originalText)
        {
            Match initializer = InlineMapInitializerRegex.Match(expression ?? string.Empty);
            if (!initializer.Success)
            {
                return null;
            }

            string itemText = initializer.Groups["items"].Value;
            MatchCollection pairs = InlineMapPairRegex.Matches(itemText);
            string remaining = InlineMapPairRegex.Replace(itemText, string.Empty);
            for (int characterIndex = 0; characterIndex < remaining.Length; characterIndex++)
            {
                char character = remaining[characterIndex];
                if (!char.IsWhiteSpace(character) && character != '{' && character != '}' && character != ',')
                {
                    return new List<EnumerationSourceItem>();
                }
            }

            var result = new List<EnumerationSourceItem>();
            for (int pairIndex = 0; pairIndex < pairs.Count; pairIndex++)
            {
                Match pair = pairs[pairIndex];
                string value = NormalizeVisualBasicLiteral(pair.Groups["value"].Value);
                string caption = NormalizeVisualBasicLiteral(
                    "\"" + pair.Groups["caption"].Value + "\"");
                if (!string.IsNullOrWhiteSpace(value) && ContainsChineseCharacter(caption))
                {
                    result.Add(new EnumerationSourceItem(value, caption, sourceLine,
                        pair.Value, originalText));
                }
            }
            return result;
        }

        /// <summary>XMZADD 20260915 只在同文件证据把 DataMap 字段唯一归属到一张物理表时发布枚举。</summary>
        private static void AddFieldEnumerationItems(IList<SourceEvidence> result,
            ISet<string> deduplicationKeys, IDictionary<string, HashSet<string>> fieldOwners,
            string fieldName, string modulePath, string sourcePath, string ruleName,
            IList<EnumerationSourceItem> items, string explanation)
        {
            if (items == null || items.Count == 0)
            {
                return;
            }
            HashSet<string> owners;
            if (!fieldOwners.TryGetValue(fieldName, out owners) || owners.Count != 1)
            {
                return;
            }

            string tableName = GetOnlyValue(owners);
            for (int itemIndex = 0; itemIndex < items.Count; itemIndex++)
            {
                EnumerationSourceItem item = items[itemIndex];
                AddEnumerationEvidence(result, deduplicationKeys, tableName, fieldName, modulePath,
                    item.Value, item.ChineseName, sourcePath, item.SourceLine, ruleName,
                    item.RawValue, item.OriginalText, explanation);
            }
        }

        /// <summary>XMZADD 20260915 从实体字段 Select Case 中提取常量分支及唯一中文返回结果。</summary>
        private static void ExtractVbSelectCaseEnumerations(string sourcePath, string modulePath,
            string[] codeLines, string[] originalLines, IDictionary<string, string> entityTables,
            IList<SourceEvidence> result, ISet<string> deduplicationKeys)
        {
            for (int lineIndex = 0; lineIndex < codeLines.Length; lineIndex++)
            {
                string selectorLine = GetCodeBeforeTrailingComment(codeLines[lineIndex] ?? string.Empty).Trim();
                Match selector = VbSelectCaseEntityFieldRegex.Match(selectorLine);
                if (!selector.Success)
                {
                    continue;
                }

                string tableName;
                if (!entityTables.TryGetValue(selector.Groups["variable"].Value, out tableName))
                {
                    continue;
                }
                int endLineIndex = FindVbEndSelect(codeLines, lineIndex + 1);
                if (endLineIndex < 0)
                {
                    continue;
                }

                IList<EnumerationSourceItem> items = ParseVbSelectCaseItems(
                    codeLines, originalLines, lineIndex + 1, endLineIndex);
                if (items == null || items.Count < 2)
                {
                    lineIndex = endLineIndex;
                    continue;
                }

                string fieldName = NormalizeIdentifier(selector.Groups["field"].Value);
                for (int itemIndex = 0; itemIndex < items.Count; itemIndex++)
                {
                    EnumerationSourceItem item = items[itemIndex];
                    AddEnumerationEvidence(result, deduplicationKeys, tableName, fieldName, modulePath,
                        item.Value, item.ChineseName, sourcePath, item.SourceLine, "VbSelectCaseEnum",
                        item.RawValue, item.OriginalText,
                        "实体字段 Select Case 的常量分支直接返回同一类中文业务含义。");
                }
                lineIndex = endLineIndex;
            }
        }

        /// <summary>XMZADD 20260915 在禁止嵌套的有限源码块中定位与当前 Select Case 配对的结束行。</summary>
        private static int FindVbEndSelect(string[] lines, int startLineIndex)
        {
            for (int lineIndex = startLineIndex; lineIndex < lines.Length; lineIndex++)
            {
                string code = GetCodeBeforeTrailingComment(lines[lineIndex] ?? string.Empty).Trim();
                if (code.StartsWith("Select Case", StringComparison.OrdinalIgnoreCase))
                {
                    return -1;
                }
                if (string.Equals(code, "End Select", StringComparison.OrdinalIgnoreCase))
                {
                    return lineIndex;
                }
                if (lineIndex - startLineIndex >= 160)
                {
                    return -1;
                }
            }
            return -1;
        }

        /// <summary>XMZADD 20260915 要求每个常量 Case 只有一个中文返回或同目标赋值以排除流程控制分支。</summary>
        private static IList<EnumerationSourceItem> ParseVbSelectCaseItems(string[] codeLines,
            string[] originalLines, int startLineIndex, int endLineIndex)
        {
            var result = new List<EnumerationSourceItem>();
            string outputSignature = null;
            int lineIndex = startLineIndex;
            while (lineIndex < endLineIndex)
            {
                string caseLine = GetCodeBeforeTrailingComment(codeLines[lineIndex] ?? string.Empty).Trim();
                if (caseLine.Length == 0)
                {
                    lineIndex++;
                    continue;
                }
                if (VbCaseElseRegex.IsMatch(caseLine))
                {
                    lineIndex = FindNextVbCaseLine(codeLines, lineIndex + 1, endLineIndex);
                    continue;
                }

                Match caseMatch = VbCaseConstantRegex.Match(caseLine);
                if (!caseMatch.Success)
                {
                    return null;
                }
                int nextCaseLine = FindNextVbCaseLine(codeLines, lineIndex + 1, endLineIndex);
                int executableCount = 0;
                string caption = null;
                string currentSignature = null;
                string outputLine = null;
                for (int bodyIndex = lineIndex + 1; bodyIndex < nextCaseLine; bodyIndex++)
                {
                    string body = GetCodeBeforeTrailingComment(codeLines[bodyIndex] ?? string.Empty).Trim();
                    if (body.Length == 0)
                    {
                        continue;
                    }
                    executableCount++;
                    outputLine = GetOriginalLine(originalLines, bodyIndex + 1);
                    Match returnMatch = VbReturnCaptionRegex.Match(body);
                    if (returnMatch.Success)
                    {
                        caption = NormalizeVisualBasicLiteral(
                            "\"" + returnMatch.Groups["caption"].Value + "\"");
                        currentSignature = "RETURN";
                        continue;
                    }
                    Match assignmentMatch = VbAssignmentCaptionRegex.Match(body);
                    if (assignmentMatch.Success)
                    {
                        caption = NormalizeVisualBasicLiteral(
                            "\"" + assignmentMatch.Groups["caption"].Value + "\"");
                        currentSignature = "ASSIGN:" + assignmentMatch.Groups["target"].Value.ToUpperInvariant();
                    }
                }

                if (executableCount != 1 || !ContainsChineseCharacter(caption) ||
                    (outputSignature != null && !string.Equals(outputSignature, currentSignature,
                        StringComparison.Ordinal)))
                {
                    return null;
                }
                outputSignature = currentSignature;
                string value = NormalizeVisualBasicLiteral(caseMatch.Groups["value"].Value);
                result.Add(new EnumerationSourceItem(value, caption, lineIndex + 1,
                    caseLine + " => " + caption,
                    GetOriginalLine(originalLines, lineIndex + 1) + " | " + outputLine));
                lineIndex = nextCaseLine;
            }
            return result;
        }

        /// <summary>XMZADD 20260915 定位下一个 Case 或当前 End Select 边界以隔离各分支正文。</summary>
        private static int FindNextVbCaseLine(string[] lines, int startLineIndex, int endLineIndex)
        {
            for (int lineIndex = startLineIndex; lineIndex < endLineIndex; lineIndex++)
            {
                string code = GetCodeBeforeTrailingComment(lines[lineIndex] ?? string.Empty).Trim();
                if (code.StartsWith("Case ", StringComparison.OrdinalIgnoreCase))
                {
                    return lineIndex;
                }
            }
            return endLineIndex;
        }

        /// <summary>XMZADD 20260915 规范化 VB 数字、布尔和字符串字面量且还原双引号转义。</summary>
        private static string NormalizeVisualBasicLiteral(string literal)
        {
            string value = (literal ?? string.Empty).Trim();
            if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
            {
                return value.Substring(1, value.Length - 2).Replace("\"\"", "\"").Trim();
            }
            if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
            {
                return "True";
            }
            if (string.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
            {
                return "False";
            }
            return value;
        }

        /// <summary>XMZADD 20260915 识别 VB 成员结束边界以阻断同名局部字典跨方法串用。</summary>
        private static bool IsVbMemberBoundary(string code)
        {
            return string.Equals(code, "End Sub", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(code, "End Function", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(code, "End Property", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>XMZADD 20260904 仅在同文件证据把字段唯一归属到一张表时发布标题命名，否则记录冲突。</summary>
        private static void ExtractCaptions(string sourcePath, string modulePath, string[] codeLines,
            string[] originalLines,
            IDictionary<string, string> dataColumnFields,
            IDictionary<string, HashSet<string>> fieldOwners, IList<SourceEvidence> result,
            ISet<string> deduplicationKeys)
        {
            for (int lineIndex = 0; lineIndex < codeLines.Length; lineIndex++)
            {
                string line = codeLines[lineIndex] ?? string.Empty;
                if (IsCommentOnlyLine(line))
                {
                    continue;
                }

                MatchCollection directMatches = DirectCaptionRegex.Matches(line);
                for (int matchIndex = 0; matchIndex < directMatches.Count; matchIndex++)
                {
                    Match match = directMatches[matchIndex];
                    string fieldName = NormalizeIdentifier(match.Groups["field"].Value);
                    string caption = match.Groups["caption"].Value.Trim();
                    string ruleName = string.Equals(match.Groups["collection"].Value, "Cols",
                        StringComparison.OrdinalIgnoreCase) ? "GridColumnCaption" : "DataColumnCaption";
                    AddCaptionEvidence(result, deduplicationKeys, fieldOwners, fieldName, caption,
                        sourcePath, modulePath, lineIndex + 1, ruleName, match.Value,
                        GetOriginalLine(originalLines, lineIndex + 1));
                }

                MatchCollection resourceFieldMatches = ResourceFieldCaptionRegex.Matches(line);
                for (int matchIndex = 0; matchIndex < resourceFieldMatches.Count; matchIndex++)
                {
                    Match match = resourceFieldMatches[matchIndex];
                    string caption;
                    if (!TryGetResourceFallbackCaption(match.Groups["expression"].Value, out caption))
                    {
                        continue;
                    }
                    string fieldName = NormalizeIdentifier(match.Groups["field"].Value);
                    string ruleName = string.Equals(match.Groups["collection"].Value, "Cols",
                        StringComparison.OrdinalIgnoreCase) ? "GridColumnCaption" : "DataColumnCaption";
                    AddCaptionEvidence(result, deduplicationKeys, fieldOwners, fieldName, caption,
                        sourcePath, modulePath, lineIndex + 1, ruleName, match.Value,
                        GetOriginalLine(originalLines, lineIndex + 1));
                }

                MatchCollection variableMatches = VariableCaptionRegex.Matches(line);
                for (int matchIndex = 0; matchIndex < variableMatches.Count; matchIndex++)
                {
                    Match match = variableMatches[matchIndex];
                    string fieldName;
                    if (!dataColumnFields.TryGetValue(match.Groups["variable"].Value, out fieldName))
                    {
                        continue;
                    }
                    AddCaptionEvidence(result, deduplicationKeys, fieldOwners, fieldName,
                        match.Groups["caption"].Value.Trim(), sourcePath, modulePath, lineIndex + 1,
                        "DataColumnCaption", match.Value, GetOriginalLine(originalLines, lineIndex + 1));
                }


                MatchCollection resourceVariableMatches = ResourceVariableCaptionRegex.Matches(line);
                for (int matchIndex = 0; matchIndex < resourceVariableMatches.Count; matchIndex++)
                {
                    Match match = resourceVariableMatches[matchIndex];
                    string fieldName;
                    string caption;
                    if (!dataColumnFields.TryGetValue(match.Groups["variable"].Value, out fieldName) ||
                        !TryGetResourceFallbackCaption(match.Groups["expression"].Value, out caption))
                    {
                        continue;
                    }
                    AddCaptionEvidence(result, deduplicationKeys, fieldOwners, fieldName, caption,
                        sourcePath, modulePath, lineIndex + 1, "DataColumnCaption", match.Value,
                        GetOriginalLine(originalLines, lineIndex + 1));
                }
            }
        }

        /// <summary>XMZADD 20260914 只读取资源函数中显式存在的中文后备字面量，拒绝动态资源结果。</summary>
        private static bool TryGetResourceFallbackCaption(string expression, out string caption)
        {
            caption = null;
            Match match = ResourceFallbackRegex.Match(expression ?? string.Empty);
            if (!match.Success)
            {
                return false;
            }
            caption = match.Groups["doubleCaption"].Success
                ? match.Groups["doubleCaption"].Value.Trim()
                : match.Groups["singleCaption"].Value.Trim();
            return ContainsChineseCharacter(caption);
        }

        /// <summary>XMZADD 20260904 根据字段归属数量发布可靠标题或保留不可用于命名的冲突审计证据。</summary>
        private static void AddCaptionEvidence(IList<SourceEvidence> result, ISet<string> deduplicationKeys,
            IDictionary<string, HashSet<string>> fieldOwners, string fieldName, string caption,
            string sourcePath, string modulePath, int sourceLine, string ruleName, string rawValue,
            string originalText)
        {
            HashSet<string> owners;
            if (!fieldOwners.TryGetValue(fieldName, out owners) || owners.Count == 0)
            {
                return;
            }

            if (owners.Count == 1)
            {
                // 无表名界面标题只有在唯一归属时才能提升为中文字段名，避免多表同名字段互相污染。
                AddEvidence(result, deduplicationKeys, GetOnlyValue(owners), fieldName, null,
                    modulePath, caption, fieldName, sourcePath, sourceLine, ruleName, rawValue,
                    originalText, "同文件 SQL 或实体变量把界面字段唯一归属到该物理表，可作为中文名称候选。",
                    usageKind: SourceUsageKind.Display);
                return;
            }

            AddEvidence(result, deduplicationKeys, null, fieldName, null, modulePath, null,
                fieldName, sourcePath, sourceLine, ruleName + "Conflict", rawValue, originalText,
                "同名界面字段在同文件关联多张表，仅保留冲突审计且不用于中文命名。",
                usageKind: SourceUsageKind.Display);
        }

        /// <summary>XMZADD 20260915 按物理字段、常量值和中文含义去重并创建可审计枚举证据。</summary>
        private static void AddEnumerationEvidence(IList<SourceEvidence> result,
            ISet<string> deduplicationKeys, string objectName, string fieldName, string modulePath,
            string enumValue, string enumChineseName, string sourcePath, int sourceLine,
            string ruleName, string rawValue, string originalText, string explanation)
        {
            if (string.IsNullOrWhiteSpace(objectName) || string.IsNullOrWhiteSpace(fieldName) ||
                string.IsNullOrWhiteSpace(enumValue) || !ContainsChineseCharacter(enumChineseName))
            {
                return;
            }
            string key = (ruleName ?? string.Empty) + "|" + objectName + "|" + fieldName + "|" +
                         enumValue + "|" + enumChineseName + "|" + SourceUsageKind.Enumeration.ToString();
            if (!deduplicationKeys.Add(key))
            {
                return;
            }

            result.Add(new SourceEvidence
            {
                ObjectName = objectName,
                FieldName = fieldName,
                ModulePath = modulePath,
                EnumValue = enumValue,
                EnumChineseName = enumChineseName,
                Strength = SourceEvidenceStrength.DirectBusinessCode,
                UsageKind = SourceUsageKind.Enumeration,
                Evidence = new EvidenceItem
                {
                    SourceType = "EOS业务源码",
                    SourcePath = sourcePath,
                    SourceLine = sourceLine,
                    RuleName = ruleName,
                    RawValue = string.IsNullOrWhiteSpace(rawValue)
                        ? enumValue + "=" + enumChineseName
                        : rawValue.Trim(),
                    OriginalText = string.IsNullOrWhiteSpace(originalText) ? rawValue : originalText,
                    Explanation = explanation
                }
            });
        }

        /// <summary>XMZADD 20260911 按规则、表、字段、目标、候选和读写方向去重可审计证据。</summary>
        private static void AddEvidence(IList<SourceEvidence> result, ISet<string> deduplicationKeys,
            string objectName, string fieldName, string entityName, string modulePath,
            string chineseNameCandidate, string businessIdentifierCandidate, string sourcePath,
            int sourceLine, string ruleName, string rawValue, string originalText, string explanation,
            string relationTargetObjectName = null, string relationTargetFieldName = null,
            SourceUsageKind usageKind = SourceUsageKind.Unknown,
            SourceEvidenceStrength strength = SourceEvidenceStrength.DirectBusinessCode)
        {
            string target = (relationTargetObjectName ?? string.Empty) + "." +
                            (relationTargetFieldName ?? string.Empty);
            string candidate = chineseNameCandidate ?? businessIdentifierCandidate ?? string.Empty;
            string key = (ruleName ?? string.Empty) + "|" + (objectName ?? string.Empty) + "|" +
                         (fieldName ?? string.Empty) + "|" + target + "|" + candidate + "|" + usageKind.ToString();
            // 同一业务语义仅保留最早的可审计位置，确保安全数量预算按去重结果计算。
            if (!deduplicationKeys.Add(key))
            {
                return;
            }

            result.Add(new SourceEvidence
            {
                ObjectName = objectName,
                FieldName = fieldName,
                EntityName = entityName,
                ModulePath = modulePath,
                ChineseNameCandidate = chineseNameCandidate,
                BusinessIdentifierCandidate = businessIdentifierCandidate,
                RelationTargetObjectName = relationTargetObjectName,
                RelationTargetFieldName = relationTargetFieldName,
                Strength = strength,
                UsageKind = usageKind,
                Evidence = new EvidenceItem
                {
                    SourceType = "EOS源码",
                    SourcePath = sourcePath,
                    SourceLine = sourceLine,
                    RuleName = ruleName,
                    RawValue = string.IsNullOrWhiteSpace(rawValue) ? fieldName ?? objectName ?? ruleName : rawValue.Trim(),
                    OriginalText = string.IsNullOrWhiteSpace(originalText) ? rawValue : originalText,
                    Explanation = explanation
                }
            });
        }

        /// <summary>XMZADD 20260904 记录字段在指定表中的出现，用于后续唯一表归属判断。</summary>
        private static void AddFieldOwner(IDictionary<string, HashSet<string>> fieldOwners,
            string fieldName, string tableName)
        {
            if (string.IsNullOrWhiteSpace(fieldName) || string.IsNullOrWhiteSpace(tableName))
            {
                return;
            }

            HashSet<string> owners;
            if (!fieldOwners.TryGetValue(fieldName, out owners))
            {
                owners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                fieldOwners.Add(fieldName, owners);
            }
            owners.Add(tableName);
        }

        /// <summary>XMZADD 20260904 从候选表达式中保留首个业务标识符以供后续语义推断。</summary>
        private static string ExtractBusinessIdentifier(string expression)
        {
            Match match = IdentifierRegex.Match(expression ?? string.Empty);
            return match.Success ? match.Groups["identifier"].Value : null;
        }

        /// <summary>XMZADD 20260904 去除 SQL 方括号和多余空白，统一表字段及别名比较。</summary>
        private static string NormalizeIdentifier(string value)
        {
            string result = (value ?? string.Empty).Trim();
            if (result.Length >= 2 && result[0] == '[' && result[result.Length - 1] == ']')
            {
                result = result.Substring(1, result.Length - 2);
            }
            return result.Trim();
        }

        /// <summary>XMZADD 20260904 判断清洗后的值是否仍是单一数据库标识符。</summary>
        private static bool IsSimpleIdentifier(string value)
        {
            return Regex.IsMatch(value ?? string.Empty, @"^[A-Za-z_][A-Za-z0-9_]*$");
        }

        /// <summary>XMZADD 20260905 识别无法从静态源码还原的动态数据库对象占位。</summary>
        private static bool IsUnknownSqlObject(string value)
        {
            return string.Equals(value, UnknownSqlObjectPlaceholder, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>XMZADD 20260904 识别不允许作为 SQL 表别名或字段的保留字。</summary>
        private static bool IsSqlKeyword(string value)
        {
            switch ((value ?? string.Empty).ToUpperInvariant())
            {
                case "AS":
                case "SELECT":
                case "FROM":
                case "INNER":
                case "LEFT":
                case "RIGHT":
                case "FULL":
                case "OUTER":
                case "CROSS":
                case "JOIN":
                case "ON":
                case "WHERE":
                case "GROUP":
                case "BY":
                case "ORDER":
                case "PARTITION":
                case "HAVING":
                case "INSERT":
                case "INTO":
                case "UPDATE":
                case "SET":
                case "DELETE":
                case "VALUES":
                case "AND":
                case "OR":
                case "NOT":
                case "NULL":
                case "IS":
                case "LIKE":
                case "IN":
                case "ASC":
                case "DESC":
                case "DISTINCT":
                case "TOP":
                case "CASE":
                case "WHEN":
                case "THEN":
                case "ELSE":
                case "END":
                case "TRUE":
                case "FALSE":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>XMZADD 20260904 判断当前 VB 行是否声明显式续行且允许纳入同一逻辑 SQL 窗口。</summary>
        private static bool HasVisualBasicContinuation(string line)
        {
            string code = GetCodeBeforeTrailingComment(line).TrimEnd();
            if (code.EndsWith("_", StringComparison.Ordinal))
            {
                return true;
            }
            // EOS 的 VB 代码广泛依赖运算符后的隐式续行；要求存在字符串字面量可避开旧式类型字符误判。
            return code.EndsWith("&", StringComparison.Ordinal) &&
                   code.IndexOf('"') >= 0;
        }

        /// <summary>XMZADD 20260905 去除字符串外的 VB 或 C# 行末注释，避免注释字符触发伪续行。</summary>
        private static string GetCodeBeforeTrailingComment(string line)
        {
            string value = line ?? string.Empty;
            bool insideString = false;
            for (int characterIndex = 0; characterIndex < value.Length; characterIndex++)
            {
                char character = value[characterIndex];
                if (character == '"')
                {
                    if (insideString && characterIndex + 1 < value.Length &&
                        value[characterIndex + 1] == '"')
                    {
                        characterIndex++;
                        continue;
                    }
                    insideString = !insideString;
                    continue;
                }
                if (!insideString && character == '\'')
                {
                    return value.Substring(0, characterIndex);
                }
                if (!insideString && character == '/' && characterIndex + 1 < value.Length &&
                    value[characterIndex + 1] == '/')
                {
                    return value.Substring(0, characterIndex);
                }
            }
            return value;
        }

        /// <summary>XMZADD 20260905 按文件维护 C# 块注释状态并等长屏蔽注释字符，保留源码行号和列位置。</summary>
        internal static string[] MaskCSharpBlockComments(string relativePath, string[] lines)
        {
            if (lines == null ||
                !(relativePath ?? string.Empty).EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                return lines;
            }

            var result = new string[lines.Length];
            bool insideBlockComment = false;
            bool insideVerbatimString = false;
            for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                char[] characters = (lines[lineIndex] ?? string.Empty).ToCharArray();
                bool insideRegularString = false;
                bool insideCharacterLiteral = false;
                bool escaped = false;
                for (int characterIndex = 0; characterIndex < characters.Length; characterIndex++)
                {
                    char character = characters[characterIndex];
                    if (insideBlockComment)
                    {
                        if (character == '*' && characterIndex + 1 < characters.Length &&
                            characters[characterIndex + 1] == '/')
                        {
                            characters[characterIndex] = ' ';
                            characters[characterIndex + 1] = ' ';
                            characterIndex++;
                            insideBlockComment = false;
                        }
                        else
                        {
                            characters[characterIndex] = ' ';
                        }
                        continue;
                    }

                    if (insideVerbatimString)
                    {
                        if (character == '"')
                        {
                            if (characterIndex + 1 < characters.Length && characters[characterIndex + 1] == '"')
                            {
                                characterIndex++;
                            }
                            else
                            {
                                insideVerbatimString = false;
                            }
                        }
                        continue;
                    }

                    if (insideRegularString)
                    {
                        if (escaped)
                        {
                            escaped = false;
                        }
                        else if (character == '\\')
                        {
                            escaped = true;
                        }
                        else if (character == '"')
                        {
                            insideRegularString = false;
                        }
                        continue;
                    }

                    if (insideCharacterLiteral)
                    {
                        if (escaped)
                        {
                            escaped = false;
                        }
                        else if (character == '\\')
                        {
                            escaped = true;
                        }
                        else if (character == '\'')
                        {
                            insideCharacterLiteral = false;
                        }
                        continue;
                    }

                    if (character == '/' && characterIndex + 1 < characters.Length)
                    {
                        if (characters[characterIndex + 1] == '/')
                        {
                            // 行注释后的块注释分隔符只是注释文本，不得改变后续物理行的块注释状态。
                            break;
                        }
                        if (characters[characterIndex + 1] == '*')
                        {
                            characters[characterIndex] = ' ';
                            characters[characterIndex + 1] = ' ';
                            characterIndex++;
                            insideBlockComment = true;
                            continue;
                        }
                    }

                    if (character == '"')
                    {
                        if (IsCSharpVerbatimStringStart(characters, characterIndex))
                        {
                            insideVerbatimString = true;
                        }
                        else
                        {
                            insideRegularString = true;
                        }
                    }
                    else if (character == '\'')
                    {
                        insideCharacterLiteral = true;
                    }
                }
                result[lineIndex] = new string(characters);
            }
            return result;
        }

        /// <summary>XMZADD 20260905 识别普通、插值及两种前缀顺序的 C# 逐字字符串起点。</summary>
        private static bool IsCSharpVerbatimStringStart(char[] characters, int quoteIndex)
        {
            if (quoteIndex > 0 && characters[quoteIndex - 1] == '@')
            {
                return true;
            }
            return quoteIndex > 1 && characters[quoteIndex - 2] == '@' && characters[quoteIndex - 1] == '$';
        }

        /// <summary>XMZADD 20260905 取得 SQL 赋值语句的目标变量以限定后续追加语句的共享范围。</summary>
        private static bool TryGetSqlAssignedVariable(string line, out string variableName)
        {
            variableName = null;
            if (IsCommentOnlyLine(line))
            {
                return false;
            }
            Match compoundAppend = SqlCompoundAmpersandAssignmentRegex.Match(line ?? string.Empty);
            if (compoundAppend.Success)
            {
                variableName = compoundAppend.Groups["target"].Value;
                return !string.IsNullOrWhiteSpace(variableName);
            }
            Match match = SqlVariableAssignmentRegex.Match(line ?? string.Empty);
            if (!match.Success)
            {
                return false;
            }
            variableName = match.Groups["target"].Value;
            return !string.IsNullOrWhiteSpace(variableName);
        }

        /// <summary>XMZADD 20260905 仅允许连续同变量自追加 SQL 进入当前三十二行逻辑窗口。</summary>
        private static bool IsSameVariableSqlAppend(string line, string expectedVariable)
        {
            if (IsCommentOnlyLine(line))
            {
                return false;
            }
            Match compoundAppend = SqlCompoundAmpersandAssignmentRegex.Match(line ?? string.Empty);
            if (compoundAppend.Success)
            {
                // &= 只有目标变量连续一致时才属于同一业务 SQL，防止跨变量拼接污染别名作用域。
                return string.Equals(compoundAppend.Groups["target"].Value, expectedVariable,
                    StringComparison.OrdinalIgnoreCase);
            }
            Match assignment = SqlVariableAssignmentRegex.Match(line ?? string.Empty);
            if (!assignment.Success ||
                !string.Equals(assignment.Groups["target"].Value, expectedVariable,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            Match append = SqlAppendExpressionRegex.Match(assignment.Groups["expression"].Value);
            return append.Success && string.Equals(append.Groups["source"].Value,
                expectedVariable, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>XMZADD 20260904 提取多行 VB 字符串字面量并保留换行位置，避免拼接外壳割裂 SQL 子句。</summary>
        private static string NormalizeVisualBasicSqlText(string sourceText)
        {
            string[] sourceLines = (sourceText ?? string.Empty).Split(new[] { "\n" },
                StringSplitOptions.None);
            var result = new StringBuilder();
            bool foundStringLiteral = false;
            for (int lineIndex = 0; lineIndex < sourceLines.Length; lineIndex++)
            {
                bool lineContainsStringLiteral;
                string lineText = ExtractVisualBasicStringLiterals(sourceLines[lineIndex],
                    out lineContainsStringLiteral);
                if (lineContainsStringLiteral)
                {
                    foundStringLiteral = true;
                }
                if (lineIndex > 0)
                {
                    result.Append('\n');
                }
                result.Append(lineText);
            }

            return foundStringLiteral ? result.ToString() : sourceText ?? string.Empty;
        }

        /// <summary>XMZADD 20260904 合并单行 VB 字符串片段并还原双引号转义，排除变量声明及连接符。</summary>
        private static string ExtractVisualBasicStringLiterals(string sourceLine,
            out bool foundStringLiteral)
        {
            foundStringLiteral = false;
            var result = new StringBuilder();
            var outsideExpression = new StringBuilder();
            bool insideString = false;
            string value = sourceLine ?? string.Empty;
            for (int characterIndex = 0; characterIndex < value.Length; characterIndex++)
            {
                char character = value[characterIndex];
                if (!insideString)
                {
                    if (character == '"')
                    {
                        if (foundStringLiteral && HasDynamicConcatenationExpression(outsideExpression.ToString()))
                        {
                            AppendUnknownSqlObjectPlaceholder(result);
                        }
                        outsideExpression.Length = 0;
                        insideString = true;
                        foundStringLiteral = true;
                        if (result.Length > 0 && !char.IsWhiteSpace(result[result.Length - 1]))
                        {
                            result.Append(' ');
                        }
                    }
                    else if (foundStringLiteral)
                    {
                        if (character == '\'')
                        {
                            break;
                        }
                        outsideExpression.Append(character);
                    }
                    continue;
                }

                if (character != '"')
                {
                    result.Append(character);
                    continue;
                }

                if (characterIndex + 1 < value.Length && value[characterIndex + 1] == '"')
                {
                    result.Append('"');
                    characterIndex++;
                    continue;
                }

                insideString = false;
                outsideExpression.Length = 0;
            }
            if (foundStringLiteral && !insideString &&
                HasDynamicConcatenationExpression(outsideExpression.ToString()))
            {
                AppendUnknownSqlObjectPlaceholder(result);
            }
            return foundStringLiteral ? result.ToString() : value;
        }

        /// <summary>XMZADD 20260905 判断相邻字符串之间是否包含不能静态还原的变量或属性表达式。</summary>
        private static bool HasDynamicConcatenationExpression(string expression)
        {
            string value = expression ?? string.Empty;
            for (int characterIndex = 0; characterIndex < value.Length; characterIndex++)
            {
                char character = value[characterIndex];
                if (char.IsWhiteSpace(character) || character == '&' || character == '+' ||
                    character == '_' || character == '(' || character == ')')
                {
                    continue;
                }
                return true;
            }
            return false;
        }

        /// <summary>XMZADD 20260905 在动态表达式位置保留未知对象占位以阻断后续别名被当成物理表。</summary>
        private static void AppendUnknownSqlObjectPlaceholder(StringBuilder result)
        {
            if (result.Length > 0 && !char.IsWhiteSpace(result[result.Length - 1]))
            {
                result.Append(' ');
            }
            result.Append(UnknownSqlObjectPlaceholder);
            result.Append(' ');
        }

        /// <summary>XMZADD 20260905 以等长空白屏蔽 SQL 单引号常量并正确处理连续单引号转义。</summary>
        private static string MaskSqlSingleQuotedLiterals(string sqlText)
        {
            char[] characters = (sqlText ?? string.Empty).ToCharArray();
            bool insideLiteral = false;
            for (int characterIndex = 0; characterIndex < characters.Length; characterIndex++)
            {
                char character = characters[characterIndex];
                if (!insideLiteral)
                {
                    if (character == '\'')
                    {
                        // N 前缀仅在紧邻引号且前方不是标识符字符时属于 SQL Server Unicode 字面量。
                        int prefixIndex = characterIndex - 1;
                        if (prefixIndex >= 0 &&
                            (characters[prefixIndex] == 'N' || characters[prefixIndex] == 'n') &&
                            (prefixIndex == 0 || !IsSqlIdentifierCharacter(characters[prefixIndex - 1])))
                        {
                            characters[prefixIndex] = ' ';
                        }
                        insideLiteral = true;
                        characters[characterIndex] = ' ';
                    }
                    continue;
                }

                if (character == '\r' || character == '\n')
                {
                    continue;
                }
                characters[characterIndex] = ' ';
                if (character != '\'')
                {
                    continue;
                }
                if (characterIndex + 1 < characters.Length && characters[characterIndex + 1] == '\'')
                {
                    characters[characterIndex + 1] = ' ';
                    characterIndex++;
                    continue;
                }
                insideLiteral = false;
            }
            return new string(characters);
        }

        /// <summary>XMZADD 20260915 标记 SQL 行注释和块注释，使注释内的表名与别名不能成为业务证据。</summary>
        private static bool[] BuildSqlCommentCharacterMap(string sqlText)
        {
            string value = sqlText ?? string.Empty;
            var result = new bool[value.Length];
            bool insideLineComment = false;
            bool insideBlockComment = false;
            for (int index = 0; index < value.Length; index++)
            {
                char character = value[index];
                if (insideLineComment)
                {
                    if (character == '\r' || character == '\n')
                    {
                        insideLineComment = false;
                    }
                    else
                    {
                        result[index] = true;
                    }
                    continue;
                }
                if (insideBlockComment)
                {
                    result[index] = true;
                    if (character == '*' && index + 1 < value.Length && value[index + 1] == '/')
                    {
                        result[index + 1] = true;
                        index++;
                        insideBlockComment = false;
                    }
                    continue;
                }
                if (character == '-' && index + 1 < value.Length && value[index + 1] == '-')
                {
                    result[index] = true;
                    result[index + 1] = true;
                    index++;
                    insideLineComment = true;
                }
                else if (character == '/' && index + 1 < value.Length && value[index + 1] == '*')
                {
                    result[index] = true;
                    result[index + 1] = true;
                    index++;
                    insideBlockComment = true;
                }
            }
            return result;
        }

        /// <summary>XMZADD 20260915 按字符掩码等长清除 SQL 注释并保留换行位置用于证据定位。</summary>
        private static string MaskCharactersPreservingLines(string value, bool[] characterMask)
        {
            char[] characters = (value ?? string.Empty).ToCharArray();
            int maximum = Math.Min(characters.Length, characterMask == null ? 0 : characterMask.Length);
            for (int index = 0; index < maximum; index++)
            {
                if (characterMask[index] && characters[index] != '\r' && characters[index] != '\n')
                {
                    characters[index] = ' ';
                }
            }
            return new string(characters);
        }

        /// <summary>XMZADD 20260905 判断字符能否成为未加方括号 SQL 标识符的一部分。</summary>
        private static bool IsSqlIdentifierCharacter(char character)
        {
            return char.IsLetterOrDigit(character) || character == '_';
        }

        /// <summary>XMZADD 20260905 等长屏蔽已识别语法片段，使后续字段索引仍可映射到原始物理源码行。</summary>
        private static string MaskRegexMatchesPreservingLayout(string value, Regex regex)
        {
            char[] characters = (value ?? string.Empty).ToCharArray();
            MatchCollection matches = regex.Matches(value ?? string.Empty);
            for (int matchIndex = 0; matchIndex < matches.Count; matchIndex++)
            {
                Match match = matches[matchIndex];
                int endIndex = match.Index + match.Length;
                for (int characterIndex = match.Index; characterIndex < endIndex; characterIndex++)
                {
                    if (characters[characterIndex] != '\r' && characters[characterIndex] != '\n')
                    {
                        characters[characterIndex] = ' ';
                    }
                }
            }
            return new string(characters);
        }

        /// <summary>XMZADD 20260904 判断完整注释行，避免历史 SQL 或示例代码进入证据。</summary>
        private static bool IsCommentOnlyLine(string line)
        {
            string value = (line ?? string.Empty).TrimStart();
            return value.StartsWith("'", StringComparison.Ordinal) ||
                   value.StartsWith("//", StringComparison.Ordinal) ||
                   IsVisualBasicRemComment(value);
        }

        /// <summary>XMZADD 20260905 仅把独立 REM 关键字识别为 VB 注释，保留 Remote 和 Remark 等普通代码。</summary>
        private static bool IsVisualBasicRemComment(string value)
        {
            if (!value.StartsWith("REM", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            return value.Length == 3 || char.IsWhiteSpace(value[3]);
        }

        /// <summary>XMZADD 20260904 将逻辑 SQL 中的匹配位置换算为原始源码一基行号。</summary>
        private static int GetSourceLine(SqlBlock block, int characterIndex)
        {
            return block.GetSourceLine(characterIndex);
        }

        /// <summary>XMZADD 20260904 按一基行号返回可审计源码原文并安全处理边界。</summary>
        private static string GetOriginalLine(string[] lines, int sourceLine)
        {
            int index = sourceLine - 1;
            return index >= 0 && index < lines.Length ? lines[index] ?? string.Empty : string.Empty;
        }

        /// <summary>XMZADD 20260904 从已确认只有一个元素的集合中以传统遍历取得该值。</summary>
        private static string GetOnlyValue(IEnumerable<string> values)
        {
            foreach (string value in values)
            {
                return value;
            }
            return null;
        }

        /// <summary>XMZADD 20260914 保存一个 SELECT 来源作用域内无歧义的物理表和别名。</summary>
        private sealed class SelectSourceContext
        {
            /// <summary>XMZADD 20260914 初始化大小写不敏感的作用域映射，避免 SQL 大小写差异影响归属。</summary>
            public SelectSourceContext()
            {
                Aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                AmbiguousAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                Tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            public IDictionary<string, string> Aliases { get; private set; }
            public ISet<string> AmbiguousAliases { get; private set; }
            public ISet<string> Tables { get; private set; }
            public bool HasAmbiguousUnqualifiedSource { get; set; }
        }

        /// <summary>XMZADD 20260914 保存单个 SELECT 投影项文本及其在逻辑 SQL 中的位置。</summary>
        private sealed class ProjectionSegment
        {
            /// <summary>XMZADD 20260914 创建可映射回原始源码行的投影项。</summary>
            public ProjectionSegment(int startIndex, string text)
            {
                StartIndex = startIndex;
                Text = text ?? string.Empty;
            }

            public int StartIndex { get; private set; }
            public string Text { get; private set; }
        }

        /// <summary>XMZADD 20260915 保存提取阶段尚未发布的单个枚举值、中文含义和源码位置。</summary>
        private sealed class EnumerationSourceItem
        {
            /// <summary>XMZADD 20260915 创建只含字面常量的枚举证据候选。</summary>
            public EnumerationSourceItem(string value, string chineseName, int sourceLine,
                string rawValue, string originalText)
            {
                Value = value;
                ChineseName = chineseName;
                SourceLine = sourceLine;
                RawValue = rawValue;
                OriginalText = originalText;
            }

            public string Value { get; private set; }
            public string ChineseName { get; private set; }
            public int SourceLine { get; private set; }
            public string RawValue { get; private set; }
            public string OriginalText { get; private set; }
        }

        /// <summary>XMZADD 20260915 保存一个局部 DataMap 变量的静态项并标记是否混入动态来源。</summary>
        private sealed class MapVariableState
        {
            /// <summary>XMZADD 20260915 初始化局部 DataMap 的可写常量项集合。</summary>
            public MapVariableState()
            {
                Items = new List<EnumerationSourceItem>();
            }

            public IList<EnumerationSourceItem> Items { get; private set; }
            public bool HasDynamicEntry { get; set; }
        }

        /// <summary>XMZADD 20260914 保存受三十二行约束的 SQL 逻辑窗口、别名原文及源码位置。</summary>
        private sealed class SqlBlock
        {
            private readonly IList<int> lineStartCharacterIndexes;

            /// <summary>XMZADD 20260914 初始化 SQL 窗口并分离安全字段文本与保留输出别名的原文。</summary>
            public SqlBlock(int startLineIndex, int endLineIndex, string text, string aliasText)
            {
                StartLineIndex = startLineIndex;
                EndLineIndex = endLineIndex;
                Text = text ?? string.Empty;
                AliasText = aliasText ?? string.Empty;
                lineStartCharacterIndexes = new List<int>();
                lineStartCharacterIndexes.Add(0);
                for (int characterIndex = 0; characterIndex < Text.Length; characterIndex++)
                {
                    if (Text[characterIndex] == '\n')
                    {
                        lineStartCharacterIndexes.Add(characterIndex + 1);
                    }
                }
            }

            public int StartLineIndex { get; private set; }
            public int EndLineIndex { get; private set; }
            public string Text { get; private set; }
            public string AliasText { get; private set; }

            /// <summary>XMZADD 20260905 将规范化 SQL 字符位置映射为原始物理源码的一基行号。</summary>
            public int GetSourceLine(int characterIndex)
            {
                int normalizedIndex = Math.Max(0, Math.Min(characterIndex, Text.Length));
                int lineOffset = 0;
                for (int index = 1; index < lineStartCharacterIndexes.Count; index++)
                {
                    if (lineStartCharacterIndexes[index] > normalizedIndex)
                    {
                        break;
                    }
                    lineOffset = index;
                }
                return StartLineIndex + lineOffset + 1;
            }
        }
    }
}
