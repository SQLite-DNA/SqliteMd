# SQLiteMd Release Notes

## v0.2.0 (unreleased)

### Breaking
- Replaced the previous read-only `markdown(...)` table-function and file-oriented write helpers with a single writable virtual table: `markdown_table(path, table_title, schema, key_column)`.
- Standard SQL `INSERT`/`UPDATE`/`DELETE` now mutate the backing Markdown file through SQLite virtual-table `xUpdate`.

### Added
- Writable Markdown-backed virtual table implementation with transactional staging.
- CI/release workflows check out `SQLite-DNA/SqliteDna` to build against the in-flight writable virtual-table support.

