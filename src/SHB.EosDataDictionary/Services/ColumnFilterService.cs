using System;
using System.Collections.Generic;
using SHB.EosDataDictionary.ViewModels;

namespace SHB.EosDataDictionary.Services
{
    /// <summary>XMZADD 20260828 为表、字段和关联关系列表提供按指定列执行的模糊筛选规则。</summary>
    public sealed class ColumnFilterService
    {
        /// <summary>XMZADD 20260828 判断表目录行是否满足全部启用的列筛选条件。</summary>
        public bool MatchesTable(TableDisplayModel row, IDictionary<string, string> filters)
        {
            if (row == null)
            {
                return false;
            }
            if (filters == null || filters.Count == 0)
            {
                return true;
            }

            foreach (KeyValuePair<string, string> filter in filters)
            {
                if (!Matches(GetTableValue(row, filter.Key), filter.Value))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>XMZADD 20260828 判断字段行是否满足全部启用的列筛选条件。</summary>
        public bool MatchesField(FieldDisplayModel row, IDictionary<string, string> filters)
        {
            if (row == null)
            {
                return false;
            }
            if (filters == null || filters.Count == 0)
            {
                return true;
            }

            foreach (KeyValuePair<string, string> filter in filters)
            {
                if (!Matches(GetFieldValue(row, filter.Key), filter.Value))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>XMZADD 20260828 判断关联关系行是否满足全部启用的列筛选条件。</summary>
        public bool MatchesRelation(RelationDisplayModel row, IDictionary<string, string> filters)
        {
            if (row == null)
            {
                return false;
            }
            if (filters == null || filters.Count == 0)
            {
                return true;
            }

            foreach (KeyValuePair<string, string> filter in filters)
            {
                if (!Matches(GetRelationValue(row, filter.Key), filter.Value))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>XMZADD 20260828 读取表显示模型中与筛选表头对应的列值。</summary>
        private static string GetTableValue(TableDisplayModel row, string columnName)
        {
            switch (columnName)
            {
                case "StatusText": return row.StatusText;
                case "ChineseName": return row.ChineseName;
                case "ObjectName": return row.ObjectName;
                case "ObjectTypeText": return row.ObjectTypeText;
                case "ModuleName": return row.ModuleName;
                case "EntityName": return row.EntityName;
                default: return string.Empty;
            }
        }

        /// <summary>XMZADD 20260828 读取字段显示模型中与筛选表头对应的列值。</summary>
        private static string GetFieldValue(FieldDisplayModel row, string columnName)
        {
            switch (columnName)
            {
                case "StatusText": return row.StatusText;
                case "FieldName": return row.FieldName;
                case "ChineseName": return row.ChineseName;
                case "OwnerTableName": return row.OwnerTableName;
                case "EntityPropertyName": return row.EntityPropertyName;
                case "DataType": return row.DataType;
                case "EnumText": return row.EnumText;
                case "LengthText": return row.LengthText;
                case "RequiredText": return row.RequiredText;
                case "KeyText": return row.KeyText;
                case "RelationText": return row.RelationText;
                default: return string.Empty;
            }
        }

        /// <summary>XMZADD 20260828 读取关联关系显示模型中与筛选表头对应的列值。</summary>
        private static string GetRelationValue(RelationDisplayModel row, string columnName)
        {
            switch (columnName)
            {
                case "StatusText": return row.StatusText;
                case "ParentTableName": return row.ParentTableName;
                case "ParentFieldName": return row.ParentFieldName;
                case "ChildTableName": return row.ChildTableName;
                case "ChildFieldName": return row.ChildFieldName;
                case "RelationType": return row.RelationType;
                default: return string.Empty;
            }
        }

        /// <summary>XMZADD 20260828 对单列值执行不区分大小写的包含匹配，空筛选条件视为未启用。</summary>
        private static bool Matches(string source, string filterText)
        {
            if (string.IsNullOrWhiteSpace(filterText))
            {
                return true;
            }
            return !string.IsNullOrEmpty(source) && source.IndexOf(filterText.Trim(), StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
