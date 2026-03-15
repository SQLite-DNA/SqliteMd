namespace SqliteMd.Cli;

internal static class CliTopics
{
    public static string Config => """
`sqlitemd.json` is optional, but it is the normal way to define reusable targets.

Discovery:
- `sqlitemd` looks for `sqlitemd.json` in the current directory and then walks upward.
- `--config <path>` overrides discovery.

Schema:
```json
{
  "extensionPath": ".\\SqliteMd.dll",
  "defaults": {
    "output": "table"
  },
  "targets": {
    "notes": {
      "kind": "table",
      "path": "examples\\single-file\\release-notes.md",
      "title": "Release Notes",
      "schema": "id INTEGER, title TEXT, stars INTEGER",
      "key": "id",
      "writeMode": "read_write"
    },
    "tasks": {
      "kind": "repo",
      "glob": "examples\\repo\\docs\\**\\*.md",
      "schema": "id INTEGER, title TEXT, owner TEXT, status TEXT"
    }
  }
}
```

Target kinds:
- `table`: single Markdown file, supports `read_write`, `read_only`, or `append_only`
- `repo`: read-only scan of the first Markdown table in each matching file

Path rules:
- `extensionPath`, `path`, and `glob` are resolved relative to the config file directory when they are not absolute.
""";

    public static string Diagnostics => """
Use `sqlitemd diagnose <target>` before debugging Markdown acceptance issues.

Typical workflow:
1. Run `sqlitemd diagnose notes`
2. Check `reason_code`
3. Inspect `heading`, `table_start_line`, `detected_column_count`, and capability columns

Common `reason_code` values:
- `ok`
- `no_markdown_table`
- `no_markdown_table_create_on_write`
- `missing_file`
- `missing_file_create_on_write`
- `column_count_mismatch`
- `key_column_not_found`
- `key_column_not_integer`
- `file_read_error`
- `no_files_matched`

Interpretation:
- `accepted = 1` means the target is valid for the selected mode
- `create_on_write = 1` means a single-file target has no table yet, but first write can create it
- `can_insert`, `can_update`, and `can_delete` reflect the effective write mode
""";

    public static string Output => """
Output modes:
- `table`: human-readable aligned table
- `json`: machine-oriented JSON
- `csv`: comma-separated rows with headers
- `tsv`: tab-separated rows with headers

Agent guidance:
- Prefer `--json` for automation and Codex workflows
- Use `--echo-sql` when you want to see the exact `CREATE VIRTUAL TABLE` and query statements
""";

    public static string Modes => """
Single-file write modes:
- `read_write`: default; inserts, updates, and deletes are allowed
- `read_only`: reads only; all writes are rejected
- `append_only`: reads and inserts are allowed; updates and deletes are rejected

Repo targets are always read-only.
""";

    public static string Skill => """
The companion Codex skill lives in `skills/sqlitemd-agent`.

Use it when:
- querying Markdown-backed tables
- appending rows to single-file targets
- scanning Markdown repositories
- diagnosing why files were skipped or rejected

Agent workflow:
- prefer `sqlitemd --json` for routine operations
- use `diagnose` before debugging acceptance issues
- fall back to direct SQL for joins, aggregation, and advanced ad hoc analysis
""";

    public static string HelpAll => """
SqliteMd companion CLI

This CLI complements direct `sqlite3` usage. It loads `SqliteMd.dll`, creates the same virtual tables you would create manually, and adds shortcuts for common workflows.

Quick examples:

1. Single-file read/write
   `sqlitemd init`
   `sqlitemd show notes`
   `sqlitemd append notes --set id=3 --set title="Docs polish" --set stars=8`

2. Append-only intake
   `sqlitemd show intake`
   `sqlitemd append intake --set id=11 --set item="Ship docs refresh" --set owner=govert`

3. Repo diagnostics
   `sqlitemd diagnose tasks --json`

Topics:
- `sqlitemd help config`
- `sqlitemd help diagnostics`
- `sqlitemd help output`
- `sqlitemd help modes`
- `sqlitemd help skill`

Global options:
- `--config <path>`
- `--json`
- `--output table|json|csv|tsv`
- `--echo-sql`
- `--verbose`
- `--no-color`
- `--help-all`

Exit codes:
- `0` success
- `2` usage or validation error
- `3` config or target resolution error
- `4` SQLite or extension-load error
- `5` semantic refusal or diagnostics rejection
""";

    public static string Completion(string shell) => shell switch
    {
        "pwsh" => """
Register-ArgumentCompleter -Native -CommandName sqlitemd -ScriptBlock {
    param($wordToComplete, $commandAst, $cursorPosition)
    'init','targets','show','append','diagnose','sql','query','help','completion','--config','--json','--output','--echo-sql','--verbose','--no-color','--help-all' |
        Where-Object { $_ -like "$wordToComplete*" } |
        ForEach-Object { [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterValue', $_) }
}
""",
        "bash" => """
_sqlitemd_completions()
{
  local cur="${COMP_WORDS[COMP_CWORD]}"
  local words="init targets show append diagnose sql query help completion --config --json --output --echo-sql --verbose --no-color --help-all"
  COMPREPLY=( $(compgen -W "${words}" -- "${cur}") )
}
complete -F _sqlitemd_completions sqlitemd
""",
        "zsh" => """
#compdef sqlitemd
local -a words
words=(
  "init"
  "targets"
  "show"
  "append"
  "diagnose"
  "sql"
  "query"
  "help"
  "completion"
  "--config"
  "--json"
  "--output"
  "--echo-sql"
  "--verbose"
  "--no-color"
  "--help-all"
)
_describe 'sqlitemd' words
""",
        _ => "Unsupported shell."
    };
}
