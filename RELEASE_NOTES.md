# SQLiteMd Release Notes

## v0.1.11 (unreleased)

### Added
- Added row-oriented write APIs:
  - `insert_markdown_row(path, table_title, columns_csv, row_json)` for appending a single row.
  - `rewrite_markdown_row(path, table_title, columns_csv, row_index, row_json)` for replacing a row in a markdown table.
- Added tests covering row inserts and row rewrites.

### Changed
- Added helper API section in README for function-based write intent and updated examples.
- Refined `.github/workflows/release.yml` to package a stable Windows x64 zip artifact and release it directly from tags, with manual dispatch support.

## v0.1.10

### Added
- Added `write_markdown(...)` SQL function for creating and updating Markdown documents from SQLite.
- Added tests for markdown document creation, folder-target writing, and overwrite behavior.

### Changed
- Documented the read vs. write story in README, including concrete CLI session output.

### Notes on write semantics
- `CREATE VIRTUAL TABLE ... USING markdown(...)` is a read-only projection of Markdown content.
- Native `INSERT`/`UPDATE` against that virtual table is not supported because the SqliteDna table-function integration only exposes materialized result-set tables.
- Use `write_markdown(...)` (and `overwrite` flag) for explicit write operations instead.
