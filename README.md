# D365FO Database Sync Tool

A WinForms .NET 9 application for synchronizing data from Dynamics 365 Finance & Operations Azure SQL Database (Tier2) to local SQL Server (AxDB).

## Overview

This tool helps developers synchronize data from D365FO cloud environments to their local development databases, making it easier to test with production-like data.

The main idea: make the last X records (ordered by RecId) the same between Tier2 and AxDB, ensuring your local environment has the most recent data from Tier2.

![Main dialog](Assets/MainDialog.png)

![Connections](Assets/MainDialogConnection.png)

![Post transfer scripts](Assets/MainDialogPostTransfer.png)

## Usage

### Step 1: Save Current AxDB

Before copying data, create a backup or snapshot of your current AxDB for safety:

**Option A: Create Database Snapshot (recommended for quick rollback)**
```sql
-- Create snapshot
CREATE DATABASE AxDB_MyReserveCase ON
( NAME = AxDB, FILENAME = 'E:\MSSQL_BACKUP\AxDB_MyReserveCase.ss' )
AS SNAPSHOT OF AxDB;

-- Restore from snapshot (if needed)
ALTER DATABASE AxDB SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
RESTORE DATABASE AxDB FROM DATABASE_SNAPSHOT = 'AxDB_MyReserveCase';
ALTER DATABASE AxDB SET MULTI_USER;
```

**Option B: Traditional Backup**
```sql
BACKUP DATABASE AxDB TO DISK = 'E:\MSSQL_BACKUP\AxDB_Backup.bak';
```

### Step 2: Get Tier2 Connection

Two steps required to access Tier2 database:

**Step 2.1: Whitelist Your IP Address**
1. Go to LCS environment page
2. Click **Maintain** → **Enable access**
3. Enter your IP address in the dialog

**Step 2.2: Get Database Credentials**
1. In Environment page, go to **Database accounts** section
2. Choose **Reason for access** = "AX troubleshooting (read-only to AX)"
3. Click **Request access**
4. Copy database server and user credentials

**Note:** You can avoid Step 2.2 by creating a permanent read-only user in Tier2:
```sql
CREATE USER [SyncDBReader] WITH PASSWORD = 'YOUR_SECURE_PASSWORD_HERE';
EXEC sp_addrolemember N'db_datareader', N'SyncDBReader';
GRANT VIEW DATABASE PERFORMANCE STATE TO [SyncDBReader];

-- and firewall 
exec sp_set_database_firewall_rule N'SyncDBReader', '<yourIP>', '<yourIP>'
```

### Step 3: Create Local Database User

Create a user with db_owner role on your local SQL Server:
```sql
CREATE LOGIN [SyncDBReaderLocal] WITH PASSWORD = 'YourPassword';
USE AxDB;
CREATE USER [SyncDBReaderLocal] FOR LOGIN [SyncDBReaderLocal];
EXEC sp_addrolemember 'db_owner', [SyncDBReaderLocal];
```

### Step 4: Configure Application

1. **Enter Connection Details** (Connection tab):
   - Tier2: Server address, database name, username, password from Step 2
   - AxDB: localhost\AxDB, username, password from Step 3

2. **Set Tables and Strategies** (Tables tab):

**Sample Tables to Exclude:**
```
Sys*
Batch*
*Staging
EVENTCUD
SALESCONFIRMHEADERTMP
FPREPORTCOLUMNLOG
EVENTINBO*
BUSINESSEVENTSCOMMITLOG
LAC*
DOCUHISTORY
LICENSING*
DMF*
UNITOFMEASURECONVERSIONCACHE
SECURITY*
USERSEC*
USERDATAAREAFILTER
RETAILTAB*
*ARCHIVE
SRSREPORTQUERY
BUSINESSEVENTSEXCEPTIONLOG
SALESLINEDELETE
SALESTABLEDELETE
WHSWORKUSERSESSIONLOG
SMMTRANSLOG
INVENTCLOSINGLOG
WHSWORKUSERSESSIONSTATE
WHSWORKUSERSESSION
INVENTSUMDELTADIM
INVENTSUMDELTA
COSTOBJECTSTATEMENTCACHE
```

**Copy Strategy Examples:**
```
InventDim|sql: SELECT * FROM InventDim WHERE RecId IN (SELECT RecId FROM (SELECT RecId FROM InventDim WHERE LICENSEPLATEID = '' AND PARTITION = 5637144576 AND DATAAREAID = 'USMF' AND WMSLOCATIONID = '' UNION SELECT RecId FROM (SELECT TOP 50000 RecId FROM InventDim ORDER BY RecId DESC) t) u) AND @sysRowVersionFilter ORDER BY RecId DESC
InventSum|sql: SELECT * FROM InventSum WHERE Closed = 0 AND @sysRowVersionFilter ORDER BY RecId DESC
ECORESPRODUCTIMAGE|1000
WHSCONTAINERTABLE|50000
WHSINVENTRESERVE|sql: SELECT * FROM WHSINVENTRESERVE WHERE ((HIERARCHYLEVEL = 1 AND AVAILPHYSICAL <> 0) OR MODIFIEDDATETIME > DATEADD(DAY, -93, GETUTCDATE())) AND PARTITION = 5637144576 AND DATAAREAID = 'USMF' AND @sysRowVersionFilter ORDER BY RecId DESC
```

**Records to Copy:** `100000` (default record count for RecId strategy)

**Transaction-less copy (master data only)**

*What it means:* master data, setup and parameters are refreshed from Tier2, while all **transactions in AxDB stay untouched**. Transaction tables (D365FO table groups Transaction, WorksheetHeader/Line, Worksheet, TransactionHeader/Line, Staging) are excluded, so their AxDB content is neither copied nor deleted.

*Prerequisite:* this only works when your local AxDB has the **same baseline as Tier2, just an older version** — e.g. an earlier restore of the same environment. The kept transactions then still point to item numbers, InventDimIds, RecIds etc. that exist in the refreshed master data. Against an unrelated database (Contoso demo data, another customer or project) the kept transactions would reference master data that does not exist.

*Limits:* item/product master data is reduced to items used on Tier2 `SALESLINE`; items that only your local transactions use (purchase-only, stock-only, BOM components) are not copied. Tables without their own strategy line still follow **Records to copy** — set it high enough to cover the master data tables completely.

To set it up, click **Init transaction-less copy** (Tables to Exclude group). It:
- adds `TableGroup:Transaction,WorksheetHeader,WorksheetLine,Worksheet,TransactionHeader,TransactionLine,Staging` plus the extra log/journal tables from the `[TransactionLessExclude]` section of `DefaultValues.ini` to **Tables to Exclude**, followed by your own tables from the same section of `CustomValues.ini`. Excluded tables are not copied; their AxDB content is left untouched.
- `CustomValues.ini` lives next to `DBSyncTool.exe` and is empty in the repository: add your customer-specific tables there (one name or `*` pattern per line under `[TransactionLessExclude]`). It is not included in the release package, so upgrading never overwrites it — create it yourself if it is missing:
  ```
  [TransactionLessExclude]
  MYCOMPANYLOGTABLE
  MYCOMPANYIMPORT*
  ```
- generates a **Copy strategy** line for every item/product table that has rows (found in the AxDB SQLDICTIONARY via its `ITEMID` / `PRODUCT` / `DISTINCTPRODUCT` / `DISTINCTPRODUCTVARIANT` / `PRODUCTMASTER` column, or an InventTable relation such as an `INVENTTABLE` or `INVENTITEMBARCODE` column), limited to items used on `SALESLINE`. Own item tables whose item column has another name can be added in `CustomValues.ini`:
  ```
  [TransactionLessItemRelations]
  MYALTERNATIVEITEMS|ITEM
  ```
  (text column = item number, matched per company; integer column = InventTable RecId). Example of a generated line:
  ```
  INVENTTABLE|sql: SELECT * FROM INVENTTABLE WHERE (INVENTTABLE.ITEMID = '' OR EXISTS (SELECT 1 FROM SALESLINE sl WHERE sl.ITEMID = INVENTTABLE.ITEMID AND sl.DATAAREAID = INVENTTABLE.DATAAREAID)) AND @sysRowVersionFilter
  ```
Existing lines for those tables are replaced, everything else is kept. Nothing is saved until the next Discover Tables — review the result first.

*Getting a consistent database afterwards:* if you would rather have **no** transactions than old ones next to the refreshed master data, click **Copy Truncate TSQL** (next to Copy to Clipboard). It copies a T-SQL script that empties the transaction tables: the same table groups (resolved in the target database via `SQLDICTIONARY` + `TABLEMETADATATABLE`) plus the explicit tables from `[TransactionLessExclude]` in `DefaultValues.ini` and `CustomValues.ini`. Run it yourself in SSMS **against the local AxDB only**:
- it starts with `@Execute = 0` and only lists the affected tables with their row counts;
- set `@Execute = 1` to truncate — each table in its own transaction, CDC disabled/re-enabled if the table is tracked, a batched `DELETE` when `TRUNCATE` is not allowed (foreign key / indexed view); errors are printed and the script continues.

Take a backup or snapshot first. The tool itself never runs this script.

### Step 5: Discover Tables

1. Click **Discover Tables**
2. The system will query Tier2 and display ~data to copy
3. Click **Copy to Clipboard** to export table list to Excel
4. Review tables with large **Est Size(MB)** values
5. Check all tables that exceed **Records to Copy** threshold

By default, the system matches the last X records between AxDB and Tier2. For some tables, you may need more complex logic using SQL strategies.

### Step 6: Process Tables

1. Click **Process Tables** to start copying all discovered tables
2. The system will copy all tables to AxDB using parallel workers
3. Monitor progress in the log window
4. If a table fails, use **Process Selected** (right-click menu) to retry just that table

**Tips:**
- First run may take longer as it performs delta comparison
- Subsequent runs use INCREMENTAL mode optimization (much faster)
- Tables are processed in parallel (default: 10 workers, configurable)

### Step 7: Post-Transfer Actions (Optional)

The **Post-Transfer Actions** tab lets you configure actions that run automatically after a successful transfer. Actions execute in chain: SQL Scripts → Database Backup → PowerShell Script.

**SQL Scripts:**
1. Enter SQL commands (one per line, lines starting with `--` are skipped as comments)
2. Check **Execute automatically after successful transfer** to run on every sync
3. Or click **Execute** button to run manually

**Database Backup:**
1. Enter backup path pattern using `[format]` for date-time tokens (e.g., `J:\MSSQL_BACKUP\AxDB_[yyyy_MM_dd_HHmm].bak`)
2. Check **Execute automatically** to run after successful SQL scripts
3. Progress is polled in real-time via SQL Server DMVs

**PowerShell Script:**
1. Specify path to a `.ps1` script (use **...** button to browse)
2. The script receives `-BackupFilePath` parameter with the resolved backup path
3. Check **Execute automatically after successful backup** for auto-execution
4. Click **?** button to copy a sample script template to clipboard

**Example PowerShell script** (upload backup to Azure Blob):
```powershell
param(
    [Parameter(Mandatory=$true)]
    [string]$BackupFilePath
)
Write-Host "Uploading $BackupFilePath..."
# Copy-Item -Path $BackupFilePath -Destination "\\server\share\backups\" -Force
```

## Features

### Core Functionality
- **Selective Table Copying**: Use patterns to include/exclude tables (e.g., `CUST*`, `Sys*`), or whole D365FO table groups (e.g., `TableGroup:Transaction,WorksheetLine`, resolved from the AxDB `TABLEMETADATATABLE`)
- **Transaction-less Copy**: One button sets up a master-data-only copy, with item/product tables limited to items used on sales lines
- **Simplified Copy Strategies**:
  - **RecId Strategy**: Copy top N records by RecId DESC (e.g., `CUSTTABLE|5000`)
  - **SQL Strategy**: Custom SQL queries with placeholders (e.g., `CUSTTABLE|sql:SELECT * FROM CUSTTABLE WHERE DATAAREAID='USMF'`)
  - **Truncate Option**: Force TRUNCATE before insert with `-truncate` flag, or exempt a table from the global **Force truncate mode** with `-notruncate`; `-keep` only inserts/updates the fetched rows and never deletes anything else in AxDB
- **SysRowVersion Optimization**: Intelligent change detection for incremental sync
  - **INCREMENTAL Mode**: Only sync changed records when changes < 40% threshold
  - **TRUNCATE Mode**: Full table refresh when changes > 40% threshold
  - **Timestamp Tracking**: Automatic persistence of sync state for future optimizations
- **Smart Field Mapping**: Automatically maps common fields between source and destination
- **Parallel Execution**: Configurable parallel workers (1-50) for concurrent table processing
- **Streaming for Large Tables**: A table with 1,000,000+ rows or 500+ MB to copy whose AxDB content is replaced by the fetched rows anyway (whole-table RecId copy without saved values, `-truncate` / Force truncate mode, System tables) is streamed from Tier2 straight into AxDB (TRUNCATE + SqlBulkCopy from a data reader) instead of being loaded into memory, so memory stays flat even with several parallel workers on multi-GB tables. Tier2 is read in short RecId chunks of 200,000 rows; when Azure drops the connection or kills the session, the copy waits and resumes after the last copied RecId (up to 5 times per table) instead of starting the table over. The AxDB table's nonclustered indexes (except those a foreign key needs) are disabled during the load and rebuilt afterwards — also after an error or Stop; a unique index that finds duplicates is reported with the SQL to fix it. Each index the tool disables is marked with the extended property `DBSyncTool_DisabledForLoad` until it is rebuilt, so an interrupted run is finished by the next one, while indexes that D365 itself keeps disabled (switched-off configuration keys) are never touched. Shown as `STREAM` in the Strategy column and in **Get SQL**. Like every bulk insert it is not transactional: a table that finally fails is left partly filled and is truncated again on retry
- **Delta Comparison**: Smart comparison using RECVERSION + datetime fields to skip unchanged records
- **Post-Transfer Actions**: Configurable chain of actions after successful sync:
  - **SQL Scripts**: Run custom SQL commands (e.g., cleanup, index rebuild)
  - **Database Backup**: Automated backup with date-time tokens in path and real-time progress
  - **PowerShell Script**: Run a .ps1 script with backup file path as parameter (e.g., upload to blob storage)

### Strategy Syntax

**Format:**
```
TableName|RecordCount|sql:CustomQuery -truncate|-notruncate|-keep
```

**Examples:**
```
CUSTTABLE                    # RecId: default count
SALESLINE|10000              # RecId: top 10000
INVENTTRANS|sql:SELECT * FROM INVENTTRANS WHERE DATAAREAID='USMF' ORDER BY RecId DESC
CUSTTRANS|5000|sql:SELECT TOP (@recordCount) * FROM CUSTTRANS WHERE BLOCKED=0 ORDER BY RecId DESC
VENDTABLE|5000 -truncate     # Force truncate
INVENTTRANS|50000 -notruncate  # Ignore the "Force truncate mode" checkbox for this table
INVENTDIM|sql:SELECT * FROM INVENTDIM WHERE INVENTSITEID <> '' AND @sysRowVersionFilter -keep  # Upsert only, keep all other AxDB rows
```

**Flags:**
- `-truncate` - TRUNCATE the AxDB table before inserting
- `-notruncate` - the global **Force truncate mode** checkbox does not apply to this table (automatic TRUNCATE by threshold still can)
- `-keep` - KEEP mode: the fetched rows are inserted/updated by RecId and **no other AxDB row is ever deleted or truncated** (rows deleted in Tier2 stay in AxDB). A fetched row whose unique key (e.g. INVENTDIMID) already belongs to a different local row is skipped and logged. With `@sysRowVersionFilter` in the SQL, later runs fetch only the rows changed in Tier2. Implies `-notruncate`; cannot be combined with `-truncate`

**SQL Placeholders:**
- `*` - Replaced with actual field list (only common fields between Tier2 and AxDB)
- `@recordCount` - Replaced with record count (default or explicitly specified)
- `@sysRowVersionFilter` - Replaced with `SysRowVersion >= @Threshold AND RecId >= @MinRecId`
  - **Required for SQL strategies to enable INCREMENTAL mode optimization**
  - Without this placeholder, SQL strategies fall back to standard mode

### SysRowVersion Optimization

For tables with `SysRowVersion` column:

**First Run:**
- Standard mode with delta comparison
- Saves Tier2 and AxDB timestamps after successful sync
- Smart TRUNCATE detection: auto-enables TRUNCATE if AxDB has excess records (> 40%)

**Subsequent Runs:**
- **Control Query**: Fetches only RecId + SysRowVersion (~1KB per 1000 records vs ~100MB for full data)
- **Change Detection**: Compares timestamps to calculate change percentage
- **Mode Selection**:
  - **INCREMENTAL Mode** (changes < 40%): 3-step incremental delete + selective insert
  - **TRUNCATE Mode** (changes ≥ 40%): Full table refresh

**Benefits:**
- 99%+ reduction in data transfer when no changes detected
- 20x+ faster sync for tables with minimal changes
- Automatic fallback to full refresh when needed
- Timestamps auto-saved after each table (crash-safe)

## Requirements

- Windows OS
- .NET 9.0 Runtime
- SQL Server 2019+ (for local AxDB)
- Access to D365FO Azure SQL Database (Tier2)

## Performance

*Actual results vary based on network, table structure, and data complexity*

Typical timings for 100k records:
- Initial synchronization: 4h
- Subsequent synchronization: 20-30 min

### Real-world example: full transaction-less run

Measured on 31 Oct / 1 Nov 2026 — a full **transaction-less copy** (Force truncate mode, every table copied without a row limit) from a production-sized Tier2 into an older AxDB of the same environment. The run was done twice: first without, then with the index optimization of the STREAM route.

**Environment**

| | |
|---|---|
| Source | D365FO Tier2, Azure SQL — about 25M released items, 76M rows in INVENTTABLEMODULE |
| Target | Local dev VM: 32 GB RAM, SQL Server 2019 Developer, AxDB about 200 GB (SIMPLE recovery) |
| Scope | Transaction table groups excluded (`TableGroup:` rule, ~8,500 tables) plus about 100 explicit log / transaction-like tables · ~85 Copy strategy lines, mostly item / product filters on items used on `SALESLINE` · INVENTDIM with `-keep` |
| Settings | Records to copy 100,000,000 · Force truncate mode on · Auto retry on |

**Result**

| | First run | **Second run (index optimization)** |
|---|---|---|
| Tables | 1,389 (1,388 copied, 1 excluded afterwards) | **1,369 copied, 0 failed** (20 more tables excluded as transactions) |
| Rows copied | 114.1 million | **109.1 million** |
| Rows of the same tables on Tier2 | 1.0 billion (item filters and exclusions skipped ~89%) | 1.0 billion (~89% skipped) |
| Large tables (STREAM) | 60 tables, 96.1M rows, ~18 GB | 56 tables, 91.1M rows, ~17 GB, **396 nonclustered indexes disabled during the load and rebuilt** |
| Small tables (in memory) | 1,327 tables, 18.0M rows | 1,312 tables, 18.0M rows |
| Connection drops needing a resume | 0 | 0 |
| Parallel workers | 3, raised to 6 after 3.5 h | 6 |
| SQL Server `max server memory` | 4 GB, raised to 12 GB after 20 min | 8 GB |
| AxDB `DELAYED_DURABILITY` | `FORCED` only for the last ~2.5 h | `FORCED` for the whole run |
| **Duration** | **5 h 04 min** (incl. ~7 min of interruptions) | **1 h 04 min** |
| Average throughput | ~6,250 rows/s | **~28,500 rows/s** |
| Peak memory of the tool | 2.7 GB | 2.3 GB (a streamed table needs ~100 MB regardless of size) |

The second run is about **4.8× faster** overall. Most of it comes from the index optimization; running with 6 workers and `DELAYED_DURABILITY = FORCED` from the start contributed as well.

**Slowest tables of the first run, compared**

| Table | Rows copied | Nonclustered indexes | First run | Second run (load + index rebuild) | Faster |
|---|---:|---:|---:|---:|---:|
| CustomTable | 17,735,522 | 4 | 119 min | 26.4 min (16.3 + 10.1) | 4.5× |
| DIMENSIONATTRIBUTELEVELVALUE | 10,017,950 | 5 | 118 min | 20.8 min (15.4 + 5.5) | 5.7× |
| REQITEMTABLE (sold items) | 6,880,114 | 5 | 101 min | 26.1 min (22.3 + 3.8) | 3.9× |
| DIMENSIONFOCUSLEDGERDIMENSIONREFERENCE | 9,265,514 | 2 | 62 min | 6.5 min (4.0 + 2.5) | 9.5× |
| HCMWORKERTITLE | 5,919,504 | 2 | 50 min | 11.4 min (9.9 + 1.4) | 4.4× |
| DIMENSIONATTRIBUTEVALUECOMBINATION | 1,993,358 | 49 | 48 min | 6.8 min (2.1 + 4.7) | 7.2× |
| WMSLOCATION | 2,128,268 | 21 | 45 min | 16.2 min (7.0 + 9.1) | 2.8× |
| CUSTTABLE | 416,847 | 32 | 43 min | 5.0 min (4.0 + 1.0) | 8.5× |
| DIMENSIONATTRIBUTEVALUEGROUP | 1,935,254 | 4 | 22 min | 2.0 min (1.0 + 1.0) | 10.9× |
| TAMREBATEGROUPLINK (sold items) | 2,109,525 | 4 | 21 min | 7.2 min (4.7 + 2.6) | 2.8× |
| DIMENSIONATTRIBUTEVALUESET | 270,541 | 48 | 18 min | 1.6 min (0.4 + 1.2) | 11.0× |
| INVENTTABLEMODULE (sold items) | 1,257,552 | 5 | 15 min | 15.2 min (14.9 + 0.3) | 1.0× |
| INVENTTABLE (sold items) | 419,184 | 36 | 12 min | 9.0 min (8.3 + 0.8) | 1.3× |

Tables that copy a whole table gain the most. Tables with a sold-items filter (INVENTTABLE, INVENTTABLEMODULE and the other item tables) gain little: their time goes into evaluating the filter on Tier2, not into the insert.

**What limits the speed: the local disk, not Tier2.**
- **Index maintenance** was the biggest factor in the first run: tables with 2 nonclustered indexes streamed at 11,000–14,000 rows/s, CUSTTABLE (32 indexes) at 162 rows/s, DIMENSIONATTRIBUTEVALUESET (48) at 256 rows/s. STREAM now disables nonclustered indexes during the load and rebuilds them once afterwards — the load alone then ran at about 12,000 rows/s for DIMENSIONATTRIBUTEVALUESET and 16,000 rows/s for DIMENSIONATTRIBUTEVALUECOMBINATION (CUSTTABLE, a very wide table, at 1,700 rows/s instead of 162). Indexes that D365 itself keeps disabled (switched-off configuration keys) are marked as such and left untouched; after the run exactly the same indexes were disabled as before.
- **SQL Server memory:** with `max server memory` = 4 GB most inserts waited on page reads (`PAGEIOLATCH`). 8–12 GB on a 32 GB VM helps noticeably.
- **Log flushes:** every 10,000-row batch commit waits on the transaction log (`WRITELOG`). `ALTER DATABASE AxDB SET DELAYED_DURABILITY = FORCED` for the duration of the copy made the inserts about 4–5× faster. Set it back to `DISABLED` afterwards.
- **Parallel workers:** with streaming, 6 workers are safe on a 32 GB VM. More workers help most with the ~1,300 small tables at the end, much less with the big ones, which compete for the same disk.

**Tips for your own first full run**
- Exclude what you do not need: log, staging, cache and calculated tables can hold far more rows than the master data (here about 100 such tables with roughly 400M rows on Tier2 were excluded).
- Check large copied tables for item relations; a sold-items filter typically cuts item tables to 2–25% of their rows.
- Pause Windows Update for the duration of the run — in the first run it restarted SQL Server in the middle of the copy (the tool continued after Process Tables, but the interrupted tables had to be copied again).
- After the first run, turn **Force truncate mode** off: tables with saved values then run incrementally (an incremental pass over the item tables took about 3 minutes instead of 10).

## Notes

- Configuration files stored in `Config/` directory (gitignored)
- Passwords obfuscated (not encrypted) using Base64
- Last used configuration tracked in `Config/.lastconfig`
- Timestamps auto-saved after each table completion
- Ensure proper database permissions before running
- Large tables may take significant time on first run (subsequent runs optimized)
- SQL queries in SQL strategy are passed directly to SQL Server - ensure proper formatting
- System Excluded Tables are combined with user-defined exclusions during execution
- For best performance, use parallel workers = number of CPU cores (default: 10, can be 20-30)
- Optimization threshold configurable per environment (default: 40%)
