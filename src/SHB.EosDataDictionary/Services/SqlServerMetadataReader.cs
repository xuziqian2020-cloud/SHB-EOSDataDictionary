using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Threading;
using SHB.EosDataDictionary.Models;

namespace SHB.EosDataDictionary.Services
{
    /// <summary>XMZADD 20260901 只读扫描当前 EOS SQL Server 用户表、字段、近似行数和真实约束。</summary>
    public sealed class SqlServerMetadataReader
    {
        private const string ObjectSql = @"
SELECT s.name AS SchemaName, t.name AS ObjectName,
       'TABLE' AS ObjectType,
       CAST(ep.value AS nvarchar(4000)) AS ObjectDescription,
       COALESCE(rows.ApproximateRowCount, CAST(0 AS bigint)) AS ApproximateRowCount
FROM sys.tables t
INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
LEFT JOIN sys.extended_properties ep
    ON ep.class = 1 AND ep.major_id = t.object_id AND ep.minor_id = 0
       AND ep.name = 'MS_Description'
LEFT JOIN (
    SELECT object_id, SUM(row_count) AS ApproximateRowCount
    FROM sys.dm_db_partition_stats
    WHERE index_id IN (0, 1)
    GROUP BY object_id
) rows ON rows.object_id = t.object_id
WHERE t.is_ms_shipped = 0
ORDER BY s.name, t.name;";

        private const string ColumnSql = @"
SELECT s.name AS SchemaName, t.name AS ObjectName, c.column_id AS OrdinalPosition,
       c.name AS FieldName, ty.name AS DataType, c.max_length AS MaxLength,
       c.precision AS NumericPrecision, c.scale AS NumericScale,
       c.is_nullable AS IsNullable, c.is_identity AS IsIdentity,
       c.is_computed AS IsComputed,
       CASE WHEN pk.column_id IS NULL THEN 0 ELSE 1 END AS IsPrimaryKey,
       CAST(ep.value AS nvarchar(4000)) AS FieldDescription
FROM sys.tables t
INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
INNER JOIN sys.columns c ON c.object_id = t.object_id
INNER JOIN sys.types ty ON ty.user_type_id = c.user_type_id
LEFT JOIN sys.extended_properties ep
    ON ep.class = 1 AND ep.major_id = c.object_id AND ep.minor_id = c.column_id
       AND ep.name = 'MS_Description'
LEFT JOIN (
    SELECT ic.object_id, ic.column_id
    FROM sys.indexes i
    INNER JOIN sys.index_columns ic
        ON ic.object_id = i.object_id AND ic.index_id = i.index_id
    WHERE i.is_primary_key = 1
) pk ON pk.object_id = c.object_id AND pk.column_id = c.column_id
WHERE t.is_ms_shipped = 0
ORDER BY s.name, t.name, c.column_id;";

        private const string ForeignKeySql = @"
SELECT fk.name AS ForeignKeyName, ps.name AS ParentSchemaName,
       pt.name AS ParentTableName, pc.name AS ParentFieldName,
       cs.name AS ChildSchemaName, ct.name AS ChildTableName,
       cc.name AS ChildFieldName, fk.is_disabled AS IsDisabled,
       fk.is_not_trusted AS IsNotTrusted
FROM sys.foreign_keys fk
INNER JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
INNER JOIN sys.tables pt ON pt.object_id = fk.referenced_object_id
INNER JOIN sys.schemas ps ON ps.schema_id = pt.schema_id
INNER JOIN sys.columns pc ON pc.object_id = pt.object_id AND pc.column_id = fkc.referenced_column_id
INNER JOIN sys.tables ct ON ct.object_id = fk.parent_object_id
INNER JOIN sys.schemas cs ON cs.schema_id = ct.schema_id
INNER JOIN sys.columns cc ON cc.object_id = ct.object_id AND cc.column_id = fkc.parent_column_id
WHERE pt.is_ms_shipped = 0 AND ct.is_ms_shipped = 0
ORDER BY ps.name, pt.name, cs.name, ct.name, fk.name, fkc.constraint_column_id;";

        private const string UniqueColumnSql = @"
SELECT s.name AS SchemaName, t.name AS TableName, c.name AS FieldName
FROM sys.tables t
INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
INNER JOIN sys.indexes i ON i.object_id = t.object_id AND i.is_unique = 1
INNER JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
INNER JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
WHERE t.is_ms_shipped = 0;";

        /// <summary>XMZADD 20260828 以只读结构查询加载一个 EOS 数据库快照。</summary>
        public SnapshotData Read(ConnectionProfile profile)
        {
            return Read(profile, CancellationToken.None);
        }

        /// <summary>XMZADD 20260828 以可取消的只读查询加载 EOS 数据库快照，避免用户取消后继续占用连接。</summary>
        public SnapshotData Read(ConnectionProfile profile, CancellationToken cancellationToken)
        {
            if (profile == null)
            {
                throw new ArgumentNullException("profile");
            }

            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var snapshot = new SnapshotData { RefreshedAt = DateTime.Now };
                var tableMap = new Dictionary<string, TableMetadata>(StringComparer.OrdinalIgnoreCase);
                using (var connection = new SqlConnection(profile.BuildConnectionString()))
                {
                    connection.OpenAsync(cancellationToken).GetAwaiter().GetResult();
                    cancellationToken.ThrowIfCancellationRequested();
                    ReadObjects(connection, profile, snapshot, tableMap, cancellationToken);
                    ReadColumns(connection, profile, tableMap, cancellationToken);
                    HashSet<string> uniqueColumns = ReadUniqueColumns(connection, cancellationToken);
                    ReadRelations(connection, profile, tableMap, uniqueColumns, cancellationToken);
                }

                return snapshot;
            }
            catch (SqlException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }
                throw;
            }
        }

        private static void ReadObjects(SqlConnection connection, ConnectionProfile profile,
            SnapshotData snapshot, IDictionary<string, TableMetadata> tableMap, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (var command = CreateReadOnlyCommand(ObjectSql, connection))
            using (CancellationTokenRegistration registration = RegisterCancellation(command, cancellationToken))
            using (SqlDataReader reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string schemaName = reader["SchemaName"].ToString();
                    string objectName = reader["ObjectName"].ToString();
                    string description = reader["ObjectDescription"] == DBNull.Value ? string.Empty : reader["ObjectDescription"].ToString();
                    string key = MakeKey(schemaName, objectName);
                    TableMetadata table;
                    if (tableMap.TryGetValue(key, out table))
                    {
                        if (!string.IsNullOrWhiteSpace(description) &&
                            (table.ChineseName == null || table.ChineseName.Status == ConfidenceStatus.PendingConfirmation))
                        {
                            // 同一对象出现多条元数据时只补齐描述，避免本地快照收到重复主键。
                            table.ChineseName = CreateDatabaseValue(description);
                        }
                        continue;
                    }

                    table = new TableMetadata
                    {
                        ScopeKey = profile.ScopeKey,
                        SchemaName = schemaName,
                        ObjectName = objectName,
                        ObjectType = reader["ObjectType"].ToString(),
                        ChineseName = CreateDatabaseValue(description),
                        ModuleName = CreatePendingValue(),
                        EntityName = CreatePendingValue(),
                        ApproximateRowCount = Convert.ToInt64(reader["ApproximateRowCount"])
                    };
                    snapshot.Tables.Add(table);
                    tableMap[key] = table;
                }
            }
        }

        private static void ReadColumns(SqlConnection connection, ConnectionProfile profile,
            IDictionary<string, TableMetadata> tableMap, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (var command = CreateReadOnlyCommand(ColumnSql, connection))
            using (CancellationTokenRegistration registration = RegisterCancellation(command, cancellationToken))
            using (SqlDataReader reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string key = MakeKey(reader["SchemaName"].ToString(), reader["ObjectName"].ToString());
                    TableMetadata table;
                    if (!tableMap.TryGetValue(key, out table))
                    {
                        continue;
                    }

                    string typeName = reader["DataType"].ToString();
                    int maxLength = Convert.ToInt32(reader["MaxLength"]);
                    byte precision = Convert.ToByte(reader["NumericPrecision"]);
                    byte scale = Convert.ToByte(reader["NumericScale"]);
                    table.Fields.Add(new FieldMetadata
                    {
                        FieldName = reader["FieldName"].ToString(),
                        OwnerTableName = table.ObjectName,
                        ChineseName = CreateDatabaseValue(reader["FieldDescription"] == DBNull.Value ? string.Empty : reader["FieldDescription"].ToString()),
                        EntityPropertyName = CreatePendingValue(),
                        DataType = typeName,
                        LengthText = SqlTypeFormatter.FormatLength(typeName, maxLength, precision, scale),
                        IsRequired = !Convert.ToBoolean(reader["IsNullable"]),
                        IsPrimaryKey = Convert.ToBoolean(reader["IsPrimaryKey"]),
                        IsForeignKey = false,
                        EnumName = CreatePendingValue(),
                        RelationSummary = CreatePendingValue()
                    });
                }
            }
        }

        private static HashSet<string> ReadUniqueColumns(SqlConnection connection, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uniqueColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var command = CreateReadOnlyCommand(UniqueColumnSql, connection))
            using (CancellationTokenRegistration registration = RegisterCancellation(command, cancellationToken))
            using (SqlDataReader reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    uniqueColumns.Add(MakeKey(reader["SchemaName"].ToString(), reader["TableName"].ToString()) + "." + reader["FieldName"]);
                }
            }

            return uniqueColumns;
        }

        private static void ReadRelations(SqlConnection connection, ConnectionProfile profile,
            IDictionary<string, TableMetadata> tableMap, ISet<string> uniqueColumns, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (var command = CreateReadOnlyCommand(ForeignKeySql, connection))
            using (CancellationTokenRegistration registration = RegisterCancellation(command, cancellationToken))
            using (SqlDataReader reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string parentSchema = reader["ParentSchemaName"].ToString();
                    string parentTable = reader["ParentTableName"].ToString();
                    string parentField = reader["ParentFieldName"].ToString();
                    string childSchema = reader["ChildSchemaName"].ToString();
                    string childTable = reader["ChildTableName"].ToString();
                    string childField = reader["ChildFieldName"].ToString();
                    var input = new RelationInput
                    {
                        ParentSchemaName = parentSchema,
                        ParentTableName = parentTable,
                        ParentFieldName = parentField,
                        ChildSchemaName = childSchema,
                        ChildTableName = childTable,
                        ChildFieldName = childField,
                        HasRealForeignKey = true,
                        ChildForeignKeyIsUnique = uniqueColumns.Contains(MakeKey(childSchema, childTable) + "." + childField),
                        IsBridgeTable = false
                    };
                    RelationMetadata relation = RelationInferenceService.Infer(input);
                    relation.ScopeKey = profile.ScopeKey;
                    relation.ForeignKeyName = reader["ForeignKeyName"].ToString();
                    TableMetadata parent;
                    TableMetadata child;
                    if (tableMap.TryGetValue(MakeKey(parentSchema, parentTable), out parent))
                    {
                        parent.Relations.Add(relation);
                    }
                    if (tableMap.TryGetValue(MakeKey(childSchema, childTable), out child) && child != parent)
                    {
                        child.Relations.Add(relation);
                    }
                    MarkForeignKey(child, childField);
                }
            }
        }

        private static void MarkForeignKey(TableMetadata table, string fieldName)
        {
            if (table == null)
            {
                return;
            }

            for (int i = 0; i < table.Fields.Count; i++)
            {
                if (string.Equals(table.Fields[i].FieldName, fieldName, StringComparison.OrdinalIgnoreCase))
                {
                    table.Fields[i].IsForeignKey = true;
                    break;
                }
            }
        }

        /// <summary>XMZADD 20260828 在创建 EOS 数据库命令前强制校验只读规则，防止未来误接入写库 SQL。</summary>
        private static SqlCommand CreateReadOnlyCommand(string sql, SqlConnection connection)
        {
            ReadOnlySqlGuard.Validate(sql);
            return new SqlCommand(sql, connection);
        }

        /// <summary>XMZADD 20260828 将取消请求传递给正在执行的 SQL 命令以缩短结构查询占用时间。</summary>
        private static CancellationTokenRegistration RegisterCancellation(SqlCommand command, CancellationToken cancellationToken)
        {
            return cancellationToken.Register(() =>
            {
                try
                {
                    command.Cancel();
                }
                catch (InvalidOperationException)
                {
                    // 命令已结束时无需再次取消，用户的取消状态仍由令牌负责传递。
                }
            });
        }

        private static string MakeKey(string schemaName, string objectName)
        {
            return (schemaName ?? string.Empty) + "." + (objectName ?? string.Empty);
        }

        /// <summary>XMZADD 20260901 把 SQL Server 扩展说明标记为可审计数据库证据，避免共享事件误判为人工维护。</summary>
        private static MetadataValue CreateDatabaseValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return CreatePendingValue();
            }

            return new MetadataValue
            {
                Value = value,
                Status = ConfidenceStatus.DatabaseEvidence,
                SourceSummary = "SQL Server 扩展描述",
                Evidence = new List<EvidenceItem>
                {
                    new EvidenceItem
                    {
                        SourceType = "数据库说明",
                        SourcePath = string.Empty,
                        SourceLine = 0,
                        RuleName = "SqlExtendedDescription",
                        Explanation = "SQL Server 扩展说明"
                    }
                }
            };
        }

        private static MetadataValue CreatePendingValue()
        {
            return new MetadataValue
            {
                Value = "推测：待确认",
                Status = ConfidenceStatus.PendingConfirmation,
                SourceSummary = "当前没有明确描述",
                Evidence = new List<EvidenceItem>()
            };
        }
    }

    /// <summary>XMZADD 20260828 格式化 SQL Server 字段类型和长度。</summary>
    public static class SqlTypeFormatter
    {
        /// <summary>XMZADD 20260828 根据 SQL Server 类型元数据生成可读类型文本。</summary>
        public static string Format(string typeName, int maxLength, byte precision, byte scale)
        {
            string normalized = (typeName ?? string.Empty).ToLowerInvariant();
            if (normalized == "decimal" || normalized == "numeric")
            {
                return normalized + "(" + precision + "," + scale + ")";
            }

            if (normalized == "nvarchar" || normalized == "nchar" || normalized == "varchar" ||
                normalized == "char" || normalized == "varbinary" || normalized == "binary")
            {
                return normalized + "(" + FormatLength(normalized, maxLength, precision, scale) + ")";
            }

            return normalized;
        }

        /// <summary>XMZADD 20260828 将 SQL Server 字节长度转换成用户可读长度。</summary>
        public static string FormatLength(string typeName, int maxLength, byte precision, byte scale)
        {
            string normalized = (typeName ?? string.Empty).ToLowerInvariant();
            if (maxLength < 0)
            {
                return "MAX";
            }
            if (normalized == "nvarchar" || normalized == "nchar")
            {
                return (maxLength / 2).ToString();
            }
            if (normalized == "decimal" || normalized == "numeric")
            {
                return precision + "," + scale;
            }
            if (normalized == "varchar" || normalized == "char" || normalized == "varbinary" || normalized == "binary")
            {
                return maxLength.ToString();
            }
            return "—";
        }
    }
}
