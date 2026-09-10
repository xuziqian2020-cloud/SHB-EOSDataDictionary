using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHB.EosDataDictionary.Models;
using SHB.EosDataDictionary.Services;

namespace SHB.EosDataDictionary.Tests
{
    [TestClass]
    public sealed class SnapshotCodecTests
    {
        /// <summary>XMZADD 20260901 验证集合插入顺序不会影响规范快照的完整性校验值。</summary>
        [TestMethod]
        public void Encode_SameContentInDifferentOrder_ProducesSameSha256()
        {
            var codec = new SnapshotCodec();
            SnapshotData first = CreateSnapshot(false);
            SnapshotData second = CreateSnapshot(true);

            string firstHash = codec.ComputeSha256(codec.Encode(first));
            string secondHash = codec.ComputeSha256(codec.Encode(second));

            Assert.AreEqual(firstHash, secondHash);
            Assert.AreEqual("T_ORDER", second.Tables[0].ObjectName);
            Assert.AreEqual("FStatus", second.Tables[0].Fields[0].FieldName);
            Assert.AreEqual("T_ORDER_ITEM", second.Tables[1].Relations[0].ChildTableName);
            Assert.AreEqual("B", second.Tables[1].Fields[1].EnumItems[0].Value);
            Assert.AreEqual("dbo.V_REPORT", second.ExcludedObjects[0].ObjectKey);
            Assert.AreEqual("ORD", second.Abbreviations[0].Abbreviation);
            Assert.AreEqual("KnowledgeBase", second.Abbreviations[1].Evidence[0].SourceType);
        }

        /// <summary>XMZADD 20260902 验证关系排序不再通过缓存每条关系的完整 JSON 占用额外线性内存。</summary>
        [TestMethod]
        public void SnapshotCodec_RelationJsonSortKeyCacheType_IsRemoved()
        {
            Type cacheType = typeof(SnapshotCodec).GetNestedType(
                "RelationSortItem",
                BindingFlags.NonPublic);

            Assert.IsNull(cacheType);
        }

        /// <summary>XMZADD 20260902 验证关系全部字符串字段均参与确定性排序。</summary>
        [TestMethod]
        public void Encode_RelationStringFieldsInDifferentInputOrder_ProducesSameSha256()
        {
            Action<RelationMetadata, string>[] setters =
            {
                delegate(RelationMetadata relation, string value) { relation.ScopeKey = value; },
                delegate(RelationMetadata relation, string value) { relation.ForeignKeyName = value; },
                delegate(RelationMetadata relation, string value) { relation.ParentSchemaName = value; },
                delegate(RelationMetadata relation, string value) { relation.ParentTableName = value; },
                delegate(RelationMetadata relation, string value) { relation.ParentFieldName = value; },
                delegate(RelationMetadata relation, string value) { relation.ChildSchemaName = value; },
                delegate(RelationMetadata relation, string value) { relation.ChildTableName = value; },
                delegate(RelationMetadata relation, string value) { relation.ChildFieldName = value; }
            };

            for (int index = 0; index < setters.Length; index++)
            {
                RelationMetadata left = CreateComparableRelation();
                RelationMetadata right = CreateComparableRelation();
                setters[index](left, "A");
                setters[index](right, "B");
                AssertRelationPairOrderIndependent(left, right);
            }
        }

        /// <summary>XMZADD 20260902 验证关系三类业务解释及元数据全部字段均参与确定性排序。</summary>
        [TestMethod]
        public void Encode_RelationMetadataValueFieldsInDifferentInputOrder_ProducesSameSha256()
        {
            Action<RelationMetadata, MetadataValue>[] placements =
            {
                delegate(RelationMetadata relation, MetadataValue value) { relation.RelationType = value; },
                delegate(RelationMetadata relation, MetadataValue value) { relation.BusinessMeaning = value; },
                delegate(RelationMetadata relation, MetadataValue value) { relation.Remark = value; }
            };
            for (int index = 0; index < placements.Length; index++)
            {
                RelationMetadata left = CreateComparableRelation();
                RelationMetadata right = CreateComparableRelation();
                MetadataValue leftValue = CreateComparableMetadataValue();
                MetadataValue rightValue = CreateComparableMetadataValue();
                leftValue.Value = "A";
                rightValue.Value = "B";
                placements[index](left, leftValue);
                placements[index](right, rightValue);
                AssertRelationPairOrderIndependent(left, right);
            }

            Action<MetadataValue, string>[] textSetters =
            {
                delegate(MetadataValue value, string text) { value.Value = text; },
                delegate(MetadataValue value, string text) { value.Description = text; },
                delegate(MetadataValue value, string text) { value.SourceType = text; },
                delegate(MetadataValue value, string text) { value.SourceSummary = text; },
                delegate(MetadataValue value, string text) { value.OriginalAutomaticValue = text; }
            };
            for (int index = 0; index < textSetters.Length; index++)
            {
                MetadataValue left = CreateComparableMetadataValue();
                MetadataValue right = CreateComparableMetadataValue();
                textSetters[index](left, "A");
                textSetters[index](right, "B");
                AssertRelationTypeOrderIndependent(left, right);
            }

            MetadataValue leftStatus = CreateComparableMetadataValue();
            MetadataValue rightStatus = CreateComparableMetadataValue();
            leftStatus.Status = ConfidenceStatus.Confirmed;
            rightStatus.Status = ConfidenceStatus.DatabaseEvidence;
            AssertRelationTypeOrderIndependent(leftStatus, rightStatus);

            MetadataValue leftScore = CreateComparableMetadataValue();
            MetadataValue rightScore = CreateComparableMetadataValue();
            leftScore.ConfidenceScore = 1;
            rightScore.ConfidenceScore = 2;
            AssertRelationTypeOrderIndependent(leftScore, rightScore);

            MetadataValue leftManual = CreateComparableMetadataValue();
            MetadataValue rightManual = CreateComparableMetadataValue();
            leftManual.IsManualOverride = false;
            rightManual.IsManualOverride = true;
            AssertRelationTypeOrderIndependent(leftManual, rightManual);

            MetadataValue leftLocked = CreateComparableMetadataValue();
            MetadataValue rightLocked = CreateComparableMetadataValue();
            leftLocked.IsLocked = false;
            rightLocked.IsLocked = true;
            AssertRelationTypeOrderIndependent(leftLocked, rightLocked);
        }

        /// <summary>XMZADD 20260902 验证关系业务解释中的证据全部字段及顺序均参与确定性排序。</summary>
        [TestMethod]
        public void Encode_RelationEvidenceFieldsInDifferentInputOrder_ProducesSameSha256()
        {
            Action<EvidenceItem, string>[] textSetters =
            {
                delegate(EvidenceItem evidence, string value) { evidence.SourceType = value; },
                delegate(EvidenceItem evidence, string value) { evidence.SourcePath = value; },
                delegate(EvidenceItem evidence, string value) { evidence.RuleName = value; },
                delegate(EvidenceItem evidence, string value) { evidence.RawValue = value; },
                delegate(EvidenceItem evidence, string value) { evidence.OriginalText = value; },
                delegate(EvidenceItem evidence, string value) { evidence.Explanation = value; }
            };
            for (int index = 0; index < textSetters.Length; index++)
            {
                EvidenceItem left = CreateComparableEvidence();
                EvidenceItem right = CreateComparableEvidence();
                textSetters[index](left, "A");
                textSetters[index](right, "B");
                AssertRelationEvidenceOrderIndependent(left, right);
            }

            EvidenceItem leftLine = CreateComparableEvidence();
            EvidenceItem rightLine = CreateComparableEvidence();
            leftLine.SourceLine = 1;
            rightLine.SourceLine = 2;
            AssertRelationEvidenceOrderIndependent(leftLine, rightLine);

            MetadataValue leftSequence = CreateComparableMetadataValue();
            MetadataValue rightSequence = CreateComparableMetadataValue();
            leftSequence.Evidence = new List<EvidenceItem>
            {
                new EvidenceItem { SourceType = "A" },
                new EvidenceItem { SourceType = "B" }
            };
            rightSequence.Evidence = new List<EvidenceItem>
            {
                new EvidenceItem { SourceType = "B" },
                new EvidenceItem { SourceType = "A" }
            };
            AssertRelationTypeOrderIndependent(leftSequence, rightSequence);
        }

        /// <summary>XMZADD 20260901 验证规范 JSON 压缩快照可无损还原关键结构数据。</summary>
        [TestMethod]
        public void EncodeAndDecode_ValidSnapshot_RoundTripsJsonGZip()
        {
            var codec = new SnapshotCodec();
            SnapshotData source = CreateSnapshot(false);
            byte[] content = codec.Encode(source);

            SnapshotData restored = codec.DecodeAndValidate(content, codec.ComputeSha256(content));

            Assert.AreEqual(1, restored.FormatVersion);
            Assert.AreEqual(12L, restored.Revision);
            Assert.AreEqual(2, restored.Tables.Count);
            Assert.AreEqual("FId", restored.Tables[0].Fields[0].FieldName);
            Assert.AreEqual("T_CUSTOMER", restored.Tables[0].Relations[0].ParentTableName);
            Assert.AreEqual("dbo", restored.Tables[0].Relations[0].ParentSchemaName);
            Assert.AreEqual("dbo", restored.Tables[0].Relations[0].ChildSchemaName);
        }

        /// <summary>XMZADD 20260901 验证远程内容哈希不符时不会返回未经确认的结构数据。</summary>
        [TestMethod]
        public void DecodeAndValidate_HashMismatch_ThrowsInvalidDataException()
        {
            var codec = new SnapshotCodec();
            byte[] content = codec.Encode(CreateSnapshot(false));

            Assert.ThrowsException<InvalidDataException>(
                delegate { codec.DecodeAndValidate(content, new string('0', 64)); });
        }

        /// <summary>XMZADD 20260901 验证损坏压缩内容不会被解释为可用规范快照。</summary>
        [TestMethod]
        public void DecodeAndValidate_CorruptedGZip_ThrowsInvalidDataException()
        {
            var codec = new SnapshotCodec();
            byte[] content = { 1, 2, 3, 4, 5 };

            InvalidDataException exception = Assert.ThrowsException<InvalidDataException>(
                delegate { codec.DecodeAndValidate(content, codec.ComputeSha256(content)); });

            Assert.IsNotNull(exception.InnerException);
        }

        /// <summary>XMZADD 20260901 验证对象稳定键按不区分大小写的规则保持唯一。</summary>
        [TestMethod]
        public void DecodeAndValidate_DuplicateObjectKey_ThrowsInvalidDataException()
        {
            var codec = new SnapshotCodec();
            SnapshotData snapshot = CreateSnapshot(false);
            snapshot.Tables.Add(new TableMetadata
            {
                SchemaName = "DBO",
                ObjectName = "t_customer",
                ObjectType = "TABLE"
            });
            byte[] content = codec.Encode(snapshot);

            Assert.ThrowsException<InvalidDataException>(
                delegate { codec.DecodeAndValidate(content, codec.ComputeSha256(content)); });
        }

        /// <summary>XMZADD 20260901 验证同一对象内的字段稳定键按不区分大小写的规则保持唯一。</summary>
        [TestMethod]
        public void DecodeAndValidate_DuplicateFieldKey_ThrowsInvalidDataException()
        {
            var codec = new SnapshotCodec();
            SnapshotData snapshot = CreateSnapshot(false);
            snapshot.Tables[0].Fields.Add(new FieldMetadata { FieldName = "fid" });
            byte[] content = codec.Encode(snapshot);

            Assert.ThrowsException<InvalidDataException>(
                delegate { codec.DecodeAndValidate(content, codec.ComputeSha256(content)); });
        }

        /// <summary>XMZADD 20260901 验证视图不会进入只允许业务表的远程规范快照。</summary>
        [TestMethod]
        public void DecodeAndValidate_ViewObject_ThrowsInvalidDataException()
        {
            var codec = new SnapshotCodec();
            SnapshotData snapshot = CreateSnapshot(false);
            snapshot.Tables[0].ObjectType = "VIEW";
            byte[] content = codec.Encode(snapshot);

            Assert.ThrowsException<InvalidDataException>(
                delegate { codec.DecodeAndValidate(content, codec.ComputeSha256(content)); });
        }

        /// <summary>XMZADD 20260901 验证不兼容格式版本不会进入本地规范快照缓存。</summary>
        [TestMethod]
        public void DecodeAndValidate_UnsupportedFormatVersion_ThrowsInvalidDataException()
        {
            var codec = new SnapshotCodec();
            SnapshotData snapshot = CreateSnapshot(false);
            snapshot.FormatVersion = SnapshotValidator.SupportedFormatVersion + 1;
            byte[] content = codec.Encode(snapshot);

            Assert.ThrowsException<InvalidDataException>(
                delegate { codec.DecodeAndValidate(content, codec.ComputeSha256(content)); });
        }

        /// <summary>XMZADD 20260901 验证负修订号不会破坏本地修订游标的单调语义。</summary>
        [TestMethod]
        public void DecodeAndValidate_NegativeRevision_ThrowsInvalidDataException()
        {
            var codec = new SnapshotCodec();
            SnapshotData snapshot = CreateSnapshot(false);
            snapshot.Revision = -1;
            byte[] content = codec.Encode(snapshot);

            Assert.ThrowsException<InvalidDataException>(
                delegate { codec.DecodeAndValidate(content, codec.ComputeSha256(content)); });
        }

        /// <summary>XMZADD 20260901 验证缺失模式名或对象名时无法形成可跨账套复用的稳定键。</summary>
        [TestMethod]
        public void DecodeAndValidate_EmptyObjectKey_ThrowsInvalidDataException()
        {
            var codec = new SnapshotCodec();
            SnapshotData snapshot = CreateSnapshot(false);
            snapshot.Tables[0].SchemaName = " ";
            byte[] content = codec.Encode(snapshot);

            Assert.ThrowsException<InvalidDataException>(
                delegate { codec.DecodeAndValidate(content, codec.ComputeSha256(content)); });
        }

        /// <summary>XMZADD 20260901 验证空字段名无法作为共享字典的字段稳定键。</summary>
        [TestMethod]
        public void DecodeAndValidate_EmptyFieldKey_ThrowsInvalidDataException()
        {
            var codec = new SnapshotCodec();
            SnapshotData snapshot = CreateSnapshot(false);
            snapshot.Tables[0].Fields[0].FieldName = string.Empty;
            byte[] content = codec.Encode(snapshot);

            Assert.ThrowsException<InvalidDataException>(
                delegate { codec.DecodeAndValidate(content, codec.ComputeSha256(content)); });
        }

        /// <summary>XMZADD 20260901 验证规范快照必须显式提供缩写集合。</summary>
        [TestMethod]
        public void DecodeAndValidate_NullAbbreviations_ThrowsInvalidDataException()
        {
            var codec = new SnapshotCodec();
            SnapshotData snapshot = CreateSnapshot(false);
            snapshot.Abbreviations = null;
            byte[] content = codec.Encode(snapshot);

            Assert.ThrowsException<InvalidDataException>(
                delegate { codec.DecodeAndValidate(content, codec.ComputeSha256(content)); });
        }

        /// <summary>XMZADD 20260901 验证规范字段必须显式提供枚举项集合。</summary>
        [TestMethod]
        public void DecodeAndValidate_NullEnumItems_ThrowsInvalidDataException()
        {
            var codec = new SnapshotCodec();
            SnapshotData snapshot = CreateSnapshot(false);
            snapshot.Tables[0].Fields[0].EnumItems = null;
            byte[] content = codec.Encode(snapshot);

            Assert.ThrowsException<InvalidDataException>(
                delegate { codec.DecodeAndValidate(content, codec.ComputeSha256(content)); });
        }

        /// <summary>XMZADD 20260901 验证规范缩写必须显式提供证据集合。</summary>
        [TestMethod]
        public void DecodeAndValidate_NullAbbreviationEvidence_ThrowsInvalidDataException()
        {
            var codec = new SnapshotCodec();
            SnapshotData snapshot = CreateSnapshot(false);
            snapshot.Abbreviations[0].Evidence = null;
            byte[] content = codec.Encode(snapshot);

            Assert.ThrowsException<InvalidDataException>(
                delegate { codec.DecodeAndValidate(content, codec.ComputeSha256(content)); });
        }

        /// <summary>XMZADD 20260901 验证关系集合不能用空记录占位。</summary>
        [TestMethod]
        public void DecodeAndValidate_NullRelationItem_ThrowsInvalidDataException()
        {
            var codec = new SnapshotCodec();
            SnapshotData snapshot = CreateSnapshot(false);
            snapshot.Tables[0].Relations.Add(null);
            byte[] content = codec.Encode(snapshot);

            Assert.ThrowsException<InvalidDataException>(
                delegate { codec.DecodeAndValidate(content, codec.ComputeSha256(content)); });
        }

        /// <summary>XMZADD 20260901 验证缩写条目必须具备可合并的非空稳定键。</summary>
        [TestMethod]
        public void DecodeAndValidate_EmptyAbbreviationKey_ThrowsInvalidDataException()
        {
            var codec = new SnapshotCodec();
            SnapshotData snapshot = CreateSnapshot(false);
            snapshot.Abbreviations[0].Abbreviation = " ";
            byte[] content = codec.Encode(snapshot);

            Assert.ThrowsException<InvalidDataException>(
                delegate { codec.DecodeAndValidate(content, codec.ComputeSha256(content)); });
        }

        /// <summary>XMZADD 20260901 验证字段枚举集合不能包含空记录。</summary>
        [TestMethod]
        public void DecodeAndValidate_NullEnumItem_ThrowsInvalidDataException()
        {
            var codec = new SnapshotCodec();
            SnapshotData snapshot = CreateSnapshot(false);
            snapshot.Tables[0].Fields[0].EnumItems.Add(null);
            byte[] content = codec.Encode(snapshot);

            Assert.ThrowsException<InvalidDataException>(
                delegate { codec.DecodeAndValidate(content, codec.ComputeSha256(content)); });
        }

        /// <summary>XMZADD 20260902 验证超过旧六十四 MiB 未压缩边界的合法快照仍可流式编解码。</summary>
        [TestMethod]
        public void EncodeAndDecode_UncompressedJsonBeyondOldLimit_RoundTripsSuccessfully()
        {
            var codec = new SnapshotCodec();
            SnapshotData snapshot = CreateSnapshot(false);
            string largeText = new string('A', 64 * 1024 * 1024);
            snapshot.Tables[0].Remark = new MetadataValue { Value = largeText };

            byte[] content = codec.Encode(snapshot);
            SnapshotData restored = codec.DecodeAndValidate(content, codec.ComputeSha256(content));

            Assert.AreEqual(largeText.Length, restored.Tables[0].Remark.Value.Length);
        }

        /// <summary>XMZADD 20260902 验证压缩、解压和对象图正式资源边界使用计划规定的新值。</summary>
        [TestMethod]
        public void CodecResourceBoundaries_UseExpandedLimits()
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            FieldInfo compressedLimit = typeof(SnapshotCodec).GetField("MaximumCompressedBytes", flags);
            FieldInfo decompressedLimit = typeof(SnapshotCodec).GetField("MaximumDecompressedBytes", flags);
            MethodInfo serializerFactory = typeof(SnapshotCodec).GetMethod("CreateJsonSerializer", flags);

            Assert.IsNotNull(compressedLimit);
            Assert.IsNotNull(decompressedLimit);
            Assert.IsNotNull(serializerFactory);
            Assert.AreEqual(100 * 1024 * 1024, compressedLimit.GetRawConstantValue());
            Assert.AreEqual(1024L * 1024L * 1024L, decompressedLimit.GetRawConstantValue());
            var serializer = serializerFactory.Invoke(null, new object[] { typeof(SnapshotData) })
                as System.Runtime.Serialization.Json.DataContractJsonSerializer;
            Assert.IsNotNull(serializer);
            Assert.AreEqual(int.MaxValue, serializer.MaxItemsInObjectGraph);
        }

        /// <summary>XMZADD 20260902 验证内部压缩边界允许恰好达到实际长度并在少一个字节时返回固定异常。</summary>
        [TestMethod]
        public void Encode_AdjustableCompressedLimit_EnforcesExactBoundary()
        {
            var codec = new SnapshotCodec();
            SnapshotData snapshot = CreateSnapshot(false);
            byte[] expected = codec.Encode(snapshot);
            MethodInfo encodeMethod = typeof(SnapshotCodec).GetMethod(
                "Encode",
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                new[] { typeof(SnapshotData), typeof(int) },
                null);

            Assert.IsNotNull(encodeMethod);
            var exactContent = encodeMethod.Invoke(codec, new object[] { snapshot, expected.Length }) as byte[];
            CollectionAssert.AreEqual(expected, exactContent);

            TargetInvocationException invocationException = Assert.ThrowsException<TargetInvocationException>(
                delegate { encodeMethod.Invoke(codec, new object[] { snapshot, expected.Length - 1 }); });
            var exception = invocationException.InnerException as SnapshotCompressedSizeExceededException;
            Assert.IsNotNull(exception);
            Assert.IsInstanceOfType(exception, typeof(IOException));
            Assert.AreEqual("SnapshotCompressedSizeExceededException", exception.GetType().Name);
            Assert.AreEqual("快照压缩内容超过安全大小限制。", exception.Message);
        }

        /// <summary>XMZADD 20260902 以内部小边界可靠验证流式解压累计读取超限时返回固定安全错误。</summary>
        [TestMethod]
        public void DecodeWithoutValidation_InjectedSmallLimit_ThrowsExactSizeError()
        {
            var codec = new SnapshotCodec();
            SnapshotData snapshot = CreateSnapshot(false);
            snapshot.Tables[0].Remark = new MetadataValue { Value = new string('C', 8 * 1024) };
            byte[] content = codec.Encode(snapshot);

            InvalidDataException exception = Assert.ThrowsException<InvalidDataException>(
                delegate { codec.DecodeWithoutValidation(content, 1024L); });
            Assert.AreEqual("快照解压内容超过安全大小限制。", exception.Message);
        }

        /// <summary>XMZADD 20260902 验证一万零一个合法表对象不再因旧固定数量阈值被拒绝。</summary>
        [TestMethod]
        public void EncodeAndDecode_TenThousandAndOneTables_RoundTripsSuccessfully()
        {
            var codec = new SnapshotCodec();
            var snapshot = new SnapshotData
            {
                FormatVersion = 1,
                Revision = 1,
                RefreshedAt = DateTime.UtcNow,
                Tables = new List<TableMetadata>(),
                ExcludedObjects = new List<ExcludedObjectRecord>(),
                Abbreviations = new List<AbbreviationEntry>()
            };
            for (int index = 0; index < 10001; index++)
            {
                snapshot.Tables.Add(new TableMetadata
                {
                    SchemaName = "dbo",
                    ObjectName = "T_LIMIT_" + index,
                    ObjectType = "TABLE",
                    Fields = new List<FieldMetadata>(),
                    Relations = new List<RelationMetadata>()
                });
            }
            byte[] content = codec.Encode(snapshot);

            SnapshotData restored = codec.DecodeAndValidate(content, codec.ComputeSha256(content));

            Assert.AreEqual(10001, restored.Tables.Count);
        }

        /// <summary>XMZADD 20260902 验证表、字段、关系、枚举、排除对象、缩写和证据的旧固定阈值均已移除。</summary>
        [TestMethod]
        public void SnapshotValidator_LegacyFixedCountLimits_AreRemoved()
        {
            string[] legacyLimitNames =
            {
                "MaximumTables",
                "MaximumFields",
                "MaximumRelations",
                "MaximumEnumItems",
                "MaximumExcludedObjects",
                "MaximumAbbreviations",
                "MaximumAbbreviationEvidenceItems"
            };
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            for (int index = 0; index < legacyLimitNames.Length; index++)
            {
                Assert.IsNull(
                    typeof(SnapshotValidator).GetField(legacyLimitNames[index], flags),
                    legacyLimitNames[index] + " 旧固定数量阈值仍然存在。");
            }
        }

        /// <summary>XMZADD 20260902 验证五十万零一个唯一字段可通过真实快照校验。</summary>
        [TestMethod]
        public void Validate_FieldCountBeyondLegacyLimit_DoesNotReject()
        {
            var validator = new SnapshotValidator();
            SnapshotData snapshot = CreateValidationSnapshot();
            var emptyEnumItems = new List<EnumItemMetadata>();
            var fields = new GeneratedReadOnlyList<FieldMetadata>(
                500001,
                delegate(int index)
                {
                    // 唯一稳定键按索引即时生成，避免预先分配五十万个字段对象。
                    return new FieldMetadata
                    {
                        FieldName = "F_LAZY_" + index,
                        EnumItems = emptyEnumItems
                    };
                });
            snapshot.Tables.Add(new TableMetadata
            {
                SchemaName = "dbo",
                ObjectName = "T_LAZY_FIELDS",
                ObjectType = "TABLE",
                Fields = fields,
                Relations = new List<RelationMetadata>()
            });

            validator.Validate(snapshot);

            Assert.AreEqual(500001, fields.Count);
        }

        /// <summary>XMZADD 20260902 验证五十万零一个非空关系可通过真实快照校验。</summary>
        [TestMethod]
        public void Validate_RelationCountBeyondLegacyLimit_DoesNotReject()
        {
            var validator = new SnapshotValidator();
            SnapshotData snapshot = CreateValidationSnapshot();
            var reusableRelation = new RelationMetadata();
            var relations = new GeneratedReadOnlyList<RelationMetadata>(
                500001,
                delegate
                {
                    // 关系规则只要求记录非空，复用安全对象以避免无业务价值的批量分配。
                    return reusableRelation;
                });
            snapshot.Tables.Add(new TableMetadata
            {
                SchemaName = "dbo",
                ObjectName = "T_LAZY_RELATIONS",
                ObjectType = "TABLE",
                Fields = new List<FieldMetadata>(),
                Relations = relations
            });

            validator.Validate(snapshot);

            Assert.AreEqual(500001, relations.Count);
        }

        /// <summary>XMZADD 20260902 验证一百万零一个非空枚举项可通过真实快照校验。</summary>
        [TestMethod]
        public void Validate_EnumItemCountBeyondLegacyLimit_DoesNotReject()
        {
            var validator = new SnapshotValidator();
            SnapshotData snapshot = CreateValidationSnapshot();
            var reusableEnumItem = new EnumItemMetadata { Value = "VALID" };
            var enumItems = new GeneratedReadOnlyList<EnumItemMetadata>(
                1000001,
                delegate
                {
                    // 枚举规则只要求记录非空，复用安全对象以聚焦数量阈值是否已解除。
                    return reusableEnumItem;
                });
            snapshot.Tables.Add(new TableMetadata
            {
                SchemaName = "dbo",
                ObjectName = "T_LAZY_ENUMS",
                ObjectType = "TABLE",
                Fields = new List<FieldMetadata>
                {
                    new FieldMetadata { FieldName = "F_ENUM", EnumItems = enumItems }
                },
                Relations = new List<RelationMetadata>()
            });

            validator.Validate(snapshot);

            Assert.AreEqual(1000001, enumItems.Count);
        }

        /// <summary>XMZADD 20260902 验证十万零一个唯一排除对象可通过真实快照校验。</summary>
        [TestMethod]
        public void Validate_ExcludedObjectCountBeyondLegacyLimit_DoesNotReject()
        {
            var validator = new SnapshotValidator();
            SnapshotData snapshot = CreateValidationSnapshot();
            var excludedObjects = new GeneratedReadOnlyList<ExcludedObjectRecord>(
                100001,
                delegate(int index)
                {
                    // 排除对象稳定键按索引即时生成，确保唯一性规则仍被真实执行。
                    return new ExcludedObjectRecord { ObjectKey = "dbo.T_EXCLUDED_" + index };
                });
            snapshot.ExcludedObjects = excludedObjects;

            validator.Validate(snapshot);

            Assert.AreEqual(100001, excludedObjects.Count);
        }

        /// <summary>XMZADD 20260902 验证十万零一个唯一缩写可通过真实快照校验。</summary>
        [TestMethod]
        public void Validate_AbbreviationCountBeyondLegacyLimit_DoesNotReject()
        {
            var validator = new SnapshotValidator();
            SnapshotData snapshot = CreateValidationSnapshot();
            var emptyEvidence = new List<AbbreviationEvidence>();
            var abbreviations = new GeneratedReadOnlyList<AbbreviationEntry>(
                100001,
                delegate(int index)
                {
                    // 缩写键按索引即时生成，避免为数量行为测试长期保留完整对象集合。
                    return new AbbreviationEntry
                    {
                        Abbreviation = "ABBR_" + index,
                        Evidence = emptyEvidence
                    };
                });
            snapshot.Abbreviations = abbreviations;

            validator.Validate(snapshot);

            Assert.AreEqual(100001, abbreviations.Count);
        }

        /// <summary>XMZADD 20260902 验证五十万零一个缩写证据不再触发旧累计数量拒绝。</summary>
        [TestMethod]
        public void Validate_AbbreviationEvidenceCountBeyondLegacyLimit_DoesNotReject()
        {
            var validator = new SnapshotValidator();
            SnapshotData snapshot = CreateValidationSnapshot();
            var reusableEvidence = new AbbreviationEvidence { SourceType = "Code" };
            var evidenceItems = new GeneratedReadOnlyList<AbbreviationEvidence>(
                500001,
                delegate
                {
                    // 当前结构规则只要求证据集合存在，复用安全记录避免批量对象分配。
                    return reusableEvidence;
                });
            snapshot.Abbreviations.Add(new AbbreviationEntry
            {
                Abbreviation = "EVIDENCE",
                Evidence = evidenceItems
            });

            validator.Validate(snapshot);

            Assert.AreEqual(500001, evidenceItems.Count);
        }

        /// <summary>XMZADD 20260902 创建仅包含格式和必需空集合的验证基线快照。</summary>
        private static SnapshotData CreateValidationSnapshot()
        {
            return new SnapshotData
            {
                FormatVersion = SnapshotValidator.SupportedFormatVersion,
                Revision = 1,
                RefreshedAt = new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc),
                Tables = new List<TableMetadata>(),
                ExcludedObjects = new List<ExcludedObjectRecord>(),
                Abbreviations = new List<AbbreviationEntry>()
            };
        }

        /// <summary>XMZADD 20260902 验证两条仅指定字段不同的关系在反向输入时仍生成相同压缩哈希。</summary>
        private static void AssertRelationPairOrderIndependent(RelationMetadata left, RelationMetadata right)
        {
            var codec = new SnapshotCodec();
            SnapshotData forward = CreateRelationSnapshot(left, right);
            SnapshotData reverse = CreateRelationSnapshot(right, left);

            string forwardHash = codec.ComputeSha256(codec.Encode(forward));
            string reverseHash = codec.ComputeSha256(codec.Encode(reverse));

            Assert.AreEqual(forwardHash, reverseHash);
        }

        /// <summary>XMZADD 20260902 将两类元数据值放入关系类型后验证嵌套字段排序。</summary>
        private static void AssertRelationTypeOrderIndependent(MetadataValue left, MetadataValue right)
        {
            RelationMetadata leftRelation = CreateComparableRelation();
            RelationMetadata rightRelation = CreateComparableRelation();
            leftRelation.RelationType = left;
            rightRelation.RelationType = right;
            AssertRelationPairOrderIndependent(leftRelation, rightRelation);
        }

        /// <summary>XMZADD 20260902 将两条证据放入关系类型后验证证据字段排序。</summary>
        private static void AssertRelationEvidenceOrderIndependent(EvidenceItem left, EvidenceItem right)
        {
            MetadataValue leftValue = CreateComparableMetadataValue();
            MetadataValue rightValue = CreateComparableMetadataValue();
            leftValue.Evidence.Add(left);
            rightValue.Evidence.Add(right);
            AssertRelationTypeOrderIndependent(leftValue, rightValue);
        }

        /// <summary>XMZADD 20260902 创建全部关系字符串字段一致的比较基线。</summary>
        private static RelationMetadata CreateComparableRelation()
        {
            return new RelationMetadata
            {
                ScopeKey = "BASE",
                ForeignKeyName = "BASE",
                ParentSchemaName = "BASE",
                ParentTableName = "BASE",
                ParentFieldName = "BASE",
                ChildSchemaName = "BASE",
                ChildTableName = "BASE",
                ChildFieldName = "BASE"
            };
        }

        /// <summary>XMZADD 20260902 创建全部标量字段一致且证据集合为空的元数据比较基线。</summary>
        private static MetadataValue CreateComparableMetadataValue()
        {
            return new MetadataValue
            {
                Value = "BASE",
                Description = "BASE",
                Status = ConfidenceStatus.Confirmed,
                ConfidenceScore = 1,
                SourceType = "BASE",
                SourceSummary = "BASE",
                OriginalAutomaticValue = "BASE",
                IsManualOverride = false,
                IsLocked = false,
                Evidence = new List<EvidenceItem>()
            };
        }

        /// <summary>XMZADD 20260902 创建全部可序列化证据字段一致的比较基线。</summary>
        private static EvidenceItem CreateComparableEvidence()
        {
            return new EvidenceItem
            {
                SourceType = "BASE",
                SourcePath = "BASE",
                SourceLine = 1,
                RuleName = "BASE",
                RawValue = "BASE",
                OriginalText = "BASE",
                Explanation = "BASE"
            };
        }

        /// <summary>XMZADD 20260902 创建仅含指定顺序关系的确定性编码快照。</summary>
        private static SnapshotData CreateRelationSnapshot(RelationMetadata first, RelationMetadata second)
        {
            return new SnapshotData
            {
                FormatVersion = SnapshotValidator.SupportedFormatVersion,
                Revision = 1,
                RefreshedAt = new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc),
                Tables = new List<TableMetadata>
                {
                    new TableMetadata
                    {
                        SchemaName = "dbo",
                        ObjectName = "T_RELATION_SORT",
                        ObjectType = "TABLE",
                        Fields = new List<FieldMetadata>(),
                        Relations = new List<RelationMetadata> { first, second }
                    }
                },
                ExcludedObjects = new List<ExcludedObjectRecord>(),
                Abbreviations = new List<AbbreviationEntry>()
            };
        }

        /// <summary>XMZADD 20260901 创建内容相同但集合顺序可反转的规范快照。</summary>
        private static SnapshotData CreateSnapshot(bool reverseOrder)
        {
            var customer = new TableMetadata
            {
                SchemaName = "dbo",
                ObjectName = "T_CUSTOMER",
                ObjectType = "TABLE",
                ChineseName = new MetadataValue { Value = "客户", Status = ConfidenceStatus.DatabaseEvidence },
                Fields = new List<FieldMetadata>
                {
                    new FieldMetadata
                    {
                        FieldName = "FId",
                        DataType = "int",
                        IsPrimaryKey = true,
                        EnumItems = new List<EnumItemMetadata>
                        {
                            new EnumItemMetadata
                            {
                                Value = "A",
                                ChineseName = new MetadataValue { Value = "启用", Status = ConfidenceStatus.CodeEvidence }
                            },
                            new EnumItemMetadata
                            {
                                Value = "B",
                                ChineseName = new MetadataValue { Value = "停用", Status = ConfidenceStatus.CodeEvidence }
                            }
                        }
                    },
                    new FieldMetadata { FieldName = "FName", DataType = "nvarchar", LengthText = "100" }
                },
                Relations = new List<RelationMetadata>
                {
                    new RelationMetadata
                    {
                        ForeignKeyName = "FK_ORDER_CUSTOMER",
                        ParentSchemaName = "dbo",
                        ParentTableName = "T_CUSTOMER",
                        ParentFieldName = "FId",
                        ChildSchemaName = "dbo",
                        ChildTableName = "T_ORDER",
                        ChildFieldName = "FCustomerId"
                    },
                    new RelationMetadata
                    {
                        ForeignKeyName = "FK_ITEM_CUSTOMER",
                        ParentSchemaName = "dbo",
                        ParentTableName = "T_CUSTOMER",
                        ParentFieldName = "FId",
                        ChildSchemaName = "dbo",
                        ChildTableName = "T_ORDER_ITEM",
                        ChildFieldName = "FCustomerId"
                    }
                }
            };
            var order = new TableMetadata
            {
                SchemaName = "dbo",
                ObjectName = "T_ORDER",
                ObjectType = "TABLE",
                ChineseName = new MetadataValue { Value = "订单", Status = ConfidenceStatus.CodeEvidence },
                Fields = new List<FieldMetadata>
                {
                    new FieldMetadata { FieldName = "FId", DataType = "int", IsPrimaryKey = true },
                    new FieldMetadata { FieldName = "FStatus", DataType = "int" }
                },
                Relations = new List<RelationMetadata>()
            };

            if (reverseOrder)
            {
                customer.Fields[0].EnumItems = new List<EnumItemMetadata>
                {
                    customer.Fields[0].EnumItems[1],
                    customer.Fields[0].EnumItems[0]
                };
                customer.Fields = new List<FieldMetadata> { customer.Fields[1], customer.Fields[0] };
                customer.Relations = new List<RelationMetadata> { customer.Relations[1], customer.Relations[0] };
                order.Fields = new List<FieldMetadata> { order.Fields[1], order.Fields[0] };
            }

            var materialAbbreviation = new AbbreviationEntry
            {
                Abbreviation = "MAT",
                ChineseMeaning = "物料",
                ModuleScope = "供应链",
                TableScope = "T_MATERIAL",
                Evidence = new List<AbbreviationEvidence>
                {
                    new AbbreviationEvidence
                    {
                        SourceType = "Code",
                        RelativePath = "Models/Material.vb",
                        LineNumber = 10,
                        Summary = "物料模型"
                    },
                    new AbbreviationEvidence
                    {
                        SourceType = "KnowledgeBase",
                        RelativePath = "字典/物料.md",
                        LineNumber = 20,
                        Summary = "物料术语"
                    }
                }
            };
            var orderAbbreviation = new AbbreviationEntry
            {
                Abbreviation = "ORD",
                ChineseMeaning = "订单",
                ModuleScope = "销售",
                TableScope = "T_ORDER",
                Evidence = new List<AbbreviationEvidence>()
            };
            if (reverseOrder)
            {
                materialAbbreviation.Evidence = new List<AbbreviationEvidence>
                {
                    materialAbbreviation.Evidence[1],
                    materialAbbreviation.Evidence[0]
                };
            }

            var logExclusion = new ExcludedObjectRecord
            {
                ObjectKey = "dbo.T_LOG",
                Reason = "技术日志表",
                ExcludedAtUtc = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc)
            };
            var viewExclusion = new ExcludedObjectRecord
            {
                ObjectKey = "dbo.V_REPORT",
                Reason = "报表视图",
                ExcludedAtUtc = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc)
            };

            return new SnapshotData
            {
                FormatVersion = SnapshotValidator.SupportedFormatVersion,
                Revision = 12L,
                RefreshedAt = new DateTime(2026, 9, 1, 8, 30, 0, DateTimeKind.Utc),
                Tables = reverseOrder
                    ? new List<TableMetadata> { order, customer }
                    : new List<TableMetadata> { customer, order },
                ExcludedObjects = reverseOrder
                    ? new List<ExcludedObjectRecord> { viewExclusion, logExclusion }
                    : new List<ExcludedObjectRecord> { logExclusion, viewExclusion },
                Abbreviations = reverseOrder
                    ? new List<AbbreviationEntry> { orderAbbreviation, materialAbbreviation }
                    : new List<AbbreviationEntry> { materialAbbreviation, orderAbbreviation }
            };
        }

        /// <summary>XMZADD 20260902 按索引即时生成测试项，避免大型数量边界测试预先占用等量对象内存。</summary>
        private sealed class GeneratedReadOnlyList<T> : IList<T>
        {
            private readonly int _count;
            private readonly Func<int, T> _itemFactory;

            /// <summary>XMZADD 20260902 创建具有固定数量和惰性项目工厂的只读列表。</summary>
            public GeneratedReadOnlyList(int count, Func<int, T> itemFactory)
            {
                if (count < 0)
                {
                    throw new ArgumentOutOfRangeException("count");
                }
                if (itemFactory == null)
                {
                    throw new ArgumentNullException("itemFactory");
                }

                _count = count;
                _itemFactory = itemFactory;
            }

            /// <summary>XMZADD 20260902 按需生成指定索引的测试项并禁止替换。</summary>
            public T this[int index]
            {
                get
                {
                    EnsureValidIndex(index);
                    return _itemFactory(index);
                }
                set { throw new NotSupportedException(); }
            }

            /// <summary>XMZADD 20260902 返回不依赖实际对象分配的固定测试项数量。</summary>
            public int Count { get { return _count; } }

            /// <summary>XMZADD 20260902 标识大型测试集合不可被验证逻辑修改。</summary>
            public bool IsReadOnly { get { return true; } }

            /// <summary>XMZADD 20260902 顺序比较惰性项目以返回指定项目索引。</summary>
            public int IndexOf(T item)
            {
                EqualityComparer<T> comparer = EqualityComparer<T>.Default;
                for (int index = 0; index < _count; index++)
                {
                    if (comparer.Equals(_itemFactory(index), item))
                    {
                        return index;
                    }
                }
                return -1;
            }

            /// <summary>XMZADD 20260902 禁止向固定数量测试集合插入项目。</summary>
            public void Insert(int index, T item)
            {
                throw new NotSupportedException();
            }

            /// <summary>XMZADD 20260902 禁止从固定数量测试集合移除索引项目。</summary>
            public void RemoveAt(int index)
            {
                throw new NotSupportedException();
            }

            /// <summary>XMZADD 20260902 禁止向固定数量测试集合追加项目。</summary>
            public void Add(T item)
            {
                throw new NotSupportedException();
            }

            /// <summary>XMZADD 20260902 禁止清空固定数量测试集合。</summary>
            public void Clear()
            {
                throw new NotSupportedException();
            }

            /// <summary>XMZADD 20260902 顺序比较惰性项目以判断是否包含指定项目。</summary>
            public bool Contains(T item)
            {
                return IndexOf(item) >= 0;
            }

            /// <summary>XMZADD 20260902 按索引生成并复制测试项到调用方数组。</summary>
            public void CopyTo(T[] array, int arrayIndex)
            {
                if (array == null)
                {
                    throw new ArgumentNullException("array");
                }
                if (arrayIndex < 0 || array.Length - arrayIndex < _count)
                {
                    throw new ArgumentOutOfRangeException("arrayIndex");
                }

                for (int index = 0; index < _count; index++)
                {
                    array[arrayIndex + index] = _itemFactory(index);
                }
            }

            /// <summary>XMZADD 20260902 禁止按项目值移除固定数量测试项。</summary>
            public bool Remove(T item)
            {
                throw new NotSupportedException();
            }

            /// <summary>XMZADD 20260902 按索引顺序惰性枚举测试项。</summary>
            public IEnumerator<T> GetEnumerator()
            {
                for (int index = 0; index < _count; index++)
                {
                    yield return _itemFactory(index);
                }
            }

            /// <summary>XMZADD 20260902 为非泛型集合调用提供相同的惰性枚举行为。</summary>
            IEnumerator IEnumerable.GetEnumerator()
            {
                return GetEnumerator();
            }

            /// <summary>XMZADD 20260902 在调用项目工厂前拒绝越界索引。</summary>
            private void EnsureValidIndex(int index)
            {
                if (index < 0 || index >= _count)
                {
                    throw new ArgumentOutOfRangeException("index");
                }
            }
        }
    }
}
