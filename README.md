# SQLiteMd

`SqliteMd` is a `SqliteDna` extension that exposes markdown-backed virtual tables and supports writing new `.md` tables.

## What it does

- Read one Markdown file or all `.md` files in a folder.
- Parse GitHub-style pipe tables.
- Materialize row metadata (`source_path`, `source_file_name`, `source_table_index`, `source_table_title`, `source_row`).
- Normalize duplicate headers.
- Write new markdown-backed tables to new documents.
- Supports folder scans with wildcard file pattern and optional recursion.

## Installation and quick start

1. Download the latest release asset for your platform (`SqliteMd.dll`).
2. Start `sqlite3` and load the extension with `.load`.
3. Run SQL against markdown sources.

Example session (Windows PowerShell + sqlite3):

```text
PS> Invoke-WebRequest -Uri "https://github.com/SQLite-DNA/SqliteMd/releases/latest/download/SqliteMd.dll" `
       -OutFile SqliteMd.dll
PS> sqlite3 demo.db
SQLite version 3.48.0 2025-01-09 12:00:00
sqlite> .load .\SqliteMd.dll
sqlite> .tables
sqlite> CREATE VIRTUAL TABLE notes USING markdown('notes/notes-table.md', '', 0);
sqlite> SELECT source_row, id, title, stars FROM notes ORDER BY source_row;
1|1|Release notes|4
2|2|Testing coverage|7
```

To bootstrap a new `.md` table file:

```text
sqlite> SELECT write_markdown('output/new-notes.md', 'Generated Notes', 'id,title,stars', '[["1","Plan",12],["2","Ship",24]]');
2
sqlite> .tables
sqlite> CREATE VIRTUAL TABLE generated USING markdown('output/new-notes.md', '', 0);
sqlite> SELECT source_row, id, title, stars FROM generated;
1|1|Plan|12
2|2|Ship|24
```

## API

### Read API

```sql
CREATE VIRTUAL TABLE v USING markdown(source, searchPattern, recursive);
```

- `source`: `.md` file path or folder path.
- `searchPattern`: file pattern for folder scans (default `*.md`).
- `recursive`: `0` for top-level only, `1` for recursive scan.

### Write API

```sql
SELECT write_markdown(path, table_title, columns_csv, rows_json, overwrite);
```

- `path`
  - Path to destination `.md` file.
  - If a folder path is provided, the function writes `<table_title>.md` inside that folder.
- `table_title`
  - Markdown section title + generated table caption.
- `columns_csv`
  - Comma-separated header names.
- `rows_json`
  - JSON array of row arrays.
  - Example: `[["1", "Plan", 12], ["2","Ship",24]]`
- `overwrite`
  - `0` (default): append to an existing document.
  - `1`: replace existing file content.

Returns the number of rows written.

```sql
SELECT insert_markdown_row(path, table_title, columns_csv, row_json);
```

Inserts a single row (`row_json`) into the matching markdown table. If no matching table is found, a new table is appended (or created).

- `row_json`
  - One JSON array representing a row.

```sql
SELECT rewrite_markdown_row(path, table_title, columns_csv, row_index, row_json);
```

Rewrites a row in place by `row_index` (`1` based) in the matching markdown table.
- `row_index`: Data row index (1-based) within the selected table.
- Returns `1` when exactly one row is rewritten.

### SQL `INSERT` / `UPDATE` support

`markdown(...)` is intentionally a read-only virtual table.
Native SQL `INSERT` and `UPDATE` against that table are not supported.

Why:

- SQLite virtual tables can support writes only when the extension implements the virtual-table write interface (`xUpdate`).
- `SqliteDna.Integration` table-function path used here (`SqliteTableFunction` + `DynamicTable`) emits a materialized read-only result set and does not expose `xUpdate`.
- `INSERT`/`UPDATE` therefore fail at engine level for this virtual table implementation, independent of the underlying Markdown file format.
- The extension intentionally keeps writes explicit with file-oriented helpers (`write_markdown`, `insert_markdown_row`, `rewrite_markdown_row`) to avoid ambiguous in-place SQL mutation semantics.

Use `write_markdown(...)` as the write API:
- default `overwrite = 0` appends a new table block,
- `overwrite = 1` replaces the file content.

This gives deterministic, atomic-like file writes and avoids implicit mutation semantics that are not well-defined for Markdown.

## Examples

### Read examples

```sql
CREATE VIRTUAL TABLE single USING markdown('notes/intro.md', '*.md', 0);
CREATE VIRTUAL TABLE docs_top USING markdown('notes', '*.md', 0);
CREATE VIRTUAL TABLE docs_all USING markdown('notes', '*.md', 1);
```

### Write examples

```sql
-- create a new file in a folder
SELECT write_markdown('output', 'Sprint Notes', 'id,title,stars', '[[1,"Plan",12],[2,"Ship",24]]');

-- create/overwrite a specific file
SELECT write_markdown('output/sprint.md', 'Sprint Notes', 'id,title,stars', '[[1,"Plan",12],[2,"Ship",24]]', 1);

-- row-oriented helpers
SELECT insert_markdown_row('output/sprint.md', 'Sprint Notes', 'id,title,stars', '[3,"Review",19]');
SELECT rewrite_markdown_row('output/sprint.md', 'Sprint Notes', 'id,title,stars', 2, '[2,"Ship",28]');
```

## Development

```bash
dotnet restore
dotnet build SqliteMd.sln
dotnet test SqliteMd.sln
```

## Repository layout

- `SqliteMd/`: extension implementation.
- `SqliteMd.Tests/`: test project and fixtures.
- `.github/workflows/`: CI and release automation.
