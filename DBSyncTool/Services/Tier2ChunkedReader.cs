using System.Data;
using DBSyncTool.Helpers;
using Microsoft.Data.SqlClient;

namespace DBSyncTool.Services
{
    /// <summary>
    /// Chunked, resumable Tier2 source for the STREAM route. Looks like ONE IDataReader to
    /// SqlBulkCopy, but reads Tier2 in RecId keyset chunks (StreamChunkSql.Plan.ChunkSql:
    /// TOP (ChunkSize) ... WHERE RECID > @LastRecId ORDER BY RECID), each a short query on the
    /// same connection. A chunk shorter than ChunkSize ends the stream.
    ///
    /// Resume: when a Tier2 read fails with a transient error (TransientSqlErrors - dropped
    /// connection, killed session, Azure failover), the broken connection is discarded and its
    /// pool cleared, the reader waits (StreamChunkSql.RetryDelaysSeconds, interruptible by Stop)
    /// and continues with a new connection after the RecId of the last row it handed out. That
    /// row was completely read and given to SqlBulkCopy, which never saw the error: its AxDB
    /// bulk insert (and the open batch) simply continues, so nothing is lost or duplicated and
    /// AxDB needs no resume query. After MaxResumeRetries the error is thrown (table fails).
    ///
    /// Every row is copied into a buffer with GetValues (columns in order → SequentialAccess
    /// is fine) and served from there; only one row is held, memory stays flat.
    /// </summary>
    public sealed class Tier2ChunkedReader : IDataReader
    {
        private readonly string _connectionString;
        private readonly int _commandTimeout;
        private readonly StreamChunkSql.Plan _plan;
        private readonly string _tableName;
        private readonly Action<string> _logger;
        private readonly CancellationToken _cancellationToken;

        private SqlConnection? _connection;
        private SqlCommand? _command;
        private SqlDataReader? _reader;
        private CancellationTokenRegistration _registration;

        private string[] _names = Array.Empty<string>();
        private Type[] _types = Array.Empty<Type>();
        private string[] _typeNames = Array.Empty<string>();
        private object[] _values = Array.Empty<object>();
        private int _recIdOrdinal = -1;
        private bool _hasRow;
        private bool _done;
        private int _rowsInChunk;

        public Tier2ChunkedReader(string connectionString, int commandTimeout, StreamChunkSql.Plan plan,
            string tableName, Action<string> logger, CancellationToken cancellationToken)
        {
            if (!plan.Chunked)
                throw new ArgumentException("Plan is not chunked", nameof(plan));
            _connectionString = connectionString;
            _commandTimeout = commandTimeout;
            _plan = plan;
            _tableName = tableName;
            _logger = logger;
            _cancellationToken = cancellationToken;
        }

        /// <summary>RecId of the last row handed out (resume point); long.MinValue before the first row.</summary>
        public long LastRecId { get; private set; } = long.MinValue;

        /// <summary>Lower RecId bound of a newest-N copy (null: none).</summary>
        public long? MinRecIdBound { get; private set; }

        public long RowsRead { get; private set; }
        public int ChunksOpened { get; private set; }
        public int Resumes { get; private set; }

        /// <summary>
        /// Computes the newest-N boundary (if any) and opens the first chunk, so metadata is
        /// known and a broken query fails before AxDB is touched. Transient errors are retried
        /// like in Read.
        /// </summary>
        public async Task OpenAsync()
        {
            while (true)
            {
                try
                {
                    _connection = new SqlConnection(_connectionString);
                    await _connection.OpenAsync(_cancellationToken);

                    if (_plan.BoundarySql != null && MinRecIdBound == null)
                    {
                        _logger($"[Tier2 SQL] {_tableName} newest-N boundary: {_plan.BoundarySql}");
                        using var boundary = new SqlCommand(_plan.BoundarySql, _connection) { CommandTimeout = _commandTimeout };
                        using (_cancellationToken.Register(() => { try { boundary.Cancel(); } catch { } }))
                        {
                            object? result = await boundary.ExecuteScalarAsync(_cancellationToken);
                            MinRecIdBound = result == null || result == DBNull.Value ? long.MaxValue : Convert.ToInt64(result);
                        }
                        _logger($"[Tier2] {_tableName}: @MinRecId = {MinRecIdBound}");
                    }

                    OpenChunk();
                    _names = Enumerable.Range(0, _reader!.FieldCount).Select(_reader.GetName).ToArray();
                    _types = Enumerable.Range(0, _reader.FieldCount).Select(_reader.GetFieldType).ToArray();
                    _typeNames = Enumerable.Range(0, _reader.FieldCount).Select(_reader.GetDataTypeName).ToArray();
                    _values = new object[_names.Length];
                    _recIdOrdinal = Array.FindIndex(_names, n => n.Equals("RECID", StringComparison.OrdinalIgnoreCase));
                    if (_recIdOrdinal < 0)
                        throw new InvalidOperationException("Chunked stream: the query returns no RECID column");
                    return;
                }
                catch (Exception ex) when (CanResume(ex))
                {
                    await WaitBeforeResumeAsync(ex);
                }
                catch (SqlException) when (_cancellationToken.IsCancellationRequested)
                {
                    DisposeConnection(cancelReader: true, clearPool: false);
                    throw new OperationCanceledException(_cancellationToken);
                }
                catch
                {
                    DisposeConnection(cancelReader: true, clearPool: false);
                    throw;
                }
            }
        }

        private void OpenChunk()
        {
            if (_connection == null || _connection.State != ConnectionState.Open)
            {
                _connection?.Dispose();
                _connection = new SqlConnection(_connectionString);
                _connection.Open();
            }

            _command = new SqlCommand(_plan.ChunkSql, _connection) { CommandTimeout = _commandTimeout };
            _command.Parameters.Add(StreamChunkSql.LastRecIdParameter, SqlDbType.BigInt).Value = LastRecId;
            if (_plan.BoundarySql != null)
                _command.Parameters.Add(StreamChunkSql.MinRecIdParameter, SqlDbType.BigInt).Value = MinRecIdBound ?? long.MaxValue;

            var command = _command;
            _registration = _cancellationToken.Register(() => { try { command.Cancel(); } catch { } });
            _reader = _command.ExecuteReader(CommandBehavior.SequentialAccess);
            _rowsInChunk = 0;
            ChunksOpened++;
        }

        private void CloseChunk(bool completed)
        {
            _registration.Dispose();
            if (_reader != null)
            {
                // An unfinished SqlDataReader drains all pending rows on Close - cancel first
                if (!completed && !_reader.IsClosed)
                {
                    try { _command?.Cancel(); } catch { }
                }
                try { _reader.Dispose(); } catch { }
                _reader = null;
            }
            _command?.Dispose();
            _command = null;
        }

        private void DisposeConnection(bool cancelReader, bool clearPool)
        {
            CloseChunk(completed: !cancelReader);
            if (_connection != null)
            {
                if (clearPool)
                {
                    try { SqlConnection.ClearPool(_connection); } catch { }
                }
                try { _connection.Dispose(); } catch { }
                _connection = null;
            }
        }

        private bool CanResume(Exception ex) =>
            !_cancellationToken.IsCancellationRequested &&
            Resumes < StreamChunkSql.MaxResumeRetries &&
            TransientSqlErrors.IsTransient(ex);

        private int PrepareResume(Exception ex)
        {
            Resumes++;
            int delay = StreamChunkSql.RetryDelaySeconds(Resumes);
            string from = LastRecId == long.MinValue ? "the start" : $"RecId {LastRecId}";
            _logger($"[{_tableName}] STREAM: Tier2 read failed after {RowsRead:N0} rows ({TransientSqlErrors.Describe(ex)}) - " +
                    $"resume {Resumes}/{StreamChunkSql.MaxResumeRetries} in {delay}s on a new connection, continuing after {from}");
            DisposeConnection(cancelReader: true, clearPool: true);
            return delay;
        }

        private async Task WaitBeforeResumeAsync(Exception ex)
        {
            int delay = PrepareResume(ex);
            await Task.Delay(TimeSpan.FromSeconds(delay), _cancellationToken);
        }

        public bool Read()
        {
            while (true)
            {
                if (_done)
                {
                    _hasRow = false;
                    return false;
                }
                _cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    if (_reader == null)
                        OpenChunk();

                    if (_reader!.Read())
                    {
                        _reader.GetValues(_values);
                        LastRecId = Convert.ToInt64(_values[_recIdOrdinal]);
                        _rowsInChunk++;
                        RowsRead++;
                        _hasRow = true;
                        return true;
                    }

                    // Chunk exhausted: a short chunk is the end of the data
                    bool full = _rowsInChunk >= StreamChunkSql.ChunkSize;
                    CloseChunk(completed: true);
                    if (!full)
                    {
                        _done = true;
                        _hasRow = false;
                        DisposeConnection(cancelReader: false, clearPool: false);
                        return false;
                    }
                }
                catch (Exception ex) when (CanResume(ex))
                {
                    _hasRow = false;
                    int delay = PrepareResume(ex);
                    // Synchronous wait (SqlBulkCopy calls Read synchronously); Stop ends it at once
                    if (_cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(delay)))
                        _cancellationToken.ThrowIfCancellationRequested();
                }
            }
        }

        private object Current(int i)
        {
            if (!_hasRow)
                throw new InvalidOperationException("No current row.");
            return _values[i];
        }

        // ---- IDataRecord: values from the buffered row, metadata cached from the first chunk ----
        public int FieldCount => _names.Length;
        public object this[int i] => Current(i);
        public object this[string name] => Current(GetOrdinal(name));
        public string GetName(int i) => _names[i];
        public int GetOrdinal(string name)
        {
            int i = Array.FindIndex(_names, n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (i < 0) throw new IndexOutOfRangeException(name);
            return i;
        }
        public string GetDataTypeName(int i) => _typeNames[i];
        public Type GetFieldType(int i) => _types[i];
        public object GetValue(int i) => Current(i);
        public bool IsDBNull(int i) => Current(i) is DBNull;
        public int GetValues(object[] values)
        {
            int n = Math.Min(values.Length, _values.Length);
            for (int i = 0; i < n; i++)
                values[i] = Current(i);
            return n;
        }
        public bool GetBoolean(int i) => (bool)Current(i);
        public byte GetByte(int i) => (byte)Current(i);
        public char GetChar(int i) => (char)Current(i);
        public DateTime GetDateTime(int i) => (DateTime)Current(i);
        public decimal GetDecimal(int i) => (decimal)Current(i);
        public double GetDouble(int i) => (double)Current(i);
        public float GetFloat(int i) => (float)Current(i);
        public Guid GetGuid(int i) => (Guid)Current(i);
        public short GetInt16(int i) => (short)Current(i);
        public int GetInt32(int i) => (int)Current(i);
        public long GetInt64(int i) => (long)Current(i);
        public string GetString(int i) => (string)Current(i);
        public IDataReader GetData(int i) => throw new NotSupportedException();
        public long GetBytes(int i, long fieldOffset, byte[]? buffer, int bufferoffset, int length)
        {
            var data = (byte[])Current(i);
            if (buffer == null) return data.Length;
            int count = (int)Math.Max(0, Math.Min(length, data.Length - fieldOffset));
            Array.Copy(data, fieldOffset, buffer, bufferoffset, count);
            return count;
        }
        public long GetChars(int i, long fieldoffset, char[]? buffer, int bufferoffset, int length)
        {
            var data = (string)Current(i);
            if (buffer == null) return data.Length;
            int count = (int)Math.Max(0, Math.Min(length, data.Length - fieldoffset));
            data.CopyTo((int)fieldoffset, buffer, bufferoffset, count);
            return count;
        }

        // ---- IDataReader ----
        public int Depth => 0;
        public bool IsClosed => _done && _reader == null;
        public int RecordsAffected => -1;
        public DataTable? GetSchemaTable() => null;
        public bool NextResult() => false;
        public void Close() => DisposeConnection(cancelReader: !_done, clearPool: false);
        public void Dispose() => Close();
    }
}
