using System.Collections.Generic;
using SHB.EosDataDictionary.Models;

namespace SHB.EosDataDictionary.Services
{
    /// <summary>XMZADD 20260831 以单向游标枚举需要 AI 推测的表和字段，避免大快照按批次反复从头扫描。</summary>
    public sealed class AiInferenceTargetQueue
    {
        private readonly SnapshotData _snapshot;
        private int _tableIndex;
        private int _fieldIndex;

        /// <summary>XMZADD 20260831 为当前不可变快照建立轻量游标，不复制百万级字段集合。</summary>
        public AiInferenceTargetQueue(SnapshotData snapshot)
        {
            _snapshot = snapshot;
            _fieldIndex = -1;
        }

        /// <summary>XMZADD 20260831 从上次位置继续取得一批未知项，确保总扫描复杂度随表字段数量线性增长。</summary>
        public IList<AiInferenceTarget> Take(int batchSize)
        {
            var result = new List<AiInferenceTarget>();
            if (_snapshot == null || _snapshot.Tables == null || batchSize <= 0)
            {
                return result;
            }

            while (_tableIndex < _snapshot.Tables.Count && result.Count < batchSize)
            {
                TableMetadata table = _snapshot.Tables[_tableIndex];
                if (table == null)
                {
                    MoveToNextTable();
                    continue;
                }

                if (_fieldIndex < 0)
                {
                    _fieldIndex = 0;
                    if (IsTarget(table.ChineseName))
                    {
                        result.Add(new AiInferenceTarget(table, null));
                        if (result.Count >= batchSize)
                        {
                            break;
                        }
                    }
                }

                if (table.Fields != null)
                {
                    while (_fieldIndex < table.Fields.Count && result.Count < batchSize)
                    {
                        FieldMetadata field = table.Fields[_fieldIndex];
                        _fieldIndex++;
                        if (field != null && IsTarget(field.ChineseName))
                        {
                            result.Add(new AiInferenceTarget(table, field));
                        }
                    }
                    if (_fieldIndex < table.Fields.Count)
                    {
                        break;
                    }
                }

                MoveToNextTable();
            }
            return result;
        }

        /// <summary>XMZADD 20260831 推进到下一数据库对象并重置字段游标。</summary>
        private void MoveToNextTable()
        {
            _tableIndex++;
            _fieldIndex = -1;
        }

        /// <summary>XMZADD 20260831 仅把未确认或冲突项交给 AI，保护数据库、代码和人工结论。</summary>
        private static bool IsTarget(MetadataValue value)
        {
            if (value == null)
            {
                return true;
            }
            // 人工维护和锁定结论必须跳过，避免 AI 再次推测干扰已确认业务语义。
            if (value.IsManualOverride || value.IsLocked)
            {
                return false;
            }

            return value.Status == ConfidenceStatus.PendingConfirmation || value.Status == ConfidenceStatus.Guessed ||
                   value.Status == ConfidenceStatus.GuessedConflict;
        }
    }

    /// <summary>XMZADD 20260831 保存一项待推测内容及所属表上下文。</summary>
    public sealed class AiInferenceTarget
    {
        /// <summary>XMZADD 20260831 组合表级或字段级 AI 目标，字段为空时表示推测整张表。</summary>
        public AiInferenceTarget(TableMetadata table, FieldMetadata field)
        {
            Table = table;
            Field = field;
        }

        public TableMetadata Table { get; private set; }
        public FieldMetadata Field { get; private set; }
    }
}
