using System;
using System.Collections.Generic;
using System.IO;
using SHB.EosDataDictionary.Models;

namespace SHB.EosDataDictionary.Services
{
    /// <summary>XMZADD 20260901 验证远程规范快照的格式版本、稳定键和对象范围。</summary>
    public sealed class SnapshotValidator
    {
        /// <summary>XMZADD 20260901 定义当前客户端唯一支持的规范快照格式版本。</summary>
        public const int SupportedFormatVersion = 1;

        /// <summary>XMZADD 20260902 拒绝无法安全合并到本地缓存的远程规范快照。</summary>
        public void Validate(SnapshotData snapshot)
        {
            if (snapshot == null)
            {
                throw new InvalidDataException("远程规范快照没有可用的结构数据。");
            }

            if (snapshot.FormatVersion != SupportedFormatVersion)
            {
                throw new InvalidDataException("远程规范快照格式版本不受当前客户端支持。");
            }

            if (snapshot.Revision < 0)
            {
                throw new InvalidDataException("远程规范快照修订号不能为负数。");
            }

            if (snapshot.Tables == null)
            {
                throw new InvalidDataException("远程规范快照缺少对象集合。");
            }

            var objectKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int tableIndex = 0; tableIndex < snapshot.Tables.Count; tableIndex++)
            {
                TableMetadata table = snapshot.Tables[tableIndex];
                if (table == null)
                {
                    throw new InvalidDataException("远程规范快照包含空对象记录。");
                }

                if (string.IsNullOrWhiteSpace(table.SchemaName) || string.IsNullOrWhiteSpace(table.ObjectName))
                {
                    throw new InvalidDataException("远程规范快照包含空对象稳定键。");
                }

                string objectKey = table.SchemaName.Trim() + "." + table.ObjectName.Trim();
                if (!objectKeys.Add(objectKey))
                {
                    throw new InvalidDataException("远程规范快照包含重复对象稳定键。");
                }

                // 规范快照只承载可直接发布的表对象，视图和技术对象由排除记录说明原因。
                if (!string.Equals(table.ObjectType, "TABLE", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("远程规范快照只能包含 TABLE 对象。");
                }

                ValidateFields(table);
            }

            ValidateExcludedObjects(snapshot.ExcludedObjects, objectKeys);
            ValidateAbbreviations(snapshot.Abbreviations);
        }

        /// <summary>XMZADD 20260902 在完整快照进入公开仓库前逐项拒绝凭据、连接信息和本机绝对路径形态。</summary>
        internal void ValidatePublicContent(SnapshotData snapshot)
        {
            for (int tableIndex = 0; tableIndex < snapshot.Tables.Count; tableIndex++)
            {
                TableMetadata table = snapshot.Tables[tableIndex];
                EnsurePublicText(table.ScopeKey);
                EnsurePublicText(table.SchemaName);
                EnsurePublicText(table.ObjectName);
                EnsurePublicText(table.ObjectType);
                ValidatePublicMetadataValue(table.ChineseName);
                ValidatePublicMetadataValue(table.SuggestedChineseName);
                ValidatePublicMetadataValues(table.AlternativeChineseNames);
                ValidatePublicTexts(table.RejectedSuggestionFingerprints);
                ValidatePublicMetadataValues(table.UsedByModules);
                ValidatePublicMetadataValue(table.ModuleName);
                ValidatePublicMetadataValue(table.EntityName);
                ValidatePublicMetadataValue(table.BusinessMeaning);
                ValidatePublicMetadataValue(table.Remark);

                for (int fieldIndex = 0; fieldIndex < table.Fields.Count; fieldIndex++)
                {
                    ValidatePublicField(table.Fields[fieldIndex]);
                }
                for (int relationIndex = 0; relationIndex < table.Relations.Count; relationIndex++)
                {
                    ValidatePublicRelation(table.Relations[relationIndex]);
                }
            }

            for (int excludedIndex = 0; excludedIndex < snapshot.ExcludedObjects.Count; excludedIndex++)
            {
                ExcludedObjectRecord excluded = snapshot.ExcludedObjects[excludedIndex];
                EnsurePublicText(excluded.ObjectKey);
                EnsurePublicText(excluded.ReasonCode);
                EnsurePublicText(excluded.Reason);
            }
            for (int abbreviationIndex = 0; abbreviationIndex < snapshot.Abbreviations.Count; abbreviationIndex++)
            {
                ValidatePublicAbbreviation(snapshot.Abbreviations[abbreviationIndex]);
            }
        }

        /// <summary>XMZADD 20260902 校验字段结构、业务解释、枚举和证据均不含公开仓库禁止内容。</summary>
        private static void ValidatePublicField(FieldMetadata field)
        {
            EnsurePublicText(field.FieldName);
            EnsurePublicText(field.OwnerTableName);
            EnsurePublicText(field.DataType);
            EnsurePublicText(field.LengthText);
            ValidatePublicMetadataValue(field.ChineseName);
            ValidatePublicMetadataValue(field.SuggestedChineseName);
            ValidatePublicMetadataValues(field.AlternativeChineseNames);
            ValidatePublicTexts(field.RejectedSuggestionFingerprints);
            ValidatePublicMetadataValue(field.EntityPropertyName);
            ValidatePublicMetadataValue(field.BusinessMeaning);
            ValidatePublicMetadataValue(field.Usage);
            ValidatePublicMetadataValue(field.EnumName);
            ValidatePublicMetadataValue(field.RelationSummary);
            ValidatePublicMetadataValue(field.Remark);
            for (int enumIndex = 0; enumIndex < field.EnumItems.Count; enumIndex++)
            {
                EnumItemMetadata item = field.EnumItems[enumIndex];
                EnsurePublicText(item.Value);
                ValidatePublicMetadataValue(item.ChineseName);
            }
        }

        /// <summary>XMZADD 20260902 校验关系稳定键和业务说明不含连接信息或本机路径。</summary>
        private static void ValidatePublicRelation(RelationMetadata relation)
        {
            EnsurePublicText(relation.ScopeKey);
            EnsurePublicText(relation.ForeignKeyName);
            EnsurePublicText(relation.ParentSchemaName);
            EnsurePublicText(relation.ParentTableName);
            EnsurePublicText(relation.ParentFieldName);
            EnsurePublicText(relation.ChildSchemaName);
            EnsurePublicText(relation.ChildTableName);
            EnsurePublicText(relation.ChildFieldName);
            ValidatePublicMetadataValue(relation.RelationType);
            ValidatePublicMetadataValue(relation.BusinessMeaning);
            ValidatePublicMetadataValue(relation.Remark);
        }

        /// <summary>XMZADD 20260902 校验一个业务解释及其有限证据，不允许原始源码正文重新进入规范快照。</summary>
        private static void ValidatePublicMetadataValue(MetadataValue value)
        {
            if (value == null)
            {
                return;
            }
            EnsurePublicText(value.Value);
            EnsurePublicText(value.Description);
            EnsurePublicText(value.SourceType);
            EnsurePublicText(value.SourceSummary);
            EnsurePublicText(value.OriginalAutomaticValue);
            if (value.Evidence == null)
            {
                return;
            }
            for (int evidenceIndex = 0; evidenceIndex < value.Evidence.Count; evidenceIndex++)
            {
                EvidenceItem evidence = value.Evidence[evidenceIndex];
                if (evidence == null || !string.IsNullOrEmpty(evidence.RawValue) ||
                    !string.IsNullOrEmpty(evidence.OriginalText))
                {
                    throw new InvalidDataException("公开快照证据包含原始正文。");
                }
                EnsurePublicText(evidence.SourceType);
                EnsurePublicRelativePath(evidence.SourcePath);
                EnsurePublicText(evidence.RuleName);
                EnsurePublicText(evidence.Explanation);
            }
        }

        /// <summary>XMZADD 20260911 逐项校验参考名称和使用模块，防止新增证据层绕过公开内容安全边界。</summary>
        private static void ValidatePublicMetadataValues(IList<MetadataValue> values)
        {
            if (values == null)
            {
                return;
            }
            for (int index = 0; index < values.Count; index++)
            {
                ValidatePublicMetadataValue(values[index]);
            }
        }

        /// <summary>XMZADD 20260911 校验拒绝建议指纹文本，避免异常维护内容携带凭据或本机路径。</summary>
        private static void ValidatePublicTexts(IList<string> values)
        {
            if (values == null)
            {
                return;
            }
            for (int index = 0; index < values.Count; index++)
            {
                EnsurePublicText(values[index]);
            }
        }

        /// <summary>XMZADD 20260902 校验共享缩写及其相对来源，不允许本机知识库位置进入仓库。</summary>
        private static void ValidatePublicAbbreviation(AbbreviationEntry abbreviation)
        {
            EnsurePublicText(abbreviation.Abbreviation);
            EnsurePublicText(abbreviation.ChineseMeaning);
            EnsurePublicText(abbreviation.ModuleScope);
            EnsurePublicText(abbreviation.TableScope);
            for (int evidenceIndex = 0; evidenceIndex < abbreviation.Evidence.Count; evidenceIndex++)
            {
                AbbreviationEvidence evidence = abbreviation.Evidence[evidenceIndex];
                EnsurePublicText(evidence.SourceType);
                EnsurePublicRelativePath(evidence.RelativePath);
                EnsurePublicText(evidence.Summary);
            }
        }

        /// <summary>XMZADD 20260902 拒绝公开字符串中的凭据赋值、连接属性和绝对路径形态。</summary>
        private static void EnsurePublicText(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }
            if (SensitiveTextGuard.ContainsCredentialShape(value) ||
                value.IndexOf("ApplicationIntent", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("Data Source", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("Initial Catalog", StringComparison.OrdinalIgnoreCase) >= 0 ||
                ContainsAbsolutePathShape(value))
            {
                throw new InvalidDataException("公开快照包含凭据、连接信息或本机路径形态。");
            }
        }

        /// <summary>XMZADD 20260902 只允许证据使用不含盘符、协议和父级跳转的仓库相对路径。</summary>
        private static void EnsurePublicRelativePath(string value)
        {
            string path = value ?? string.Empty;
            if (path.Length > 512 || Path.IsPathRooted(path) || path.IndexOf(':') >= 0 ||
                path.IndexOf("..", StringComparison.Ordinal) >= 0 || ContainsAbsolutePathShape(path))
            {
                throw new InvalidDataException("公开快照证据路径无效。");
            }
            EnsurePublicText(path);
        }

        /// <summary>XMZADD 20260902 识别盘符、UNC、URL 和 Unix 根路径，避免它们藏在说明字段中进入公开快照。</summary>
        private static bool ContainsAbsolutePathShape(string value)
        {
            string text = value.Trim();
            if (text.StartsWith("/", StringComparison.Ordinal) ||
                text.StartsWith("\\\\", StringComparison.Ordinal) ||
                text.StartsWith("//", StringComparison.Ordinal))
            {
                return true;
            }
            for (int index = 0; index + 2 < value.Length; index++)
            {
                if (char.IsLetter(value[index]) && value[index + 1] == ':' &&
                    (value[index + 2] == '\\' || value[index + 2] == '/'))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>XMZADD 20260902 验证同一对象内的字段稳定键完整且唯一。</summary>
        private static void ValidateFields(TableMetadata table)
        {
            if (table.Fields == null)
            {
                throw new InvalidDataException("远程规范快照中的对象缺少字段集合。");
            }

            if (table.Relations == null)
            {
                throw new InvalidDataException("远程规范快照中的对象缺少关系集合。");
            }

            var fieldKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int fieldIndex = 0; fieldIndex < table.Fields.Count; fieldIndex++)
            {
                FieldMetadata field = table.Fields[fieldIndex];
                if (field == null || string.IsNullOrWhiteSpace(field.FieldName))
                {
                    throw new InvalidDataException("远程规范快照包含空字段稳定键。");
                }

                if (!fieldKeys.Add(field.FieldName.Trim()))
                {
                    throw new InvalidDataException("远程规范快照在同一对象内包含重复字段稳定键。");
                }

                if (field.EnumItems == null)
                {
                    throw new InvalidDataException("远程规范快照中的字段缺少枚举项集合。");
                }

                for (int enumIndex = 0; enumIndex < field.EnumItems.Count; enumIndex++)
                {
                    if (field.EnumItems[enumIndex] == null)
                    {
                        throw new InvalidDataException("远程规范快照中的字段包含空枚举项。");
                    }
                }
            }

            for (int relationIndex = 0; relationIndex < table.Relations.Count; relationIndex++)
            {
                if (table.Relations[relationIndex] == null)
                {
                    throw new InvalidDataException("远程规范快照中的对象包含空关系记录。");
                }
            }
        }

        /// <summary>XMZADD 20260902 验证排除对象也使用唯一稳定键且不会与已发布对象重叠。</summary>
        private static void ValidateExcludedObjects(
            IList<ExcludedObjectRecord> excludedObjects,
            HashSet<string> objectKeys)
        {
            if (excludedObjects == null)
            {
                throw new InvalidDataException("远程规范快照缺少排除对象集合。");
            }

            for (int index = 0; index < excludedObjects.Count; index++)
            {
                ExcludedObjectRecord excludedObject = excludedObjects[index];
                if (excludedObject == null || string.IsNullOrWhiteSpace(excludedObject.ObjectKey))
                {
                    throw new InvalidDataException("远程规范快照包含空排除对象稳定键。");
                }

                if (!objectKeys.Add(excludedObject.ObjectKey.Trim()))
                {
                    throw new InvalidDataException("远程规范快照包含重复对象稳定键。");
                }
            }
        }

        /// <summary>XMZADD 20260902 验证缩写键和证据集合可供共享字典安全合并。</summary>
        private static void ValidateAbbreviations(IList<AbbreviationEntry> abbreviations)
        {
            if (abbreviations == null)
            {
                throw new InvalidDataException("远程规范快照缺少缩写集合。");
            }

            for (int index = 0; index < abbreviations.Count; index++)
            {
                AbbreviationEntry abbreviation = abbreviations[index];
                if (abbreviation == null)
                {
                    throw new InvalidDataException("远程规范快照包含空缩写记录。");
                }

                if (string.IsNullOrWhiteSpace(abbreviation.Abbreviation))
                {
                    throw new InvalidDataException("远程规范快照包含空缩写稳定键。");
                }

                if (abbreviation.Evidence == null)
                {
                    throw new InvalidDataException("远程规范快照中的缩写缺少证据集合。");
                }
            }
        }
    }
}
