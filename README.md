# SQLiteMd

`SqliteMd` is a `SqliteDna` extension that treats a Markdown pipe table as a writable SQLite virtual table.

The backing store is a `.md` file on disk. `INSERT`, `UPDATE`, and `DELETE` mutate the Markdown table through SQLite virtual-table `xUpdate`.

## Quick start

1. Download the latest release asset (`SqliteMd.dll`).
2. Load it in `sqlite3`.
3. Create a virtual table backed by a Markdown file.

Example session (Windows PowerShell + sqlite3):

```text
PS> Invoke-WebRequest -Uri "https://github.com/SQLite-DNA/SqliteMd/releases/latest/download/SqliteMd.dll" `
       -OutFile SqliteMd.dll
PS> sqlite3 demo.db
SQLite version 3.48.0 2025-01-09 12:00:00
sqlite> .load .\SqliteMd.dll
sqlite> CREATE VIRTUAL TABLE notes
   ...> USING markdown_table('notes.md', 'Notes', 'id INTEGER, title TEXT, stars INTEGER', 'id');
sqlite> INSERT INTO notes(id, title, stars) VALUES (1, 'Release notes', 4);
sqlite> INSERT INTO notes(id, title, stars) VALUES (2, 'Testing coverage', 7);
sqlite> SELECT id, title, stars FROM notes ORDER BY id;
1|Release notes|4
2|Testing coverage|7
sqlite> UPDATE notes SET stars = 10 WHERE id = 2;
sqlite> DELETE FROM notes WHERE id = 1;
sqlite> SELECT id, title, stars FROM notes ORDER BY id;
2|Testing coverage|10
```

## API

```sql
CREATE VIRTUAL TABLE t
USING markdown_table(path, table_title, schema, key_column);
```

- `path`
  - Markdown file path (with or without `.md` extension).
  - If `path` is a directory, the extension writes `<table_title>.md` inside that directory.
- `table_title`
  - Markdown heading used to find the table (`# <table_title>`).
  - Use `''` to target a file with exactly one table and no heading.
- `schema`
  - Column definitions (names + types) for the table.
  - Example: `id INTEGER, title TEXT, stars INTEGER`
- `key_column`
  - Column name from `schema` used as the SQLite rowid mapping.
  - Must be declared as `INTEGER` in `schema`.

### Semantics

- Reads parse the Markdown table from disk.
- Writes stage changes using SQLite transaction callbacks and rewrite the table block in the Markdown document.
- The key column is the stable row identity. `rowid` must match the key column value.

## Development (local)

This repo currently builds against a local checkout of `SQLite-DNA/SqliteDna` (for writable virtual-table support).

Expected folder layout:

```text
C:\Work\
  SqliteMd\
  SQLite-DNA\
    SqliteDna\
```

Commands:

```bash
dotnet restore
dotnet build SqliteMd/SqliteMd.csproj -c Release
dotnet test SqliteMd.Tests/SqliteMd.Tests.csproj -c Release
```

