using System.Text.RegularExpressions;

namespace DBSyncTool.Helpers
{
    /// <summary>
    /// Matches table names against the lines of a "Tables to Copy" / "Tables to Exclude" list.
    ///
    /// Supported line types:
    ///  - exact name           CUSTTABLE                 (case-insensitive)
    ///  - wildcard pattern     Sys*  /  *Staging         ('*' = zero or more characters)
    ///  - table group rule     TableGroup:Transaction,WorksheetHeader,5,6
    ///                         (D365FO TableGroup enum names or numbers; resolved from the
    ///                         AxDB TABLEMETADATATABLE)
    ///
    /// Patterns are compiled once, so a long list stays fast: exact names are a hash lookup
    /// and each wildcard regex is built only once per matcher.
    /// </summary>
    public sealed class TablePatternMatcher
    {
        public const string TableGroupPrefix = "TableGroup:";

        /// <summary>D365FO TableGroup enum (value stored in TABLEMETADATATABLE.TABLEGROUP).</summary>
        public static readonly IReadOnlyDictionary<string, int> TableGroupValues =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["Miscellaneous"] = 0,
                ["Parameter"] = 1,
                ["Group"] = 2,
                ["Main"] = 3,
                ["Transaction"] = 4,
                ["WorksheetHeader"] = 5,
                ["WorksheetLine"] = 6,
                ["Framework"] = 7,
                ["Reference"] = 8,
                ["Worksheet"] = 9,
                ["TransactionHeader"] = 10,
                ["TransactionLine"] = 11,
                ["Staging"] = 12
            };

        private readonly HashSet<string> _exactNames = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<Regex> _wildcards = new();
        private readonly HashSet<int> _tableGroups = new();
        private readonly IReadOnlyDictionary<string, int>? _tableGroupMap;

        /// <param name="patterns">Trimmed, non-empty pattern lines.</param>
        /// <param name="tableGroupMap">
        /// SQL table name (case-insensitive) → TableGroup. Required when any line is a
        /// TableGroup rule; tables missing from the map never match a group rule.
        /// </param>
        public TablePatternMatcher(IEnumerable<string> patterns, IReadOnlyDictionary<string, int>? tableGroupMap = null)
        {
            _tableGroupMap = tableGroupMap;

            foreach (var raw in patterns)
            {
                var pattern = raw.Trim();
                if (pattern.Length == 0)
                    continue;

                if (IsTableGroupRule(pattern))
                {
                    _tableGroups.UnionWith(ParseTableGroupRule(pattern));
                }
                else if (pattern.Contains('*'))
                {
                    string regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
                    _wildcards.Add(new Regex(regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
                }
                else
                {
                    _exactNames.Add(pattern);
                }
            }

            if (_tableGroups.Count > 0 && _tableGroupMap == null)
                throw new InvalidOperationException("A TableGroup rule needs the table group map (AxDB TABLEMETADATATABLE)");
        }

        /// <summary>True when at least one line is a TableGroup rule.</summary>
        public bool HasTableGroupRule => _tableGroups.Count > 0;

        public bool Matches(string tableName)
        {
            if (_exactNames.Contains(tableName))
                return true;

            if (_tableGroups.Count > 0 &&
                _tableGroupMap!.TryGetValue(tableName, out int group) &&
                _tableGroups.Contains(group))
                return true;

            foreach (var regex in _wildcards)
            {
                if (regex.IsMatch(tableName))
                    return true;
            }

            return false;
        }

        public static bool IsTableGroupRule(string pattern)
        {
            return pattern.TrimStart().StartsWith(TableGroupPrefix, StringComparison.OrdinalIgnoreCase);
        }

        public static bool ContainsTableGroupRule(IEnumerable<string> patterns)
        {
            return patterns.Any(IsTableGroupRule);
        }

        /// <summary>
        /// Parses "TableGroup:Transaction,WorksheetHeader,12" into enum values.
        /// Throws on an empty rule or an unknown group name/number.
        /// </summary>
        public static HashSet<int> ParseTableGroupRule(string pattern)
        {
            var body = pattern.Trim().Substring(TableGroupPrefix.Length);
            var groups = new HashSet<int>();

            foreach (var part in body.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var token = part.Trim();
                if (token.Length == 0)
                    continue;

                if (TableGroupValues.TryGetValue(token, out int value) ||
                    (int.TryParse(token, out value) && TableGroupValues.Values.Contains(value)))
                {
                    groups.Add(value);
                    continue;
                }

                throw new Exception(
                    $"Unknown table group '{token}' in '{pattern.Trim()}'. Valid values: " +
                    string.Join(", ", TableGroupValues.OrderBy(kv => kv.Value).Select(kv => $"{kv.Key} ({kv.Value})")));
            }

            if (groups.Count == 0)
                throw new Exception($"'{pattern.Trim()}' does not name any table group");

            return groups;
        }
    }
}
