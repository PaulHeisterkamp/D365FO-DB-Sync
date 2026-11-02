using System.Data;
using Microsoft.Data.SqlClient;
using DBSyncTool.Helpers;
using DBSyncTool.Models;

namespace DBSyncTool.Services
{
    public class Tier2DataService
    {
        private readonly ConnectionSettings _connectionSettings;
        private readonly string _connectionString;
        private readonly Action<string> _logger;

        public Tier2DataService(ConnectionSettings connectionSettings, Action<string> logger)
        {
            _connectionSettings = connectionSettings;
            _connectionString = connectionSettings.BuildConnectionString(isAzure: true);
            _logger = logger;
        }

        /// <summary>
        /// Tests the connection to Tier2 database
        /// </summary>
        public async Task<bool> TestConnectionAsync()
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();
            return connection.State == ConnectionState.Open;
        }

        /// <summary>
        /// Discovers all tables in Tier2 database
        /// Returns list of (TableName, RowCount, SizeGB, BytesPerRow)
        /// </summary>
        public async Task<List<(string TableName, long RowCount, decimal SizeGB, long BytesPerRow)>> DiscoverTablesAsync(string? specificTableName = null, bool includeEmpty = false)
        {
            // Build query with optional table name filter
            string tableFilter = !string.IsNullOrWhiteSpace(specificTableName)
                ? "AND UPPER(o.name) = @TableName"
                : "";

            string havingClause = includeEmpty
                ? "HAVING MAX(s.row_count) >= 0"
                : "HAVING MAX(s.row_count) > 0";

            string query = $@"
                SELECT
                    o.name AS TableName,
                    MAX(s.row_count) AS [RowCount],
                    SUM(s.reserved_page_count) * 8.0 / (1024 * 1024) AS [SizeGB],
                    CASE
                        WHEN MAX(s.row_count) > 0
                        THEN (8 * 1024 * SUM(s.reserved_page_count)) / MAX(s.row_count)
                        ELSE 0
                    END AS [BytesPerRow]
                FROM sys.dm_db_partition_stats s
                INNER JOIN sys.objects o ON o.object_id = s.object_id
                WHERE o.type = 'U'
                {tableFilter}
                GROUP BY o.name
                {havingClause}
                ORDER BY [SizeGB] DESC";

            _logger($"[Tier2] Executing table discovery query");
            _logger($"[Tier2 SQL] {query}");

            var results = new List<(string, long, decimal, long)>();

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
                string tableName = reader.GetString(0);
                long rowCount = reader.GetInt64(1);
                decimal sizeGB = reader.GetDecimal(2);
                long bytesPerRow = reader.GetInt64(3);

                // Only include D365 tables (uppercase letters, numbers, underscores)
                if (IsD365Table(tableName))
                {
                    results.Add((tableName, rowCount, sizeGB, bytesPerRow));
                }
            }

            return results;
        }

        /// <summary>
        /// Gets row count / size info for many tables in one query directly from SQL Server
        /// metadata (no SQLDICTIONARY). Used for System tables that may be absent from
        /// SQLDICTIONARY. Returned dictionary is keyed by UPPER table name; tables that do
        /// not exist are simply absent from the result.
        /// </summary>
        public async Task<Dictionary<string, (long RowCount, decimal SizeGB, long BytesPerRow)>> GetSystemTablesInfoAsync(IEnumerable<string> tableNames)
        {
            var result = new Dictionary<string, (long, decimal, long)>(StringComparer.OrdinalIgnoreCase);
            var names = tableNames.Select(n => n.ToUpper()).Distinct().ToList();
            if (names.Count == 0)
                return result;

            using var connection = new SqlConnection(_connectionString);
            using var command = new SqlCommand(string.Empty, connection);
            string inClause = BuildInClause(command, names);

            command.CommandText = $@"
                SELECT
                    o.name AS TableName,
                    MAX(s.row_count) AS [RowCount],
                    SUM(s.reserved_page_count) * 8.0 / (1024 * 1024) AS [SizeGB],
                    CASE
                        WHEN MAX(s.row_count) > 0
                        THEN (8 * 1024 * SUM(s.reserved_page_count)) / MAX(s.row_count)
                        ELSE 0
                    END AS [BytesPerRow]
                FROM sys.dm_db_partition_stats s
                INNER JOIN sys.objects o ON o.object_id = s.object_id
                WHERE o.type = 'U'
                  AND SCHEMA_NAME(o.schema_id) = 'dbo'
                  AND UPPER(o.name) IN ({inClause})
                GROUP BY o.name";
            command.CommandTimeout = _connectionSettings.CommandTimeout;

            await connection.OpenAsync();
            using var reader = await command.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                string name = reader.GetString(0).ToUpper();
                long rowCount = reader.GetInt64(1);
                decimal sizeGB = reader.GetDecimal(2);
                long bytesPerRow = reader.GetInt64(3);
                result[name] = (rowCount, sizeGB, bytesPerRow);
            }

            return result;
        }

        /// <summary>
        /// Gets the physical column names for many tables in one query from
        /// INFORMATION_SCHEMA.COLUMNS. Used for System tables that are not present in
        /// SQLDICTIONARY. Returned dictionary is keyed by UPPER table name, columns ordered
        /// by ORDINAL_POSITION.
        /// </summary>
        public async Task<Dictionary<string, List<string>>> GetTablesColumnsAsync(IEnumerable<string> tableNames)
        {
            return await GetTablesColumnsAsync(_connectionString, _connectionSettings.CommandTimeout, tableNames);
        }

        /// <summary>
        /// Shared INFORMATION_SCHEMA column lookup usable against any connection string.
        /// </summary>
        internal static async Task<Dictionary<string, List<string>>> GetTablesColumnsAsync(
            string connectionString, int commandTimeout, IEnumerable<string> tableNames)
        {
            var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var names = tableNames.Select(n => n.ToUpper()).Distinct().ToList();
            if (names.Count == 0)
                return result;

            using var connection = new SqlConnection(connectionString);
            using var command = new SqlCommand(string.Empty, connection);
            string inClause = BuildInClause(command, names);

            command.CommandText = $@"
                SELECT c.TABLE_NAME, c.COLUMN_NAME
                FROM INFORMATION_SCHEMA.COLUMNS c
                INNER JOIN INFORMATION_SCHEMA.TABLES t
                    ON t.TABLE_SCHEMA = c.TABLE_SCHEMA
                   AND t.TABLE_NAME = c.TABLE_NAME
                WHERE c.TABLE_SCHEMA = 'dbo'
                  AND t.TABLE_TYPE = 'BASE TABLE'
                  AND UPPER(c.TABLE_NAME) IN ({inClause})
                ORDER BY c.TABLE_NAME, c.ORDINAL_POSITION";
            command.CommandTimeout = commandTimeout;

            await connection.OpenAsync();
            using var reader = await command.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                string table = reader.GetString(0).ToUpper();
                string column = reader.GetString(1);
                if (!result.TryGetValue(table, out var list))
                {
                    list = new List<string>();
                    result[table] = list;
                }
                list.Add(column);
            }

            return result;
        }

        /// <summary>
        /// Adds an @p0,@p1,... parameter set for an IN clause and returns the placeholder list.
        /// </summary>
        private static string BuildInClause(SqlCommand command, List<string> values)
        {
            var placeholders = new List<string>(values.Count);
            for (int i = 0; i < values.Count; i++)
            {
                string p = "@p" + i;
                command.Parameters.AddWithValue(p, values[i]);
                placeholders.Add(p);
            }
            return string.Join(",", placeholders);
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

            _logger($"[Tier2 SQL] Getting TableID for {tableName}: {query}");

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

            _logger($"[Tier2 SQL] Getting fields for TableID {tableId}");

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
        /// Fetches data from a table using RecId strategy
        /// </summary>
        public async Task<DataTable> FetchDataByRecIdAsync(string tableName, List<string> fields, int recordCount, CancellationToken cancellationToken)
        {
            string fieldList = string.Join(", ", fields.Select(f => $"[{f}]"));
            string query = $"SELECT TOP ({recordCount}) {fieldList} FROM [{tableName}] ORDER BY RecId DESC";

            _logger($"[Tier2 SQL] Fetching data from {tableName}: {query}");

            return await ExecuteQueryAsync(query, cancellationToken);
        }

        /// <summary>
        /// Fetches data from a table using ModifiedDate strategy
        /// </summary>
        public async Task<DataTable> FetchDataByModifiedDateAsync(string tableName, List<string> fields, int days, CancellationToken cancellationToken)
        {
            DateTime cutoffDate = DateTime.UtcNow.AddDays(-days);
            string fieldList = string.Join(", ", fields.Select(f => $"[{f}]"));
            string query = $"SELECT {fieldList} FROM [{tableName}] WHERE [MODIFIEDDATETIME] > @CutoffDate";

            _logger($"[Tier2 SQL] Fetching data from {tableName} (ModifiedDate > {cutoffDate:yyyy-MM-dd HH:mm:ss}): {query}");

            using var connection = new SqlConnection(_connectionString);
            using var command = new SqlCommand(query, connection);
            command.Parameters.AddWithValue("@CutoffDate", cutoffDate);
            command.CommandTimeout = _connectionSettings.CommandTimeout;

            var dataTable = new DataTable();

            await connection.OpenAsync(cancellationToken);
            using var adapter = new SqlDataAdapter(command);
            FillCancellable(adapter, command, dataTable, cancellationToken);

            return dataTable;
        }

        /// <summary>
        /// Fetches data using pre-generated SQL with optional date parameter
        /// </summary>
        public async Task<DataTable> FetchDataBySqlAsync(string tableName, string sql, CancellationToken cancellationToken)
        {
            _logger($"[Tier2 SQL] Fetching data from {tableName}: {sql}");

            using var connection = new SqlConnection(_connectionString);
            using var command = new SqlCommand(sql, connection);
            command.CommandTimeout = _connectionSettings.CommandTimeout;

            var dataTable = new DataTable();

            await connection.OpenAsync(cancellationToken);
            using var adapter = new SqlDataAdapter(command);
            FillCancellable(adapter, command, dataTable, cancellationToken);

            return dataTable;
        }

        /// <summary>
        /// An open Tier2 reader for the STREAM route. Disposing it closes reader, command and
        /// connection and drops the cancellation registration.
        /// </summary>
        public sealed class StreamSource : IDisposable
        {
            internal SqlConnection Connection { get; init; } = null!;
            internal SqlCommand Command { get; init; } = null!;
            public SqlDataReader Reader { get; internal set; } = null!;
            public CommandBehavior Behavior { get; internal set; }
            internal CancellationTokenRegistration Registration { get; set; }

            /// <summary>Set by the caller once every row was read.</summary>
            public bool Completed { get; set; }

            public void Dispose()
            {
                Registration.Dispose();

                // Closing a SqlDataReader that still has rows pending drains them all from the
                // server first - for a multi-GB table after an AxDB error that would take as long
                // as the copy itself. Cancel the command first so Close returns at once.
                if (!Completed && Reader != null && !Reader.IsClosed)
                {
                    try { Command.Cancel(); } catch { /* best-effort */ }
                }

                try { Reader?.Dispose(); } catch { /* reader may be mid-stream after a cancel */ }
                Command.Dispose();
                Connection.Dispose();
            }
        }

        /// <summary>
        /// STREAM route: executes the normal fetch SQL (same text as FetchDataBySqlAsync) on a
        /// dedicated connection and returns the open reader instead of filling a DataTable.
        /// CommandBehavior.SequentialAccess (columns are consumed strictly in order by
        /// StreamingDataReader's GetValues); if the server/driver rejects it, it falls back to
        /// the default behavior. Same CommandTimeout as every Tier2 query (per network read,
        /// like DataAdapter.Fill). The cancellation token is wired to SqlCommand.Cancel so Stop
        /// aborts the server-side query while rows are flowing.
        /// </summary>
        public async Task<StreamSource> OpenStreamReaderAsync(string tableName, string sql, CancellationToken cancellationToken)
        {
            _logger($"[Tier2 SQL] Streaming data from {tableName}: {sql}");

            var connection = new SqlConnection(_connectionString);
            var command = new SqlCommand(sql, connection) { CommandTimeout = _connectionSettings.CommandTimeout };
            var source = new StreamSource { Connection = connection, Command = command };

            try
            {
                await connection.OpenAsync(cancellationToken);
                source.Registration = cancellationToken.Register(() =>
                {
                    try { command.Cancel(); } catch { /* best-effort */ }
                });

                try
                {
                    source.Reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
                    source.Behavior = CommandBehavior.SequentialAccess;
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException && !cancellationToken.IsCancellationRequested)
                {
                    _logger($"[Tier2] {tableName}: SequentialAccess not usable ({ex.Message}), streaming with default behavior");
                    source.Reader = await command.ExecuteReaderAsync(CommandBehavior.Default, cancellationToken);
                    source.Behavior = CommandBehavior.Default;
                }

                return source;
            }
            catch (SqlException) when (cancellationToken.IsCancellationRequested)
            {
                source.Dispose();
                throw new OperationCanceledException(cancellationToken);
            }
            catch
            {
                source.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Chunked STREAM route: opens a <see cref="Tier2ChunkedReader"/> for the plan built by
        /// StreamChunkSql (RecId keyset chunks, resumable after transient connection errors).
        /// The first chunk is opened here, before AxDB is touched.
        /// </summary>
        public async Task<Tier2ChunkedReader> OpenChunkedStreamReaderAsync(string tableName, StreamChunkSql.Plan plan, CancellationToken cancellationToken)
        {
            _logger($"[Tier2 SQL] Streaming {tableName} in chunks of {StreamChunkSql.ChunkSize:N0} rows ({plan.Description}): {plan.ChunkSql}");

            var reader = new Tier2ChunkedReader(_connectionString, _connectionSettings.CommandTimeout, plan, tableName, _logger, cancellationToken);
            try
            {
                await reader.OpenAsync();
                return reader;
            }
            catch
            {
                reader.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Fetches control data (RecId, SysRowVersion) for optimized comparison
        /// </summary>
        public async Task<DataTable> FetchControlDataAsync(
            string tableName,
            int recordCount,
            string? sqlTemplate,
            CancellationToken cancellationToken)
        {
            string sql;

            if (!string.IsNullOrWhiteSpace(sqlTemplate))
            {
                // SQL strategy: Replace * with RecId, SysRowVersion
                // Also replace @recordCount placeholder if present
                // Replace @sysRowVersionFilter with (1 = 1) - not used in control queries
                sql = sqlTemplate
                    .Replace("*", "RecId, SysRowVersion", StringComparison.OrdinalIgnoreCase)
                    .Replace("@recordCount", recordCount.ToString())
                    .Replace("@sysRowVersionFilter", "(1 = 1)", StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                // RecId strategy: Default control query
                sql = $@"
                SELECT TOP ({recordCount}) RecId, SysRowVersion
                FROM [{tableName}]
                ORDER BY RecId DESC";
            }

            _logger($"[Tier2 SQL] Control query: {sql}");

            using var connection = new SqlConnection(_connectionString);
            using var command = new SqlCommand(sql, connection);
            command.CommandTimeout = _connectionSettings.CommandTimeout;

            var dataTable = new DataTable();
            await connection.OpenAsync(cancellationToken);

            using var adapter = new SqlDataAdapter(command);
            FillCancellable(adapter, command, dataTable, cancellationToken);

            return dataTable;
        }

        /// <summary>
        /// Fetches data with SysRowVersion filter
        /// </summary>
        public async Task<DataTable> FetchDataByTimestampAsync(
            string tableName,
            List<string> fields,
            int recordCount,
            byte[] timestampThreshold,
            long minRecId,
            string? sqlTemplate,
            CancellationToken cancellationToken)
        {
            string sql;
            string fieldList = string.Join(", ", fields.Select(f => $"[{f}]"));

            if (!string.IsNullOrWhiteSpace(sqlTemplate) &&
                sqlTemplate.Contains("@sysRowVersionFilter", StringComparison.OrdinalIgnoreCase))
            {
                // SQL strategy with placeholder: Use template
                sql = sqlTemplate
                    .Replace("*", fieldList, StringComparison.OrdinalIgnoreCase)
                    .Replace("@recordCount", recordCount.ToString())
                    .Replace("@sysRowVersionFilter", "SysRowVersion >= @Threshold AND RecId >= @MinRecId", StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                // RecId strategy OR SQL without placeholder: Use default query
                sql = $@"
                SELECT TOP ({recordCount}) {fieldList}
                FROM [{tableName}]
                WHERE SysRowVersion >= @Threshold
                  AND RecId >= @MinRecId
                ORDER BY RecId DESC";
            }

            _logger($"[Tier2 SQL] Timestamp query: {sql}");
            byte[] effectiveThreshold = timestampThreshold ?? new byte[8];
            _logger($"[Tier2] @Threshold = {TimestampHelper.ToHexString(effectiveThreshold)}, @MinRecId = {minRecId}");

            using var connection = new SqlConnection(_connectionString);
            using var command = new SqlCommand(sql, connection);

            // Ensure timestamp parameter is never null - use minimum timestamp (all zeros) if null
            var param = command.Parameters.Add("@Threshold", System.Data.SqlDbType.Binary, 8);
            param.Value = effectiveThreshold;

            command.Parameters.AddWithValue("@MinRecId", minRecId);
            command.CommandTimeout = _connectionSettings.CommandTimeout;

            var dataTable = new DataTable();
            await connection.OpenAsync(cancellationToken);

            using var adapter = new SqlDataAdapter(command);
            FillCancellable(adapter, command, dataTable, cancellationToken);

            return dataTable;
        }

        /// <summary>
        /// Executes a query and returns the result as a DataTable
        /// </summary>
        private async Task<DataTable> ExecuteQueryAsync(string query, CancellationToken cancellationToken)
        {
            using var connection = new SqlConnection(_connectionString);
            using var command = new SqlCommand(query, connection);
            command.CommandTimeout = _connectionSettings.CommandTimeout;

            var dataTable = new DataTable();

            await connection.OpenAsync(cancellationToken);
            using var adapter = new SqlDataAdapter(command);
            FillCancellable(adapter, command, dataTable, cancellationToken);

            return dataTable;
        }

        /// <summary>
        /// Runs the synchronous SqlDataAdapter.Fill while honoring the cancellation token.
        /// SqlDataAdapter.Fill blocks the thread and cannot be cancelled directly, so we
        /// register the token to call SqlCommand.Cancel(), which aborts the in-flight query
        /// on the server. This makes a blocked Fill() return immediately when Stop is pressed,
        /// without converting the load logic to async reader-based methods.
        /// </summary>
        private static void FillCancellable(SqlDataAdapter adapter, SqlCommand command, DataTable dataTable, CancellationToken cancellationToken)
        {
            using (cancellationToken.Register(() =>
            {
                try { command.Cancel(); } catch { /* best-effort cancel; ignore if already finished */ }
            }))
            {
                try
                {
                    adapter.Fill(dataTable);
                }
                catch (SqlException) when (cancellationToken.IsCancellationRequested)
                {
                    // Fill was aborted by command.Cancel() - surface as cancellation
                    throw new OperationCanceledException(cancellationToken);
                }
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

            _logger("[Tier2] Loading SQLDICTIONARY cache...");
            _logger($"[Tier2 SQL] {query}");

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

            _logger($"[Tier2] Loaded SQLDICTIONARY cache: {cache.GetStats()}");

            return cache;
        }

        /// <summary>
        /// Checks if a table name matches D365 naming pattern (uppercase, numbers, underscores)
        /// </summary>
        private bool IsD365Table(string tableName)
        {
            if (string.IsNullOrWhiteSpace(tableName))
                return false;

            return tableName.All(c => char.IsUpper(c) || char.IsDigit(c) || c == '_');
        }
    }
}
