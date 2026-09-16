using System.Collections.Generic;
using SHB.EosDataDictionary.Models;

namespace SHB.EosDataDictionary.Services
{
    /// <summary>XMZADD 20260916 根据数据库约束和相互独立的 EOS 代码证据推导关联置信级别。</summary>
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

        /// <summary>XMZADD 20260916 仅在实体关系与 JOIN 一致或两个独立业务文件 JOIN 一致时生成正式代码关系。</summary>
        public static RelationMetadata InferCodeRelation(RelationInput input, IList<EvidenceItem> evidence,
            bool hasEntityEvidence, int independentJoinFileCount)
        {
            bool isFormal = (hasEntityEvidence && independentJoinFileCount > 0) ||
                            independentJoinFileCount > 1;
            string sourceSummary;
            if (hasEntityEvidence && independentJoinFileCount > 0)
            {
                sourceSummary = "EOS 实体对象属性与业务 SQL JOIN 一致";
            }
            else if (independentJoinFileCount > 1)
            {
                sourceSummary = "两个独立业务文件的 SQL JOIN 一致";
            }
            else if (hasEntityEvidence)
            {
                sourceSummary = "仅发现 EOS 实体对象属性，等待业务 JOIN 交叉验证";
            }
            else
            {
                sourceSummary = "仅发现单个业务 SQL JOIN，作为参考关系";
            }

            var publishedEvidence = new List<EvidenceItem>();
            for (int index = 0; evidence != null && index < evidence.Count; index++)
            {
                if (evidence[index] != null)
                {
                    publishedEvidence.Add(evidence[index]);
                }
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
                    Value = isFormal ? "代码关联：多对一" : "参考关联：多对一",
                    Status = isFormal ? ConfidenceStatus.CodeEvidence : ConfidenceStatus.Guessed,
                    ConfidenceScore = isFormal ? 95 : 70,
                    SourceType = "EOS代码关系",
                    SourceSummary = sourceSummary,
                    Evidence = publishedEvidence
                }
            };
        }
    }
}
