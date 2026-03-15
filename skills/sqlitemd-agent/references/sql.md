# Raw SQL Reference

Use raw SQL when the user wants custom projections, joins, aggregates, or a direct sqlite workflow.

## Single-file table

```sql
CREATE VIRTUAL TABLE notes
USING markdown_table(
  'notes.md',
  'Notes',
  'id INTEGER, title TEXT, stars INTEGER',
  'id'
);

SELECT id, title, stars
FROM notes
ORDER BY id;
```

## Mode-aware single-file table

```sql
CREATE VIRTUAL TABLE notes_ro
USING markdown_table_mode(
  'notes.md',
  'Notes',
  'id INTEGER, title TEXT, stars INTEGER',
  'id',
  'read_only'
);
```

## Repository or glob scan

```sql
CREATE VIRTUAL TABLE tasks
USING markdown_glob(
  'docs/**/*.md',
  'id INTEGER, title TEXT, owner TEXT, status TEXT'
);

SELECT id, title, owner, status, _path
FROM tasks
ORDER BY _path, id;
```

## Diagnostics

```sql
CREATE VIRTUAL TABLE diag
USING markdown_diagnostics(
  'glob',
  'docs/**/*.md',
  'id INTEGER, title TEXT, owner TEXT, status TEXT',
  '',
  ''
);

SELECT path, accepted, reason_code, reason_detail
FROM diag
ORDER BY path;
```

## When to leave the CLI

- Use raw SQL for joins across multiple attached targets.
- Use raw SQL for aggregates, grouping, and advanced filtering.
- Use a direct sqlite session when the user explicitly wants interactive SQL editing.
