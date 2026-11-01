using System.Data;
using System.Diagnostics;
using Microsoft.Data.SqlClient;
using DBSyncTool.Helpers;
using DBSyncTool.Models;

namespace DBSyncTool.Services
{
    public class AxDbDataService
    {
        private const int DELETE_BATCH_SIZE = 5000;
        public const int SEQUENCE_GAP = 10000;

        // Enable/disable-trigger DDL acquires a schema lock. On a live AxDB another session can
        // hold a lock on the table, which would block the ALTER. Cap it so a worker can never
        // hang forever on trigger toggling (the insert itself still uses the configured timeout).
        private const int TriggerCommandTimeoutSeconds = 120;

        private readonly ConnectionSettings _connectionSettings;
        private readonly string _connectionString;
        private readonly Action<string> _logger;

        public AxDbDataService(ConnectionSettings connectionSettings, Action<string> logger)
        {
            _connectionSettings = connectionSettings;
            _connectionString = connectionSettings.BuildConnectionString(isAzure: false);
            _logger = logger;
        }

        /// <summary>
        /// Gets the connection string for AxDB
        /// </summary>
        public string GetConnectionString() => _connectionString;

        /// <summary>
        /// Tests the connection to AxDB database
        /// </summary>
        public async Task<bool> TestConnectionAsync()
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();
            return connection.State == ConnectionState.Open;
        }

        /// <summary>
        /// Gets the TableID for a table from SQLDICTIONARY
        /// </summary>
        public async Task<int?> GetTableIdAsync(string tableName)
        {
            const string query = @"
                SELECT TableID
                FROM SQLDICTIONARY
                WHERE UPPER(name) = UPPER(@TableName)
                  AND FIELDID = 0";

            _logger($"[AxDB SQL] Getting TableID for {tableName}");

            using var connection = new SqlConnection(_connectionString);
            using var command = new SqlCommand(query, connection);
            command.Parameters.AddWithValue("@TableName", tableName);
            command.CommandTimeout = _connectionSettings.CommandTimeout;

            await connection.OpenAsync();
            var result = await command.ExecuteScalarAsync();

            return result != null ? Convert.ToInt32(result) : null;
        }

        /// <summary>
        /// Gets the field names for a table from SQLDICTIONARY
        /// </summary>
        public async Task<List<string>> GetTableFieldsAsync(int tableId)
        {
            const string query = @"
                SELECT SQLName
                FROM SQLDICTIONARY
                WHERE TableID = @TableId
                  AND FIELDID <> 0";

            _logger($"[AxDB SQL] Getting fields for TableID {tableId}");

            var fields = new List<string>();

            using var connection = new SqlConnection(_connectionString);
            using var command = new SqlCommand(query, connection);
            command.Parameters.AddWithValue("@TableId", tableId);
            command.CommandTimeout = _connectionSettings.CommandTimeout;

            await connection.OpenAsync();
            using var reader = await command.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                fields.Add(reader.GetString(0));
            }

            return fields;
        }

        /// <summary>
        /// Gets the physical column names for many tables in one query from
        /// INFORMATION_SCHEMA.COLUMNS. Used for System tables that are not present in
        /// SQLDICTIONARY. Returned dictionary is keyed by UPPER table name; tables that do
        /// not exist in AxDB are simply absent from the result.
        /// </summary>
        public async Task<Dictionary<string, List<string>>> GetTablesColumnsAsync(IEnumerable<string> tableNames)
        {
            return await Tier2DataService.GetTablesColumnsAsync(_connectionString, _connectionSettings.CommandTimeout, tableNames);
        }

        /// <summary>
        /// Inserts data for a System table: TRUNCATE (with DELETE fallback) then bulk insert
        /// all rows. No delta comparison, no RecId/sequence handling, no timestamps.
        /// Triggers are disabled around the operation and always re-enabled.
        /// </summary>
        public async Task<int> InsertSystemTableDataAsync(TableInfo tableInfo, CancellationToken cancellationToken)
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            // Step 1: Disable triggers (harmless if the table has none)
            string disableTriggersSql = $"ALTER TABLE [{tableInfo.TableName}] DISABLE TRIGGER ALL";
            _logger($"[AxDB SQL] {disableTriggersSql}");
            await ExecuteNonQueryAsync(disableTriggersSql, connection, null, cancellationToken, TriggerCommandTimeoutSeconds);

            try
            {
                // Step 2: Truncate (falls back to DELETE if referenced by FK/view)
                var deleteStopwatch = Stopwatch.StartNew();
                await TruncateWithFallbackAsync(tableInfo.TableName, connection, null, cancellationToken);
                deleteStopwatch.Stop();
                tableInfo.DeleteTimeSeconds = (decimal)deleteStopwatch.Elapsed.TotalSeconds;

                // Step 3: Bulk insert all fetched rows
                int rowCount = 0;
                var insertStopwatch = Stopwatch.StartNew();
                if (tableInfo.CachedData != null && tableInfo.CachedData.Rows.Count > 0)
                {
                    _logger($"[AxDB] Bulk inserting {tableInfo.CachedData.Rows.Count} rows into {tableInfo.TableName}");
                    using var bulkCopy = new SqlBulkCopy(connection);
                    bulkCopy.DestinationTableName = tableInfo.TableName;
                    bulkCopy.BatchSize = 10000;
                    bulkCopy.BulkCopyTimeout = _connectionSettings.CommandTimeout;

                    foreach (DataColumn column in tableInfo.CachedData.Columns)
                    {
                        bulkCopy.ColumnMappings.Add(column.ColumnName, column.ColumnName);
                    }

                    await bulkCopy.WriteToServerAsync(tableInfo.CachedData, cancellationToken);
                    rowCount = tableInfo.CachedData.Rows.Count;
                }
                insertStopwatch.Stop();
                tableInfo.InsertTimeSeconds = (decimal)insertStopwatch.Elapsed.TotalSeconds;

                return rowCount;
            }
            finally
            {
                // Always re-enable triggers, even on error/cancellation
                try
                {
                    await ExecuteNonQueryAsync($"ALTER TABLE [{tableInfo.TableName}] ENABLE TRIGGER ALL", connection, null,
                        CancellationToken.None, TriggerCommandTimeoutSeconds);
                }
                catch
                {
                    // Ignore errors when re-enabling triggers
                }
            }
        }

        /// <summary>Outcome of <see cref="StreamInsertAsync"/> (index handling, for the log / timings).</summary>
        public sealed class StreamInsertResult
        {
            public int IndexesDisabled { get; set; }          // disabled by this run
            public int IndexesPreDisabled { get; set; }       // disabled + DBSyncTool marker (interrupted run) - rebuilt too
            public int IndexesLeftDisabled { get; set; }      // disabled without marker = D365's own, never touched
            public int IndexesKeptForForeignKeys { get; set; }
            public int IndexesRebuilt { get; set; }
            public double DisableSeconds { get; set; }
            public double RebuildSeconds { get; set; }
        }

        /// <summary>
        /// Thrown when a nonclustered index disabled for a STREAM load cannot be rebuilt (e.g. a
        /// unique index finds duplicates). The message names the index and the SQL to fix it.
        /// </summary>
        public sealed class IndexRebuildException : Exception
        {
            public IndexRebuildException(string message, Exception? inner) : base(message, inner) { }
        }

        private sealed record StreamIndex(string Name, bool IsUnique, string KeyColumns);

        /// <summary>
        /// STREAM route: disable triggers → TRUNCATE (DELETE fallback) → disable the enabled
        /// nonclustered indexes (not those referenced by a foreign key) → SqlBulkCopy straight
        /// from <paramref name="source"/> (a streaming Tier2 reader, never materialized) →
        /// REBUILD exactly the disabled indexes (incl. ones left disabled by an earlier crash) →
        /// enable triggers → optional sequence update.
        /// The index rebuild and the trigger re-enable run in EVERY outcome (success, AxDB or
        /// Tier2 error, Stop) without the cancellation token, on a fresh connection when the bulk
        /// copy left the original one unusable. A failed rebuild ends the table with an
        /// <see cref="IndexRebuildException"/> (other indexes are still rebuilt).
        /// No transaction: like the in-memory bulk insert, every 10,000-row batch commits on its
        /// own, so a failure leaves a partial table - a retry starts with the TRUNCATE again.
        /// TableLock (bulk update lock on the freshly truncated table, allows minimal logging)
        /// and BulkCopyTimeout 0 (the duration is set by the Tier2 read speed, not by AxDB).
        /// Sets DeleteTimeSeconds (TRUNCATE) and InsertTimeSeconds (index disable + bulk copy
        /// incl. the time spent waiting for Tier2 - the caller splits that off - + rebuild + sequence).
        /// </summary>
        public async Task<StreamInsertResult> StreamInsertAsync(
            TableInfo tableInfo,
            IDataReader source,
            bool updateSequence,
            Action<long>? progress,
            CancellationToken cancellationToken)
        {
            string t = tableInfo.TableName;
            var result = new StreamInsertResult();
            var toRebuild = new List<StreamIndex>();
            var connection = new SqlConnection(_connectionString);

            try
            {
                await connection.OpenAsync(cancellationToken);

                string disableTriggersSql = $"ALTER TABLE [{t}] DISABLE TRIGGER ALL";
                _logger($"[AxDB SQL] {disableTriggersSql}");
                await ExecuteNonQueryAsync(disableTriggersSql, connection, null, cancellationToken, TriggerCommandTimeoutSeconds);

                Exception? failure = null;
                var insertStopwatch = new Stopwatch();
                try
                {
                    var deleteStopwatch = Stopwatch.StartNew();
                    await TruncateWithFallbackAsync(t, connection, null, cancellationToken);
                    deleteStopwatch.Stop();
                    tableInfo.DeleteTimeSeconds = (decimal)deleteStopwatch.Elapsed.TotalSeconds;

                    insertStopwatch.Start();
                    await DisableNonclusteredIndexesAsync(t, connection, toRebuild, result, cancellationToken);

                    _logger($"[AxDB] Streaming bulk insert into {t} (TableLock, batch size 10,000, no transaction)");
                    using (var bulkCopy = new SqlBulkCopy(connection, SqlBulkCopyOptions.TableLock, null))
                    {
                        bulkCopy.DestinationTableName = t;
                        bulkCopy.BatchSize = 10000;
                        bulkCopy.BulkCopyTimeout = 0;
                        bulkCopy.EnableStreaming = true;

                        for (int i = 0; i < source.FieldCount; i++)
                        {
                            string name = source.GetName(i);
                            bulkCopy.ColumnMappings.Add(name, name);
                        }

                        if (progress != null)
                        {
                            bulkCopy.NotifyAfter = StreamModeRules.ProgressEveryRows;
                            bulkCopy.SqlRowsCopied += (_, e) => progress(e.RowsCopied);
                        }

                        await bulkCopy.WriteToServerAsync(source, cancellationToken);
                    }
                }
                catch (Exception ex)
                {
                    failure = ex;
                }

                // ---- Every outcome from here on: rebuild the disabled indexes, re-enable triggers ----
                var rebuildFailures = new List<string>();
                try
                {
                    if (failure != null || connection.State != ConnectionState.Open)
                    {
                        // An aborted bulk copy can leave the connection unusable: close it for good
                        // (ClearPool ends the server session) and continue on a fresh one
                        try { SqlConnection.ClearPool(connection); } catch { }
                        connection.Dispose();
                        connection = new SqlConnection(_connectionString);
                        await connection.OpenAsync(CancellationToken.None);
                    }

                    if (toRebuild.Count > 0)
                    {
                        if (failure != null)
                            _logger($"[AxDB] {t}: rebuilding {toRebuild.Count} disabled indexes after cancel/failure");
                        rebuildFailures = await RebuildIndexesAsync(t, toRebuild, connection, result);
                    }

                    string enableTriggersSql = $"ALTER TABLE [{t}] ENABLE TRIGGER ALL";
                    _logger($"[AxDB SQL] {enableTriggersSql}");
                    await ExecuteNonQueryAsync(enableTriggersSql, connection, null, CancellationToken.None, TriggerCommandTimeoutSeconds);
                }
                catch (Exception cleanupEx) when (failure != null)
                {
                    // Keep the original error; say exactly what is left to do by hand
                    string left = toRebuild.Count > result.IndexesRebuilt
                        ? " Run for every disabled index: " + string.Join(" ; ", toRebuild.Select(i => StreamIndexSql.Rebuild(t, i.Name))) + " ;"
                        : "";
                    string message = $"Cleanup after the failed load of {t} failed ({cleanupEx.Message}).{left} ALTER TABLE [{t}] ENABLE TRIGGER ALL";
                    _logger($"[AxDB] ERROR: {message}");
                    rebuildFailures.Add(message);
                }

                if (failure != null)
                {
                    if (rebuildFailures.Count > 0 && failure is not OperationCanceledException && !cancellationToken.IsCancellationRequested)
                        throw new IndexRebuildException(failure.Message + " | " + string.Join(" | ", rebuildFailures), failure);
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
                }

                if (updateSequence)
                    await UpdateSequenceAsync(tableInfo, connection, null, cancellationToken);

                insertStopwatch.Stop();
                tableInfo.InsertTimeSeconds = (decimal)insertStopwatch.Elapsed.TotalSeconds;

                if (rebuildFailures.Count > 0)
                    throw new IndexRebuildException(string.Join(" | ", rebuildFailures), null);

                return result;
            }
            finally
            {
                connection.Dispose();
            }
        }

        /// <summary>
        /// Disables every enabled nonclustered rowstore index of the (empty) table except those a
        /// foreign key references. Each one first gets the DBSyncTool marker (extended property
        /// StreamIndexSql.MarkerName), then ALTER INDEX ... DISABLE. Disabled indexes WITH the
        /// marker are leftovers of an interrupted run and are rebuilt too; disabled indexes
        /// WITHOUT it are disabled by D365 on purpose (configuration keys, index tuning) and are
        /// never touched. A stale marker on an enabled index (interrupted between marker and
        /// DISABLE, or between REBUILD and marker drop) is removed first.
        /// <paramref name="toRebuild"/> is filled as it goes, so a failure halfway still rebuilds
        /// what was disabled.
        /// </summary>
        private async Task DisableNonclusteredIndexesAsync(string tableName, SqlConnection connection,
            List<StreamIndex> toRebuild, StreamInsertResult result, CancellationToken cancellationToken)
        {
            var sw = Stopwatch.StartNew();
            var candidates = new List<StreamIndex>();
            var keptForFk = new List<string>();
            var leftovers = new List<string>();
            var d365Disabled = new List<string>();
            var staleMarkers = new List<string>();

            using (var command = new SqlCommand(StreamIndexSql.ListIndexesSql, connection))
            {
                command.Parameters.AddWithValue("@Table", StreamIndexSql.QualifiedTable(tableName));
                command.CommandTimeout = TriggerCommandTimeoutSeconds;
                using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var index = new StreamIndex(reader.GetString(0), reader.GetBoolean(2), reader.IsDBNull(4) ? "" : reader.GetString(4));
                    bool disabled = reader.GetBoolean(1);
                    bool fkReferenced = reader.GetInt32(3) == 1;
                    bool marked = reader.GetInt32(5) == 1;

                    if (disabled && marked)
                    {
                        toRebuild.Add(index);
                        leftovers.Add(index.Name);
                    }
                    else if (disabled)
                    {
                        d365Disabled.Add(index.Name);
                    }
                    else
                    {
                        if (marked)
                            staleMarkers.Add(index.Name);
                        if (fkReferenced)
                            keptForFk.Add(index.Name);
                        else
                            candidates.Add(index);
                    }
                }
            }

            if (leftovers.Count > 0)
                _logger($"[AxDB] {tableName}: {leftovers.Count} nonclustered index(es) still disabled by an interrupted DBSyncTool run (marker {StreamIndexSql.MarkerName}) - rebuilt after the load too: {string.Join(", ", leftovers)}");
            if (d365Disabled.Count > 0)
                _logger($"[AxDB] {tableName}: left disabled (not disabled by DBSyncTool): {string.Join(", ", d365Disabled)}");
            if (keptForFk.Count > 0)
                _logger($"[AxDB] {tableName}: kept enabled (referenced by a foreign key): {string.Join(", ", keptForFk)}");

            foreach (var name in staleMarkers)
            {
                _logger($"[AxDB] {tableName}: removing stale marker from enabled index {name}");
                await ExecuteNonQueryAsync(StreamIndexSql.DropMarker(tableName, name), connection, null, cancellationToken, TriggerCommandTimeoutSeconds);
            }

            string stamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            foreach (var index in candidates)
            {
                // Marker first: from here on the index is ours, even if the process dies
                await ExecuteNonQueryAsync(StreamIndexSql.AddMarker(tableName, index.Name, stamp), connection, null, cancellationToken, TriggerCommandTimeoutSeconds);
                toRebuild.Add(index);
                await ExecuteNonQueryAsync(StreamIndexSql.Disable(tableName, index.Name), connection, null, cancellationToken, TriggerCommandTimeoutSeconds);
                result.IndexesDisabled++;
            }

            sw.Stop();
            result.IndexesPreDisabled = leftovers.Count;
            result.IndexesLeftDisabled = d365Disabled.Count;
            result.IndexesKeptForForeignKeys = keptForFk.Count;
            result.DisableSeconds = sw.Elapsed.TotalSeconds;
            _logger($"[AxDB] {tableName}: disabled {result.IndexesDisabled} nonclustered index(es) for the load in {sw.Elapsed.TotalSeconds:F2}s " +
                    $"(marker {StreamIndexSql.MarkerName} + ALTER INDEX ... DISABLE; rebuilt after the load)");
        }

        /// <summary>
        /// ALTER INDEX ... REBUILD for each index, one after the other, without the cancellation
        /// token (generous timeout), then its DBSyncTool marker is dropped (kept when the rebuild
        /// fails, so the next STREAM run rebuilds it). A connection lost on the way is replaced and the index
        /// retried once; any other error is collected (message names the index and the fix SQL)
        /// and the remaining indexes are still rebuilt.
        /// </summary>
        private async Task<List<string>> RebuildIndexesAsync(string tableName, List<StreamIndex> indexes,
            SqlConnection connection, StreamInsertResult result)
        {
            var failures = new List<string>();
            var sw = Stopwatch.StartNew();
            var current = connection;
            SqlConnection? replacement = null;

            try
            {
                foreach (var index in indexes)
                {
                    string sql = StreamIndexSql.Rebuild(tableName, index.Name);
                    for (int attempt = 1; ; attempt++)
                    {
                        var indexSw = Stopwatch.StartNew();
                        try
                        {
                            await ExecuteNonQueryAsync(sql, current, null, CancellationToken.None, StreamIndexSql.RebuildCommandTimeoutSeconds);
                            result.IndexesRebuilt++;
                            _logger($"[AxDB SQL] {sql}   ({indexSw.Elapsed.TotalSeconds:F2}s)");
                            try
                            {
                                await ExecuteNonQueryAsync(StreamIndexSql.DropMarker(tableName, index.Name), current, null, CancellationToken.None, TriggerCommandTimeoutSeconds);
                            }
                            catch (Exception markerEx)
                            {
                                // Harmless: the index is enabled; the next STREAM run removes the stale marker
                                _logger($"[AxDB] WARNING: {tableName}: index {index.Name} rebuilt, but its marker could not be removed: {markerEx.Message}");
                            }
                            break;
                        }
                        catch (Exception ex) when (attempt == 1 && current.State != ConnectionState.Open)
                        {
                            _logger($"[AxDB] {tableName}: connection lost during index rebuild ({ex.Message}) - retrying on a new connection");
                            replacement?.Dispose();
                            replacement = new SqlConnection(_connectionString);
                            await replacement.OpenAsync(CancellationToken.None);
                            current = replacement;
                        }
                        catch (Exception ex)
                        {
                            string fix = index.IsUnique && !string.IsNullOrEmpty(index.KeyColumns)
                                ? $"find the duplicates with: {StreamIndexSql.DuplicateCheck(tableName, index.KeyColumns)} ; remove them, then run: {sql}"
                                : $"fix the cause, then run: {sql}";
                            string message = $"Index {StreamIndexSql.Quote(index.Name)} on {tableName} could not be rebuilt after the load and stays DISABLED " +
                                             $"({(ex is SqlException se ? $"SqlException {se.Number}: " : "")}{ex.Message.Trim()}) - {fix}";
                            failures.Add(message);
                            _logger($"[AxDB] ERROR: {message}");
                            break;
                        }
                    }
                }
            }
            finally
            {
                replacement?.Dispose();
            }

            sw.Stop();
            result.RebuildSeconds = sw.Elapsed.TotalSeconds;
            _logger($"[AxDB] {tableName}: rebuilt {result.IndexesRebuilt} of {indexes.Count} nonclustered index(es) in {sw.Elapsed.TotalSeconds:F2}s" +
                    (failures.Count > 0 ? $" - {failures.Count} FAILED" : ""));
            return failures;
        }

        /// <summary>Counts of a KEEP route upsert (see <see cref="UpsertKeepExistingAsync"/>).</summary>
        public sealed class KeepUpsertResult
        {
            public int Fetched { get; set; }
            public int SkippedConflicts { get; set; }
            public int Updated { get; set; }      // existing AxDB RecIds replaced with the Tier2 row
            public int InsertedNew { get; set; }  // RecIds that did not exist in AxDB
        }

        /// <summary>
        /// KEEP route (-keep): upserts the fetched rows by RecId in ONE transaction and never
        /// deletes or truncates any other AxDB row.
        ///  1. bulk copy the fetched rows into #keep (insertable columns only - no rowversion)
        ///  2. per unique index (other than RecId): drop #keep rows whose key belongs to a
        ///     different local row that stays - logged as "skipped: conflicts with local row"
        ///  3. DELETE the AxDB rows whose RecId is in #keep, INSERT all #keep rows
        ///  4. sequence update; triggers disabled around the DML
        /// SQL text comes from <see cref="KeepModeSql"/> (shared with Get SQL).
        /// </summary>
        public async Task<KeepUpsertResult> UpsertKeepExistingAsync(TableInfo tableInfo, CancellationToken cancellationToken)
        {
            string table = tableInfo.TableName;
            var data = tableInfo.CachedData;
            var result = new KeepUpsertResult { Fetched = data?.Rows.Count ?? 0 };

            if (data == null || data.Rows.Count == 0)
            {
                _logger($"[KEEP] {table}: nothing fetched - AxDB not touched");
                tableInfo.DeleteTimeSeconds = 0;
                tableInfo.InsertTimeSeconds = 0;
                return result;
            }

            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            // Metadata: insertable columns and unique indexes of the AxDB table
            string objectName = $"[dbo].[{table}]";
            var nonInsertable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = new SqlCommand(KeepModeSql.NonInsertableColumnsQuery, connection))
            {
                cmd.Parameters.Add("@ObjectName", SqlDbType.NVarChar, 300).Value = objectName;
                cmd.CommandTimeout = _connectionSettings.CommandTimeout;
                using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                    nonInsertable.Add(reader.GetString(0));
            }

            var columns = data.Columns.Cast<DataColumn>()
                .Select(c => c.ColumnName)
                .Where(c => !nonInsertable.Contains(c))
                .ToList();
            if (!columns.Any(c => c.Equals("RECID", StringComparison.OrdinalIgnoreCase)))
                throw new Exception($"{table}: -keep needs a RECID column in the fetched data");

            var indexColumns = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var filteredIndexes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = new SqlCommand(KeepModeSql.UniqueIndexesQuery, connection))
            {
                cmd.Parameters.Add("@ObjectName", SqlDbType.NVarChar, 300).Value = objectName;
                cmd.CommandTimeout = _connectionSettings.CommandTimeout;
                using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    string indexName = reader.GetString(0);
                    if (!indexColumns.TryGetValue(indexName, out var list))
                        indexColumns[indexName] = list = new List<string>();
                    list.Add(reader.GetString(1));
                    if (reader.GetBoolean(2))
                        filteredIndexes.Add(indexName);
                }
            }

            var columnSet = new HashSet<string>(columns, StringComparer.OrdinalIgnoreCase);
            var checks = new List<(string Name, List<string> Keys)>();
            foreach (var (indexName, keys) in indexColumns)
            {
                // An index containing RecId can never be violated by a different RecId
                if (keys.Any(k => k.Equals("RECID", StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (filteredIndexes.Contains(indexName))
                {
                    _logger($"[KEEP] {table}: WARNING - filtered unique index {indexName} is not checked for conflicts");
                    continue;
                }
                var missing = keys.Where(k => !columnSet.Contains(k)).ToList();
                if (missing.Count > 0)
                {
                    _logger($"[KEEP] {table}: WARNING - unique index {indexName} not checked, key column(s) not copied: {string.Join(", ", missing)}");
                    continue;
                }
                checks.Add((indexName, keys));
            }
            _logger($"[KEEP] {table}: {columns.Count} column(s) upserted" +
                    (nonInsertable.Count > 0 ? $" (not inserted, generated by SQL Server: {string.Join(", ", nonInsertable)})" : "") +
                    $"; unique key checks: {(checks.Count > 0 ? string.Join(", ", checks.Select(c => $"{c.Name}({string.Join(",", c.Keys)})")) : "none")}");

            string disableTriggersSql = $"ALTER TABLE [{table}] DISABLE TRIGGER ALL";
            string enableTriggersSql = $"ALTER TABLE [{table}] ENABLE TRIGGER ALL";
            using var transaction = connection.BeginTransaction();

            try
            {
                _logger($"[AxDB SQL] {disableTriggersSql}");
                await ExecuteNonQueryAsync(disableTriggersSql, connection, transaction, cancellationToken, TriggerCommandTimeoutSeconds);

                // Step 1: fetched rows → #keep
                var deleteStopwatch = Stopwatch.StartNew();
                string createSql = KeepModeSql.CreateTempTable(table, columns);
                _logger($"[AxDB SQL] {createSql}");
                await ExecuteNonQueryAsync(createSql, connection, transaction, cancellationToken);

                using (var bulkCopy = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, transaction))
                {
                    bulkCopy.DestinationTableName = KeepModeSql.TempTable;
                    bulkCopy.BatchSize = 10000;
                    bulkCopy.BulkCopyTimeout = _connectionSettings.CommandTimeout;
                    foreach (var column in columns)
                        bulkCopy.ColumnMappings.Add(column, column);
                    await bulkCopy.WriteToServerAsync(data, cancellationToken);
                }
                await ExecuteNonQueryAsync(KeepModeSql.IndexTempTable(), connection, transaction, cancellationToken);

                // Step 2: unique key conflicts with local rows that stay
                var samples = new List<string>();
                var perIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int pass = 1; pass <= KeepModeSql.MaxConflictPasses; pass++)
                {
                    int removedThisPass = 0;
                    foreach (var (indexName, keys) in checks)
                    {
                        if (samples.Count < KeepModeSql.ConflictSampleSize)
                        {
                            using var sampleCmd = new SqlCommand(
                                KeepModeSql.SelectConflicts(table, keys, KeepModeSql.ConflictSampleSize - samples.Count),
                                connection, transaction);
                            sampleCmd.CommandTimeout = _connectionSettings.CommandTimeout;
                            using var reader = await sampleCmd.ExecuteReaderAsync(cancellationToken);
                            while (await reader.ReadAsync(cancellationToken))
                            {
                                var keyText = string.Join(", ", keys.Select((k, i) => $"{k}={reader.GetValue(2 + i)}"));
                                samples.Add($"RecId {reader.GetInt64(0)} vs local RecId {reader.GetInt64(1)} on {indexName} ({keyText})");
                            }
                        }

                        string deleteConflictsSql = KeepModeSql.DeleteConflicts(table, keys);
                        if (pass == 1)
                            _logger($"[AxDB SQL] {deleteConflictsSql}");
                        using var deleteCmd = new SqlCommand(deleteConflictsSql, connection, transaction);
                        deleteCmd.CommandTimeout = _connectionSettings.CommandTimeout;
                        int removed = await deleteCmd.ExecuteNonQueryAsync(cancellationToken);
                        if (removed > 0)
                        {
                            perIndex[indexName] = perIndex.GetValueOrDefault(indexName) + removed;
                            removedThisPass += removed;
                        }
                    }
                    result.SkippedConflicts += removedThisPass;
                    if (removedThisPass == 0)
                        break;
                }

                if (result.SkippedConflicts > 0)
                {
                    _logger($"[KEEP] {table}: {result.SkippedConflicts} fetched row(s) skipped: conflicts with local row " +
                            $"({string.Join(", ", perIndex.Select(p => $"{p.Key}: {p.Value}"))})");
                    foreach (var sample in samples)
                        _logger($"[KEEP] {table}:   skipped {sample}");
                    if (result.SkippedConflicts > samples.Count)
                        _logger($"[KEEP] {table}:   ... {result.SkippedConflicts - samples.Count} more not listed");
                }

                // Step 3: replace the AxDB rows with the same RecId - nothing else is deleted
                string deleteReplacedSql = KeepModeSql.DeleteReplaced(table);
                _logger($"[AxDB SQL] {deleteReplacedSql}");
                using (var cmd = new SqlCommand(deleteReplacedSql, connection, transaction))
                {
                    cmd.CommandTimeout = _connectionSettings.CommandTimeout;
                    result.Updated = await cmd.ExecuteNonQueryAsync(cancellationToken);
                }
                deleteStopwatch.Stop();
                tableInfo.DeleteTimeSeconds = (decimal)deleteStopwatch.Elapsed.TotalSeconds;

                var insertStopwatch = Stopwatch.StartNew();
                string insertSql = KeepModeSql.Insert(table, columns);
                _logger($"[AxDB SQL] {insertSql}");
                int inserted;
                using (var cmd = new SqlCommand(insertSql, connection, transaction))
                {
                    cmd.CommandTimeout = _connectionSettings.CommandTimeout;
                    inserted = await cmd.ExecuteNonQueryAsync(cancellationToken);
                }
                result.InsertedNew = inserted - result.Updated;

                await ExecuteNonQueryAsync(KeepModeSql.DropTempTable(), connection, transaction, cancellationToken);

                _logger($"[AxDB SQL] {enableTriggersSql}");
                await ExecuteNonQueryAsync(enableTriggersSql, connection, transaction, cancellationToken, TriggerCommandTimeoutSeconds);

                // Step 4: sequence
                await UpdateSequenceAsync(tableInfo, connection, transaction, cancellationToken);

                transaction.Commit();
                insertStopwatch.Stop();
                tableInfo.InsertTimeSeconds = (decimal)insertStopwatch.Elapsed.TotalSeconds;

                return result;
            }
            catch
            {
                // Rollback also undoes the DISABLE TRIGGER; enable again anyway (best effort)
                try { transaction.Rollback(); } catch { /* connection may be gone */ }
                try
                {
                    await ExecuteNonQueryAsync(enableTriggersSql, connection, null, CancellationToken.None, TriggerCommandTimeoutSeconds);
                }
                catch
                {
                    // Ignore errors when re-enabling triggers
                }
                throw;
            }
        }

        /// <summary>
        /// Inserts data into a table using SqlBulkCopy
        /// Handles deletes, trigger disabling, bulk insert, trigger enabling, and sequence update
        /// </summary>
        public async Task<int> InsertDataAsync(TableInfo tableInfo, CancellationToken cancellationToken)
        {
            // When UseTruncate is set and Tier2 has 0 rows, just truncate the AxDB table
            if ((tableInfo.CachedData == null || tableInfo.CachedData.Rows.Count == 0) && tableInfo.UseTruncate)
            {
                using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync(cancellationToken);
                _logger($"[AxDB] {tableInfo.TableName}: Tier2 empty, truncating AxDB table");
                await TruncateWithFallbackAsync(tableInfo.TableName, conn, null, cancellationToken);
                return 0;
            }

            if (tableInfo.CachedData == null || tableInfo.CachedData.Rows.Count == 0)
                return 0;

            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            DataTable? filteredDataToDispose = null;  // Track filtered DataTable for disposal - declared outside try for catch block access

            try
            {
                _logger($"[AxDB] Starting insert for table {tableInfo.TableName} ({tableInfo.CachedData.Rows.Count} rows)");

                // Determine if we should use delta comparison
                bool shouldCompare = ShouldUseComparison(tableInfo);

                ComparisonResult? comparison = null;
                DataTable dataToInsert = tableInfo.CachedData;
                HashSet<long> recIdsToDelete = new HashSet<long>();

                if (shouldCompare)
                {
                    var compareStopwatch = Stopwatch.StartNew();

                    // Get comparison context (intersection of Tier2 and AxDB columns)
                    var axDbColumns = await GetAxDbComparisonColumnsAsync(
                        tableInfo.TableName, connection, null, cancellationToken);
                    var context = BuildComparisonContext(tableInfo.CachedData, axDbColumns);

                    if (!context.CanCompare)
                    {
                        _logger($"[AxDB] {tableInfo.TableName}: RECVERSION not in both databases, using full sync");
                        shouldCompare = false;
                    }
                    else
                    {
                        // Log comparison mode
                        string mode = context.IsFallbackMode
                            ? "RECVERSION only (RECVERSION=1 excluded)"
                            : $"RECVERSION + {(context.HasCreatedDateTime ? "CREATEDDATETIME " : "")}{(context.HasModifiedDateTime ? "MODIFIEDDATETIME" : "")}".Trim();
                        _logger($"[AxDB] {tableInfo.TableName}: Comparison mode: {mode}");

                        // Fetch version map from AxDB
                        long minRecId = GetMinRecId(tableInfo.CachedData);
                        var axDbVersions = await GetAxDbVersionMapAsync(
                            tableInfo.TableName, minRecId, context, connection, null, cancellationToken);

                        // Compare records
                        comparison = CompareRecords(tableInfo.CachedData, axDbVersions, context, tableInfo.StoredMaxRecId);

                        compareStopwatch.Stop();
                        tableInfo.CompareTimeSeconds = (decimal)compareStopwatch.Elapsed.TotalSeconds;
                        tableInfo.ComparisonUsed = true;
                        tableInfo.UnchangedCount = comparison.UnchangedRecIds.Count;
                        tableInfo.ModifiedCount = comparison.ModifiedRecIds.Count;
                        tableInfo.NewInTier2Count = comparison.NewRecIds.Count;
                        tableInfo.DeletedFromAxDbCount = comparison.DeletedRecIds.Count;

                        _logger($"[AxDB] Compared {tableInfo.TableName}: {comparison.UnchangedRecIds.Count:N0} unchanged, " +
                               $"{comparison.ModifiedRecIds.Count:N0} modified, {comparison.NewRecIds.Count:N0} new, " +
                               $"{comparison.DeletedRecIds.Count:N0} deleted in {tableInfo.CompareTimeSeconds:F2}s");

                        // Check if anything needs to be done
                        if (comparison.ModifiedRecIds.Count == 0 &&
                            comparison.NewRecIds.Count == 0 &&
                            comparison.DeletedRecIds.Count == 0)
                        {
                            _logger($"[AxDB] {tableInfo.TableName}: All records unchanged, skipping delete/insert");
                            tableInfo.DeleteTimeSeconds = 0;
                            tableInfo.InsertTimeSeconds = 0;

                            // Still update sequence with gap
                            await UpdateSequenceAsync(tableInfo, connection, null, cancellationToken);

                            return 0;
                        }

                        // Prepare for delete and insert
                        recIdsToDelete = new HashSet<long>(comparison.ModifiedRecIds);
                        recIdsToDelete.UnionWith(comparison.DeletedRecIds);

                        var recIdsToInsert = new HashSet<long>(comparison.ModifiedRecIds);
                        recIdsToInsert.UnionWith(comparison.NewRecIds);

                        filteredDataToDispose = FilterDataTableByRecIds(tableInfo.CachedData, recIdsToInsert);
                        dataToInsert = filteredDataToDispose;

                        _logger($"[AxDB] {tableInfo.TableName}: Will delete {recIdsToDelete.Count:N0}, insert {dataToInsert.Rows.Count:N0}");

                        // OPTIMIZATION: If too many changes, use TRUNCATE instead of delta DELETE
                        // Calculate change percentage based on total fetched records
                        long totalChanged = comparison.ModifiedRecIds.Count + comparison.NewRecIds.Count + comparison.DeletedRecIds.Count;
                        double changePercent = tableInfo.CachedData.Rows.Count > 0
                            ? (double)totalChanged / tableInfo.CachedData.Rows.Count * 100
                            : 0;

                        // Get truncate threshold from table (set by orchestrator from config)
                        double truncateThreshold = tableInfo.TruncateThresholdPercent > 0
                            ? tableInfo.TruncateThresholdPercent
                            : 40.0; // Default fallback

                        if (changePercent >= truncateThreshold)
                        {
                            _logger($"[AxDB] {tableInfo.TableName}: Change percentage {changePercent:F1}% >= threshold {truncateThreshold}%, switching to TRUNCATE mode");

                            // Switch to full table mode - clear comparison flag and use all data
                            shouldCompare = false;
                            comparison = null;
                            recIdsToDelete.Clear();
                            dataToInsert = tableInfo.CachedData;  // Use full dataset
                            filteredDataToDispose?.Dispose();  // Dispose filtered copy since we're not using it
                            filteredDataToDispose = null;
                        }
                    }
                }

                // Additional check: If copying entire table, delete records below MinRecId
                // This handles the case where AxDB has old records with RecIds below Tier2 range
                bool shouldDeleteBelowMinRecId = false;
                if (shouldCompare && comparison != null &&
                    tableInfo.Tier2RowCount > 0 &&
                    tableInfo.RecordsToCopy > 0 &&
                    tableInfo.Tier2RowCount <= tableInfo.RecordsToCopy)
                {
                    shouldDeleteBelowMinRecId = true;
                }

                // Step 1: Disable triggers (BEFORE any DELETE or INSERT operations)
                string disableTriggersSql = $"ALTER TABLE [{tableInfo.TableName}] DISABLE TRIGGER ALL";
                _logger($"[AxDB SQL] {disableTriggersSql}");
                await ExecuteNonQueryAsync(disableTriggersSql, connection, null, cancellationToken, TriggerCommandTimeoutSeconds);

                // Step 2: Delete existing records
                var deleteStopwatch = Stopwatch.StartNew();

                if (shouldCompare && comparison != null)
                {
                    // If copying entire table, first delete records below MinRecId
                    if (shouldDeleteBelowMinRecId)
                    {
                        long minRecId = GetMinRecId(tableInfo.CachedData);
                        await DeleteBelowMinRecIdAsync(tableInfo.TableName, minRecId, connection, null, cancellationToken);
                    }

                    // Delta delete: only modified + deleted RecIds
                    await DeleteByRecIdListAsync(tableInfo.TableName, recIdsToDelete, connection, null, cancellationToken);
                }
                else
                {
                    // Full delete: current behavior based on strategy
                    await DeleteExistingRecordsAsync(tableInfo, connection, null, cancellationToken);
                }

                deleteStopwatch.Stop();
                tableInfo.DeleteTimeSeconds = (decimal)deleteStopwatch.Elapsed.TotalSeconds;

                // Step 3: Bulk insert data
                _logger($"[AxDB] Bulk inserting {dataToInsert.Rows.Count} rows into {tableInfo.TableName}");
                var insertStopwatch = Stopwatch.StartNew();

                if (dataToInsert.Rows.Count > 0)
                {
                    using (var bulkCopy = new SqlBulkCopy(connection))
                    {
                        bulkCopy.DestinationTableName = tableInfo.TableName;
                        bulkCopy.BatchSize = 10000;
                        bulkCopy.BulkCopyTimeout = _connectionSettings.CommandTimeout;

                        // Map columns
                        foreach (DataColumn column in dataToInsert.Columns)
                        {
                            bulkCopy.ColumnMappings.Add(column.ColumnName, column.ColumnName);
                        }

                        await bulkCopy.WriteToServerAsync(dataToInsert, cancellationToken);
                    }
                }

                insertStopwatch.Stop();
                tableInfo.InsertTimeSeconds = (decimal)insertStopwatch.Elapsed.TotalSeconds;

                // Step 4: Enable triggers (always, even if errors occur)
                string enableTriggersSql = $"ALTER TABLE [{tableInfo.TableName}] ENABLE TRIGGER ALL";
                _logger($"[AxDB SQL] {enableTriggersSql}");
                await ExecuteNonQueryAsync(enableTriggersSql, connection, null, cancellationToken, TriggerCommandTimeoutSeconds);

                // Step 5: Update sequence
                await UpdateSequenceAsync(tableInfo, connection, null, cancellationToken);

                int rowCount = dataToInsert.Rows.Count;

                // Dispose filtered DataTable if we created one
                filteredDataToDispose?.Dispose();

                return rowCount;
            }
            catch
            {
                // Always try to re-enable triggers on error. Use CancellationToken.None so this
                // still runs when the operation was cancelled, but keep a finite timeout so it
                // cannot hang the worker.
                try
                {
                    await ExecuteNonQueryAsync($"ALTER TABLE [{tableInfo.TableName}] ENABLE TRIGGER ALL", connection, null,
                        CancellationToken.None, TriggerCommandTimeoutSeconds);
                }
                catch
                {
                    // Ignore errors when re-enabling triggers
                }

                // Dispose filtered DataTable even on error
                filteredDataToDispose?.Dispose();

                throw;
            }
        }

        /// <summary>
        /// Deletes existing records based on the copy strategy and cleanup rules
        /// </summary>
        private async Task DeleteExistingRecordsAsync(TableInfo tableInfo, SqlConnection connection, SqlTransaction? transaction, CancellationToken cancellationToken)
        {
            if (tableInfo.CachedData == null || tableInfo.CachedData.Rows.Count == 0)
                return;

            // Optimization: If Tier2 has fewer rows than we're configured to copy, use TRUNCATE
            // This means we're copying the entire source table, so TRUNCATE is much faster than DELETE
            if (tableInfo.Tier2RowCount > 0 &&
                tableInfo.RecordsToCopy > 0 &&
                tableInfo.Tier2RowCount <= tableInfo.RecordsToCopy &&
                !tableInfo.UseTruncate)
            {
                _logger($"[AxDB] Optimization: Tier2 has {tableInfo.Tier2RowCount} rows, copying {tableInfo.RecordsToCopy} - using TRUNCATE instead of DELETE");
                await TruncateWithFallbackAsync(tableInfo.TableName, connection, transaction, cancellationToken);
                return;
            }

            // If UseTruncate flag is set, always truncate
            if (tableInfo.UseTruncate)
            {
                await TruncateWithFallbackAsync(tableInfo.TableName, connection, transaction, cancellationToken);
                return;
            }

            // Apply cleanup rules based on strategy type
            switch (tableInfo.StrategyType)
            {
                case CopyStrategyType.RecId:
                case CopyStrategyType.Sql:
                    // RecId/Sql: DELETE WHERE RecId >= @MinRecId
                    await DeleteByRecIdAsync(tableInfo, connection, transaction, cancellationToken);
                    break;

                default:
                    throw new Exception($"Unsupported strategy type: {tableInfo.StrategyType}");
            }
        }

        /// <summary>
        /// Attempts TRUNCATE TABLE, falls back to DELETE FROM if the table is referenced by views or foreign keys
        /// </summary>
        private async Task TruncateWithFallbackAsync(string tableName, SqlConnection connection, SqlTransaction? transaction, CancellationToken cancellationToken)
        {
            string truncateQuery = $"TRUNCATE TABLE [{tableName}]";
            _logger($"[AxDB SQL] {truncateQuery}");

            try
            {
                using var command = new SqlCommand(truncateQuery, connection, transaction);
                command.CommandTimeout = _connectionSettings.CommandTimeout;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (SqlException ex) when (ex.Number == 4712 || ex.Number == 3732 || ex.Message.Contains("Cannot TRUNCATE TABLE"))
            {
                _logger($"[AxDB] TRUNCATE failed for {tableName} (referenced by another object), falling back to DELETE");
                string deleteQuery = $"DELETE FROM [{tableName}]";
                _logger($"[AxDB SQL] {deleteQuery}");

                using var deleteCommand = new SqlCommand(deleteQuery, connection, transaction);
                deleteCommand.CommandTimeout = _connectionSettings.CommandTimeout;
                await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        private async Task DeleteByRecIdAsync(TableInfo tableInfo, SqlConnection connection, SqlTransaction? transaction, CancellationToken cancellationToken)
        {
            long minRecId = GetMinRecId(tableInfo.CachedData!);
            string deleteQuery = $"DELETE FROM [{tableInfo.TableName}] WHERE RecId >= @MinRecId";
            _logger($"[AxDB SQL] Deleting by RecId: {deleteQuery} (MinRecId={minRecId})");

            using var command = new SqlCommand(deleteQuery, connection, transaction);
            command.Parameters.AddWithValue("@MinRecId", minRecId);
            command.CommandTimeout = _connectionSettings.CommandTimeout;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }


        /// <summary>
        /// Updates the sequence for a table if needed
        /// </summary>
        private async Task UpdateSequenceAsync(TableInfo tableInfo, SqlConnection connection, SqlTransaction? transaction, CancellationToken cancellationToken)
        {
            // Get max RecId from the table
            string maxRecIdQuery = $"SELECT MAX(RecId) FROM [{tableInfo.TableName}]";
            using var command1 = new SqlCommand(maxRecIdQuery, connection, transaction);
            command1.CommandTimeout = _connectionSettings.CommandTimeout;
            var maxRecIdResult = await command1.ExecuteScalarAsync(cancellationToken);

            if (maxRecIdResult == null || maxRecIdResult == DBNull.Value)
            {
                _logger($"[AxDB] {tableInfo.TableName}: No records found, skipping sequence update");
                return;
            }

            long maxRecId = Convert.ToInt64(maxRecIdResult);

            // Get current sequence value (use AxDB TableId for local sequences)
            string sequenceName = $"SEQ_{tableInfo.AxDbTableId}";
            string currentSeqQuery = "SELECT CAST(current_value AS BIGINT) FROM sys.sequences WHERE name = @SequenceName";

            using var command2 = new SqlCommand(currentSeqQuery, connection, transaction);
            command2.Parameters.AddWithValue("@SequenceName", sequenceName);
            command2.CommandTimeout = _connectionSettings.CommandTimeout;
            var currentSeqResult = await command2.ExecuteScalarAsync(cancellationToken);

            if (currentSeqResult == null || currentSeqResult == DBNull.Value)
            {
                _logger($"[AxDB] {tableInfo.TableName}: Sequence {sequenceName} not found in sys.sequences (AxDbTableId={tableInfo.AxDbTableId}), skipping sequence update");
                return;
            }

            long currentSeq = Convert.ToInt64(currentSeqResult);

            // Calculate new sequence value with gap
            long newSeq = Math.Max(maxRecId, currentSeq) + SEQUENCE_GAP;

            string updateSeqQuery = $"ALTER SEQUENCE [{sequenceName}] RESTART WITH {newSeq}";
            _logger($"[AxDB SQL] {updateSeqQuery}");

            using var command3 = new SqlCommand(updateSeqQuery, connection, transaction);
            command3.CommandTimeout = _connectionSettings.CommandTimeout;
            await command3.ExecuteNonQueryAsync(cancellationToken);
        }

        /// <summary>
        /// Executes a non-query command
        /// </summary>
        private async Task ExecuteNonQueryAsync(string query, SqlConnection connection, SqlTransaction? transaction,
            CancellationToken cancellationToken, int? commandTimeoutOverride = null)
        {
            using var command = new SqlCommand(query, connection, transaction);
            command.CommandTimeout = commandTimeoutOverride ?? _connectionSettings.CommandTimeout;

            // Register the token to physically abort the in-flight command (e.g. a DDL ALTER
            // blocked on a schema lock) so Stop can interrupt it and the worker can exit.
            using (cancellationToken.Register(() => { try { command.Cancel(); } catch { /* best-effort */ } }))
            {
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        /// <summary>
        /// Loads entire SQLDICTIONARY into memory cache for fast lookups
        /// This dramatically reduces query count from ~4000 to 1 during PrepareTableList
        /// </summary>
        public async Task<SqlDictionaryCache> LoadSqlDictionaryCacheAsync(string? specificTableName = null)
        {
            // Build query with optional table name filter
            // NOTE: We must filter by TableID, not by name, because the name column contains
            // table names when FIELDID=0 and field names when FIELDID<>0
            string tableFilter = !string.IsNullOrWhiteSpace(specificTableName)
                ? "WHERE TableID = (SELECT TableID FROM SQLDICTIONARY WHERE UPPER(name) = @TableName AND FIELDID = 0)"
                : "";

            string query = $@"
                SELECT name, TableID, FIELDID, SQLName
                FROM SQLDICTIONARY
                {tableFilter}
                ORDER BY TableID, FIELDID";

            _logger("[AxDB] Loading SQLDICTIONARY cache...");
            _logger($"[AxDB SQL] {query}");

            var cache = new SqlDictionaryCache();

            using var connection = new SqlConnection(_connectionString);
            using var command = new SqlCommand(query, connection);
            command.CommandTimeout = _connectionSettings.CommandTimeout;

            // Add parameter if filtering by specific table
            if (!string.IsNullOrWhiteSpace(specificTableName))
            {
                command.Parameters.AddWithValue("@TableName", specificTableName);
            }

            await connection.OpenAsync();
            using var reader = await command.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                string name = reader.GetString(0);
                int tableId = reader.GetInt32(1);
                int fieldId = reader.GetInt32(2);
                string sqlName = reader.GetString(3);

                if (fieldId == 0)
                {
                    // This is a table entry
                    cache.TableNameToId[name.ToUpper()] = tableId;
                }
                else
                {
                    // This is a field entry
                    if (!cache.TableIdToFields.ContainsKey(tableId))
                    {
                        cache.TableIdToFields[tableId] = new List<string>();
                    }
                    cache.TableIdToFields[tableId].Add(sqlName);
                }
            }

            _logger($"[AxDB] Loaded SQLDICTIONARY cache: {cache.GetStats()}");

            return cache;
        }

        /// <summary>
        /// Loads the TableGroup of every table from the AxDB TABLEMETADATATABLE, keyed by SQL
        /// table name (case-insensitive). Used to resolve "TableGroup:..." lines in the
        /// Tables to Copy / Tables to Exclude lists.
        /// </summary>
        public async Task<Dictionary<string, int>> GetTableGroupsAsync()
        {
            const string query = @"
                SELECT d.SQLNAME, m.TABLEGROUP
                FROM SQLDICTIONARY d
                INNER JOIN TABLEMETADATATABLE m ON m.TABLEID = d.TABLEID
                WHERE d.FIELDID = 0 AND d.ARRAY = 0";

            _logger("[AxDB] Loading table groups (SQLDICTIONARY + TABLEMETADATATABLE)...");

            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            using var connection = new SqlConnection(_connectionString);
            using var command = new SqlCommand(query, connection);
            command.CommandTimeout = _connectionSettings.CommandTimeout;

            await connection.OpenAsync();
            using var reader = await command.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                if (reader.IsDBNull(0) || reader.IsDBNull(1))
                    continue;
                result[reader.GetString(0)] = Convert.ToInt32(reader.GetValue(1));
            }

            _logger($"[AxDB] Loaded table groups for {result.Count} tables");
            return result;
        }

        /// <summary>
        /// Tables (dbo base tables in AxDB) whose SQLDICTIONARY entry has an item or product key
        /// column, with their TableGroup and which key/company columns they have.
        /// </summary>
        public async Task<List<ItemTableCandidate>> GetItemTableCandidatesAsync(IReadOnlyDictionary<string, string>? itemRelations = null)
        {
            // Names come from code constants and the [TransactionLessItemRelations] ini lines -
            // quote them safely, since the ini is user-edited.
            static string Quote(string v) => "N'" + v.Replace("'", "''") + "'";

            var columns = TransactionLessHelper.KeyColumns
                .Concat(TransactionLessHelper.CompanyColumns)
                .Concat(TransactionLessHelper.RelationTables.Values.Select(r => r.Column))
                .Concat(itemRelations?.Values ?? Enumerable.Empty<string>())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            string allColumns = string.Join(", ", columns.Select(c => Quote(c.ToUpperInvariant())));
            string keyColumns = string.Join(", ", TransactionLessHelper.KeyColumns.Select(Quote));
            string relationTables = string.Join(", ", TransactionLessHelper.RelationTables.Keys
                .Concat(itemRelations?.Keys ?? Enumerable.Empty<string>())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(t => Quote(t.ToUpperInvariant())));

            // Data type per column: an INVENTTABLE column is a RecId (bigint) on some tables and an
            // ItemId (nvarchar) on others, and the filter differs.
            string query = $@"
                SELECT t.SQLNAME, m.TABLEGROUP, f.SQLNAME, ISNULL(TYPE_NAME(c.system_type_id), '')
                FROM SQLDICTIONARY t
                INNER JOIN TABLEMETADATATABLE m ON m.TABLEID = t.TABLEID
                INNER JOIN SQLDICTIONARY f ON f.TABLEID = t.TABLEID AND f.FIELDID <> 0
                LEFT JOIN sys.columns c ON c.object_id = OBJECT_ID(N'dbo.' + QUOTENAME(t.SQLNAME)) AND c.name = f.SQLNAME
                WHERE t.FIELDID = 0 AND t.ARRAY = 0
                  AND UPPER(f.SQLNAME) IN ({allColumns})
                  AND (EXISTS (SELECT 1 FROM SQLDICTIONARY k
                               WHERE k.TABLEID = t.TABLEID AND k.FIELDID <> 0 AND UPPER(k.SQLNAME) IN ({keyColumns}))
                       OR UPPER(t.SQLNAME) IN ({relationTables}))
                  AND EXISTS (SELECT 1 FROM sys.tables st
                              WHERE st.name = t.SQLNAME AND st.schema_id = SCHEMA_ID('dbo'))";

            _logger("[AxDB] Loading item/product tables from SQLDICTIONARY...");

            var map = new Dictionary<string, ItemTableCandidate>(StringComparer.OrdinalIgnoreCase);

            using var connection = new SqlConnection(_connectionString);
            using var command = new SqlCommand(query, connection);
            command.CommandTimeout = _connectionSettings.CommandTimeout;

            await connection.OpenAsync();
            using var reader = await command.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                string tableName = reader.GetString(0).ToUpperInvariant();
                if (!map.TryGetValue(tableName, out var candidate))
                {
                    candidate = new ItemTableCandidate
                    {
                        TableName = tableName,
                        TableGroup = Convert.ToInt32(reader.GetValue(1))
                    };
                    map[tableName] = candidate;
                }
                string column = reader.GetString(2).ToUpperInvariant();
                candidate.Columns.Add(column);
                candidate.ColumnTypes[column] = reader.GetString(3);
            }

            _logger($"[AxDB] Found {map.Count} tables with an item/product column");
            return map.Values.OrderBy(c => c.TableName, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// Columns of a table from the AxDB SQLDICTIONARY (FIELDID &lt;&gt; 0) that exist in the
        /// dbo table, with their SQL data type from INFORMATION_SCHEMA.COLUMNS. Empty when the
        /// table is unknown or missing.
        /// </summary>
        public async Task<List<TableColumnInfo>> GetTableColumnTypesAsync(string tableName)
        {
            const string query = @"
                SELECT f.SQLNAME, f.FIELDID, f.ARRAY, c.DATA_TYPE
                FROM SQLDICTIONARY t
                INNER JOIN SQLDICTIONARY f ON f.TABLEID = t.TABLEID AND f.FIELDID <> 0
                INNER JOIN INFORMATION_SCHEMA.COLUMNS c
                    ON c.TABLE_SCHEMA = 'dbo' AND c.TABLE_NAME = t.SQLNAME AND c.COLUMN_NAME = f.SQLNAME
                WHERE t.FIELDID = 0 AND t.ARRAY = 0 AND t.SQLNAME = @TableName
                ORDER BY f.FIELDID, f.ARRAY";

            _logger($"[AxDB] Loading {tableName} columns (SQLDICTIONARY + INFORMATION_SCHEMA)...");

            var result = new List<TableColumnInfo>();

            using var connection = new SqlConnection(_connectionString);
            using var command = new SqlCommand(query, connection);
            command.Parameters.Add("@TableName", SqlDbType.NVarChar, 128).Value = tableName;
            command.CommandTimeout = _connectionSettings.CommandTimeout;

            await connection.OpenAsync();
            using var reader = await command.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                result.Add(new TableColumnInfo(
                    reader.GetString(0),
                    Convert.ToInt32(reader.GetValue(1)),
                    Convert.ToInt32(reader.GetValue(2)),
                    reader.GetString(3)));
            }

            _logger($"[AxDB] Found {result.Count} columns for {tableName}");
            return result;
        }

        /// <summary>
        /// Check if DataTable has a column (case-insensitive)
        /// </summary>
        private bool HasColumn(DataTable table, string columnName)
        {
            foreach (DataColumn col in table.Columns)
            {
                if (col.ColumnName.Equals(columnName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Get column name with correct case from DataTable
        /// </summary>
        private string? GetColumnName(DataTable table, string columnName)
        {
            foreach (DataColumn col in table.Columns)
            {
                if (col.ColumnName.Equals(columnName, StringComparison.OrdinalIgnoreCase))
                    return col.ColumnName;
            }
            return null;
        }

        /// <summary>
        /// Query AxDB to determine which comparison columns exist
        /// </summary>
        private async Task<HashSet<string>> GetAxDbComparisonColumnsAsync(
            string tableName,
            SqlConnection connection,
            SqlTransaction? transaction,
            CancellationToken cancellationToken)
        {
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string query = @"
                SELECT COLUMN_NAME
                FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_NAME = @TableName
                  AND UPPER(COLUMN_NAME) IN ('RECVERSION', 'CREATEDDATETIME', 'MODIFIEDDATETIME')";

            using var command = new SqlCommand(query, connection, transaction);
            command.Parameters.AddWithValue("@TableName", tableName);
            command.CommandTimeout = _connectionSettings.CommandTimeout;

            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                columns.Add(reader.GetString(0));
            }

            return columns;
        }

        /// <summary>
        /// Build comparison context from Tier2 data and AxDB columns
        /// </summary>
        private ComparisonContext BuildComparisonContext(DataTable tier2Data, HashSet<string> axDbColumns)
        {
            var context = new ComparisonContext();

            // Check intersection for each comparison field
            context.HasRecVersion = HasColumn(tier2Data, "RECVERSION") &&
                                    axDbColumns.Contains("RECVERSION");

            context.HasCreatedDateTime = HasColumn(tier2Data, "CREATEDDATETIME") &&
                                          axDbColumns.Contains("CREATEDDATETIME");

            context.HasModifiedDateTime = HasColumn(tier2Data, "MODIFIEDDATETIME") &&
                                           axDbColumns.Contains("MODIFIEDDATETIME");

            return context;
        }

        /// <summary>
        /// Determines if delta comparison should be used for this table
        /// </summary>
        private bool ShouldUseComparison(TableInfo tableInfo)
        {
            // Skip if truncating (no point comparing)
            if (tableInfo.UseTruncate)
                return false;

            // Skip if no data
            if (tableInfo.CachedData == null || tableInfo.CachedData.Rows.Count == 0)
                return false;

            // Skip if RECVERSION column not present
            if (!HasColumn(tableInfo.CachedData, "RECVERSION"))
            {
                _logger($"[AxDB] {tableInfo.TableName}: RECVERSION not found, using full sync");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Fetch RecId and comparison fields from AxDB
        /// </summary>
        private async Task<Dictionary<long, AxDbRecordVersion>> GetAxDbVersionMapAsync(
            string tableName,
            long minRecId,
            ComparisonContext context,
            SqlConnection connection,
            SqlTransaction? transaction,
            CancellationToken cancellationToken)
        {
            var result = new Dictionary<long, AxDbRecordVersion>();

            // Build column list based on context
            var columns = new List<string> { "RecId", "RECVERSION" };
            if (context.HasCreatedDateTime)
                columns.Add("CREATEDDATETIME");
            if (context.HasModifiedDateTime)
                columns.Add("MODIFIEDDATETIME");

            string columnList = string.Join(", ", columns);
            string query = $"SELECT {columnList} FROM [{tableName}] WHERE RecId >= @MinRecId";

            _logger($"[AxDB SQL] Fetching version map: {query} (MinRecId={minRecId})");

            using var command = new SqlCommand(query, connection, transaction);
            command.Parameters.AddWithValue("@MinRecId", minRecId);
            command.CommandTimeout = _connectionSettings.CommandTimeout;

            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                long recId = reader.GetInt64(0);
                var versionInfo = new AxDbRecordVersion
                {
                    RecVersion = reader.GetInt32(1),
                    CreatedDateTime = context.HasCreatedDateTime ? reader.GetValue(2) : null,
                    ModifiedDateTime = context.HasModifiedDateTime
                        ? reader.GetValue(context.HasCreatedDateTime ? 3 : 2)
                        : null
                };
                result[recId] = versionInfo;
            }

            _logger($"[AxDB] Fetched {result.Count:N0} records for comparison");
            return result;
        }

        /// <summary>
        /// Compare Tier2 data with AxDB version map
        /// </summary>
        private ComparisonResult CompareRecords(
            DataTable tier2Data,
            Dictionary<long, AxDbRecordVersion> axDbVersions,
            ComparisonContext context,
            long? maxTransferredRecId = null)
        {
            var result = new ComparisonResult();
            var tier2RecIds = new HashSet<long>();

            // Get column names with correct case
            string recIdCol = GetColumnName(tier2Data, "RECID") ?? "RECID";
            string recVersionCol = GetColumnName(tier2Data, "RECVERSION") ?? "RECVERSION";
            string? createdDateTimeCol = context.HasCreatedDateTime
                ? GetColumnName(tier2Data, "CREATEDDATETIME") : null;
            string? modifiedDateTimeCol = context.HasModifiedDateTime
                ? GetColumnName(tier2Data, "MODIFIEDDATETIME") : null;

            foreach (DataRow row in tier2Data.Rows)
            {
                if (row[recIdCol] == DBNull.Value)
                    continue;

                long recId = Convert.ToInt64(row[recIdCol]);
                tier2RecIds.Add(recId);

                // Check if RecId exists in AxDB
                if (!axDbVersions.TryGetValue(recId, out var axDbVersion))
                {
                    result.NewRecIds.Add(recId);
                    continue;
                }

                // Get Tier2 values
                int tier2RecVersion = Convert.ToInt32(row[recVersionCol]);

                // Fallback mode: RecVersion=1 optimization using MaxRecId
                if (context.IsFallbackMode && tier2RecVersion == 1)
                {
                    // MaxRecId optimization: if both have RecVersion=1 AND RecId <= stored max
                    if (axDbVersion.RecVersion == 1 &&
                        maxTransferredRecId.HasValue &&
                        recId <= maxTransferredRecId.Value)
                    {
                        result.UnchangedRecIds.Add(recId);
                        continue;
                    }
                    // Otherwise treat as modified (existing behavior)
                    result.ModifiedRecIds.Add(recId);
                    continue;
                }

                // Compare all available fields
                bool allMatch = true;

                // Compare RECVERSION
                if (tier2RecVersion != axDbVersion.RecVersion)
                {
                    allMatch = false;
                }

                // Compare CREATEDDATETIME if available
                if (allMatch && context.HasCreatedDateTime && createdDateTimeCol != null)
                {
                    object tier2Value = row[createdDateTimeCol];
                    object? axDbValue = axDbVersion.CreatedDateTime;
                    if (!ValuesEqual(tier2Value, axDbValue))
                    {
                        allMatch = false;
                    }
                }

                // Compare MODIFIEDDATETIME if available
                if (allMatch && context.HasModifiedDateTime && modifiedDateTimeCol != null)
                {
                    object tier2Value = row[modifiedDateTimeCol];
                    object? axDbValue = axDbVersion.ModifiedDateTime;
                    if (!ValuesEqual(tier2Value, axDbValue))
                    {
                        allMatch = false;
                    }
                }

                if (allMatch)
                {
                    result.UnchangedRecIds.Add(recId);
                }
                else
                {
                    result.ModifiedRecIds.Add(recId);
                }
            }

            // Find deleted records (in AxDB but not in Tier2 set)
            foreach (var axDbRecId in axDbVersions.Keys)
            {
                if (!tier2RecIds.Contains(axDbRecId))
                {
                    result.DeletedRecIds.Add(axDbRecId);
                }
            }

            return result;
        }

        /// <summary>
        /// Compare two values for equality (handles DBNull and null)
        /// </summary>
        private bool ValuesEqual(object? value1, object? value2)
        {
            // Both null or DBNull
            if ((value1 == null || value1 == DBNull.Value) &&
                (value2 == null || value2 == DBNull.Value))
                return true;

            // One is null/DBNull, other is not
            if (value1 == null || value1 == DBNull.Value ||
                value2 == null || value2 == DBNull.Value)
                return false;

            // Both have values - exact comparison
            return value1.Equals(value2);
        }

        /// <summary>
        /// Deletes records below MinRecId (for full table copy optimization)
        /// </summary>
        private async Task DeleteBelowMinRecIdAsync(
            string tableName,
            long minRecId,
            SqlConnection connection,
            SqlTransaction? transaction,
            CancellationToken cancellationToken)
        {
            string deleteQuery = $"DELETE FROM [{tableName}] WHERE RecId < @MinRecId";

            using var command = new SqlCommand(deleteQuery, connection, transaction);
            command.Parameters.AddWithValue("@MinRecId", minRecId);
            command.CommandTimeout = _connectionSettings.CommandTimeout;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        /// <summary>
        /// Deletes records by RecId list in batches
        /// </summary>
        private async Task DeleteByRecIdListAsync(
            string tableName,
            IEnumerable<long> recIds,
            SqlConnection connection,
            SqlTransaction? transaction,
            CancellationToken cancellationToken)
        {
            var recIdList = recIds.ToList();
            if (recIdList.Count == 0)
                return;

            _logger($"[AxDB] Deleting {recIdList.Count:N0} records by RecId list");

            for (int i = 0; i < recIdList.Count; i += DELETE_BATCH_SIZE)
            {
                var batch = recIdList.Skip(i).Take(DELETE_BATCH_SIZE);
                string inClause = string.Join(",", batch);
                string deleteQuery = $"DELETE FROM [{tableName}] WHERE RecId IN ({inClause})";

                using var command = new SqlCommand(deleteQuery, connection, transaction);
                command.CommandTimeout = _connectionSettings.CommandTimeout;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        /// <summary>
        /// Filter DataTable to only include specified RecIds
        /// </summary>
        private DataTable FilterDataTableByRecIds(DataTable source, HashSet<long> recIdsToKeep)
        {
            var filtered = source.Clone();
            string recIdCol = GetColumnName(source, "RECID") ?? "RECID";

            foreach (DataRow row in source.Rows)
            {
                if (row[recIdCol] != DBNull.Value)
                {
                    long recId = Convert.ToInt64(row[recIdCol]);
                    if (recIdsToKeep.Contains(recId))
                    {
                        filtered.ImportRow(row);
                    }
                }
            }

            return filtered;
        }

        /// <summary>
        /// Gets the minimum RecId from a DataTable
        /// </summary>
        private long GetMinRecId(DataTable data)
        {
            if (!data.Columns.Contains("RecId"))
                return 0;

            long minRecId = long.MaxValue;
            foreach (DataRow row in data.Rows)
            {
                if (row["RecId"] != DBNull.Value)
                {
                    long recId = Convert.ToInt64(row["RecId"]);
                    if (recId < minRecId)
                        minRecId = recId;
                }
            }

            return minRecId == long.MaxValue ? 0 : minRecId;
        }


        /// <summary>
        /// Gets the list of RecIds from a DataTable
        /// </summary>
        private List<long> GetRecIdList(DataTable data)
        {
            var recIds = new List<long>();

            if (!data.Columns.Contains("RecId"))
                return recIds;

            foreach (DataRow row in data.Rows)
            {
                if (row["RecId"] != DBNull.Value)
                {
                    recIds.Add(Convert.ToInt64(row["RecId"]));
                }
            }

            return recIds;
        }

        /// <summary>
        /// Gets count of changed records in AxDB (for SysRowVersion optimization)
        /// </summary>
        public async Task<long> GetChangedCountAsync(
            string tableName,
            byte[] timestampThreshold,
            CancellationToken cancellationToken)
        {
            string sql = $"SELECT COUNT(*) FROM [{tableName}] WHERE SysRowVersion > @Threshold";

            using var connection = new SqlConnection(_connectionString);
            using var command = new SqlCommand(sql, connection);
            command.Parameters.Add("@Threshold", System.Data.SqlDbType.Binary, 8).Value = timestampThreshold;
            command.CommandTimeout = _connectionSettings.CommandTimeout;

            await connection.OpenAsync(cancellationToken);
            return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        }

        /// <summary>
        /// Gets set of table names that have at least 1 row in AxDB (fast DMV-based query)
        /// </summary>
        /// <summary>
        /// Row count of every user table in AxDB (one query on sys.dm_db_partition_stats), keyed
        /// by table name (case-insensitive). Used as the size proxy for SQL-filter estimates.
        /// </summary>
        public async Task<Dictionary<string, long>> GetTableRowCountsAsync(CancellationToken cancellationToken)
        {
            const string sql = @"
                SELECT o.name, SUM(s.row_count)
                FROM sys.dm_db_partition_stats s
                INNER JOIN sys.objects o ON o.object_id = s.object_id
                WHERE o.type = 'U' AND s.index_id IN (0, 1)
                GROUP BY o.name";

            var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

            using var connection = new SqlConnection(_connectionString);
            using var command = new SqlCommand(sql, connection);
            command.CommandTimeout = _connectionSettings.CommandTimeout;

            await connection.OpenAsync(cancellationToken);
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                result[reader.GetString(0)] = Convert.ToInt64(reader.GetValue(1));

            return result;
        }

        public async Task<HashSet<string>> GetNonEmptyTableNamesAsync(CancellationToken cancellationToken)
        {
            string sql = @"
                SELECT o.name
                FROM sys.dm_db_partition_stats s
                INNER JOIN sys.objects o ON o.object_id = s.object_id
                WHERE o.type = 'U'
                GROUP BY o.name
                HAVING MAX(s.row_count) > 0";

            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            using var connection = new SqlConnection(_connectionString);
            using var command = new SqlCommand(sql, connection);
            command.CommandTimeout = _connectionSettings.CommandTimeout;

            await connection.OpenAsync(cancellationToken);
            using var reader = await command.ExecuteReaderAsync(cancellationToken);

            while (await reader.ReadAsync(cancellationToken))
            {
                result.Add(reader.GetString(0));
            }

            return result;
        }

        /// <summary>
        /// Gets total row count in AxDB table (for SysRowVersion optimization)
        /// </summary>
        public async Task<long> GetRowCountAsync(string tableName, CancellationToken cancellationToken)
        {
            string sql = $"SELECT COUNT(*) FROM [{tableName}]";

            using var connection = new SqlConnection(_connectionString);
            using var command = new SqlCommand(sql, connection);
            command.CommandTimeout = _connectionSettings.CommandTimeout;

            await connection.OpenAsync(cancellationToken);
            return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        }

        /// <summary>
        /// Gets all RecIds from AxDB table (for SysRowVersion optimization)
        /// </summary>
        public async Task<HashSet<long>> GetRecIdSetAsync(
            string tableName,
            SqlConnection connection,
            SqlTransaction? transaction,
            CancellationToken cancellationToken)
        {
            string sql = $"SELECT RecId FROM [{tableName}]";

            using var command = new SqlCommand(sql, connection, transaction);
            command.CommandTimeout = _connectionSettings.CommandTimeout;

            var result = new HashSet<long>();
            using var reader = await command.ExecuteReaderAsync(cancellationToken);

            while (await reader.ReadAsync(cancellationToken))
            {
                result.Add(reader.GetInt64(0));
            }

            return result;
        }

        /// <summary>
        /// Gets max SysRowVersion from AxDB table (for SysRowVersion optimization)
        /// </summary>
        public async Task<byte[]?> GetMaxTimestampAsync(
            string tableName,
            SqlConnection connection,
            SqlTransaction? transaction,
            CancellationToken cancellationToken)
        {
            string sql = $"SELECT MAX(SysRowVersion) FROM [{tableName}]";

            using var command = new SqlCommand(sql, connection, transaction);
            command.CommandTimeout = _connectionSettings.CommandTimeout;

            using (cancellationToken.Register(() => { try { command.Cancel(); } catch { /* best-effort */ } }))
            {
                var result = await command.ExecuteScalarAsync(cancellationToken);
                return result as byte[];
            }
        }

        /// <summary>
        /// Executes optimized incremental delete operations (for SysRowVersion optimization)
        /// </summary>
        public async Task ExecuteIncrementalDeletesAsync(
            TableInfo table,
            DataTable tier2Control,
            byte[] tier2Timestamp,
            byte[] axdbTimestamp,
            SqlConnection connection,
            SqlTransaction? transaction,
            CancellationToken cancellationToken)
        {
            var deleteStopwatch = System.Diagnostics.Stopwatch.StartNew();

            // Create temp table with Tier2 RecIds
            _logger($"[AxDB] Creating temp table for {table.TableName}");
            await CreateTier2ControlTempTableAsync(tier2Control, connection, transaction, cancellationToken);

            // 1.1: Delete records modified in Tier2
            string delete1 = $@"
                DELETE FROM [{table.TableName}]
                WHERE RecId IN (
                    SELECT RecId FROM #Tier2Control
                    WHERE SysRowVersion > @Tier2Timestamp
                )";
            _logger($"[AxDB SQL] Delete Tier2-modified: {delete1}");
            _logger($"[AxDB] @Tier2Timestamp = {TimestampHelper.ToHexString(tier2Timestamp)}");

            using (var cmd = new SqlCommand(delete1, connection, transaction))
            {
                cmd.Parameters.Add("@Tier2Timestamp", System.Data.SqlDbType.Binary, 8).Value = tier2Timestamp;
                cmd.CommandTimeout = _connectionSettings.CommandTimeout;
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            // 1.2: Delete records modified in AxDB
            string delete2 = $@"
                DELETE FROM [{table.TableName}]
                WHERE SysRowVersion > @AxDBTimestamp";
            _logger($"[AxDB SQL] Delete AxDB-modified: {delete2}");
            _logger($"[AxDB] @AxDBTimestamp = {TimestampHelper.ToHexString(axdbTimestamp)}");

            using (var cmd = new SqlCommand(delete2, connection, transaction))
            {
                cmd.Parameters.Add("@AxDBTimestamp", System.Data.SqlDbType.Binary, 8).Value = axdbTimestamp;
                cmd.CommandTimeout = _connectionSettings.CommandTimeout;
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            // 1.3: Delete records not in Tier2 target set
            string delete3 = $@"
                DELETE FROM [{table.TableName}]
                WHERE NOT EXISTS (
                    SELECT 1 FROM #Tier2Control t
                    WHERE t.RecId = [{table.TableName}].RecId
                )";
            _logger($"[AxDB SQL] Delete not-in-Tier2: {delete3}");

            using (var cmd = new SqlCommand(delete3, connection, transaction))
            {
                cmd.CommandTimeout = _connectionSettings.CommandTimeout;
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            // Drop temp table
            using (var cmd = new SqlCommand("DROP TABLE #Tier2Control", connection, transaction))
            {
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            deleteStopwatch.Stop();
            table.DeleteTimeSeconds = (decimal)deleteStopwatch.Elapsed.TotalSeconds;
        }

        private async Task CreateTier2ControlTempTableAsync(
            DataTable tier2Control,
            SqlConnection connection,
            SqlTransaction? transaction,
            CancellationToken cancellationToken)
        {
            // Create temp table
            string createSql = @"
                CREATE TABLE #Tier2Control (
                    RecId BIGINT PRIMARY KEY,
                    SysRowVersion BINARY(8)
                )";

            using (var cmd = new SqlCommand(createSql, connection, transaction))
            {
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            // Bulk insert Tier2 control data
            using var bulkCopy = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, transaction);
            bulkCopy.DestinationTableName = "#Tier2Control";
            bulkCopy.ColumnMappings.Add("RecId", "RecId");
            bulkCopy.ColumnMappings.Add("SysRowVersion", "SysRowVersion");
            bulkCopy.BatchSize = 10000;
            bulkCopy.BulkCopyTimeout = _connectionSettings.CommandTimeout;

            await bulkCopy.WriteToServerAsync(tier2Control, cancellationToken);
        }
    }

    /// <summary>
    /// Context for comparison - which fields are available in BOTH databases
    /// </summary>
    public class ComparisonContext
    {
        public bool HasRecVersion { get; set; }
        public bool HasCreatedDateTime { get; set; }
        public bool HasModifiedDateTime { get; set; }

        public bool IsFallbackMode => !HasCreatedDateTime && !HasModifiedDateTime;
        public bool CanCompare => HasRecVersion;
    }

    /// <summary>
    /// Version info for a single AxDB record
    /// </summary>
    public class AxDbRecordVersion
    {
        public int RecVersion { get; set; }
        public object? CreatedDateTime { get; set; }   // DateTime, DBNull, or null
        public object? ModifiedDateTime { get; set; }  // DateTime, DBNull, or null
    }

    /// <summary>
    /// Result of comparing Tier2 data with AxDB data
    /// </summary>
    public class ComparisonResult
    {
        public HashSet<long> UnchangedRecIds { get; set; } = new HashSet<long>();
        public HashSet<long> ModifiedRecIds { get; set; } = new HashSet<long>();
        public HashSet<long> NewRecIds { get; set; } = new HashSet<long>();
        public HashSet<long> DeletedRecIds { get; set; } = new HashSet<long>();
    }
}
