namespace DBSyncTool.Helpers
{
    /// <summary>
    /// SQL for the STREAM route's index handling: the enabled nonclustered rowstore indexes of
    /// the (just emptied) AxDB table are disabled before the bulk load and rebuilt afterwards -
    /// maintaining dozens of D365 indexes row by row is what makes large loads disk-bound.
    ///
    /// Marker: D365 itself keeps some nonclustered indexes DISABLED on purpose (indexes of
    /// switched-off configuration keys, index-tuning indexes). Those must never be rebuilt. So
    /// every index the route disables first gets an extended property
    /// <see cref="MarkerName"/> (value = UTC time) - stored in the database, it survives a killed
    /// process. A disabled index WITH the marker is a leftover of an interrupted run and is
    /// rebuilt; a disabled index WITHOUT it belongs to D365 and is never touched. The marker is
    /// dropped after a successful rebuild and kept when the rebuild fails.
    /// Pure, shared by AxDbDataService.StreamInsertAsync (runtime) and SqlPreviewBuilder (Get SQL).
    /// </summary>
    public static class StreamIndexSql
    {
        /// <summary>Extended property set on an index while DBSyncTool has it disabled for a load.</summary>
        public const string MarkerName = "DBSyncTool_DisabledForLoad";

        /// <summary>
        /// Per index, generous: a rebuild must never be cut short (the index would stay
        /// disabled), but a lock wait must not hang a worker forever either.
        /// </summary>
        public const int RebuildCommandTimeoutSeconds = 4 * 60 * 60;

        /// <summary>
        /// Nonclustered rowstore indexes (type 2, not hypothetical) with: disabled flag, unique
        /// flag, whether a foreign key references the index (disabling it would disable the FK),
        /// the key columns (for the duplicate check in an error message) and whether the index
        /// carries the DBSyncTool marker (extended property, class 7 = index).
        /// Parameter @Table = N'[dbo].[TABLE]'.
        /// </summary>
        public const string ListIndexesSql = @"SELECT i.name, i.is_disabled, i.is_unique,
       CASE WHEN EXISTS (SELECT 1 FROM sys.foreign_keys fk
                         WHERE fk.referenced_object_id = i.object_id AND fk.key_index_id = i.index_id)
            THEN 1 ELSE 0 END AS fk_referenced,
       (SELECT STRING_AGG(QUOTENAME(c.name), ', ') WITHIN GROUP (ORDER BY ic.key_ordinal)
          FROM sys.index_columns ic
          JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
         WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0) AS key_columns,
       CASE WHEN EXISTS (SELECT 1 FROM sys.extended_properties ep
                         WHERE ep.class = 7 AND ep.major_id = i.object_id AND ep.minor_id = i.index_id
                           AND ep.name = N'" + MarkerName + @"')
            THEN 1 ELSE 0 END AS dbsynctool_marked
  FROM sys.indexes i
 WHERE i.object_id = OBJECT_ID(@Table) AND i.type = 2 AND i.is_hypothetical = 0
 ORDER BY i.index_id";

        /// <summary>
        /// Nonclustered columnstore indexes (type 6) of the table with: disabled flag, whether
        /// they carry the DBSyncTool marker, and whether the table has an ENABLED nonclustered
        /// rowstore index containing a rowversion column (SYSROWVERSION, system_type_id 189, key
        /// or included). That combination makes SqlBulkCopy fail on SQL Server 2019 (assertion
        /// 3624 / error 17066 in xmerror.cpp, the session is killed) - with or without TableLock,
        /// with any row count; INSERT ... SELECT is not affected. D365 creates such tables itself
        /// (e.g. GENERALJOURNALACCOUNTENTRY: I_7581COLUMNSTOREIDX + the dbo&lt;TABLE&gt;&lt;GUID&gt;
        /// SYSROWVERSION indexes). Disabling the columnstore index for the load avoids it.
        /// Parameter @Table = N'[dbo].[TABLE]'.
        /// </summary>
        public const string ListColumnstoreSql = @"SELECT i.name, i.is_disabled,
       CASE WHEN EXISTS (SELECT 1 FROM sys.extended_properties ep
                         WHERE ep.class = 7 AND ep.major_id = i.object_id AND ep.minor_id = i.index_id
                           AND ep.name = N'" + MarkerName + @"')
            THEN 1 ELSE 0 END AS dbsynctool_marked,
       CASE WHEN EXISTS (SELECT 1 FROM sys.indexes r
                           JOIN sys.index_columns ic ON ic.object_id = r.object_id AND ic.index_id = r.index_id
                           JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                         WHERE r.object_id = i.object_id AND r.type = 2 AND r.is_disabled = 0
                           AND r.is_hypothetical = 0 AND c.system_type_id = 189)
            THEN 1 ELSE 0 END AS rowversion_index_enabled
  FROM sys.indexes i
 WHERE i.object_id = OBJECT_ID(@Table) AND i.type = 6 AND i.is_hypothetical = 0
 ORDER BY i.index_id";

        public static string QualifiedTable(string tableName) => $"[dbo].{Quote(tableName)}";

        public static string Quote(string name) => "[" + name.Replace("]", "]]") + "]";

        private static string Literal(string value) => "N'" + value.Replace("'", "''") + "'";

        private static string MarkerTarget(string tableName, string indexName) =>
            $"@level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = {Literal(tableName)}, " +
            $"@level2type = N'INDEX', @level2name = {Literal(indexName)}";

        /// <summary>Sets the marker (run BEFORE the DISABLE, so a disabled index always has it).</summary>
        public static string AddMarker(string tableName, string indexName, string utcTimestamp) =>
            $"EXEC sys.sp_addextendedproperty @name = N'{MarkerName}', @value = {Literal(utcTimestamp)}, {MarkerTarget(tableName, indexName)}";

        /// <summary>Removes the marker (after a successful rebuild, or a stale one on an enabled index).</summary>
        public static string DropMarker(string tableName, string indexName) =>
            $"EXEC sys.sp_dropextendedproperty @name = N'{MarkerName}', {MarkerTarget(tableName, indexName)}";

        public static string Disable(string tableName, string indexName) =>
            $"ALTER INDEX {Quote(indexName)} ON {QualifiedTable(tableName)} DISABLE";

        public static string Rebuild(string tableName, string indexName) =>
            $"ALTER INDEX {Quote(indexName)} ON {QualifiedTable(tableName)} REBUILD";

        /// <summary>Rows that violate a unique index (shown in the error when its rebuild fails).</summary>
        public static string DuplicateCheck(string tableName, string keyColumns) =>
            $"SELECT {keyColumns}, COUNT(*) AS [ROWS] FROM {QualifiedTable(tableName)} GROUP BY {keyColumns} HAVING COUNT(*) > 1";
    }
}
