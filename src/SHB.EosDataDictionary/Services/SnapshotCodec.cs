using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using SHB.EosDataDictionary.Models;

namespace SHB.EosDataDictionary.Services
{
    /// <summary>XMZADD 20260902 标识规范快照压缩输出超过统一传输边界，供发布流程进行稳定类型分类。</summary>
    internal sealed class SnapshotCompressedSizeExceededException : IOException
    {
        internal const string FixedMessage = "快照压缩内容超过安全大小限制。";

        /// <summary>XMZADD 20260902 创建不携带实际内容长度的固定压缩容量异常。</summary>
        internal SnapshotCompressedSizeExceededException()
            : base(FixedMessage)
        {
        }
    }

    /// <summary>XMZADD 20260901 使用确定性 JSON/GZip 格式编解码规范快照并校验内容完整性。</summary>
    public sealed class SnapshotCodec
    {
        internal const int MaximumCompressedBytes = 100 * 1024 * 1024;
        internal const long MaximumDecompressedBytes = 1024L * 1024L * 1024L;
        private const int DecompressionBufferSize = 81920;
        private const string ContentSizeExceededMessage = "快照解压内容超过安全大小限制。";
        private readonly SnapshotValidator _validator = new SnapshotValidator();

        /// <summary>XMZADD 20260902 将排序后的快照直接流式写入受压缩大小限制的 JSON/GZip 内容。</summary>
        public byte[] Encode(SnapshotData snapshot)
        {
            return Encode(snapshot, MaximumCompressedBytes);
        }

        /// <summary>XMZADD 20260902 使用内部可调压缩边界编码快照，便于低内存验证正式输出保护规则。</summary>
        internal byte[] Encode(SnapshotData snapshot, int maximumCompressedBytes)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException("snapshot");
            }
            if (maximumCompressedBytes < 0)
            {
                throw new ArgumentOutOfRangeException("maximumCompressedBytes");
            }

            SnapshotData orderedSnapshot = CreateOrderedSnapshot(snapshot);
            DataContractJsonSerializer serializer = CreateJsonSerializer(typeof(SnapshotData));
            using (var output = new SizeLimitedMemoryStream(maximumCompressedBytes))
            {
                using (var compressedStream = new GZipStream(output, CompressionMode.Compress, true))
                {
                    // 直接压缩序列化结果，避免大型规范快照同时保留完整未压缩 JSON 副本。
                    serializer.WriteObject(compressedStream, orderedSnapshot);
                }

                return output.ToArray();
            }
        }

        /// <summary>XMZADD 20260901 校验远程压缩内容哈希并仅返回通过规范规则的快照。</summary>
        public SnapshotData DecodeAndValidate(byte[] compressedContent, string expectedSha256)
        {
            if (compressedContent == null || compressedContent.Length == 0)
            {
                throw new InvalidDataException("远程规范快照内容为空。");
            }

            string actualSha256 = ComputeSha256(compressedContent);
            if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("远程规范快照完整性校验失败。");
            }

            SnapshotData snapshot = DecodeWithoutValidation(compressedContent);
            _validator.Validate(snapshot);
            return snapshot;
        }

        /// <summary>XMZADD 20260901 计算压缩快照内容的 SHA-256 十六进制校验值。</summary>
        public string ComputeSha256(byte[] content)
        {
            if (content == null)
            {
                throw new ArgumentNullException("content");
            }

            using (SHA256 sha256 = SHA256.Create())
            {
                byte[] hash = sha256.ComputeHash(content);
                return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        /// <summary>XMZADD 20260902 为本地历史恢复流式解码新格式但不套用远程规范限制。</summary>
        internal SnapshotData DecodeWithoutValidation(byte[] compressedContent)
        {
            return DecodeWithoutValidation(compressedContent, MaximumDecompressedBytes);
        }

        /// <summary>XMZADD 20260902 使用内部可调读取边界解码快照，便于低内存验证正式解压保护规则。</summary>
        internal SnapshotData DecodeWithoutValidation(byte[] compressedContent, long maximumDecompressedBytes)
        {
            if (compressedContent == null || compressedContent.Length == 0)
            {
                throw new InvalidDataException("快照内容为空。");
            }
            if (compressedContent.Length > MaximumCompressedBytes)
            {
                throw new InvalidDataException(SnapshotCompressedSizeExceededException.FixedMessage);
            }
            if (maximumDecompressedBytes < 0)
            {
                throw new ArgumentOutOfRangeException("maximumDecompressedBytes");
            }

            try
            {
                using (var input = new MemoryStream(compressedContent, false))
                using (var compressedStream = new GZipStream(input, CompressionMode.Decompress))
                using (var jsonStream = new SizeLimitedReadStream(compressedStream, maximumDecompressedBytes))
                {
                    DataContractJsonSerializer serializer = CreateJsonSerializer(typeof(SnapshotData));
                    // 反序列化器直接消费受计数保护的解压流，避免复制完整 JSON 到第二个内存流。
                    var snapshot = serializer.ReadObject(jsonStream) as SnapshotData;
                    if (snapshot == null)
                    {
                        throw new SerializationException("快照无法还原为结构数据。");
                    }

                    // 读取到压缩流末尾才能同时核验剩余内容、GZip 尾部和完整解压总量。
                    DrainRemainingContent(jsonStream);
                    NormalizeNameLayerCollections(snapshot);
                    return snapshot;
                }
            }
            catch (InvalidDataException exception)
            {
                if (string.Equals(exception.Message, ContentSizeExceededMessage, StringComparison.Ordinal))
                {
                    throw;
                }

                throw new InvalidDataException("快照内容无法解压或反序列化，请重新同步。", exception);
            }
            catch (Exception exception)
            {
                if (exception is OutOfMemoryException || exception is StackOverflowException)
                {
                    throw;
                }

                // 对外只说明快照业务状态，避免把远程原始内容带入异常信息。
                throw new InvalidDataException("快照内容无法解压或反序列化，请重新同步。", exception);
            }
        }

        /// <summary>XMZADD 20260902 读取反序列化后的剩余压缩内容以完成 GZip 完整性和资源边界校验。</summary>
        private static void DrainRemainingContent(Stream source)
        {
            var buffer = new byte[DecompressionBufferSize];
            while (source.Read(buffer, 0, buffer.Length) > 0)
            {
            }
        }

        /// <summary>XMZADD 20260901 创建只调整集合顺序的快照副本，避免改变界面当前展示顺序。</summary>
        private static SnapshotData CreateOrderedSnapshot(SnapshotData snapshot)
        {
            var orderedSnapshot = new SnapshotData
            {
                FormatVersion = snapshot.FormatVersion,
                Revision = snapshot.Revision,
                RefreshedAt = snapshot.RefreshedAt,
                ExcludedObjects = CreateOrderedExcludedObjects(snapshot.ExcludedObjects),
                Abbreviations = CreateOrderedAbbreviations(snapshot.Abbreviations)
            };

            if (snapshot.Tables == null)
            {
                orderedSnapshot.Tables = null;
                return orderedSnapshot;
            }

            var tables = new List<TableMetadata>();
            for (int index = 0; index < snapshot.Tables.Count; index++)
            {
                tables.Add(CreateOrderedTable(snapshot.Tables[index]));
            }

            tables.Sort(CompareTables);
            orderedSnapshot.Tables = tables;
            return orderedSnapshot;
        }

        /// <summary>XMZADD 20260901 复制对象元数据并独立排序字段和关系集合。</summary>
        private static TableMetadata CreateOrderedTable(TableMetadata table)
        {
            if (table == null)
            {
                return null;
            }

            var orderedTable = new TableMetadata
            {
                ScopeKey = table.ScopeKey,
                SchemaName = table.SchemaName,
                ObjectName = table.ObjectName,
                ObjectType = table.ObjectType,
                ChineseName = CompactOfficialName(table.ChineseName),
                SuggestedChineseName = table.SuggestedChineseName,
                AlternativeChineseNames = CopyMetadataValues(table.AlternativeChineseNames),
                RejectedSuggestionFingerprints = CopyStrings(table.RejectedSuggestionFingerprints),
                UsedByModules = CopyMetadataValues(table.UsedByModules),
                ModuleName = table.ModuleName,
                EntityName = table.EntityName,
                BusinessMeaning = table.BusinessMeaning,
                Remark = table.Remark,
                Category = table.Category,
                ApproximateRowCount = table.ApproximateRowCount,
                KeepWhenEmpty = table.KeepWhenEmpty
            };

            if (table.Fields == null)
            {
                orderedTable.Fields = null;
            }
            else
            {
                var fields = new List<FieldMetadata>();
                for (int index = 0; index < table.Fields.Count; index++)
                {
                    fields.Add(CreateOrderedField(table.Fields[index]));
                }

                fields.Sort(CompareFields);
                orderedTable.Fields = fields;
            }

            orderedTable.Relations = CreateOrderedRelations(table.Relations);
            return orderedTable;
        }

        /// <summary>XMZADD 20260901 完整复制字段并独立排序枚举项，避免改变编辑界面的原始集合。</summary>
        private static FieldMetadata CreateOrderedField(FieldMetadata field)
        {
            if (field == null)
            {
                return null;
            }

            var orderedField = new FieldMetadata
            {
                FieldName = field.FieldName,
                ChineseName = CompactOfficialName(field.ChineseName),
                SuggestedChineseName = field.SuggestedChineseName,
                AlternativeChineseNames = CopyMetadataValues(field.AlternativeChineseNames),
                RejectedSuggestionFingerprints = CopyStrings(field.RejectedSuggestionFingerprints),
                OwnerTableName = field.OwnerTableName,
                EntityPropertyName = field.EntityPropertyName,
                BusinessMeaning = field.BusinessMeaning,
                Usage = field.Usage,
                DataType = field.DataType,
                LengthText = field.LengthText,
                IsRequired = field.IsRequired,
                IsPrimaryKey = field.IsPrimaryKey,
                IsForeignKey = field.IsForeignKey,
                EnumName = field.EnumName,
                RelationSummary = field.RelationSummary,
                Remark = field.Remark
            };

            if (field.EnumItems == null)
            {
                orderedField.EnumItems = null;
                return orderedField;
            }

            var enumItems = new List<EnumItemMetadata>();
            for (int index = 0; index < field.EnumItems.Count; index++)
            {
                EnumItemMetadata enumItem = field.EnumItems[index];
                enumItems.Add(enumItem == null
                    ? null
                    : new EnumItemMetadata
                    {
                        Value = enumItem.Value,
                        ChineseName = enumItem.ChineseName
                    });
            }

            enumItems.Sort(CompareEnumItems);
            orderedField.EnumItems = enumItems;
            return orderedField;
        }

        /// <summary>XMZADD 20260910 复制参考名称集合以隔离编码排序副本与界面正在维护的集合。</summary>
        private static IList<MetadataValue> CopyMetadataValues(IList<MetadataValue> source)
        {
            var result = new List<MetadataValue>();
            if (source == null)
            {
                return result;
            }
            for (int index = 0; index < source.Count; index++)
            {
                result.Add(source[index]);
            }
            result.Sort(CompareMetadataValues);
            return result;
        }

        /// <summary>XMZADD 20260910 复制建议指纹集合以避免编码过程改动调用方顺序。</summary>
        private static IList<string> CopyStrings(IList<string> source)
        {
            var result = new List<string>();
            if (source == null)
            {
                return result;
            }
            for (int index = 0; index < source.Count; index++)
            {
                result.Add(source[index]);
            }
            result.Sort(StringComparer.Ordinal);
            return result;
        }

        /// <summary>XMZADD 20260916 序列化时省略未达到准入门槛的空正式名，避免海量字段重复保存同一占位说明。</summary>
        private static MetadataValue CompactOfficialName(MetadataValue value)
        {
            if (value == null)
            {
                return null;
            }
            if (string.IsNullOrWhiteSpace(value.Value) &&
                value.Status == ConfidenceStatus.PendingConfirmation &&
                !value.IsManualOverride && !value.IsLocked &&
                (value.Evidence == null || value.Evidence.Count == 0))
            {
                return null;
            }
            return value;
        }

        /// <summary>XMZADD 20260910 将旧快照缺失或显式为空的参考名称集合恢复为可直接维护的空列表。</summary>
        private static void NormalizeNameLayerCollections(SnapshotData snapshot)
        {
            if (snapshot.Tables == null)
            {
                return;
            }
            for (int tableIndex = 0; tableIndex < snapshot.Tables.Count; tableIndex++)
            {
                TableMetadata table = snapshot.Tables[tableIndex];
                if (table == null)
                {
                    continue;
                }
                table.AlternativeChineseNames = CopyMutableList(table.AlternativeChineseNames);
                table.RejectedSuggestionFingerprints = CopyMutableList(table.RejectedSuggestionFingerprints);
                table.UsedByModules = CopyMutableList(table.UsedByModules);
                if (table.Fields == null)
                {
                    continue;
                }
                for (int fieldIndex = 0; fieldIndex < table.Fields.Count; fieldIndex++)
                {
                    FieldMetadata field = table.Fields[fieldIndex];
                    if (field == null)
                    {
                        continue;
                    }
                    field.AlternativeChineseNames = CopyMutableList(field.AlternativeChineseNames);
                    field.RejectedSuggestionFingerprints = CopyMutableList(field.RejectedSuggestionFingerprints);
                }
            }
        }

        /// <summary>XMZADD 20260911 将快照集合恢复为非空可变列表，保证解码后仍可继续维护参考译名。</summary>
        private static IList<T> CopyMutableList<T>(IList<T> source)
        {
            var result = new List<T>();
            if (source == null)
            {
                return result;
            }
            for (int index = 0; index < source.Count; index++)
            {
                result.Add(source[index]);
            }
            return result;
        }

        /// <summary>XMZADD 20260901 复制并按对象键和排除原因稳定排列排除记录。</summary>
        private static IList<ExcludedObjectRecord> CreateOrderedExcludedObjects(
            IList<ExcludedObjectRecord> excludedObjects)
        {
            if (excludedObjects == null)
            {
                return null;
            }

            var result = new List<ExcludedObjectRecord>();
            for (int index = 0; index < excludedObjects.Count; index++)
            {
                ExcludedObjectRecord excludedObject = excludedObjects[index];
                result.Add(excludedObject == null
                    ? null
                    : new ExcludedObjectRecord
                    {
                        ObjectKey = excludedObject.ObjectKey,
                        ReasonCode = excludedObject.ReasonCode,
                        Reason = excludedObject.Reason,
                        Category = excludedObject.Category,
                        ApproximateRowCount = excludedObject.ApproximateRowCount,
                        ExcludedAtUtc = excludedObject.ExcludedAtUtc
                    });
            }

            result.Sort(CompareExcludedObjects);
            return result;
        }

        /// <summary>XMZADD 20260901 复制并按缩写业务范围稳定排列缩写及其证据。</summary>
        private static IList<AbbreviationEntry> CreateOrderedAbbreviations(
            IList<AbbreviationEntry> abbreviations)
        {
            if (abbreviations == null)
            {
                return null;
            }

            var result = new List<AbbreviationEntry>();
            for (int index = 0; index < abbreviations.Count; index++)
            {
                AbbreviationEntry abbreviation = abbreviations[index];
                result.Add(abbreviation == null
                    ? null
                    : new AbbreviationEntry
                    {
                        Abbreviation = abbreviation.Abbreviation,
                        ChineseMeaning = abbreviation.ChineseMeaning,
                        ModuleScope = abbreviation.ModuleScope,
                        TableScope = abbreviation.TableScope,
                        ConfidenceScore = abbreviation.ConfidenceScore,
                        Status = abbreviation.Status,
                        Evidence = CreateOrderedAbbreviationEvidence(abbreviation.Evidence)
                    });
            }

            result.Sort(CompareAbbreviations);
            return result;
        }

        /// <summary>XMZADD 20260901 复制并按来源位置稳定排列缩写证据。</summary>
        private static IList<AbbreviationEvidence> CreateOrderedAbbreviationEvidence(
            IList<AbbreviationEvidence> evidenceItems)
        {
            if (evidenceItems == null)
            {
                return null;
            }

            var result = new List<AbbreviationEvidence>();
            for (int index = 0; index < evidenceItems.Count; index++)
            {
                AbbreviationEvidence evidence = evidenceItems[index];
                result.Add(evidence == null
                    ? null
                    : new AbbreviationEvidence
                    {
                        SourceType = evidence.SourceType,
                        RelativePath = evidence.RelativePath,
                        LineNumber = evidence.LineNumber,
                        Summary = evidence.Summary
                    });
            }

            result.Sort(CompareAbbreviationEvidence);
            return result;
        }

        /// <summary>XMZADD 20260902 复制关系引用并按全部数据契约字段稳定排列，避免缓存完整关系 JSON。</summary>
        private static IList<RelationMetadata> CreateOrderedRelations(IList<RelationMetadata> relations)
        {
            if (relations == null)
            {
                return null;
            }

            var result = new List<RelationMetadata>();
            for (int index = 0; index < relations.Count; index++)
            {
                result.Add(relations[index]);
            }

            result.Sort(CompareRelations);
            return result;
        }

        /// <summary>XMZADD 20260901 按模式名和对象名稳定排列规范对象。</summary>
        private static int CompareTables(TableMetadata left, TableMetadata right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left == null)
            {
                return -1;
            }

            if (right == null)
            {
                return 1;
            }

            int comparison = CompareStableText(left.SchemaName, right.SchemaName);
            if (comparison != 0)
            {
                return comparison;
            }

            return CompareStableText(left.ObjectName, right.ObjectName);
        }

        /// <summary>XMZADD 20260901 按字段名稳定排列同一对象内的字段。</summary>
        private static int CompareFields(FieldMetadata left, FieldMetadata right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left == null)
            {
                return -1;
            }

            if (right == null)
            {
                return 1;
            }

            return CompareStableText(left.FieldName, right.FieldName);
        }

        /// <summary>XMZADD 20260902 按枚举值和完整中文解释稳定排列字段枚举项。</summary>
        private static int CompareEnumItems(EnumItemMetadata left, EnumItemMetadata right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left == null)
            {
                return -1;
            }

            if (right == null)
            {
                return 1;
            }

            int comparison = CompareStableText(left.Value, right.Value);
            if (comparison != 0)
            {
                return comparison;
            }

            return CompareMetadataValues(left.ChineseName, right.ChineseName);
        }

        /// <summary>XMZADD 20260902 按关系全部字符串和业务解释字段稳定比较，不生成关系 JSON 副本。</summary>
        private static int CompareRelations(RelationMetadata left, RelationMetadata right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }
            if (left == null)
            {
                return -1;
            }
            if (right == null)
            {
                return 1;
            }

            int comparison = CompareStableText(left.ScopeKey, right.ScopeKey);
            if (comparison != 0)
            {
                return comparison;
            }
            comparison = CompareStableText(left.ForeignKeyName, right.ForeignKeyName);
            if (comparison != 0)
            {
                return comparison;
            }
            comparison = CompareStableText(left.ParentSchemaName, right.ParentSchemaName);
            if (comparison != 0)
            {
                return comparison;
            }
            comparison = CompareStableText(left.ParentTableName, right.ParentTableName);
            if (comparison != 0)
            {
                return comparison;
            }
            comparison = CompareStableText(left.ParentFieldName, right.ParentFieldName);
            if (comparison != 0)
            {
                return comparison;
            }
            comparison = CompareStableText(left.ChildSchemaName, right.ChildSchemaName);
            if (comparison != 0)
            {
                return comparison;
            }
            comparison = CompareStableText(left.ChildTableName, right.ChildTableName);
            if (comparison != 0)
            {
                return comparison;
            }
            comparison = CompareStableText(left.ChildFieldName, right.ChildFieldName);
            if (comparison != 0)
            {
                return comparison;
            }
            comparison = CompareMetadataValues(left.RelationType, right.RelationType);
            if (comparison != 0)
            {
                return comparison;
            }
            comparison = CompareMetadataValues(left.BusinessMeaning, right.BusinessMeaning);
            if (comparison != 0)
            {
                return comparison;
            }
            return CompareMetadataValues(left.Remark, right.Remark);
        }

        /// <summary>XMZADD 20260902 按业务解释全部标量字段和证据顺序稳定比较。</summary>
        private static int CompareMetadataValues(MetadataValue left, MetadataValue right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }
            if (left == null)
            {
                return -1;
            }
            if (right == null)
            {
                return 1;
            }

            int comparison = CompareStableText(left.Value, right.Value);
            if (comparison != 0)
            {
                return comparison;
            }
            comparison = CompareStableText(left.Description, right.Description);
            if (comparison != 0)
            {
                return comparison;
            }
            comparison = left.Status.CompareTo(right.Status);
            if (comparison != 0)
            {
                return comparison;
            }
            comparison = left.ConfidenceScore.CompareTo(right.ConfidenceScore);
            if (comparison != 0)
            {
                return comparison;
            }
            comparison = CompareStableText(left.SourceType, right.SourceType);
            if (comparison != 0)
            {
                return comparison;
            }
            comparison = CompareStableText(left.SourceSummary, right.SourceSummary);
            if (comparison != 0)
            {
                return comparison;
            }
            comparison = CompareStableText(left.OriginalAutomaticValue, right.OriginalAutomaticValue);
            if (comparison != 0)
            {
                return comparison;
            }
            comparison = left.IsManualOverride.CompareTo(right.IsManualOverride);
            if (comparison != 0)
            {
                return comparison;
            }
            comparison = left.IsLocked.CompareTo(right.IsLocked);
            if (comparison != 0)
            {
                return comparison;
            }
            return CompareEvidenceLists(left.Evidence, right.Evidence);
        }

        /// <summary>XMZADD 20260902 按证据全部公开字段稳定比较并保留未知扩展数据的确定性。</summary>
        private static int CompareEvidenceItems(EvidenceItem left, EvidenceItem right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }
            if (left == null)
            {
                return -1;
            }
            if (right == null)
            {
                return 1;
            }

            int comparison = CompareStableText(left.SourceType, right.SourceType);
            if (comparison != 0)
            {
                return comparison;
            }
            comparison = CompareStableText(left.SourcePath, right.SourcePath);
            if (comparison != 0)
            {
                return comparison;
            }
            comparison = left.SourceLine.CompareTo(right.SourceLine);
            if (comparison != 0)
            {
                return comparison;
            }
            comparison = CompareStableText(left.RuleName, right.RuleName);
            if (comparison != 0)
            {
                return comparison;
            }
            comparison = CompareStableText(left.RawValue, right.RawValue);
            if (comparison != 0)
            {
                return comparison;
            }
            comparison = CompareStableText(left.OriginalText, right.OriginalText);
            if (comparison != 0)
            {
                return comparison;
            }
            comparison = CompareStableText(left.Explanation, right.Explanation);
            if (comparison != 0)
            {
                return comparison;
            }
            if (ReferenceEquals(left.ExtensionData, right.ExtensionData))
            {
                return 0;
            }
            if (left.ExtensionData == null)
            {
                return -1;
            }
            if (right.ExtensionData == null)
            {
                return 1;
            }

            // 扩展数据没有公开逐字段接口，仅在未知版本字段存在时按单条证据 JSON 兜底且不缓存关系内容。
            return CompareSerializedValues(left, right, typeof(EvidenceItem));
        }

        /// <summary>XMZADD 20260902 按现有证据顺序逐项比较并区分空集合和空引用。</summary>
        private static int CompareEvidenceLists(IList<EvidenceItem> left, IList<EvidenceItem> right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }
            if (left == null)
            {
                return -1;
            }
            if (right == null)
            {
                return 1;
            }

            int itemCount = Math.Min(left.Count, right.Count);
            for (int index = 0; index < itemCount; index++)
            {
                int comparison = CompareEvidenceItems(left[index], right[index]);
                if (comparison != 0)
                {
                    return comparison;
                }
            }
            return left.Count.CompareTo(right.Count);
        }

        /// <summary>XMZADD 20260901 按对象键和业务原因稳定排列排除记录。</summary>
        private static int CompareExcludedObjects(ExcludedObjectRecord left, ExcludedObjectRecord right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left == null)
            {
                return -1;
            }

            if (right == null)
            {
                return 1;
            }

            int comparison = CompareStableText(left.ObjectKey, right.ObjectKey);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = CompareStableText(left.ReasonCode, right.ReasonCode);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = CompareStableText(left.Reason, right.Reason);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = left.Category.CompareTo(right.Category);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = left.ApproximateRowCount.CompareTo(right.ApproximateRowCount);
            if (comparison != 0)
            {
                return comparison;
            }

            return left.ExcludedAtUtc.CompareTo(right.ExcludedAtUtc);
        }

        /// <summary>XMZADD 20260901 按缩写、模块、表范围和中文含义稳定排列缩写。</summary>
        private static int CompareAbbreviations(AbbreviationEntry left, AbbreviationEntry right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left == null)
            {
                return -1;
            }

            if (right == null)
            {
                return 1;
            }

            int comparison = CompareStableText(left.Abbreviation, right.Abbreviation);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = CompareStableText(left.ModuleScope, right.ModuleScope);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = CompareStableText(left.TableScope, right.TableScope);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = CompareStableText(left.ChineseMeaning, right.ChineseMeaning);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = left.ConfidenceScore.CompareTo(right.ConfidenceScore);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = left.Status.CompareTo(right.Status);
            if (comparison != 0)
            {
                return comparison;
            }

            return CompareAbbreviationEvidenceLists(left.Evidence, right.Evidence);
        }

        /// <summary>XMZADD 20260901 按来源类型、相对路径、行号和摘要稳定排列缩写证据。</summary>
        private static int CompareAbbreviationEvidence(AbbreviationEvidence left, AbbreviationEvidence right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left == null)
            {
                return -1;
            }

            if (right == null)
            {
                return 1;
            }

            int comparison = CompareStableText(left.SourceType, right.SourceType);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = CompareStableText(left.RelativePath, right.RelativePath);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = left.LineNumber.CompareTo(right.LineNumber);
            if (comparison != 0)
            {
                return comparison;
            }

            return CompareStableText(left.Summary, right.Summary);
        }

        /// <summary>XMZADD 20260901 对已排序证据逐项比较以消除重复缩写的次序歧义。</summary>
        private static int CompareAbbreviationEvidenceLists(
            IList<AbbreviationEvidence> left,
            IList<AbbreviationEvidence> right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left == null)
            {
                return -1;
            }

            if (right == null)
            {
                return 1;
            }

            int itemCount = Math.Min(left.Count, right.Count);
            for (int index = 0; index < itemCount; index++)
            {
                int comparison = CompareAbbreviationEvidence(left[index], right[index]);
                if (comparison != 0)
                {
                    return comparison;
                }
            }

            return left.Count.CompareTo(right.Count);
        }

        /// <summary>XMZADD 20260901 先按不区分大小写的业务键比较，再用原始大小写消除排序歧义。</summary>
        private static int CompareStableText(string left, string right)
        {
            int comparison = string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
            if (comparison != 0)
            {
                return comparison;
            }

            return string.Compare(left, right, StringComparison.Ordinal);
        }

        /// <summary>XMZADD 20260902 序列化双方不透明扩展数据并按 UTF-8 字节稳定比较。</summary>
        private static int CompareSerializedValues(object left, object right, Type valueType)
        {
            byte[] leftContent = SerializeJson(left, valueType);
            byte[] rightContent = SerializeJson(right, valueType);
            int contentLength = Math.Min(leftContent.Length, rightContent.Length);
            for (int index = 0; index < contentLength; index++)
            {
                int comparison = leftContent[index].CompareTo(rightContent[index]);
                if (comparison != 0)
                {
                    return comparison;
                }
            }
            return leftContent.Length.CompareTo(rightContent.Length);
        }

        /// <summary>XMZADD 20260902 使用固定数据契约生成单条扩展证据的 UTF-8 JSON 字节。</summary>
        private static byte[] SerializeJson(object value, Type valueType)
        {
            DataContractJsonSerializer serializer = CreateJsonSerializer(valueType);
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, value);
                return stream.ToArray();
            }
        }

        /// <summary>XMZADD 20260902 创建不以固定项目数量截断大型 EOS 元数据图的 JSON 数据契约序列化器。</summary>
        private static DataContractJsonSerializer CreateJsonSerializer(Type valueType)
        {
            return new DataContractJsonSerializer(
                valueType,
                new DataContractJsonSerializerSettings
                {
                    MaxItemsInObjectGraph = int.MaxValue
                });
        }

        /// <summary>XMZADD 20260902 限制规范快照的压缩输出总量，避免生成超出传输边界的内容。</summary>
        private sealed class SizeLimitedMemoryStream : MemoryStream
        {
            private readonly long _maximumLength;

            /// <summary>XMZADD 20260902 创建具有固定最大压缩长度的内存流。</summary>
            public SizeLimitedMemoryStream(long maximumLength)
            {
                _maximumLength = maximumLength;
            }

            /// <summary>XMZADD 20260902 在分配内存前拒绝会越过快照压缩上限的块写入。</summary>
            public override void Write(byte[] buffer, int offset, int count)
            {
                if (buffer == null)
                {
                    throw new ArgumentNullException("buffer");
                }
                if (offset < 0)
                {
                    throw new ArgumentOutOfRangeException("offset");
                }
                if (count < 0)
                {
                    throw new ArgumentOutOfRangeException("count");
                }
                if (buffer.Length - offset < count)
                {
                    throw new ArgumentException("写入缓冲区范围无效。");
                }

                EnsureWriteWithinLimit(count);
                base.Write(buffer, offset, count);
            }

            /// <summary>XMZADD 20260902 在分配内存前拒绝会越过快照压缩上限的单字节写入。</summary>
            public override void WriteByte(byte value)
            {
                EnsureWriteWithinLimit(1);
                base.WriteByte(value);
            }

            /// <summary>XMZADD 20260902 阻止通过预设流长度绕过压缩写入上限。</summary>
            public override void SetLength(long value)
            {
                if (value > _maximumLength)
                {
                    throw new SnapshotCompressedSizeExceededException();
                }

                base.SetLength(value);
            }

            /// <summary>XMZADD 20260902 统一判断下一次写入是否仍在压缩内容传输边界内。</summary>
            private void EnsureWriteWithinLimit(int count)
            {
                if (Position > _maximumLength - count)
                {
                    throw new SnapshotCompressedSizeExceededException();
                }
            }
        }

        /// <summary>XMZADD 20260902 包装解压流并累计实际读取量，阻断高压缩比内容耗尽内存。</summary>
        private sealed class SizeLimitedReadStream : Stream
        {
            private readonly Stream _source;
            private readonly long _maximumBytes;
            private long _totalBytesRead;

            /// <summary>XMZADD 20260902 创建按实际解压读取量执行上限保护的只读流。</summary>
            public SizeLimitedReadStream(Stream source, long maximumBytes)
            {
                if (source == null)
                {
                    throw new ArgumentNullException("source");
                }
                if (maximumBytes < 0)
                {
                    throw new ArgumentOutOfRangeException("maximumBytes");
                }

                _source = source;
                _maximumBytes = maximumBytes;
            }

            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { throw new NotSupportedException(); } }

            public override long Position
            {
                get { return _totalBytesRead; }
                set { throw new NotSupportedException(); }
            }

            /// <summary>XMZADD 20260902 只允许从包装的解压流读取，不需要向底层刷新数据。</summary>
            public override void Flush()
            {
            }

            /// <summary>XMZADD 20260902 在累计读取越过业务安全边界的首个字节时立即拒绝。</summary>
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (buffer == null)
                {
                    throw new ArgumentNullException("buffer");
                }
                if (offset < 0)
                {
                    throw new ArgumentOutOfRangeException("offset");
                }
                if (count < 0)
                {
                    throw new ArgumentOutOfRangeException("count");
                }
                if (buffer.Length - offset < count)
                {
                    throw new ArgumentException("读取缓冲区范围无效。");
                }
                if (count == 0)
                {
                    return 0;
                }

                long remainingBytes = _maximumBytes - _totalBytesRead;
                int requestedCount = count;
                if (remainingBytes < count)
                {
                    // 额外读取一个字节用于区分刚好到达上限和确实越界。
                    requestedCount = (int)remainingBytes + 1;
                }

                int readLength = _source.Read(buffer, offset, requestedCount);
                if (readLength > remainingBytes)
                {
                    throw new InvalidDataException(ContentSizeExceededMessage);
                }

                _totalBytesRead += readLength;
                return readLength;
            }

            /// <summary>XMZADD 20260902 对单字节读取应用与块读取相同的累计解压边界。</summary>
            public override int ReadByte()
            {
                int value = _source.ReadByte();
                if (value < 0)
                {
                    return value;
                }
                if (_totalBytesRead >= _maximumBytes)
                {
                    throw new InvalidDataException(ContentSizeExceededMessage);
                }

                _totalBytesRead++;
                return value;
            }

            /// <summary>XMZADD 20260902 明确禁止在顺序解压流上改变读取位置。</summary>
            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotSupportedException();
            }

            /// <summary>XMZADD 20260902 明确禁止改变只读解压流长度。</summary>
            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }

            /// <summary>XMZADD 20260902 明确禁止向解压读取包装流写入内容。</summary>
            public override void Write(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }
        }

    }
}
