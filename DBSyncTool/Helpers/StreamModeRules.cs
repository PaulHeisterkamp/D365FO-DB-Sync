using DBSyncTool.Models;

namespace DBSyncTool.Helpers
{
    /// <summary>
    /// Decides whether a table takes the STREAM route: TRUNCATE the AxDB table, then pipe the
    /// Tier2 rows from a SqlDataReader straight into SqlBulkCopy, without loading the table into
    /// a DataTable. Only used where the final AxDB content is exactly the fetched set anyway, so
    /// the result is identical to the in-memory route - only the memory use differs.
    /// Pure function of <see cref="TableInfo"/>: used by CopyOrchestrator.ProcessSingleTableAsync
    /// (runtime), SqlPreviewBuilder (Get SQL) and the grid's Strategy column, which therefore
    /// always agree.
    /// </summary>
    public static class StreamModeRules
    {
        /// <summary>Stream when at least this many rows are expected ...</summary>
        public const long MinRowsToStream = 1_000_000;

        /// <summary>... or at least this many MB (BytesPerRow x expected rows).</summary>
        public const decimal MinSizeMBToStream = 500m;

        /// <summary>Progress line in the log every N streamed rows (SqlBulkCopy.NotifyAfter).</summary>
        public const int ProgressEveryRows = 1_000_000;

        /// <summary>
        /// Rows the table is expected to deliver: the whole table for System and SQL strategies
        /// (a custom query can return everything, so Tier2RowCount is the safe upper bound),
        /// MIN(records to copy, Tier2 rows) for a RecId strategy.
        /// </summary>
        public static long ExpectedRows(TableInfo table)
        {
            return table.StrategyType == CopyStrategyType.RecId
                ? Math.Min(Math.Max(table.RecordsToCopy, 0), table.Tier2RowCount)
                : table.Tier2RowCount;
        }

        /// <summary>
        /// Expected size in MB = BytesPerRow x expected rows (BytesPerRow = reserved pages incl.
        /// indexes / rows from sys.dm_db_partition_stats, the same measure as Est Size(MB)).
        /// </summary>
        public static decimal ExpectedSizeMB(TableInfo table)
        {
            long rows = ExpectedRows(table);
            return table.BytesPerRow > 0 && rows > 0
                ? (decimal)table.BytesPerRow * rows / 1_000_000m
                : 0m;
        }

        /// <summary>True when the expected data reaches one of the thresholds.</summary>
        public static bool IsLarge(TableInfo table)
        {
            return ExpectedRows(table) >= MinRowsToStream || ExpectedSizeMB(table) >= MinSizeMBToStream;
        }

        /// <summary>
        /// Eligibility (checked in this order, mirroring the routing System → KEEP → UseTruncate
        /// → optimized → standard):
        ///  - KEEP (-keep): never - it upserts and must keep the other AxDB rows
        ///  - System table: yes (always TRUNCATE + full copy)
        ///  - UseTruncate (-truncate / Force truncate mode): yes, any strategy (TRUNCATE + full fetch)
        ///  - optimized mode (both saved timestamps present): never - incremental only loads
        ///    RecId + SysRowVersion control data
        ///  - RecId strategy copying the whole table (records to copy >= Tier2 rows): yes - the
        ///    standard route ends with AxDB = fetched set (TRUNCATE or delta incl. the delete
        ///    below MinRecId). A saved MaxRecId does not matter: it only marks rows unchanged in
        ///    the delta comparison, the end state is the same.
        ///  - everything else (newest-N RecId copies, SQL strategies without -truncate): never
        /// and in every "yes" case only when <see cref="IsLarge"/>.
        /// </summary>
        public static bool ShouldStream(TableInfo table, out string reason)
        {
            reason = string.Empty;

            if (table.KeepExisting)
                return false;

            string why;
            if (table.StrategyType == CopyStrategyType.System)
            {
                why = "System table (TRUNCATE + whole table)";
            }
            else if (table.UseTruncate)
            {
                why = "-truncate / Force truncate mode (TRUNCATE + full fetch)";
            }
            else if (table.UseOptimizedMode)
            {
                return false;
            }
            else if (table.StrategyType == CopyStrategyType.RecId &&
                     table.Tier2RowCount > 0 &&
                     table.RecordsToCopy >= table.Tier2RowCount)
            {
                why = $"RecId strategy copies the whole table ({table.RecordsToCopy:N0} >= {table.Tier2RowCount:N0} Tier2 rows) " +
                      "and no saved timestamps - AxDB ends equal to the fetched set";
            }
            else
            {
                return false;
            }

            if (!IsLarge(table))
                return false;

            reason = $"{why}; expected {ExpectedRows(table):N0} rows / {ExpectedSizeMB(table):N0} MB " +
                     $"(threshold {MinRowsToStream:N0} rows or {MinSizeMBToStream:N0} MB)";
            return true;
        }

        public static bool ShouldStream(TableInfo table) => ShouldStream(table, out _);
    }
}
