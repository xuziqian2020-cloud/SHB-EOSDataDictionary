using System;
using System.Collections.Generic;
using SHB.EosDataDictionary.Models;

namespace SHB.EosDataDictionary.Services
{
    /// <summary>XMZADD 20260828 生成清晰标注为演示数据的 EOS 字典预览快照。</summary>
    public static class PreviewDataFactory
    {
        /// <summary>XMZADD 20260828 创建用于首次预览界面和交互流程的示例结构。</summary>
        public static SnapshotData Create()
        {
            var snapshot = new SnapshotData { RefreshedAt = DateTime.Now };
            TableMetadata order = CreateTable("采购管理", "采购订单", "T_PUR_ORDER", "Kis_T_PUR_ORDER", ConfidenceStatus.Confirmed);
            TableMetadata supplier = CreateTable("基础资料", "供应商", "T_BD_SUPPLIER", "Kis_T_BD_SUPPLIER", ConfidenceStatus.Confirmed);
            TableMetadata material = CreateTable("基础资料", "物料", "T_BD_MATERIAL", "Kis_T_BD_MATERIAL", ConfidenceStatus.CodeEvidence);
            TableMetadata inventory = CreateTable("仓储管理", "库存余额", "T_STK_INVENTORY", "Kis_T_STK_INVENTORY", ConfidenceStatus.Guessed);
            TableMetadata manufacture = CreateTable("生产制造", "生产工单", "T_MFG_ORDER", "推测：未匹配实体", ConfidenceStatus.Guessed);
            TableMetadata quality = CreateTable("质量管理", "质量检验记录", "T_QM_INSPECT", "推测：未匹配实体", ConfidenceStatus.Guessed);
            TableMetadata view = CreateTable("采购管理", "采购订单汇总视图", "V_PUR_ORDER_SUM", "推测：视图无实体", ConfidenceStatus.Guessed);
            view.ObjectType = "VIEW";

            AddField(order, "FID", "订单主键", "FId", "int", "—", true, true, ConfidenceStatus.Confirmed);
            AddField(order, "FNUMBER", "订单编号", "FNumber", "nvarchar", "30", true, false, ConfidenceStatus.CodeEvidence);
            AddField(order, "FSUPPLIERID", "供应商", "FSupplierId", "int", "—", true, false, ConfidenceStatus.CodeEvidence);
            FieldMetadata status = AddField(order, "FSTATUS", "状态（推测）", "FStatus", "int", "—", true, false, ConfidenceStatus.Guessed);
            status.EnumName = Value("PurchaseOrderStatus（推测）", ConfidenceStatus.Guessed, "源码枚举命名候选");
            status.EnumItems.Add(new EnumItemMetadata { Value = "1", ChineseName = Value("新建", ConfidenceStatus.CodeEvidence, "状态注释") });
            status.EnumItems.Add(new EnumItemMetadata { Value = "2", ChineseName = Value("已审核", ConfidenceStatus.CodeEvidence, "状态注释") });
            AddField(order, "FCREATEDATE", "创建日期", "FCreateDate", "datetime", "—", false, false, ConfidenceStatus.CodeEvidence);

            AddField(supplier, "FSUPPLIERID", "供应商主键", "Kis_T_BD_SUPPLIER.FSUPPLIERID", "int", "—", true, true, ConfidenceStatus.Confirmed);
            AddField(supplier, "FNUMBER", "供应商编码", "Kis_T_BD_SUPPLIER.FNUMBER", "nvarchar", "30", true, false, ConfidenceStatus.CodeEvidence);
            AddField(supplier, "FNAME", "供应商名称", "Kis_T_BD_SUPPLIER.FNAME", "nvarchar", "120", true, false, ConfidenceStatus.CodeEvidence);
            AddField(supplier, "FSTATUS", "推测：状态（FSTATUS）", "Kis_T_BD_SUPPLIER.FSTATUS", "int", "—", false, false, ConfidenceStatus.Guessed);

            AddField(material, "FMATERIALID", "物料主键", "Kis_T_BD_MATERIAL.FMATERIALID", "int", "—", true, true, ConfidenceStatus.CodeEvidence);
            AddField(material, "FNUMBER", "物料编码", "Kis_T_BD_MATERIAL.FNUMBER", "nvarchar", "80", true, false, ConfidenceStatus.CodeEvidence);
            AddField(material, "FNAME", "物料名称", "Kis_T_BD_MATERIAL.FNAME", "nvarchar", "200", true, false, ConfidenceStatus.CodeEvidence);
            AddField(material, "FUNITID", "推测：单位编号（FUNITID）", "Kis_T_BD_MATERIAL.FUNITID", "int", "—", false, false, ConfidenceStatus.Guessed);

            AddField(inventory, "FID", "库存记录主键", "Kis_T_STK_INVENTORY.FID", "bigint", "—", true, true, ConfidenceStatus.CodeEvidence);
            AddField(inventory, "FMATERIALID", "物料编号", "Kis_T_STK_INVENTORY.FMATERIALID", "int", "—", true, false, ConfidenceStatus.CodeEvidence);
            AddField(inventory, "FQTY", "数量", "Kis_T_STK_INVENTORY.FQTY", "decimal", "18,6", false, false, ConfidenceStatus.Guessed);

            AddField(manufacture, "FID", "工单主键", "推测：未匹配实体.FID", "bigint", "—", true, true, ConfidenceStatus.Guessed);
            AddField(manufacture, "FNUMBER", "推测：编号（FNUMBER）", "推测：未匹配实体.FNUMBER", "nvarchar", "50", true, false, ConfidenceStatus.Guessed);
            AddField(manufacture, "FMATERIALID", "物料编号", "推测：未匹配实体.FMATERIALID", "int", "—", true, false, ConfidenceStatus.Guessed);

            AddField(quality, "FID", "检验记录主键", "推测：未匹配实体.FID", "bigint", "—", true, true, ConfidenceStatus.Guessed);
            AddField(quality, "FRESULT", "推测：结果（FRESULT）", "推测：未匹配实体.FRESULT", "int", "—", false, false, ConfidenceStatus.Guessed);
            AddField(quality, "FCREATEDATE", "创建日期", "推测：未匹配实体.FCREATEDATE", "datetime", "—", false, false, ConfidenceStatus.Guessed);

            AddField(view, "FORDERNUMBER", "订单编号", "推测：视图无实体.FORDERNUMBER", "nvarchar", "30", false, false, ConfidenceStatus.Guessed);
            AddField(view, "FSUPPLIERNAME", "供应商名称", "推测：视图无实体.FSUPPLIERNAME", "nvarchar", "120", false, false, ConfidenceStatus.Guessed);
            AddField(view, "FAMOUNT", "推测：金额（FAMOUNT）", "推测：视图无实体.FAMOUNT", "decimal", "18,2", false, false, ConfidenceStatus.Guessed);

            AddRelation(order, supplier, "FSUPPLIERID", "FSUPPLIERID", "FK_PUR_ORDER_SUPPLIER", "一对多", ConfidenceStatus.Confirmed);
            AddRelation(order, material, "FMATERIALID", "FMATERIALID", "FK_PUR_ORDER_MATERIAL", "一对多", ConfidenceStatus.CodeEvidence);
            AddRelation(material, inventory, "FMATERIALID", "FMATERIALID", "推测：字段名关联", "推测：一对多", ConfidenceStatus.Guessed);
            AddRelation(material, manufacture, "FMATERIALID", "FMATERIALID", "推测：字段名关联", "推测：一对多", ConfidenceStatus.Guessed);

            snapshot.Tables.Add(order);
            snapshot.Tables.Add(supplier);
            snapshot.Tables.Add(material);
            snapshot.Tables.Add(inventory);
            snapshot.Tables.Add(manufacture);
            snapshot.Tables.Add(quality);
            snapshot.Tables.Add(view);
            return snapshot;
        }

        private static TableMetadata CreateTable(string module, string chineseName, string objectName, string entity, ConfidenceStatus status)
        {
            return new TableMetadata
            {
                ScopeKey = "preview|EOS演示库",
                SchemaName = "dbo",
                ObjectName = objectName,
                ObjectType = "TABLE",
                ModuleName = Value(module, status, "演示数据"),
                ChineseName = Value(chineseName, status, "演示数据"),
                EntityName = Value(entity, status, "演示数据")
            };
        }

        private static FieldMetadata AddField(TableMetadata table, string fieldName, string chineseName,
            string entityProperty, string dataType, string length, bool required, bool primaryKey, ConfidenceStatus status)
        {
            var field = new FieldMetadata
            {
                FieldName = fieldName,
                OwnerTableName = table.ObjectName,
                ChineseName = Value(chineseName, status, "演示数据"),
                EntityPropertyName = Value(entityProperty, status, "演示数据"),
                DataType = dataType,
                LengthText = length,
                IsRequired = required,
                IsPrimaryKey = primaryKey,
                IsForeignKey = false,
                EnumName = Value("未发现明确枚举", ConfidenceStatus.PendingConfirmation, "演示数据"),
                RelationSummary = Value("推测：未发现明确关联", ConfidenceStatus.Guessed, "演示数据")
            };
            table.Fields.Add(field);
            return field;
        }

        private static void AddRelation(TableMetadata parent, TableMetadata child, string parentField,
            string childField, string foreignKeyName, string type, ConfidenceStatus status)
        {
            var relation = new RelationMetadata
            {
                ScopeKey = "preview|EOS演示库",
                ForeignKeyName = foreignKeyName,
                ParentTableName = parent.ObjectName,
                ParentFieldName = parentField,
                ChildTableName = child.ObjectName,
                ChildFieldName = childField,
                RelationType = Value(type, status, "演示数据")
            };
            parent.Relations.Add(relation);
            child.Relations.Add(relation);
            MarkForeignKey(parent, parentField);
            MarkForeignKey(child, childField);
        }

        private static void MarkForeignKey(TableMetadata table, string fieldName)
        {
            for (int i = 0; i < table.Fields.Count; i++)
            {
                if (string.Equals(table.Fields[i].FieldName, fieldName, StringComparison.OrdinalIgnoreCase))
                {
                    table.Fields[i].IsForeignKey = true;
                }
            }
        }

        private static MetadataValue Value(string value, ConfidenceStatus status, string source)
        {
            return new MetadataValue
            {
                Value = value,
                Status = status,
                SourceSummary = source,
                Evidence = new List<EvidenceItem>()
            };
        }
    }
}
