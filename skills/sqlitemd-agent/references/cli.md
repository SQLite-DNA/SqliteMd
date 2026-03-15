# CLI Reference

Use the `sqlitemd` CLI first for routine automation and agent work.

## Rules

- Prefer `--json` for machine-readable output.
- Prefer configured targets over one-off command arguments when a project has `sqlitemd.json`.
- Use `diagnose` before trying to infer why a target is empty, rejected, or failing to load.
- Use `--echo-sql` when you need to explain or debug the generated SQL.

## Core commands

Initialize a sample config:

```text
sqlitemd init
```

List configured targets:

```text
sqlitemd targets list --json
```

Show rows from a target:

```text
sqlitemd show notes --json
```

Append a row to a single-file table target:

```text
sqlitemd append notes --set id=4 --set title="New item" --set stars=5 --json
```

Run diagnostics:

```text
sqlitemd diagnose notes --json
sqlitemd diagnose tasks --json
```

Run raw SQL against a configured target:

```text
sqlitemd sql notes --query "SELECT id, title FROM notes ORDER BY id" --json
```

Run a one-off query without config:

```text
sqlitemd query --kind table --path notes.md --title Notes --schema "id INTEGER, title TEXT, stars INTEGER" --key id --query "SELECT * FROM source" --json
```

## Output expectations

- `show` returns `ok`, `command`, `target`, `columns`, `rows`, and `rowCount`.
- `append` returns `ok`, `command`, `target`, `affected`, and `row`.
- `diagnose` returns `ok`, `command`, `target`, and `diagnostics`.
- Failures return `ok: false`, plus a stable `code` and `message`.

## Agent defaults

- Always start with `--json` unless the user explicitly asks for human-oriented terminal output.
- If a target name fails to resolve, inspect `sqlitemd.json` before inventing target metadata.
- For repetitive operations on one project, prefer aliases from config over repeated raw path/schema arguments.
