using System.Data;
using System.Diagnostics;

namespace DBSyncTool.Helpers
{
    /// <summary>
    /// IDataReader decorator for the STREAM route: sits between the Tier2 SqlDataReader and
    /// SqlBulkCopy and, while rows pass through, tracks row count, MIN/MAX(RecId),
    /// MAX(SysRowVersion), an approximate payload size and the time spent reading from Tier2.
    ///
    /// Each row is read once with GetValues() into a reused buffer and served from there. This
    /// keeps the source reader usable with CommandBehavior.SequentialAccess (which only allows
    /// increasing column ordinals) even though SqlBulkCopy reads the columns in destination
    /// column order. Only one row is held at a time, so memory stays flat.
    ///
    /// Deliberately implements IDataReader only (not DbDataReader): SqlBulkCopy then reads every
    /// value through GetValue, which is all the buffer supports.
    /// </summary>
    public sealed class StreamingDataReader : IDataReader
    {
        private readonly IDataReader _inner;
        private readonly CancellationToken _cancellationToken;
        private readonly object[] _values;
        private readonly int _recIdOrdinal;
        private readonly int _rowVersionOrdinal;
        private long _readTicks;
        private bool _hasRow;

        public StreamingDataReader(IDataReader inner, CancellationToken cancellationToken)
        {
            _inner = inner;
            _cancellationToken = cancellationToken;
            _values = new object[inner.FieldCount];
            _recIdOrdinal = FindOrdinal(inner, "RECID");
            _rowVersionOrdinal = FindOrdinal(inner, "SYSROWVERSION");
        }

        /// <summary>Rows handed to the consumer so far.</summary>
        public long RowCount { get; private set; }

        /// <summary>MIN / MAX(RecId) of the rows read; null when there is no RecId column or no row.</summary>
        public long? MinRecId { get; private set; }
        public long? MaxRecId { get; private set; }

        /// <summary>MAX(SysRowVersion) of the rows read; null when there is no such column or no row.</summary>
        public byte[]? MaxRowVersion { get; private set; }

        public bool HasRecIdColumn => _recIdOrdinal >= 0;
        public bool HasRowVersionColumn => _rowVersionOrdinal >= 0;

        /// <summary>Approximate payload bytes (strings 2 bytes/char, binary by length, other values 8 bytes).</summary>
        public long ApproxBytes { get; private set; }

        /// <summary>Time spent inside the Tier2 reader (network + server), i.e. the "fetch" share.</summary>
        public TimeSpan ReadTime => TimeSpan.FromSeconds((double)_readTicks / Stopwatch.Frequency);

        /// <summary>Set when the Tier2 reader itself threw, so the caller can report FetchError instead of InsertError.</summary>
        public Exception? SourceException { get; private set; }

        private static int FindOrdinal(IDataReader reader, string name)
        {
            for (int i = 0; i < reader.FieldCount; i++)
            {
                if (string.Equals(reader.GetName(i), name, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return -1;
        }

        public bool Read()
        {
            _cancellationToken.ThrowIfCancellationRequested();

            long start = Stopwatch.GetTimestamp();
            try
            {
                _hasRow = _inner.Read();
                if (_hasRow)
                    _inner.GetValues(_values);
            }
            catch (Exception ex)
            {
                SourceException = ex;
                _hasRow = false;
                throw;
            }
            finally
            {
                _readTicks += Stopwatch.GetTimestamp() - start;
            }

            if (!_hasRow)
                return false;

            RowCount++;

            if (_recIdOrdinal >= 0 && _values[_recIdOrdinal] is not DBNull)
            {
                long recId = Convert.ToInt64(_values[_recIdOrdinal]);
                if (MinRecId == null || recId < MinRecId) MinRecId = recId;
                if (MaxRecId == null || recId > MaxRecId) MaxRecId = recId;
            }

            if (_rowVersionOrdinal >= 0 && _values[_rowVersionOrdinal] is byte[] rowVersion &&
                TimestampHelper.CompareTimestamp(rowVersion, MaxRowVersion) > 0)
            {
                MaxRowVersion = rowVersion;  // GetValues returns a new array per row - safe to keep
            }

            long bytes = 0;
            foreach (var value in _values)
            {
                bytes += value switch
                {
                    string s => 2L * s.Length,
                    byte[] b => b.Length,
                    DBNull => 0,
                    _ => 8
                };
            }
            ApproxBytes += bytes;

            return true;
        }

        private object Current(int i)
        {
            if (!_hasRow)
                throw new InvalidOperationException("No current row.");
            return _values[i];
        }

        // ---- IDataRecord: values from the buffered row, metadata from the source ----
        public int FieldCount => _inner.FieldCount;
        public object this[int i] => Current(i);
        public object this[string name] => Current(GetOrdinal(name));
        public string GetName(int i) => _inner.GetName(i);
        public int GetOrdinal(string name) => _inner.GetOrdinal(name);
        public string GetDataTypeName(int i) => _inner.GetDataTypeName(i);
        public Type GetFieldType(int i) => _inner.GetFieldType(i);
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
        public int Depth => _inner.Depth;
        public bool IsClosed => _inner.IsClosed;
        public int RecordsAffected => _inner.RecordsAffected;
        public DataTable? GetSchemaTable() => _inner.GetSchemaTable();
        public bool NextResult() => false;  // one result set only
        public void Close() => _inner.Close();
        public void Dispose() => _inner.Dispose();
    }
}
