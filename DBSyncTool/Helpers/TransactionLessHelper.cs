namespace DBSyncTool.Helpers
{
    /// <summary>
    /// A table from the AxDB SQLDICTIONARY that references items or products, with the key
    /// columns it has (upper-case SQL names, see <see cref="TransactionLessHelper.KeyColumns"/>).
    /// </summary>
    public sealed class ItemTableCandidate
    {
        public string TableName { get; init; } = string.Empty;
        public int TableGroup { get; init; }
        public HashSet<string> Columns { get; init; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>SQL data type per column (e.g. "bigint", "nvarchar"); empty when unknown.</summary>
        public Dictionary<string, string> ColumnTypes { get; init; } = new(StringComparer.OrdinalIgnoreCase);

        public bool Has(string column) => Columns.Contains(column);

        public bool IsText(string column) =>
            ColumnTypes.TryGetValue(column, out var type) && type.Contains("char", StringComparison.OrdinalIgnoreCase);

        public bool IsInteger(string column) =>
            ColumnTypes.TryGetValue(column, out var type) && (type.Equals("bigint", StringComparison.OrdinalIgnoreCase) || type.Equals("int", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A column of a table from the AxDB SQLDICTIONARY (FIELDID &lt;&gt; 0) with its SQL data type
    /// from INFORMATION_SCHEMA.COLUMNS. <see cref="Array"/> is the SQLDICTIONARY ARRAY index
    /// (a UtcDateTime field has a second entry, ARRAY = 2, for its ...TZID column).
    /// </summary>
    public sealed record TableColumnInfo(string SqlName, int FieldId, int Array, string DataType);

    /// <summary>Result of <see cref="TransactionLessHelper.BuildInventDimStrategyLine"/>.</summary>
    public sealed class InventDimStrategy
    {
        /// <summary>The Copy strategy line, or null when INVENTDIM has none of the required columns.</summary>
        public string? Line { get; set; }

        /// <summary>At least one of these must be non-empty.</summary>
        public List<string> RequiredAnyColumns { get; } = new();

        /// <summary>Inventory dimension columns that must be empty (column → empty literal).</summary>
        public List<(string Column, string EmptyValue)> EmptyColumns { get; } = new();

        /// <summary>Non-dimension columns ignored by the filter (system/key/hash/companion columns).</summary>
        public List<string> IgnoredColumns { get; } = new();

        /// <summary>Dimension candidates whose data type has no known "empty" value - not filtered.</summary>
        public List<string> UnsupportedColumns { get; } = new();
    }

    /// <summary>
    /// "Init transaction-less copy": builds the Tables to Exclude additions and the Copy
    /// strategy lines that limit item/product master data to items used on sales lines.
    ///
    /// Transaction tables are excluded (not copied; AxDB content is left as it is). Master
    /// data that references items is filtered on Tier2 to the items found in SALESLINE, so
    /// the copied item tables stay consistent with each other. INVENTDIM is limited to the
    /// site / warehouse / status level dimensions (<see cref="BuildInventDimStrategyLine"/>).
    /// Pure functions only - the database lookups live in AxDbDataService / MainForm.
    /// </summary>
    public static class TransactionLessHelper
    {
        /// <summary>DefaultValues.ini section holding the exclusion lines.</summary>
        public const string ExcludeSection = "TransactionLessExclude";

        /// <summary>
        /// DefaultValues.ini / CustomValues.ini section with item relation tables whose item column
        /// has a non-standard name, one "TABLE|COLUMN" per line (e.g. a customer alternative-items
        /// table with an ITEM column). See <see cref="ParseItemRelations"/>.
        /// </summary>
        public const string ItemRelationSection = "TransactionLessItemRelations";

        /// <summary>
        /// Parses "TABLE|COLUMN" lines into table → column (both upper-case). Invalid lines are
        /// returned in <paramref name="invalid"/> so the caller can log them.
        /// </summary>
        public static Dictionary<string, string> ParseItemRelations(IEnumerable<string> lines, out List<string> invalid)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            invalid = new List<string>();
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0)
                    continue;
                var parts = line.Split('|');
                if (parts.Length != 2 || parts[0].Trim().Length == 0 || parts[1].Trim().Length == 0 ||
                    parts.Any(p => p.Contains('*')))
                {
                    invalid.Add(line);
                    continue;
                }
                result[parts[0].Trim().ToUpperInvariant()] = parts[1].Trim().ToUpperInvariant();
            }
            return result;
        }

        /// <summary>Fallback when the ini section is missing: the transaction table groups.</summary>
        public const string DefaultTableGroupRule =
            "TableGroup:Transaction,WorksheetHeader,WorksheetLine,Worksheet,TransactionHeader,TransactionLine,Staging";

        /// <summary>Columns that tie a table to an item (ITEMID) or to an EcoResProduct RecId.</summary>
        public static readonly string[] KeyColumns =
        {
            "ITEMID", "PRODUCT", "DISTINCTPRODUCT", "DISTINCTPRODUCTVARIANT", "PRODUCTMASTER",
            // InventTable relations without an ITEMID column:
            //  INVENTTABLE       - bigint = InventTable.RecId (MCRINVENTTABLE), nvarchar = ItemId (CustomTable)
            //  INVENTITEMBARCODE - InventItemBarcode.RecId (ECORESPRODUCTBARCODEASSOCIATION)
            "INVENTTABLE", "INVENTITEMBARCODE"
        };

        /// <summary>RecIds of all items used on sales lines.</summary>
        private const string SoldItemRecIdsSql =
            "SELECT it.RECID FROM INVENTTABLE it WHERE EXISTS (SELECT 1 FROM SALESLINE sl WHERE sl.ITEMID = it.ITEMID AND sl.DATAAREAID = it.DATAAREAID)";

        /// <summary>RecIds of the barcodes of those items.</summary>
        private const string SoldItemBarcodeRecIdsSql =
            "SELECT b.RECID FROM INVENTITEMBARCODE b WHERE EXISTS (SELECT 1 FROM SALESLINE sl WHERE sl.ITEMID = b.ITEMID AND sl.DATAAREAID = b.DATAAREAID)";

        /// <summary>
        /// InventTable relation tables whose link column has a generic name (e.g. REFRECID), so the
        /// relation cannot be derived from the column name: table → (column, filter on that column).
        /// Checked first in <see cref="BuildStrategyLine"/>. Verified against AxDB data.
        /// </summary>
        public static readonly IReadOnlyDictionary<string, (string Column, string Filter, string Description)> RelationTables =
            new Dictionary<string, (string, string, string)>(StringComparer.OrdinalIgnoreCase)
            {
                // MCRINVENTTABLEINDEX.REFRECID = MCRINVENTTABLE.RECID, MCRINVENTTABLE.INVENTTABLE = INVENTTABLE.RECID
                ["MCRINVENTTABLEINDEX"] = ("REFRECID",
                    "MCRINVENTTABLEINDEX.REFRECID IN (SELECT m.RECID FROM MCRINVENTTABLE m INNER JOIN INVENTTABLE it ON it.RECID = m.INVENTTABLE " +
                    "WHERE EXISTS (SELECT 1 FROM SALESLINE sl WHERE sl.ITEMID = it.ITEMID AND sl.DATAAREAID = it.DATAAREAID))",
                    "REFRECID → MCRINVENTTABLE of sold items"),

                // Polymorphic link (REFTABLEID/REFRECID): rebate group assignments. 99.99% of the rows
                // reference INVENTTABLE; those are limited to sold items, other references (CUSTTABLE,
                // VENDTABLE) are kept. The InventTable TableId is read from SQLDICTIONARY on the source.
                ["TAMREBATEGROUPLINK"] = ("REFRECID",
                    "(TAMREBATEGROUPLINK.REFTABLEID <> (SELECT d.TABLEID FROM SQLDICTIONARY d WHERE d.FIELDID = 0 AND d.ARRAY = 0 AND d.SQLNAME = 'INVENTTABLE') " +
                    "OR TAMREBATEGROUPLINK.REFRECID IN (SELECT it.RECID FROM INVENTTABLE it " +
                    "WHERE EXISTS (SELECT 1 FROM SALESLINE sl WHERE sl.ITEMID = it.ITEMID AND sl.DATAAREAID = it.DATAAREAID)))",
                    "REFTABLEID/REFRECID → InventTable rows of sold items, other references kept")
            };

        /// <summary>Company columns used to match the item's legal entity with SALESLINE.DATAAREAID.</summary>
        public static readonly string[] CompanyColumns = { "DATAAREAID", "ITEMDATAAREAID" };

        // NOTE: no '*' anywhere in these fragments - the SQL strategy replaces every '*' with
        // the field list. Subqueries therefore use SELECT 1 / explicit columns.

        /// <summary>EcoResProduct RecIds of the released products of all items used on sales lines.</summary>
        private const string SoldProductsSql =
            "SELECT it.PRODUCT FROM INVENTTABLE it WHERE EXISTS (SELECT 1 FROM SALESLINE sl WHERE sl.ITEMID = it.ITEMID AND sl.DATAAREAID = it.DATAAREAID)";

        /// <summary>
        /// Variants of those products (product masters sold as items), as a join - nesting
        /// SoldProductsSql in another IN made SQL Server pick nested loops that re-read
        /// INVENTTABLE per row (up to ~99M logical reads on one table).
        /// </summary>
        private const string SoldVariantsSql =
            "SELECT pv.RECID FROM ECORESPRODUCT pv INNER JOIN INVENTTABLE it ON it.PRODUCT = pv.PRODUCTMASTER WHERE EXISTS (SELECT 1 FROM SALESLINE sl WHERE sl.ITEMID = it.ITEMID AND sl.DATAAREAID = it.DATAAREAID)";

        /// <summary>
        /// All products to keep: released products of sold items plus their variants. Used as
        /// ONE IN list (no OR between several IN subqueries) so the optimizer builds the small
        /// product set once and semi-joins it against the big table.
        /// </summary>
        private const string SoldProductSetSql = SoldProductsSql + " UNION ALL " + SoldVariantsSql;

        /// <summary>0 = row is not product specific (e.g. a global unit conversion) - always kept.</summary>
        private const string NotProductSpecificSql = "SELECT CAST(0 AS BIGINT) UNION ALL ";

        /// <summary>
        /// Builds the Copy strategy line for a table, or null when the table has no usable
        /// item/product column. <paramref name="filterDescription"/> says which column is used.
        /// </summary>
        public static string? BuildStrategyLine(ItemTableCandidate table, out string filterDescription,
            IReadOnlyDictionary<string, string>? itemRelations = null)
        {
            string t = table.TableName.ToUpperInvariant();
            string? filter = null;
            filterDescription = string.Empty;

            if (itemRelations != null && itemRelations.TryGetValue(t, out var itemColumn) && table.Has(itemColumn))
            {
                // Configured item relation ([TransactionLessItemRelations]): text column = ItemId,
                // integer column = InventTable.RecId
                if (table.IsInteger(itemColumn))
                {
                    filter = $"{t}.{itemColumn} IN ({NotProductSpecificSql}{SoldItemRecIdsSql})";
                    filterDescription = $"{itemColumn} (= InventTable.RecId, configured) → sold items";
                }
                else if (table.Has("DATAAREAID"))
                {
                    filter = ItemFilter(t, "DATAAREAID", itemColumn);
                    filterDescription = $"{itemColumn} (= ItemId, configured) + DATAAREAID";
                }
                else
                {
                    filter = $"({t}.{itemColumn} = '' OR EXISTS (SELECT 1 FROM SALESLINE sl WHERE sl.ITEMID = {t}.{itemColumn}))";
                    filterDescription = $"{itemColumn} (= ItemId, configured, any company)";
                }
            }
            else if (RelationTables.TryGetValue(t, out var relation) && table.Has(relation.Column))
            {
                filter = relation.Filter;
                filterDescription = relation.Description;
            }
            else if (t == "ECORESPRODUCT")
            {
                // Released products plus their variants (variants point to the master via PRODUCTMASTER)
                filter = $"ECORESPRODUCT.RECID IN ({SoldProductSetSql})";
                filterDescription = "RECID/PRODUCTMASTER → sold products";
            }
            else if (table.Has("ITEMID") && table.Has("DATAAREAID"))
            {
                filter = ItemFilter(t, "DATAAREAID");
                filterDescription = "ITEMID + DATAAREAID";
            }
            else if (table.Has("ITEMID") && table.Has("ITEMDATAAREAID"))
            {
                filter = ItemFilter(t, "ITEMDATAAREAID");
                filterDescription = "ITEMID + ITEMDATAAREAID";
            }
            else if (FirstProductColumn(table) is string productColumn)
            {
                // Shared (cross-company) product tables. 0 = not product specific (e.g. a
                // global unit conversion) and is always kept - it is part of the IN list
                // instead of an OR, see SoldProductSetSql.
                filter = productColumn == "PRODUCTMASTER"
                    ? $"{t}.PRODUCTMASTER IN ({NotProductSpecificSql}{SoldProductsSql})"
                    : $"{t}.{productColumn} IN ({NotProductSpecificSql}{SoldProductSetSql})";
                filterDescription = $"{productColumn} → sold products";
            }
            else if (table.Has("ITEMID"))
            {
                // No company column: match the item in any company
                filter = $"({t}.ITEMID = '' OR EXISTS (SELECT 1 FROM SALESLINE sl WHERE sl.ITEMID = {t}.ITEMID))";
                filterDescription = "ITEMID (any company)";
            }
            else if (table.Has("INVENTTABLE") && table.IsText("INVENTTABLE"))
            {
                // Text INVENTTABLE column = the item number (e.g. CustomTable)
                filter = table.Has("DATAAREAID")
                    ? ItemFilter(t, "DATAAREAID", "INVENTTABLE")
                    : $"({t}.INVENTTABLE = '' OR EXISTS (SELECT 1 FROM SALESLINE sl WHERE sl.ITEMID = {t}.INVENTTABLE))";
                filterDescription = table.Has("DATAAREAID") ? "INVENTTABLE (= ItemId) + DATAAREAID" : "INVENTTABLE (= ItemId, any company)";
            }
            else if (table.Has("INVENTTABLE") && table.IsInteger("INVENTTABLE"))
            {
                // Integer INVENTTABLE column = InventTable.RecId (e.g. MCRINVENTTABLE); 0 = no item, kept
                filter = $"{t}.INVENTTABLE IN ({NotProductSpecificSql}{SoldItemRecIdsSql})";
                filterDescription = "INVENTTABLE (= InventTable.RecId) → sold items";
            }
            else if (table.Has("INVENTITEMBARCODE") && table.IsInteger("INVENTITEMBARCODE"))
            {
                // InventItemBarcode.RecId (e.g. ECORESPRODUCTBARCODEASSOCIATION); 0 = no barcode, kept
                filter = $"{t}.INVENTITEMBARCODE IN ({NotProductSpecificSql}{SoldItemBarcodeRecIdsSql})";
                filterDescription = "INVENTITEMBARCODE (= InventItemBarcode.RecId) → sold items";
            }

            if (filter == null)
                return null;

            return $"{t}|sql: SELECT * FROM {t} WHERE {filter} AND @sysRowVersionFilter";
        }

        /// <summary>Table name of the inventory dimension table.</summary>
        public const string InventDimTable = "INVENTDIM";

        /// <summary>
        /// INVENTDIM rows are kept when at least one of these is set (site / warehouse /
        /// inventory status level dimensions, as referenced by item master data such as
        /// default order settings and site/warehouse specific setups)...
        /// </summary>
        public static readonly string[] InventDimRequiredColumns = { "INVENTSITEID", "INVENTLOCATIONID", "INVENTSTATUSID" };

        /// <summary>
        /// ...and all other inventory dimensions are empty. Every INVENTDIM column is treated
        /// as an inventory dimension except these key/hash columns, the system fields
        /// (FIELDID &gt;= <see cref="SystemFieldIdStart"/> or a name in
        /// <see cref="SystemFieldNames"/>), deprecated DEL_ fields, and the ...TZID companion
        /// column of a UtcDateTime dimension (its value is a time zone id even when the date is
        /// empty, e.g. INVENTDIMENSION9TZID = 37001).
        /// </summary>
        public static readonly string[] InventDimNonDimensionColumns =
        {
            "INVENTDIMID",   // the key itself
            "SHA1HASHHEX",   // hash of the dimension values (unique index)
            "SHA3HASHHEX",
            "HASHTYPE"
        };

        /// <summary>D365 system fields have FIELDID 60000+ (DATAAREAID 61448, RECID 65534, ...).</summary>
        public const int SystemFieldIdStart = 60000;

        /// <summary>System fields by name, in case a field id is unusual.</summary>
        public static readonly string[] SystemFieldNames =
        {
            "RECID", "RECVERSION", "PARTITION", "DATAAREAID", "SYSROWVERSION",
            "MODIFIEDDATETIME", "MODIFIEDBY", "MODIFIEDTRANSACTIONID",
            "CREATEDDATETIME", "CREATEDBY", "CREATEDTRANSACTIONID"
        };

        /// <summary>
        /// Builds the INVENTDIM Copy strategy line from the AxDB SQLDICTIONARY columns of
        /// INVENTDIM: rows with a site, warehouse or inventory status (at least one of
        /// <see cref="InventDimRequiredColumns"/>) and every other inventory dimension empty.
        /// "Empty" depends on the SQL type: '' for strings, the D365 null date 1900-01-01 for
        /// dates, 0 for numbers. The line ends with " -keep" (KEEP route: upsert only, the other
        /// AxDB INVENTDIM rows - referenced by the kept transaction tables - are never deleted).
        /// </summary>
        public static InventDimStrategy BuildInventDimStrategyLine(IEnumerable<TableColumnInfo> columns)
        {
            var systemNames = new HashSet<string>(SystemFieldNames, StringComparer.OrdinalIgnoreCase);
            var nonDimension = new HashSet<string>(InventDimNonDimensionColumns, StringComparer.OrdinalIgnoreCase);
            var required = new HashSet<string>(InventDimRequiredColumns, StringComparer.OrdinalIgnoreCase);

            var list = columns
                .GroupBy(c => c.SqlName, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(c => c.SqlName, StringComparer.Ordinal)
                .ToList();
            var dateColumns = new HashSet<string>(
                list.Where(c => EmptyValueFor(c.DataType) == "'1900-01-01'").Select(c => c.SqlName),
                StringComparer.OrdinalIgnoreCase);

            var result = new InventDimStrategy();
            var requiredPresent = new List<string>();

            foreach (var column in list)
            {
                string name = column.SqlName.ToUpperInvariant();

                if (required.Contains(name))
                {
                    requiredPresent.Add(name);
                    continue;
                }

                bool isTzCompanion = name.EndsWith("TZID", StringComparison.Ordinal) &&
                                     dateColumns.Contains(name.Substring(0, name.Length - 4));
                if (column.FieldId >= SystemFieldIdStart || systemNames.Contains(name) ||
                    nonDimension.Contains(name) || name.StartsWith("DEL_", StringComparison.Ordinal) ||
                    isTzCompanion)
                {
                    result.IgnoredColumns.Add(name);
                    continue;
                }

                string? empty = EmptyValueFor(column.DataType);
                if (empty == null)
                {
                    result.UnsupportedColumns.Add($"{name} ({column.DataType})");
                    continue;
                }
                result.EmptyColumns.Add((name, empty));
            }

            // Keep the order of InventDimRequiredColumns (site, warehouse, status)
            result.RequiredAnyColumns.AddRange(InventDimRequiredColumns.Where(c => requiredPresent.Contains(c)));
            if (result.RequiredAnyColumns.Count == 0)
                return result;

            // No '*' anywhere except SELECT * - the SQL strategy replaces every '*'
            var conditions = new List<string>
            {
                "(" + string.Join(" OR ", result.RequiredAnyColumns.Select(c => $"{c} <> ''")) + ")"
            };
            conditions.AddRange(result.EmptyColumns.Select(c => $"{c.Column} = {c.EmptyValue}"));

            result.Line = $"{InventDimTable}|sql: SELECT * FROM {InventDimTable} WHERE {string.Join(" AND ", conditions)} AND @sysRowVersionFilter -keep";
            return result;
        }

        /// <summary>The D365 "empty" literal for a SQL data type, or null when there is none.</summary>
        public static string? EmptyValueFor(string dataType)
        {
            switch (dataType.ToLowerInvariant())
            {
                case "nvarchar":
                case "varchar":
                case "nchar":
                case "char":
                    return "''";
                case "datetime":
                case "datetime2":
                case "date":
                case "smalldatetime":
                    return "'1900-01-01'";
                case "int":
                case "bigint":
                case "smallint":
                case "tinyint":
                case "numeric":
                case "decimal":
                case "real":
                case "float":
                case "money":
                case "bit":
                    return "0";
                case "uniqueidentifier":
                    return "'00000000-0000-0000-0000-000000000000'";
                default:
                    return null;
            }
        }

        private static string ItemFilter(string t, string companyColumn, string itemColumn = "ITEMID")
        {
            // Rows without an item are not item specific and are kept
            return $"({t}.{itemColumn} = '' OR EXISTS (SELECT 1 FROM SALESLINE sl WHERE sl.ITEMID = {t}.{itemColumn} AND sl.DATAAREAID = {t}.{companyColumn}))";
        }

        private static string? FirstProductColumn(ItemTableCandidate table)
        {
            foreach (var column in new[] { "PRODUCT", "DISTINCTPRODUCT", "DISTINCTPRODUCTVARIANT", "PRODUCTMASTER" })
            {
                if (table.Has(column))
                    return column;
            }
            return null;
        }

        /// <summary>
        /// Appends the lines missing from <paramref name="existingText"/> (case-insensitive,
        /// whitespace-trimmed comparison). Existing lines are kept unchanged and in order.
        /// </summary>
        public static string MergeExcludeLines(string existingText, IEnumerable<string> newLines, out List<string> added)
        {
            var lines = SplitLines(existingText);
            var present = new HashSet<string>(lines, StringComparer.OrdinalIgnoreCase);
            added = new List<string>();

            foreach (var raw in newLines)
            {
                var line = raw.Trim();
                if (line.Length == 0 || !present.Add(line))
                    continue;
                lines.Add(line);
                added.Add(line);
            }

            return string.Join(Environment.NewLine, lines);
        }

        /// <summary>
        /// Merges generated strategy lines into the existing Copy strategy text: a line for a
        /// table that already has one replaces it in place (unless identical), new tables are
        /// appended. Lines for other tables are not touched.
        /// </summary>
        public static string MergeStrategyLines(
            string existingText,
            IEnumerable<string> newLines,
            out List<string> addedTables,
            out List<string> replacedTables)
        {
            var generated = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var generatedOrder = new List<string>();
            foreach (var line in newLines)
            {
                var name = SavedValuesHelper.GetStrategyTableName(line);
                if (string.IsNullOrEmpty(name) || generated.ContainsKey(name))
                    continue;
                generated[name] = line.Trim();
                generatedOrder.Add(name);
            }

            addedTables = new List<string>();
            replacedTables = new List<string>();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<string>();

            foreach (var line in SplitLines(existingText))
            {
                var name = SavedValuesHelper.GetStrategyTableName(line);
                if (!string.IsNullOrEmpty(name) && generated.TryGetValue(name, out var replacement))
                {
                    if (used.Add(name))
                    {
                        result.Add(replacement);
                        if (!string.Equals(line, replacement, StringComparison.Ordinal))
                            replacedTables.Add(name);
                    }
                    // A second line for the same table would be a duplicate - drop it
                    continue;
                }
                result.Add(line);
            }

            foreach (var name in generatedOrder)
            {
                if (used.Contains(name))
                    continue;
                result.Add(generated[name]);
                addedTables.Add(name);
            }

            return string.Join(Environment.NewLine, result);
        }

        private static List<string> SplitLines(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return new List<string>();

            return text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .ToList();
        }
    }
}
