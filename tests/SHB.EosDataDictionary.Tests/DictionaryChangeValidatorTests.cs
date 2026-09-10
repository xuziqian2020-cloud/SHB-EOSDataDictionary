using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHB.EosDataDictionary.Models;
using SHB.EosDataDictionary.Services;

namespace SHB.EosDataDictionary.Tests
{
    /// <summary>XMZADD 20260901 验证公开 Issue 事件只能通过固定业务和结构白名单进入共享快照。</summary>
    [TestClass]
    public sealed class DictionaryChangeValidatorTests
    {
        /// <summary>XMZADD 20260901 验证普通用户白名单与编辑窗口一致并由 GitHub API 可信身份重新署名。</summary>
        [TestMethod]
        public void ValidateAndNormalize_OrdinaryWhitelist_BindsTrustedAuthorWithoutMutatingInput()
        {
            string[] tableProperties = { "ChineseName", "BusinessMeaning", "ModuleName", "EntityName", "Remark", "Category", "KeepWhenEmpty" };
            string[] fieldProperties = { "ChineseName", "BusinessMeaning", "Usage", "EntityPropertyName", "Remark", "RelationSummary" };
            var batch = CreateBatch();
            for (int index = 0; index < tableProperties.Length; index++)
            {
                batch.Operations.Add(CreateSet("table_" + index, string.Empty, tableProperties[index],
                    tableProperties[index] == "Category" ? "Business" : tableProperties[index] == "KeepWhenEmpty" ? "True" : "新值"));
            }
            for (int index = 0; index < fieldProperties.Length; index++)
            {
                batch.Operations.Add(CreateSet("field_" + index, "FNAME", fieldProperties[index], "新值"));
            }

            DictionaryChangeValidationResult result = new DictionaryChangeValidator().ValidateAndNormalize(
                batch, "12345", new List<string> { "99999" });

            Assert.IsTrue(result.IsValid, JoinErrors(result));
            Assert.AreEqual(13, result.NormalizedBatch.Operations.Count);
            Assert.AreEqual("12345", result.NormalizedBatch.AuthorGitHubUserId);
            for (int index = 0; index < result.NormalizedBatch.Operations.Count; index++)
            {
                Assert.AreEqual("12345", result.NormalizedBatch.Operations[index].AuthorGitHubUserId);
                Assert.AreEqual("forged-publisher", batch.Operations[index].AuthorGitHubUserId);
                Assert.AreNotSame(batch.Operations[index], result.NormalizedBatch.Operations[index]);
            }
        }

        /// <summary>XMZADD 20260901 验证字段键是否存在必须与属性作用域严格一致。</summary>
        [TestMethod]
        public void ValidateAndNormalize_PropertyScopeMismatch_ReportsEveryError()
        {
            var batch = CreateBatch();
            batch.Operations.Add(CreateSet("scope_table", "FNAME", "ModuleName", "采购"));
            batch.Operations.Add(CreateSet("scope_field", string.Empty, "Usage", "展示"));

            DictionaryChangeValidationResult result = new DictionaryChangeValidator().ValidateAndNormalize(
                batch, "12345", new List<string>());

            Assert.IsFalse(result.IsValid);
            Assert.AreEqual(2, CountErrors(result, "PROPERTY_SCOPE_INVALID"));
        }

        /// <summary>XMZADD 20260901 验证正文伪造 publisher 身份不能越过真实 Issue 作者权限。</summary>
        [TestMethod]
        public void ValidateAndNormalize_ForgedPublisherAuthor_UsesTrustedOrdinaryUserForPermission()
        {
            var batch = CreateBatch();
            DictionaryChangeOperation operation = CreateSet("physical_1", "FNAME", "DataType", "nvarchar");
            operation.AuthorGitHubUserId = "99999";
            batch.Operations.Add(operation);

            DictionaryChangeValidationResult result = new DictionaryChangeValidator().ValidateAndNormalize(
                batch, "12345", new List<string> { "99999" });

            Assert.IsFalse(result.IsValid);
            Assert.IsTrue(HasError(result, "STRUCTURE_PERMISSION_REQUIRED"));
        }

        /// <summary>XMZADD 20260901 验证 publisher ID 按不可变数字 ID 精确比较并可提交类型化结构操作。</summary>
        [TestMethod]
        public void ValidateAndNormalize_TrustedPublisher_AllowsTypedStructureOperations()
        {
            var batch = CreateBatch();
            batch.Operations.Add(CreateAddTable("add_table", "dbo.T_ORDER"));
            batch.Operations.Add(CreateAddField("add_field", "dbo.T_ORDER", "FID"));
            batch.Operations.Add(CreateAddRelation("add_relation"));

            DictionaryChangeValidationResult result = new DictionaryChangeValidator().ValidateAndNormalize(
                batch, "12345", new List<string> { "12345" });

            Assert.IsTrue(result.IsValid, JoinErrors(result));
            Assert.AreNotSame(batch.Operations[0].TablePayload, result.NormalizedBatch.Operations[0].TablePayload);
            Assert.AreNotSame(batch.Operations[1].FieldPayload, result.NormalizedBatch.Operations[1].FieldPayload);
            Assert.AreNotSame(batch.Operations[2].RelationPayload, result.NormalizedBatch.Operations[2].RelationPayload);
        }

        /// <summary>XMZADD 20260901 验证结构发布者的相对短证据通过校验并被深拷贝为固定公开字段。</summary>
        [TestMethod]
        public void ValidateAndNormalize_PublisherEvidence_IsStrictAndDeepCopied()
        {
            var batch = CreateBatch();
            DictionaryChangeOperation operation = CreateSet("evidence_valid", "FNAME", "ChineseName", "订单名称");
            operation.Evidence = new List<EvidenceItem>
            {
                new EvidenceItem
                {
                    SourceType = "EOS源码",
                    SourcePath = "Order/OrderEntity.vb",
                    SourceLine = 12,
                    RuleName = "EntityProperty",
                    Explanation = "实体属性中文摘要"
                }
            };
            batch.Operations.Add(operation);

            DictionaryChangeValidationResult result = new DictionaryChangeValidator().ValidateAndNormalize(
                batch, "12345", new List<string> { "12345" });

            Assert.IsTrue(result.IsValid, JoinErrors(result));
            Assert.AreNotSame(operation.Evidence, result.NormalizedBatch.Operations[0].Evidence);
            Assert.AreEqual("Order/OrderEntity.vb", result.NormalizedBatch.Operations[0].Evidence[0].SourcePath);
            Assert.IsNull(result.NormalizedBatch.Operations[0].Evidence[0].OriginalText);
            Assert.IsNull(result.NormalizedBatch.Operations[0].Evidence[0].RawValue);
        }

        /// <summary>XMZADD 20260901 验证非发布者、绝对路径、凭据摘要和源码正文证据全部被公开协议拒绝。</summary>
        [TestMethod]
        public void ValidateAndNormalize_UnsafeOrUntrustedEvidence_IsRejected()
        {
            var ordinaryBatch = CreateBatch();
            DictionaryChangeOperation ordinary = CreateSet("evidence_ordinary", "FNAME", "ChineseName", "订单名称");
            ordinary.Evidence = CreateEvidence("Order/Order.vb", "短摘要");
            ordinaryBatch.Operations.Add(ordinary);

            var unsafeBatch = CreateBatch();
            DictionaryChangeOperation absolute = CreateSet("evidence_absolute", "FNAME", "ChineseName", "订单名称");
            absolute.Evidence = CreateEvidence("C:/private/Order.vb", "短摘要");
            unsafeBatch.Operations.Add(absolute);
            DictionaryChangeOperation secret = CreateSet("evidence_secret", "FNAME", "ChineseName", "订单名称");
            secret.Evidence = CreateEvidence("Order/Order.vb", "token=fake");
            unsafeBatch.Operations.Add(secret);
            DictionaryChangeOperation body = CreateSet("evidence_body", "FNAME", "ChineseName", "订单名称");
            body.Evidence = CreateEvidence("Order/Order.vb", "短摘要");
            body.Evidence[0].OriginalText = "完整源码正文";
            unsafeBatch.Operations.Add(body);

            DictionaryChangeValidationResult ordinaryResult = new DictionaryChangeValidator().ValidateAndNormalize(
                ordinaryBatch, "22222", new List<string> { "12345" });
            DictionaryChangeValidationResult unsafeResult = new DictionaryChangeValidator().ValidateAndNormalize(
                unsafeBatch, "12345", new List<string> { "12345" });

            Assert.IsTrue(HasError(ordinaryResult, "EVIDENCE_PERMISSION_REQUIRED"));
            Assert.AreEqual(3, CountErrors(unsafeResult, "EVIDENCE_INVALID"));
        }

        /// <summary>XMZADD 20260901 验证 C# 与 Python 共用的凭据和不可见字符向量在证据与业务赋值中全部被拒绝。</summary>
        [TestMethod]
        public void ValidateAndNormalize_SharedEvidenceRejectionVectors_AreRejected()
        {
            string vectorPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "evidence_rejection_vectors.txt");
            string[] lines = File.ReadAllLines(vectorPath, Encoding.UTF8);
            int checkedCount = 0;
            for (int index = 0; index < lines.Length; index++)
            {
                string line = lines[index];
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }
                int separator = line.IndexOf('|');
                Assert.IsTrue(separator > 0, "共享证据向量格式无效。");
                string label = line.Substring(0, separator);
                string value = Encoding.UTF8.GetString(Convert.FromBase64String(line.Substring(separator + 1)));
                var batch = CreateBatch();
                DictionaryChangeOperation operation = CreateSet("vector_" + label, "FNAME", "ChineseName", "订单名称");
                operation.Evidence = CreateEvidence("Order/Order.vb", value);
                batch.Operations.Add(operation);

                DictionaryChangeValidationResult result = new DictionaryChangeValidator().ValidateAndNormalize(
                    batch, "12345", new List<string> { "12345" });

                var valueBatch = CreateBatch();
                DictionaryChangeOperation valueOperation = CreateSet(
                    "value_vector_" + label, "FNAME", "ChineseName", value);
                valueBatch.Operations.Add(valueOperation);
                DictionaryChangeValidationResult valueResult = new DictionaryChangeValidator().ValidateAndNormalize(
                    valueBatch, "12345", new List<string> { "12345" });

                Assert.IsFalse(result.IsValid, "未拒绝共享证据向量：" + label);
                Assert.IsTrue(HasError(result, "EVIDENCE_INVALID"), "错误代码不一致：" + label);
                Assert.IsFalse(valueResult.IsValid, "未拒绝共享赋值向量：" + label);
                checkedCount++;
            }
            Assert.IsTrue(checkedCount > 0);
        }

        /// <summary>XMZADD 20260901 验证结构载荷必须与固定 ChangeKind、对象键和字段键严格匹配。</summary>
        [TestMethod]
        public void ValidateAndNormalize_InvalidStructurePayloadCombinations_AreRejected()
        {
            var batch = CreateBatch();
            DictionaryChangeOperation table = CreateAddTable("bad_table", "dbo.T_ORDER");
            table.TablePayload.Fields.Add(new FieldStructurePayload { FieldName = "FID" });
            batch.Operations.Add(table);
            DictionaryChangeOperation field = CreateAddField("bad_field", "dbo.T_ORDER", "FID");
            field.FieldKey = "FOTHER";
            batch.Operations.Add(field);
            DictionaryChangeOperation relation = CreateAddRelation("bad_relation");
            relation.ObjectKey = "dbo.T_PARENT";
            batch.Operations.Add(relation);

            DictionaryChangeValidationResult result = new DictionaryChangeValidator().ValidateAndNormalize(
                batch, "12345", new List<string> { "12345" });

            Assert.IsFalse(result.IsValid);
            Assert.AreEqual(3, CountErrors(result, "PAYLOAD_INVALID"));
        }

        /// <summary>XMZADD 20260901 验证分类和布尔业务规则只接受规范化发布文本。</summary>
        [TestMethod]
        public void ValidateAndNormalize_NonCanonicalCategoryAndBoolean_AreRejected()
        {
            var batch = CreateBatch();
            batch.Operations.Add(CreateSet("category_numeric", string.Empty, "Category", "1"));
            batch.Operations.Add(CreateSet("category_unknown", string.Empty, "Category", "Unclassified"));
            batch.Operations.Add(CreateSet("boolean_lower", string.Empty, "KeepWhenEmpty", "true"));

            DictionaryChangeValidationResult result = new DictionaryChangeValidator().ValidateAndNormalize(
                batch, "12345", new List<string>());

            Assert.IsTrue(HasError(result, "CATEGORY_INVALID"));
            Assert.AreEqual(2, CountErrors(result, "CATEGORY_INVALID"));
            Assert.IsTrue(HasError(result, "BOOLEAN_INVALID"));
        }

        /// <summary>XMZADD 20260901 验证畸形键、未知属性、未知类型、空项和重复操作号均完整报告且不回显载荷。</summary>
        [TestMethod]
        public void ValidateAndNormalize_MalformedBatch_ReturnsCompleteSafeErrors()
        {
            var batch = CreateBatch();
            batch.Operations.Add(null);
            DictionaryChangeOperation badKey = CreateSet("bad/key", string.Empty, "NotAllowedSecret", "private-value");
            badKey.ObjectKey = "dbo..T_ORDER";
            batch.Operations.Add(badKey);
            DictionaryChangeOperation duplicate = CreateSet("dup_1", string.Empty, "ChineseName", "A");
            batch.Operations.Add(duplicate);
            batch.Operations.Add(CreateSet("dup_1", string.Empty, "ChineseName", "B"));
            DictionaryChangeOperation kind = CreateSet("kind_1", string.Empty, "ChineseName", "C");
            kind.ChangeKind = "ExecuteWorkflow";
            batch.Operations.Add(kind);

            DictionaryChangeValidationResult result = new DictionaryChangeValidator().ValidateAndNormalize(
                batch, "12345", new List<string>());

            Assert.IsTrue(HasError(result, "OPERATION_NULL"));
            Assert.IsTrue(HasError(result, "OPERATION_ID_INVALID"));
            Assert.IsTrue(HasError(result, "OBJECT_KEY_INVALID"));
            Assert.IsTrue(HasError(result, "PROPERTY_NOT_ALLOWED"));
            Assert.IsTrue(HasError(result, "OPERATION_ID_DUPLICATE"));
            Assert.IsTrue(HasError(result, "CHANGE_KIND_INVALID"));
            Assert.IsFalse(JoinErrors(result).Contains("private-value"));
            Assert.IsFalse(JoinErrors(result).Contains("NotAllowedSecret"));
        }

        /// <summary>XMZADD 20260901 验证操作数、单文本、作者身份和总 JSON 字节限制均在进入应用层前生效。</summary>
        [TestMethod]
        public void ValidateAndNormalize_SizeAndAuthorLimits_AreEnforced()
        {
            var tooMany = CreateBatch();
            for (int index = 0; index < 101; index++)
            {
                tooMany.Operations.Add(CreateSet("many_" + index, string.Empty, "ChineseName", "值"));
            }
            DictionaryChangeValidationResult manyResult = new DictionaryChangeValidator().ValidateAndNormalize(
                tooMany, "12345", new List<string>());
            Assert.IsTrue(HasError(manyResult, "OPERATION_LIMIT_EXCEEDED"));

            var tooLong = CreateBatch();
            tooLong.Operations.Add(CreateSet("long_1", string.Empty, "ChineseName", new string('长', 2001)));
            DictionaryChangeValidationResult longResult = new DictionaryChangeValidator().ValidateAndNormalize(
                tooLong, "12345", new List<string>());
            Assert.IsTrue(HasError(longResult, "TEXT_TOO_LONG"));

            DictionaryChangeValidationResult authorResult = new DictionaryChangeValidator().ValidateAndNormalize(
                CreateBatchWithOneValidOperation(), "not-a-numeric-id", new List<string>());
            Assert.IsTrue(HasError(authorResult, "AUTHOR_INVALID"));

            var oversized = CreateBatch();
            for (int index = 0; index < 100; index++)
            {
                DictionaryChangeOperation operation = CreateSet("bytes_" + index, string.Empty, "ChineseName", new string('新', 2000));
                operation.OldValue = new string('旧', 2000);
                oversized.Operations.Add(operation);
            }
            DictionaryChangeValidationResult bytesResult = new DictionaryChangeValidator().ValidateAndNormalize(
                oversized, "12345", new List<string>());
            Assert.IsTrue(HasError(bytesResult, "PAYLOAD_TOO_LARGE"));
        }

        /// <summary>XMZADD 20260901 验证远程原始 JSON 在反序列化前限流并递归拒绝所有未知成员和本地覆盖。</summary>
        [TestMethod]
        public void ValidateAndNormalizeJson_UnknownMembersOverridesAndOversize_AreRejected()
        {
            var validator = new DictionaryChangeValidator();
            string operation = "{\"OperationId\":\"remote_1\",\"ObjectKey\":\"dbo.T_ORDER\",\"FieldKey\":\"\",\"PropertyName\":\"ChineseName\",\"NewValue\":\"名称\",\"ChangeKind\":\"Set\"}";
            string unknownTop = "{\"BatchId\":\"remote_batch\",\"Operations\":[" + operation + "],\"Overrides\":[],\"UnknownTop\":\"hidden\"}";
            string unknownNestedOperation = operation.Substring(0, operation.Length - 1) + ",\"UnknownOperation\":\"hidden\"}";
            string unknownNested = "{\"BatchId\":\"remote_batch\",\"Operations\":[" + unknownNestedOperation + "],\"Overrides\":[]}";
            string unknownPayloadOperation = "{\"OperationId\":\"remote_2\",\"ObjectKey\":\"dbo.T_NEW\",\"FieldKey\":\"\",\"PropertyName\":\"\",\"ChangeKind\":\"AddTable\",\"TablePayload\":{\"SchemaName\":\"dbo\",\"ObjectName\":\"T_NEW\",\"ObjectType\":\"TABLE\",\"ApproximateRowCount\":0,\"Fields\":[],\"Relations\":[],\"UnknownPayload\":1}}";
            string unknownPayload = "{\"BatchId\":\"remote_batch\",\"Operations\":[" + unknownPayloadOperation + "],\"Overrides\":[]}";
            string overrides = "{\"BatchId\":\"remote_batch\",\"Operations\":[" + operation + "],\"Overrides\":[{\"ScopeKey\":\"local\",\"ObjectName\":\"T_ORDER\",\"PropertyName\":\"ChineseName\"}]}";

            Assert.IsTrue(HasError(validator.ValidateAndNormalizeJson(Encoding.UTF8.GetBytes(unknownTop), "123", new List<string>()), "UNKNOWN_MEMBER"));
            Assert.IsTrue(HasError(validator.ValidateAndNormalizeJson(Encoding.UTF8.GetBytes(unknownNested), "123", new List<string>()), "UNKNOWN_MEMBER"));
            Assert.IsTrue(HasError(validator.ValidateAndNormalizeJson(Encoding.UTF8.GetBytes(unknownPayload), "123", new List<string> { "123" }), "UNKNOWN_MEMBER"));
            Assert.IsTrue(HasError(validator.ValidateAndNormalizeJson(Encoding.UTF8.GetBytes(overrides), "123", new List<string>()), "OVERRIDES_NOT_ALLOWED"));

            string oversized = "{\"UnknownLarge\":\"" + new string('大', DictionaryChangeValidator.MaximumPayloadBytes) + "\"}";
            DictionaryChangeValidationResult oversizedResult = validator.ValidateAndNormalizeJson(
                Encoding.UTF8.GetBytes(oversized), "123", new List<string>());
            Assert.AreEqual(1, oversizedResult.Errors.Count);
            Assert.IsTrue(HasError(oversizedResult, "PAYLOAD_TOO_LARGE"));
        }

        /// <summary>XMZADD 20260901 验证规范 JSON 使用预留代码块包装后的 60KiB 边界，边界加一字节必须在解析前拒绝。</summary>
        [TestMethod]
        public void ValidateAndNormalizeJson_UsesIssueSafePayloadByteBoundary()
        {
            Assert.AreEqual((60 * 1024) - 15, DictionaryChangeValidator.MaximumPayloadBytes);
            string json = DictionaryJsonSerializer.SerializeBatch(CreateBatchWithOneValidOperation());
            int paddingLength = DictionaryChangeValidator.MaximumPayloadBytes - Encoding.UTF8.GetByteCount(json);
            Assert.IsTrue(paddingLength > 0);
            byte[] exactBoundary = Encoding.UTF8.GetBytes(json + new string(' ', paddingLength));
            byte[] overBoundary = Encoding.UTF8.GetBytes(json + new string(' ', paddingLength + 1));
            var validator = new DictionaryChangeValidator();

            DictionaryChangeValidationResult exactResult = validator.ValidateAndNormalizeJson(
                exactBoundary, "12345", new List<string>());
            DictionaryChangeValidationResult overResult = validator.ValidateAndNormalizeJson(
                overBoundary, "12345", new List<string>());

            Assert.IsTrue(exactResult.IsValid, JoinErrors(exactResult));
            Assert.IsTrue(HasError(overResult, "PAYLOAD_TOO_LARGE"));
        }

        /// <summary>XMZADD 20260901 验证无未知成员的表字段关系远程载荷不会因扩展数据容器被误拒绝。</summary>
        [TestMethod]
        public void ValidateAndNormalizeJson_KnownTableFieldAndRelationPayloads_AreAccepted()
        {
            var batch = CreateBatch();
            batch.Operations.Add(CreateAddTable("remote_table", "dbo.T_ORDER"));
            batch.Operations.Add(CreateAddField("remote_field", "dbo.T_ORDER", "FID"));
            batch.Operations.Add(CreateAddRelation("remote_relation"));
            byte[] json = Encoding.UTF8.GetBytes(DictionaryJsonSerializer.SerializeBatch(batch));

            DictionaryChangeValidationResult result = new DictionaryChangeValidator().ValidateAndNormalizeJson(
                json, "123", new List<string> { "123" });

            Assert.IsTrue(result.IsValid, JoinErrors(result));
            Assert.AreEqual(3, result.NormalizedBatch.Operations.Count);
        }

        /// <summary>XMZADD 20260901 验证超过100项后立即返回数量错误而不遍历或克隆恶意嵌套载荷。</summary>
        [TestMethod]
        public void ValidateAndNormalizeJson_TooManyOperations_DoesNotTraverseNestedPayloads()
        {
            var json = new StringBuilder();
            json.Append("{\"BatchId\":\"remote_many\",\"Overrides\":[],\"Operations\":[");
            for (int index = 0; index < 101; index++)
            {
                if (index > 0) json.Append(',');
                json.Append("{\"OperationId\":\"remote_").Append(index)
                    .Append("\",\"ObjectKey\":\"dbo.T_ORDER\",\"FieldKey\":\"\",\"PropertyName\":\"ChineseName\",\"NewValue\":\"值\",\"ChangeKind\":\"Set\"");
                if (index == 100) json.Append(",\"TablePayload\":{\"UnknownNested\":\"must-not-be-walked\"}");
                json.Append('}');
            }
            json.Append("]}");

            DictionaryChangeValidationResult result = new DictionaryChangeValidator().ValidateAndNormalizeJson(
                Encoding.UTF8.GetBytes(json.ToString()), "123", new List<string>());

            Assert.AreEqual(1, result.Errors.Count);
            Assert.IsTrue(HasError(result, "OPERATION_LIMIT_EXCEEDED"));
        }

        /// <summary>XMZADD 20260901 验证常见 SQL Server 类型、长度和 EOS 标识符使用同一严格结构规范。</summary>
        [TestMethod]
        public void ValidateAndNormalize_PhysicalTypesLengthsAndIdentifiers_AreStrictAndCanonical()
        {
            string[] types =
            {
                "bigint", "binary", "bit", "char", "date", "datetime", "datetime2", "datetimeoffset",
                "decimal", "float", "geography", "geometry", "hierarchyid", "image", "int", "money",
                "nchar", "ntext", "numeric", "nvarchar", "real", "rowversion", "smalldatetime", "smallint",
                "smallmoney", "sql_variant", "text", "time", "timestamp", "tinyint", "uniqueidentifier",
                "varbinary", "varchar", "xml"
            };
            var valid = CreateBatch();
            for (int index = 0; index < types.Length; index++)
            {
                string length = types[index] == "decimal" || types[index] == "numeric" ? "018,002" :
                    types[index] == "binary" || types[index] == "char" || types[index] == "nchar" ||
                    types[index] == "nvarchar" || types[index] == "varbinary" || types[index] == "varchar" ? "010" : "—";
                DictionaryChangeOperation operation = CreateAddField("type_" + index, "dbo.T_ORDER", "F_" + index);
                operation.FieldPayload.DataType = index == 19 ? "NVARCHAR" : types[index];
                operation.FieldPayload.LengthText = length;
                valid.Operations.Add(operation);
            }

            DictionaryChangeValidationResult validResult = new DictionaryChangeValidator().ValidateAndNormalize(
                valid, "123", new List<string> { "123" });
            Assert.IsTrue(validResult.IsValid, JoinErrors(validResult));
            Assert.AreEqual("nvarchar", validResult.NormalizedBatch.Operations[19].FieldPayload.DataType);
            Assert.AreEqual("10", validResult.NormalizedBatch.Operations[19].FieldPayload.LengthText);
            Assert.AreEqual("18,2", validResult.NormalizedBatch.Operations[8].FieldPayload.LengthText);

            var invalid = CreateBatch();
            DictionaryChangeOperation badType = CreateAddField("bad_type", "dbo.T_ORDER", "FTYPE");
            badType.FieldPayload.DataType = "PowerShell:C:\\secret";
            badType.FieldPayload.LengthText = "—";
            invalid.Operations.Add(badType);
            DictionaryChangeOperation badLength = CreateAddField("bad_length", "dbo.T_ORDER", "FLENGTH");
            badLength.FieldPayload.DataType = "int";
            badLength.FieldPayload.LengthText = "C:\\private";
            invalid.Operations.Add(badLength);
            DictionaryChangeOperation mismatched = CreateAddField("bad_match", "dbo.T_ORDER", "FMATCH");
            mismatched.FieldPayload.DataType = "decimal";
            mismatched.FieldPayload.LengthText = "2,3";
            invalid.Operations.Add(mismatched);
            DictionaryChangeOperation badIdentifier = CreateAddField("bad_identifier", "dbo.T_ORDER", "F BAD=:");
            badIdentifier.FieldPayload.DataType = "int";
            badIdentifier.FieldPayload.LengthText = "—";
            invalid.Operations.Add(badIdentifier);

            DictionaryChangeValidationResult invalidResult = new DictionaryChangeValidator().ValidateAndNormalize(
                invalid, "123", new List<string> { "123" });
            Assert.IsFalse(invalidResult.IsValid);
            Assert.IsTrue(HasError(invalidResult, "STRUCTURE_VALUE_INVALID"));
            Assert.IsTrue(HasError(invalidResult, "FIELD_KEY_INVALID"));
        }

        /// <summary>XMZADD 20260901 验证新增字段长度遵循 SQL Server 各字符和二进制类型的实际上限且拒绝零长度。</summary>
        [TestMethod]
        public void ValidateAndNormalize_AddFieldLengthBoundaries_MatchSqlServerRules()
        {
            var valid = CreateBatch();
            valid.Operations.Add(CreateAddFieldWithType("nvarchar_10", "FNVARCHAR10", "nvarchar", "10"));
            valid.Operations.Add(CreateAddFieldWithType("nvarchar_max", "FNVARCHARMAX", "nvarchar", "MAX"));
            valid.Operations.Add(CreateAddFieldWithType("varchar_8000", "FVARCHAR8000", "varchar", "8000"));
            valid.Operations.Add(CreateAddFieldWithType("varbinary_max", "FVARBINARYMAX", "varbinary", "MAX"));
            valid.Operations.Add(CreateAddFieldWithType("nchar_4000", "FNCHAR4000", "nchar", "4000"));
            valid.Operations.Add(CreateAddFieldWithType("char_8000", "FCHAR8000", "char", "8000"));
            valid.Operations.Add(CreateAddFieldWithType("binary_8000", "FBINARY8000", "binary", "8000"));
            valid.Operations.Add(CreateAddFieldWithType("decimal_38", "FDECIMAL38", "decimal", "38,38"));

            DictionaryChangeValidationResult validResult = new DictionaryChangeValidator().ValidateAndNormalize(
                valid, "123", new List<string> { "123" });
            Assert.IsTrue(validResult.IsValid, JoinErrors(validResult));

            var invalid = CreateBatch();
            invalid.Operations.Add(CreateAddFieldWithType("int_length", "FINTLENGTH", "int", "10"));
            invalid.Operations.Add(CreateAddFieldWithType("nvarchar_zero", "FNVARCHARZERO", "nvarchar", "0"));
            invalid.Operations.Add(CreateAddFieldWithType("nvarchar_over", "FNVARCHAROVER", "nvarchar", "4001"));
            invalid.Operations.Add(CreateAddFieldWithType("varchar_over", "FVARCHAROVER", "varchar", "8001"));
            invalid.Operations.Add(CreateAddFieldWithType("varbinary_over", "FVARBINARYOVER", "varbinary", "8001"));
            invalid.Operations.Add(CreateAddFieldWithType("nchar_over", "FNCHAROVER", "nchar", "4001"));
            invalid.Operations.Add(CreateAddFieldWithType("char_over", "FCHAROVER", "char", "8001"));
            invalid.Operations.Add(CreateAddFieldWithType("binary_over", "FBINARYOVER", "binary", "8001"));
            invalid.Operations.Add(CreateAddFieldWithType("decimal_precision_zero", "FDECIMALZERO", "decimal", "0,0"));
            invalid.Operations.Add(CreateAddFieldWithType("decimal_precision_over", "FDECIMALOVER", "decimal", "39,0"));
            invalid.Operations.Add(CreateAddFieldWithType("numeric_scale_over", "FNUMERICSCALE", "numeric", "10,11"));

            DictionaryChangeValidationResult invalidResult = new DictionaryChangeValidator().ValidateAndNormalize(
                invalid, "123", new List<string> { "123" });

            Assert.IsFalse(invalidResult.IsValid);
            Assert.AreEqual(invalid.Operations.Count, CountErrors(invalidResult, "STRUCTURE_VALUE_INVALID"));
        }

        /// <summary>XMZADD 20260901 验证 Set 必须显式携带 NewValue，空字符串作为清空语义仍合法。</summary>
        [TestMethod]
        public void ValidateAndNormalize_SetNullNewValueRejectedButEmptyAccepted()
        {
            var nullBatch = CreateBatch();
            nullBatch.Operations.Add(CreateSet("null_value", string.Empty, "ChineseName", null));
            DictionaryChangeValidationResult nullResult = new DictionaryChangeValidator().ValidateAndNormalize(
                nullBatch, "123", new List<string>());
            Assert.IsTrue(HasError(nullResult, "NEW_VALUE_REQUIRED"));

            var emptyBatch = CreateBatch();
            emptyBatch.Operations.Add(CreateSet("empty_value", string.Empty, "ChineseName", string.Empty));
            DictionaryChangeValidationResult emptyResult = new DictionaryChangeValidator().ValidateAndNormalize(
                emptyBatch, "123", new List<string>());
            Assert.IsTrue(emptyResult.IsValid, JoinErrors(emptyResult));
        }

        private static DictionaryChangeBatch CreateBatchWithOneValidOperation()
        {
            DictionaryChangeBatch batch = CreateBatch();
            batch.Operations.Add(CreateSet("valid_1", string.Empty, "ChineseName", "名称"));
            return batch;
        }

        private static DictionaryChangeBatch CreateBatch()
        {
            return new DictionaryChangeBatch
            {
                BatchId = "batch_valid",
                AuthorGitHubUserId = "forged-publisher",
                CreatedAtUtc = DateTime.UtcNow
            };
        }

        private static DictionaryChangeOperation CreateSet(string id, string fieldKey, string propertyName, string newValue)
        {
            return new DictionaryChangeOperation
            {
                OperationId = id,
                AuthorGitHubUserId = "forged-publisher",
                ObjectKey = "dbo.T_ORDER",
                FieldKey = fieldKey,
                PropertyName = propertyName,
                OldValue = "旧值",
                NewValue = newValue,
                ChangeKind = "Set",
                CreatedAtUtc = DateTime.UtcNow
            };
        }

        private static DictionaryChangeOperation CreateAddTable(string id, string objectKey)
        {
            return new DictionaryChangeOperation
            {
                OperationId = id,
                AuthorGitHubUserId = "forged-publisher",
                ObjectKey = objectKey,
                FieldKey = string.Empty,
                PropertyName = string.Empty,
                ChangeKind = "AddTable",
                CreatedAtUtc = DateTime.UtcNow,
                TablePayload = new TableStructurePayload
                {
                    SchemaName = "dbo",
                    ObjectName = "T_ORDER",
                    ObjectType = "TABLE",
                    ApproximateRowCount = 0L
                }
            };
        }

        private static DictionaryChangeOperation CreateAddField(string id, string objectKey, string fieldKey)
        {
            return new DictionaryChangeOperation
            {
                OperationId = id,
                AuthorGitHubUserId = "forged-publisher",
                ObjectKey = objectKey,
                FieldKey = fieldKey,
                PropertyName = string.Empty,
                ChangeKind = "AddField",
                CreatedAtUtc = DateTime.UtcNow,
                FieldPayload = new FieldStructurePayload
                {
                    FieldName = fieldKey,
                    OwnerTableName = "T_ORDER",
                    DataType = "int",
                    LengthText = "—"
                }
            };
        }

        private static DictionaryChangeOperation CreateAddFieldWithType(
            string id,
            string fieldKey,
            string dataType,
            string lengthText)
        {
            DictionaryChangeOperation operation = CreateAddField(id, "dbo.T_ORDER", fieldKey);
            operation.FieldPayload.DataType = dataType;
            operation.FieldPayload.LengthText = lengthText;
            return operation;
        }

        private static DictionaryChangeOperation CreateAddRelation(string id)
        {
            return new DictionaryChangeOperation
            {
                OperationId = id,
                AuthorGitHubUserId = "forged-publisher",
                ObjectKey = "dbo.T_CHILD",
                FieldKey = string.Empty,
                PropertyName = string.Empty,
                ChangeKind = "AddRelation",
                CreatedAtUtc = DateTime.UtcNow,
                RelationPayload = new RelationStructurePayload
                {
                    ForeignKeyName = "FK_CHILD_PARENT",
                    ParentSchemaName = "dbo",
                    ParentTableName = "T_PARENT",
                    ParentFieldName = "FID",
                    ChildSchemaName = "dbo",
                    ChildTableName = "T_CHILD",
                    ChildFieldName = "FPARENTID"
                }
            };
        }

        /// <summary>XMZADD 20260901 创建结构发布证据集合以复用权限和安全边界测试。</summary>
        private static IList<EvidenceItem> CreateEvidence(string path, string explanation)
        {
            return new List<EvidenceItem>
            {
                new EvidenceItem
                {
                    SourceType = "EOS源码",
                    SourcePath = path,
                    SourceLine = 1,
                    RuleName = "EntityProperty",
                    Explanation = explanation
                }
            };
        }

        private static bool HasError(DictionaryChangeValidationResult result, string code)
        {
            return CountErrors(result, code) > 0;
        }

        private static int CountErrors(DictionaryChangeValidationResult result, string code)
        {
            int count = 0;
            for (int index = 0; index < result.Errors.Count; index++)
            {
                if (string.Equals(result.Errors[index].Code, code, StringComparison.Ordinal))
                {
                    count++;
                }
            }
            return count;
        }

        private static string JoinErrors(DictionaryChangeValidationResult result)
        {
            string value = string.Empty;
            for (int index = 0; index < result.Errors.Count; index++)
            {
                value += result.Errors[index].Code + ":" + result.Errors[index].Message + ";";
            }
            return value;
        }
    }
}
