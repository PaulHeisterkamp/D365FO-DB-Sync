using System.Text;
using DBSyncTool.Models;

namespace DBSyncTool.Helpers
{
    /// <summary>
    /// SQL for the chunked STREAM route: the Tier2 rows are read in RecId keyset chunks
    ///   SELECT TOP (ChunkSize) &lt;cols&gt; FROM &lt;source&gt; WHERE [RECID] &gt; @LastRecId ORDER BY [RECID]
    /// so every Tier2 query is short (a dropped connection costs one chunk, not the table) and a
    /// lost connection can resume right after the last RecId handed to SqlBulkCopy.
    /// Pure (TableInfo in, text out): used by Tier2DataService / Tier2ChunkedReader at runtime and
    /// by SqlPreviewBuilder for Get SQL, so both show the same statements.
    /// </summary>
    public static class StreamChunkSql
    {
        /// <summary>
        /// Rows per Tier2 query. 200,000 rows of a wide D365 table (~1 KB) is ~200 MB / a few
        /// seconds on Azure SQL - short enough that index maintenance or a gateway reset rarely
        /// hits a query, large enough that the per-chunk seek + plan overhead is negligible.
        /// </summary>
        public const int ChunkSize = 200_000;

        /// <summary>Transient Tier2 errors tolerated per table (each resumes after the last copied RecId).</summary>
        public const int MaxResumeRetries = 5;

        /// <summary>Wait before resume attempt 1, 2, 3, ... (the last value repeats).</summary>
        public static readonly int[] RetryDelaysSeconds = { 5, 15, 30, 60, 60 };

        public const string LastRecIdParameter = "@LastRecId";
        public const string MinRecIdParameter = "@MinRecId";
        private const string DerivedAlias = "[stream_src]";

        public sealed class Plan
        {
            /// <summary>False → single long reader (today's behavior), see <see cref="FallbackReason"/>.</summary>
            public bool Chunked { get; init; }
            public string FallbackReason { get; init; } = string.Empty;

            /// <summary>Per-chunk query with @LastRecId (and @MinRecId when <see cref="BoundarySql"/> is set).</summary>
            public string ChunkSql { get; init; } = string.Empty;

            /// <summary>
            /// Newest-N RecId copies only: run once before the first chunk, its result becomes
            /// @MinRecId so every chunk stays inside the TOP N set (null: no boundary).
            /// </summary>
            public string? BoundarySql { get; init; }

            /// <summary>How the source was derived, for the log and Get SQL.</summary>
            public string Description { get; init; } = string.Empty;
        }

        public static string RetryDelayText() => string.Join(", ", RetryDelaysSeconds.Select(s => s + "s"));

        public static int RetryDelaySeconds(int attempt) =>
            RetryDelaysSeconds[Math.Min(Math.Max(attempt, 1), RetryDelaysSeconds.Length) - 1];

        public static Plan Build(TableInfo table)
        {
            string? recIdColumn = table.CopyableFields.FirstOrDefault(f => f.Equals("RECID", StringComparison.OrdinalIgnoreCase));
            if (recIdColumn == null)
                return Fallback("the table has no RECID column (keyset chunks need RecId)");

            string cols = string.Join(", ", table.CopyableFields.Select(f => $"[{f}]"));
            string top = $"SELECT TOP ({ChunkSize}) {cols}";

            if (table.StrategyType == CopyStrategyType.System ||
                (table.StrategyType == CopyStrategyType.RecId && table.RecordsToCopy >= table.Tier2RowCount))
            {
                return new Plan
                {
                    Chunked = true,
                    Description = "whole table, ascending RecId",
                    ChunkSql = $"{top} FROM [{table.TableName}] WHERE [RECID] > {LastRecIdParameter} ORDER BY [RECID]"
                };
            }

            if (table.StrategyType == CopyStrategyType.RecId)
            {
                // Newest N rows: fix the lower RecId bound once, then walk up from there
                return new Plan
                {
                    Chunked = true,
                    Description = $"newest {table.RecordsToCopy:N0} rows: RecId >= MIN(RecId) of the TOP {table.RecordsToCopy:N0} by RecId DESC, fixed once before the first chunk",
                    BoundarySql = $"SELECT MIN([RECID]) FROM (SELECT TOP ({table.RecordsToCopy}) [RECID] FROM [{table.TableName}] ORDER BY [RECID] DESC) AS [b]",
                    ChunkSql = $"{top} FROM [{table.TableName}] WHERE [RECID] >= {MinRecIdParameter} AND [RECID] > {LastRecIdParameter} ORDER BY [RECID]"
                };
            }

            // SQL strategy: the fetch SQL (placeholders already replaced exactly as for the
            // in-memory fetch) becomes a derived table
            if (!TryMakeDerivedTable(table.FetchSql, out string inner, out string reason, out bool orderByRemoved))
                return Fallback(reason);

            return new Plan
            {
                Chunked = true,
                Description = "strategy SQL as derived table" + (orderByRemoved ? " (its trailing ORDER BY removed - chunks are ordered by RecId)" : ""),
                // newlines: the strategy SQL may end with a -- comment
                ChunkSql = $"{top} FROM (\n{inner}\n) AS {DerivedAlias} WHERE {DerivedAlias}.[RECID] > {LastRecIdParameter} ORDER BY {DerivedAlias}.[RECID]"
            };
        }

        private static Plan Fallback(string reason) => new Plan { Chunked = false, FallbackReason = reason };

        /// <summary>
        /// Checks that <paramref name="sql"/> is one plain SELECT that can sit in a derived table
        /// and removes a trailing top-level ORDER BY (not allowed there without TOP, and without
        /// meaning for the set). Refused: CTEs / anything not starting with SELECT, several
        /// statements, and top-level TOP / OFFSET / FETCH / INTO / OPTION / FOR / COMPUTE - a TOP
        /// N set re-evaluated per chunk would be wrong or very expensive.
        /// </summary>
        public static bool TryMakeDerivedTable(string sql, out string inner, out string reason, out bool orderByRemoved)
        {
            inner = string.Empty;
            reason = string.Empty;
            orderByRemoved = false;

            string text = (sql ?? string.Empty).Trim();
            while (text.EndsWith(';'))
                text = text.Substring(0, text.Length - 1).TrimEnd();

            var tokens = TopLevelWords(text, out bool unbalanced, out bool hasSemicolon);
            if (unbalanced)
            {
                reason = "the strategy SQL could not be parsed (unbalanced quotes, brackets or parentheses)";
                return false;
            }
            if (hasSemicolon)
            {
                reason = "the strategy SQL contains more than one statement";
                return false;
            }
            if (tokens.Count == 0 || !tokens[0].Word.Equals("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                reason = "the strategy SQL does not start with SELECT (e.g. a WITH/CTE), it cannot be wrapped as a derived table";
                return false;
            }

            foreach (var t in tokens)
            {
                switch (t.Word.ToUpperInvariant())
                {
                    case "TOP":
                    case "OFFSET":
                    case "FETCH":
                        reason = $"the strategy SQL has a top-level {t.Word.ToUpperInvariant()} - its row set would be re-evaluated for every chunk";
                        return false;
                    case "INTO":
                    case "OPTION":
                    case "FOR":
                    case "COMPUTE":
                        reason = $"the strategy SQL has a top-level {t.Word.ToUpperInvariant()} clause, it cannot be used as a derived table";
                        return false;
                }
            }

            // Last top-level ORDER BY → strip to the end (nothing allowed after it got here)
            int cut = -1;
            for (int i = tokens.Count - 2; i >= 0; i--)
            {
                if (tokens[i].Word.Equals("ORDER", StringComparison.OrdinalIgnoreCase) &&
                    tokens[i + 1].Word.Equals("BY", StringComparison.OrdinalIgnoreCase))
                {
                    cut = tokens[i].Start;
                    break;
                }
            }
            if (cut >= 0)
            {
                text = text.Substring(0, cut).TrimEnd();
                orderByRemoved = true;
            }

            inner = text;
            return true;
        }

        private readonly record struct Token(string Word, int Start);

        /// <summary>
        /// Words outside parentheses, string literals, quoted/bracketed identifiers and comments.
        /// </summary>
        private static List<Token> TopLevelWords(string s, out bool unbalanced, out bool hasSemicolon)
        {
            var tokens = new List<Token>();
            unbalanced = false;
            hasSemicolon = false;
            int depth = 0;
            int i = 0;

            while (i < s.Length)
            {
                char c = s[i];

                if (c == '\'' || c == '"' || c == '[')
                {
                    char close = c == '[' ? ']' : c;
                    i++;
                    while (true)
                    {
                        if (i >= s.Length) { unbalanced = true; return tokens; }
                        if (s[i] == close)
                        {
                            if (i + 1 < s.Length && s[i + 1] == close) { i += 2; continue; }  // escaped '' ]] ""
                            i++;
                            break;
                        }
                        i++;
                    }
                    continue;
                }

                if (c == '-' && i + 1 < s.Length && s[i + 1] == '-')
                {
                    while (i < s.Length && s[i] != '\n') i++;
                    continue;
                }

                if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
                {
                    int nest = 1;
                    i += 2;
                    while (nest > 0)
                    {
                        if (i + 1 >= s.Length) { unbalanced = true; return tokens; }
                        if (s[i] == '/' && s[i + 1] == '*') { nest++; i += 2; }
                        else if (s[i] == '*' && s[i + 1] == '/') { nest--; i += 2; }
                        else i++;
                    }
                    continue;
                }

                if (c == '(') { depth++; i++; continue; }
                if (c == ')')
                {
                    depth--;
                    if (depth < 0) { unbalanced = true; return tokens; }
                    i++;
                    continue;
                }
                if (c == ';' && depth == 0) { hasSemicolon = true; i++; continue; }

                if (char.IsLetter(c) || c == '_' || c == '@' || c == '#')
                {
                    int start = i;
                    var sb = new StringBuilder();
                    while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_' || s[i] == '@' || s[i] == '#' || s[i] == '$'))
                        sb.Append(s[i++]);
                    // N'...' prefix: the quote is handled on the next loop pass
                    if (depth == 0)
                        tokens.Add(new Token(sb.ToString(), start));
                    continue;
                }

                i++;
            }

            if (depth != 0)
                unbalanced = true;
            return tokens;
        }
    }
}
