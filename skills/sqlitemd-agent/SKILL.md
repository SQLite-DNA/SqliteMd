---
name: sqlitemd-agent
description: Use this skill when the user wants to inspect Markdown-backed tables, append rows to a single Markdown table, scan Markdown repositories as queryable data, or diagnose why SqliteMd did not accept a file. Prefer the `sqlitemd` CLI with `--json` for common tasks, and fall back to raw SQL or a direct sqlite session for advanced joins, aggregation, or ad hoc analysis.
---

# SqliteMd Agent

## Overview

Use this skill to work with SqliteMd through the companion `sqlitemd` CLI or the raw SQL extension surface. The default path is: resolve the target, run diagnostics first when anything is unclear, use `show` or `append` for routine operations, and use raw SQL only when the workflow is too complex for the shortcut commands.

## Workflow

1. Prefer the CLI for routine work.
   Use `sqlitemd --json` so output is stable and easy to consume.

2. Diagnose before guessing.
   If a target fails to load or seems to return no rows, run `sqlitemd diagnose <target> --json` first.

3. Use shortcuts for common single-target actions.
   Use `show` for reads and `append` for single-file inserts.

4. Fall back to raw SQL for advanced analysis.
   Use `sqlitemd sql <target> --query ...` or a direct sqlite session for joins, grouping, or multi-target work.

5. Do not hand-edit Markdown files when SqliteMd can perform the change safely.

## Quick Start

Read a configured target:

```text
sqlitemd show notes --json
```

Append a row to a single-file table target:

```text
sqlitemd append notes --set id=3 --set title="Examples" --set stars=9 --json
```

Diagnose why a target is not usable:

```text
sqlitemd diagnose notes --json
```

Run a custom query against an attached target:

```text
sqlitemd sql notes --query "SELECT id, title FROM notes ORDER BY id" --json
```

## When to Use Which Surface

- Use the CLI for routine reads, appends, diagnostics, and scripted automation.
- Use `sqlitemd sql` when you still want the CLI runtime but need custom SQL.
- Use a direct sqlite session only when the user explicitly wants interactive work or the SQL is large enough that an external session is clearer.

## References

- CLI commands and output conventions: `references/cli.md`
- Raw SQL attachment patterns: `references/sql.md`
- Diagnostics flow and reason codes: `references/diagnostics.md`
- Example config asset: `assets/sqlitemd.json.example`
