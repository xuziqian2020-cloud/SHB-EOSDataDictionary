using System.Collections.Generic;
using SHB.EosDataDictionary.Models;

namespace SHB.EosDataDictionary.Services
{
    /// <summary>XMZADD 20260828 根据 EOS 数据库约束推导关联类型并明确标出推测关系。</summary>
    public static class RelationInferenceService
    {
        /// <summary>XMZADD 20260828 生成父子表字段映射和关系类型。</summary>
        public static RelationMetadata Infer(RelationInput input)
        {
            string relationType;
            ConfidenceStatus status;

            if (input.HasRealForeignKey && input.ChildForeignKeyIsUnique)
            {
                relationType = "一对一";
                status = ConfidenceStatus.DatabaseEvidence;
            }
            else if (input.HasRealForeignKey && input.IsBridgeTable)
            {
                relationType = "多对多";
                status = ConfidenceStatus.Guessed;
            }
            else if (input.HasRealForeignKey)
            {
                relationType = "一对多";
                status = ConfidenceStatus.DatabaseEvidence;
            }
            else
            {
                relationType = "推测：字段命名可能表示关联";
                status = ConfidenceStatus.Guessed;
            }

            return new RelationMetadata
            {
                ParentSchemaName = input.ParentSchemaName,
                ParentTableName = input.ParentTableName,
                ParentFieldName = input.ParentFieldName,
                ChildSchemaName = input.ChildSchemaName,
                ChildTableName = input.ChildTableName,
                ChildFieldName = input.ChildFieldName,
                RelationType = new MetadataValue
                {
                    Value = relationType,
                    Status = status,
                    SourceSummary = input.HasRealForeignKey ? "SQL Server 结构约束" : "字段命名推测",
                    Evidence = new List<EvidenceItem>()
                }
            };
        }
    }
}
