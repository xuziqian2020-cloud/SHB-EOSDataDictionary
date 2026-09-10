using System;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHB.EosDataDictionary.Models;
using SHB.EosDataDictionary.Services;

namespace SHB.EosDataDictionary.Tests
{
    /// <summary>XMZADD 20260901 验证共享字典事件、修订和缩写模型保持稳定且不携带本机凭据。</summary>
    [TestClass]
    public sealed class DictionarySyncModelTests
    {
        /// <summary>XMZADD 20260901 验证字典修改事件使用稳定业务键和全局唯一操作编号。</summary>
        [TestMethod]
        public void DictionaryChangeOperation_Create_UsesStableObjectAndPropertyKeys()
        {
            DictionaryChangeOperation operation = DictionaryChangeOperation.Create(
                "github-user-123", "dbo.T_BD_MATERIAL", "FNAME", "ChineseName", "物料名称");
            DictionaryChangeOperation anotherOperation = DictionaryChangeOperation.Create(
                "github-user-123", "dbo.T_BD_MATERIAL", "FNAME", "ChineseName", "物料名称");

            Assert.IsFalse(string.IsNullOrWhiteSpace(operation.OperationId));
            Assert.AreNotEqual(operation.OperationId, anotherOperation.OperationId);
            Assert.AreEqual("github-user-123", operation.AuthorGitHubUserId);
            Assert.AreEqual("dbo.T_BD_MATERIAL", operation.ObjectKey);
            Assert.AreEqual("FNAME", operation.FieldKey);
            Assert.AreEqual("ChineseName", operation.PropertyName);
            Assert.AreEqual("物料名称", operation.NewValue);
        }

        /// <summary>XMZADD 20260901 验证表级修改使用空字段键而不引入另一套定位规则。</summary>
        [TestMethod]
        public void DictionaryChangeOperation_Create_AllowsEmptyTableFieldKey()
        {
            DictionaryChangeOperation operation = DictionaryChangeOperation.Create(
                "github-user-123", "dbo.T_BD_MATERIAL", string.Empty, "ChineseName", "物料");

            Assert.AreEqual(string.Empty, operation.FieldKey);
        }

        /// <summary>XMZADD 20260901 验证同步模型的集合默认可直接用于传统循环。</summary>
        [TestMethod]
        public void DictionarySyncModels_Collections_DefaultToEmptyLists()
        {
            var batch = new DictionaryChangeBatch();
            var package = new DictionaryRevisionPackage();
            var abbreviation = new AbbreviationEntry();

            Assert.IsNotNull(batch.Overrides);
            Assert.IsNotNull(batch.Operations);
            Assert.IsNotNull(package.Batches);
            Assert.IsNotNull(abbreviation.Evidence);
            Assert.AreEqual(0, batch.Overrides.Count);
            Assert.AreEqual(0, batch.Operations.Count);
            Assert.AreEqual(0, package.Batches.Count);
            Assert.AreEqual(0, abbreviation.Evidence.Count);
        }

        /// <summary>XMZADD 20260901 验证新增共享契约可由数据契约序列化器往返保存。</summary>
        [TestMethod]
        public void SnapshotData_CanRoundTripWithDataContractSerializer()
        {
            var snapshot = new SnapshotData
            {
                FormatVersion = 1,
                Revision = 12L
            };
            snapshot.ExcludedObjects.Add(new ExcludedObjectRecord { ObjectKey = "dbo.T_LOG", Reason = "技术日志表" });
            snapshot.Abbreviations.Add(new AbbreviationEntry { Abbreviation = "MAT", ChineseMeaning = "物料" });

            var serializer = new DataContractSerializer(typeof(SnapshotData));
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, snapshot);
                stream.Position = 0;
                var restored = (SnapshotData)serializer.ReadObject(stream);

                Assert.AreEqual(1, restored.FormatVersion);
                Assert.AreEqual(12L, restored.Revision);
                Assert.AreEqual("dbo.T_LOG", restored.ExcludedObjects[0].ObjectKey);
                Assert.AreEqual("MAT", restored.Abbreviations[0].Abbreviation);
            }
        }

        /// <summary>XMZADD 20260901 验证共享数据契约不会意外承载凭据或本机绝对目录。</summary>
        [TestMethod]
        public void SharedDataContracts_DoNotExposeCredentialOrAbsolutePathFields()
        {
            Type[] contractTypes =
            {
                typeof(SnapshotData),
                typeof(TableMetadata),
                typeof(FieldMetadata),
                typeof(RelationMetadata),
                typeof(MetadataValue),
                typeof(EvidenceItem),
                typeof(DictionaryOverride),
                typeof(DictionaryChangeOperation),
                typeof(DictionaryChangeBatch),
                typeof(DictionaryRevisionPackage),
                typeof(DictionarySyncState),
                typeof(ExcludedObjectRecord),
                typeof(GitHubUserIdentity),
                typeof(SnapshotManifest),
                typeof(AbbreviationEntry),
                typeof(AbbreviationEvidence)
            };
            string[] forbiddenNameParts =
            {
                "Token",
                "Password",
                "ConnectionString",
                "AbsolutePath",
                "ApiKey",
                "Secret",
                "Credential"
            };

            for (int typeIndex = 0; typeIndex < contractTypes.Length; typeIndex++)
            {
                PropertyInfo[] properties = contractTypes[typeIndex].GetProperties();
                for (int propertyIndex = 0; propertyIndex < properties.Length; propertyIndex++)
                {
                    for (int forbiddenIndex = 0; forbiddenIndex < forbiddenNameParts.Length; forbiddenIndex++)
                    {
                        Assert.IsFalse(
                            properties[propertyIndex].Name.IndexOf(forbiddenNameParts[forbiddenIndex], StringComparison.OrdinalIgnoreCase) >= 0,
                            contractTypes[typeIndex].Name + "." + properties[propertyIndex].Name + " 不应出现在共享数据契约中。");
                    }
                }
            }
        }

        /// <summary>XMZADD 20260901 验证类型化表字段关系载荷经本地 JSON 往返后仍完整且集合可继续增量追加。</summary>
        [TestMethod]
        public void DictionaryJsonSerializer_StructurePayloads_RoundTripAsMutableCollections()
        {
            var batch = new DictionaryChangeBatch { BatchId = "payload_roundtrip" };
            var operation = new DictionaryChangeOperation
            {
                OperationId = "payload_1",
                ObjectKey = "dbo.T_CHILD",
                ChangeKind = "AddTable",
                TablePayload = new TableStructurePayload { SchemaName = "dbo", ObjectName = "T_CHILD", ObjectType = "TABLE" },
                FieldPayload = new FieldStructurePayload { FieldName = "FID", OwnerTableName = "T_CHILD", DataType = "int" },
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
            operation.TablePayload.Fields.Add(new FieldStructurePayload { FieldName = "FPARENTID", OwnerTableName = "T_CHILD" });
            operation.TablePayload.Relations.Add(operation.RelationPayload);
            batch.Operations.Add(operation);

            DictionaryChangeBatch restored = DictionaryJsonSerializer.DeserializeBatch(DictionaryJsonSerializer.SerializeBatch(batch));
            restored.Operations.Add(new DictionaryChangeOperation { OperationId = "payload_2" });
            restored.Operations[0].TablePayload.Fields.Add(new FieldStructurePayload { FieldName = "FNEW" });

            Assert.AreEqual("FID", restored.Operations[0].FieldPayload.FieldName);
            Assert.AreEqual("FK_CHILD_PARENT", restored.Operations[0].RelationPayload.ForeignKeyName);
            Assert.AreEqual(2, restored.Operations[0].TablePayload.Fields.Count);
            Assert.AreEqual(2, restored.Operations.Count);
        }
    }
}
