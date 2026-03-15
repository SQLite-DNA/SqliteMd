# Diagnostics Reference

## First move

If a target is missing rows, failing to attach, or being skipped during a repo scan, run diagnostics first:

```text
sqlitemd diagnose <target> --json
```

For raw SQL workflows:

```sql
CREATE VIRTUAL TABLE diag
USING markdown_table_diagnostics(
  'notes.md',
  'Notes',
  'id INTEGER, title TEXT, stars INTEGER',
  'id'
);
```

## Common reason codes

- `ok`: target accepted without qualification
- `no_markdown_table`: no Markdown table found
- `no_markdown_table_create_on_write`: no table exists yet, but the single-file target can create one on first write
- `missing_file`: target file does not exist and read-only mode will remain empty
- `missing_file_create_on_write`: target file does not exist, but single-file writes can create it
- `column_count_mismatch`: Markdown table width does not match the supplied schema
- `key_column_not_found`: declared key column is not in the schema
- `key_column_not_integer`: key column exists but is not declared as `INTEGER`
- `no_files_matched`: repo/glob target matched no files
- `unsupported_write_mode`: CLI or SQL requested an invalid single-file write mode

## Practical flow

1. Resolve the target name or one-off target arguments.
2. Run diagnostics.
3. If the failure is schema-related, fix the schema or key declaration first.
4. If the failure is target-related, fix the path, glob, or missing file issue.
5. Only after diagnostics are clean should you switch to `show`, `append`, or raw SQL analysis.

## Agent rule

Do not guess why a Markdown file was skipped when `diagnose` can answer it directly.
