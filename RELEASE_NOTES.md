# SqliteMd Release

SqliteMd keeps Markdown as the source of truth and adds a rigorous SQLite surface on top.

This release includes:

- `SqliteMd.dll` and `SqliteMd.pdb` for direct SQLite usage
- `sqlitemd.exe` and `sqlitemd-win-x64.zip` for the companion CLI
- `sqlitemd.<version>.nupkg` for local .NET tool installation
- `sqlitemd-agent-skill.zip` for Codex

## Quick Start: Direct SQLite

```text
sqlite3 demo.db
.load .\SqliteMd.dll
```

Create a Markdown-backed table:

```sql
CREATE VIRTUAL TABLE notes
USING markdown_table(
  'notes.md',
  'Notes',
  'id INTEGER, title TEXT, stars INTEGER',
  'id'
);

INSERT INTO notes(id, title, stars)
VALUES (1, 'Release notes', 4);

SELECT id, title, stars
FROM notes
ORDER BY id;
```

## Quick Start: CLI

The easiest route is `sqlitemd-win-x64.zip`. It already places `SqliteMd.dll` beside `sqlitemd.exe`.

```powershell
.\sqlitemd.exe init
.\sqlitemd.exe targets list
.\sqlitemd.exe show notes
.\sqlitemd.exe append intake --set id=1 --set item="Ship docs refresh" --set owner=govert
.\sqlitemd.exe diagnose tasks --json
```

One-off query without config:

```powershell
.\sqlitemd.exe query `
  --kind table `
  --path .\notes.md `
  --title Notes `
  --schema "id INTEGER, title TEXT, stars INTEGER" `
  --key id `
  --query "SELECT id, title FROM source ORDER BY id" `
  --json
```

## Highlights

- Writable Markdown-backed virtual tables for single files
- `read_write`, `read_only`, and `append_only` modes through `markdown_table_mode(...)`
- First-table selection that ignores Markdown preamble
- Repository scans through `markdown_glob(...)` and `markdown_repo(...)`
- Unified diagnostics that explain acceptance, rejection, and capabilities
- A companion CLI with stable JSON output, target aliases, append/show shortcuts, and raw SQL passthrough
- A Codex skill that standardizes agent workflows around `sqlitemd --json`

## Installation Notes

- The current published build targets Windows x64.
- By default, `sqlitemd` looks for `SqliteMd.dll` beside the executable.
- Use `extensionPath` in `sqlitemd.json` when the extension lives elsewhere.
- The `.nupkg` asset is intended for local `dotnet tool install --add-source .`, not NuGet.org.

## Documentation

- Full README: https://github.com/SQLite-DNA/SqliteMd/blob/master/README.md
- CLI skill source: https://github.com/SQLite-DNA/SqliteMd/blob/master/skills/sqlitemd-agent/SKILL.md
