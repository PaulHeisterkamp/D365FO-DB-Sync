using DBSyncTool.Models;

namespace DBSyncTool.Helpers
{
    /// <summary>
    /// "Est Size(MB)" of a table before it is fetched (Discover Tables / re-apply on Process
    /// Tables). Pure function so the rule can be tested and reused.
    ///
    /// - RecId strategy, or a SQL strategy that uses @recordCount: MIN(records to copy, Tier2 rows).
    /// - SQL strategy without @recordCount: a filter of unknown selectivity - counting the whole
    ///   Tier2 table would inflate the total (a sold-items filter keeps ~2% of INVENTTABLE).
    ///   When the table is in sync (saved values → optimized mode, not -keep), the current AxDB
    ///   row count is used as a proxy for the filtered set; otherwise the size is unknown (null).
    /// The real size replaces the estimate as soon as the table has been fetched.
    /// </summary>
    public static class SizeEstimate
    {
        /// <returns>Estimated MB, or null when it cannot be estimated before the fetch.</returns>
        public static decimal? EstimateMB(
            CopyStrategyType strategyType,
            string? sqlTemplate,
            long recordsToCopy,
            long tier2Rows,
            long bytesPerRow,
            long? axDbRows,
            bool useOptimizedMode,
            bool keepExisting)
        {
            if (strategyType == CopyStrategyType.Sql &&
                (sqlTemplate == null || !sqlTemplate.Contains("@recordCount", StringComparison.OrdinalIgnoreCase)))
            {
                if (useOptimizedMode && !keepExisting && axDbRows is long axRows && axRows > 0)
                    return ToMB(bytesPerRow, Math.Min(axRows, tier2Rows));
                return null;
            }

            return ToMB(bytesPerRow, Math.Min(recordsToCopy, tier2Rows));
        }

        private static decimal ToMB(long bytesPerRow, long rows) =>
            bytesPerRow > 0 && rows > 0 ? (decimal)bytesPerRow * rows / 1_000_000m : 0m;
    }
}
