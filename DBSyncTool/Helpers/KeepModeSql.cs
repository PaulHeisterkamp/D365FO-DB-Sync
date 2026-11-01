using DBSyncTool.Models;

namespace DBSyncTool.Helpers
{
    /// <summary>
    /// SQL of the KEEP route (strategy flag -keep): the fetched Tier2 rows are upserted by
    /// RecId and nothing else in the AxDB table is ever deleted or truncated. Shared by
    /// AxDbDataService.UpsertKeepExistingAsync (runtime) and SqlPreviewBuilder (Get SQL), so
    /// both always show the same statements.
    /// </summary>
    public static class KeepModeSql
    {
        /// <summary>Session temp table holding the fetched rows.</summary>
        public const string TempTable = "#keep";

        /// <summary>Number of conflicting rows written to the log.</summary>
        public const int ConflictSampleSize = 20;

        /// <summary>Upper bound for the conflict passes (see <see cref="DeleteConflicts"/>).</summary>
        public const int MaxConflictPasses = 10;

        /// <summary>
        /// True when only rows changed in Tier2 since the saved Tier2 timestamp are fetched:
        /// SQL strategy with @sysRowVersionFilter, a SYSROWVERSION column and a saved Tier2
        /// timestamp. Otherwise the whole strategy is fetched (@sysRowVersionFilter → (1 = 1),
        /// RecId strategy → TOP N).
        /// </summary>
        public static bool UsesIncrementalFetch(TableInfo table)
        {
            return table.StrategyType == CopyStrategyType.Sql &&
                   !string.IsNullOrWhiteSpace(table.SqlTemplate) &&
                   table.SqlTemplate.Contains("@sysRowVersionFilter", StringComparison.OrdinalIgnoreCase) &&
                   table.CopyableFields.Any(f => f.Equals("SYSROWVERSION", StringComparison.OrdinalIgnoreCase)) &&
                   table.StoredTier2Timestamp != null;
        }

        public static string ColumnList(IEnumerable<string> columns, string? alias = null)
        {
            string prefix = alias == null ? "" : alias + ".";
            return string.Join(", ", columns.Select(c => $"{prefix}[{c}]"));
        }

        /// <summary>Columns that cannot be inserted explicitly (rowversion/timestamp, computed).</summary>
        public const string NonInsertableColumnsQuery = @"
SELECT c.name
FROM sys.columns c
WHERE c.object_id = OBJECT_ID(@ObjectName)
  AND (c.system_type_id = 189 OR c.is_computed = 1)";

        /// <summary>Key columns of the enabled, unfiltered unique indexes of the table.</summary>
        public const string UniqueIndexesQuery = @"
SELECT i.name, c.name, i.has_filter
FROM sys.indexes i
INNER JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
INNER JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
WHERE i.object_id = OBJECT_ID(@ObjectName) AND i.is_unique = 1 AND i.is_disabled = 0 AND i.is_hypothetical = 0
ORDER BY i.name, ic.key_ordinal";

        /// <summary>Empty copy of the AxDB columns (keeps their types and collations).</summary>
        public static string CreateTempTable(string table, IReadOnlyList<string> columns)
        {
            return $"SELECT TOP (0) {ColumnList(columns)} INTO {TempTable} FROM [{table}]";
        }

        public static string IndexTempTable()
        {
            return $"CREATE CLUSTERED INDEX IX_keep_RECID ON {TempTable} ([RECID])";
        }

        private static string KeyEquals(IReadOnlyList<string> keyColumns)
        {
            return string.Join(" AND ", keyColumns.Select(c => $"t.[{c}] = k.[{c}]"));
        }

        /// <summary>
        /// t is a different AxDB row that stays (its RecId is not replaced from #keep), so a
        /// #keep row with the same unique key cannot be inserted next to it.
        /// </summary>
        private static string OtherLocalRow()
        {
            return $"t.[RECID] <> k.[RECID] AND NOT EXISTS (SELECT 1 FROM {TempTable} k2 WHERE k2.[RECID] = t.[RECID])";
        }

        /// <summary>Sample of the conflicts for one unique index (logged).</summary>
        public static string SelectConflicts(string table, IReadOnlyList<string> keyColumns, int top)
        {
            return $"SELECT TOP ({top}) k.[RECID], t.[RECID], {ColumnList(keyColumns, "k")} " +
                   $"FROM {TempTable} k INNER JOIN [{table}] t ON {KeyEquals(keyColumns)} WHERE {OtherLocalRow()}";
        }

        /// <summary>
        /// Drops the #keep rows that would violate a unique index because a different local
        /// row already has the same key. Repeated until a pass removes nothing: removing a row
        /// keeps its AxDB counterpart, which can in turn block another #keep row.
        /// </summary>
        public static string DeleteConflicts(string table, IReadOnlyList<string> keyColumns)
        {
            return $"DELETE k FROM {TempTable} k WHERE EXISTS (SELECT 1 FROM [{table}] t WHERE {KeyEquals(keyColumns)} AND {OtherLocalRow()})";
        }

        /// <summary>Removes the AxDB rows that are replaced (updated) from #keep - nothing else.</summary>
        public static string DeleteReplaced(string table)
        {
            return $"DELETE t FROM [{table}] t WHERE EXISTS (SELECT 1 FROM {TempTable} k WHERE k.[RECID] = t.[RECID])";
        }

        public static string Insert(string table, IReadOnlyList<string> columns)
        {
            string list = ColumnList(columns);
            return $"INSERT INTO [{table}] ({list}) SELECT {list} FROM {TempTable}";
        }

        public static string DropTempTable()
        {
            return $"DROP TABLE {TempTable}";
        }
    }
}
