# SQLiteMd

`SqliteMd` is a `SqliteDna` extension that exposes one or more Markdown-backed virtual tables.

## What it does

- Supports loading one Markdown file or a directory.
- Parses GitHub-style pipe tables in `.md` files.
- Exposes each table row as rows in a virtual table with metadata + data columns.
- Supports folder scans with wildcard file pattern and optional recursion.
- Handles header normalization and duplicate header names.

## API

The extension exposes one virtual table function:

```sql
CREATE VIRTUAL TABLE v USING markdown(source, searchPattern, recursive);
```

- `source`
  - File path (`.md`) or directory path.
- `searchPattern`
  - Optional file pattern (default `*.md`), used when `source` is a directory.
- `recursive`
  - Optional `0` or `1`. `1` enables recursive folder scan.

### Metadata columns

Every row includes:

- `source_path`
- `source_file_name`
- `source_table_index`
- `source_table_title`
- `source_row`

### Data columns

The Markdown table header names are transformed into valid SQLite identifiers.
If headers duplicate, stable suffixes are appended (`header`, `header_2`, …).
Types are inferred per-column as `INTEGER`, `REAL`, or `TEXT`.

## Examples

```sql
-- Single file
CREATE VIRTUAL TABLE notes USING markdown('notes/intro.md');

-- Directory (top-level only)
CREATE VIRTUAL TABLE docs USING markdown('notes', '*.md', 0);

-- Recursive folder scan
CREATE VIRTUAL TABLE docs_all USING markdown('notes', '*.md', 1);
```

## Development

```bash
dotnet restore
dotnet build SqliteMd.sln
dotnet test SqliteMd.sln
```

## Repository layout

- `SqliteMd/` extension implementation.
- `SqliteMd.Tests/` test project and fixtures.
- `.github/workflows/` CI and release automation.

