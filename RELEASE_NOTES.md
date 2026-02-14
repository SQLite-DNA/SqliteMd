# SQLiteMd Release Notes

## v0.1.10 (unreleased)

### Added
- Added `write_markdown(...)` SQL function for creating and updating Markdown documents from SQLite.
- Added tests for markdown document creation, folder-target writing, and overwrite behavior.

### Changed
- Documented the read vs. write story in README, including concrete CLI session output.

### Notes on write semantics
- `CREATE VIRTUAL TABLE ... USING markdown(...)` is a read-only projection of Markdown content.
- Native `INSERT`/`UPDATE` against that virtual table is not supported because the SqliteDna table-function integration only exposes materialized result-set tables.
- Use `write_markdown(...)` (and `overwrite` flag) for explicit write operations instead.
