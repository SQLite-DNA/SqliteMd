using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SqliteMd.Cli;

public static class SqliteMdCliApp
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly string InitTemplate = """
{
  "extensionPath": ".\\SqliteMd.dll",
  "defaults": {
    "output": "table"
  },
  "targets": {
    "notes": {
      "kind": "table",
      "path": "notes.md",
      "title": "Notes",
      "schema": "id INTEGER, title TEXT, stars INTEGER",
      "key": "id",
      "writeMode": "read_write"
    },
    "intake": {
      "kind": "table",
      "path": "weekly-status.md",
      "title": "Weekly Status",
      "schema": "id INTEGER, item TEXT, owner TEXT",
      "key": "id",
      "writeMode": "append_only"
    },
    "tasks": {
      "kind": "repo",
      "glob": "docs\\**\\*.md",
      "schema": "id INTEGER, title TEXT, owner TEXT, status TEXT"
    }
  }
}
""";

    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr, string workingDirectory)
    {
        var normalizedWorkingDirectory = Path.GetFullPath(string.IsNullOrWhiteSpace(workingDirectory)
            ? Directory.GetCurrentDirectory()
            : workingDirectory);

        if (args.Any(arg => string.Equals(arg, "--help-all", StringComparison.OrdinalIgnoreCase)))
        {
            await stdout.WriteLineAsync(CliTopics.HelpAll);
            return 0;
        }

        var root = BuildRootCommand(normalizedWorkingDirectory, stdout, stderr);
        using var redirect = new ConsoleRedirect(stdout, stderr);
        return await root.InvokeAsync(args);
    }

    private static RootCommand BuildRootCommand(string workingDirectory, TextWriter stdout, TextWriter stderr)
    {
        var configOption = new Option<string?>("--config", "Path to sqlitemd.json.");
        var jsonOption = new Option<bool>("--json", "Emit JSON output.");
        var outputOption = new Option<string>("--output", () => "table", "Output format: table, json, csv, or tsv.");
        var echoSqlOption = new Option<bool>("--echo-sql", "Print generated SQL to stderr.");
        var verboseOption = new Option<bool>("--verbose", "Print extra execution details to stderr.");
        var noColorOption = new Option<bool>("--no-color", "Reserved for plain-text output without ANSI styling.");

        var root = new RootCommand(
            """
            Companion CLI for SqliteMd.

            This tool auto-loads `SqliteMd.dll`, creates the same virtual tables you would create in SQLite manually, and adds shortcut commands for common Markdown-table workflows.

            End-to-end examples:
              sqlitemd show notes
              sqlitemd append intake --set id=11 --set item="Ship docs refresh" --set owner=govert
              sqlitemd diagnose tasks --json
            """);
        root.Name = "sqlitemd";

        root.AddGlobalOption(configOption);
        root.AddGlobalOption(jsonOption);
        root.AddGlobalOption(outputOption);
        root.AddGlobalOption(echoSqlOption);
        root.AddGlobalOption(verboseOption);
        root.AddGlobalOption(noColorOption);

        var initCommand = new Command("init",
            """
            Write a starter `sqlitemd.json` in the current directory.

            Config-based usage:
              sqlitemd init

            Exit codes:
              0 success
              2 usage or validation error
              3 config write refusal
            """);
        var forceOption = new Option<bool>("--force", "Overwrite an existing sqlitemd.json.");
        initCommand.AddOption(forceOption);
        initCommand.SetHandler(async (InvocationContext context) =>
        {
            var execution = GetExecutionOptions(context, workingDirectory, configOption, jsonOption, outputOption, echoSqlOption, verboseOption, noColorOption);
            await ExecuteWithHandlingAsync(context, execution, stdout, stderr, async () =>
            {
                var targetPath = Path.Combine(execution.WorkingDirectory, "sqlitemd.json");
                if (File.Exists(targetPath) && !context.ParseResult.GetValueForOption(forceOption))
                {
                    throw new CliFailure(3, "config_exists", $"Configuration already exists at '{targetPath}'.", new { path = targetPath });
                }

                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                await File.WriteAllTextAsync(targetPath, InitTemplate + Environment.NewLine, Encoding.UTF8);

                var payload = new
                {
                    ok = true,
                    command = "init",
                    path = targetPath,
                    created = true
                };

                await WriteResultAsync(execution, stdout, payload, $"Wrote {targetPath}");
            });
        });
        root.AddCommand(initCommand);

        var targetsCommand = new Command("targets", "Inspect configured target aliases.");
        var targetsListCommand = new Command("list",
            """
            List aliases from `sqlitemd.json`.

            Config-based usage:
              sqlitemd targets list

            JSON example:
              sqlitemd targets list --json

            Exit codes:
              0 success
              3 config or target resolution error
            """);
        targetsListCommand.SetHandler(async (InvocationContext context) =>
        {
            var execution = GetExecutionOptions(context, workingDirectory, configOption, jsonOption, outputOption, echoSqlOption, verboseOption, noColorOption);
            await ExecuteWithHandlingAsync(context, execution, stdout, stderr, async () =>
            {
                var resolution = LoadRequiredConfig(execution);
                var items = resolution.Config.Targets
                    .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(pair => new
                    {
                        name = pair.Key,
                        kind = NormalizeKind(pair.Value.Kind),
                        path = ResolveTargetPreview(pair.Value, resolution.Path),
                        schema = pair.Value.Schema,
                        key = pair.Value.Key,
                        writeMode = NormalizeKind(pair.Value.Kind) == "repo"
                            ? "read_only"
                            : NormalizeWriteMode(pair.Value.WriteMode, allowDefault: true)
                    })
                    .ToArray();

                var payload = new
                {
                    ok = true,
                    command = "targets list",
                    configPath = resolution.Path,
                    targets = items
                };

                if (execution.Json || execution.Output.Equals("json", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteJsonAsync(stdout, payload);
                    return;
                }

                var lines = new List<string>
                {
                    $"Config: {resolution.Path}"
                };
                lines.AddRange(items.Select(item => $"{item.name} [{item.kind}] {item.path}"));
                await stdout.WriteLineAsync(string.Join(Environment.NewLine, lines));
            });
        });
        targetsCommand.AddCommand(targetsListCommand);
        root.AddCommand(targetsCommand);

        var targetArgument = new Argument<string>("target", "Target alias from sqlitemd.json.");

        var showCommand = new Command("show",
            """
            Query all rows from a configured target.

            Config-based usage:
              sqlitemd show notes

            JSON example:
              sqlitemd show notes --json

            Exit codes:
              0 success
              3 config or target resolution error
              4 SQLite or extension-load error
            """);
        showCommand.AddArgument(targetArgument);
        showCommand.SetHandler(async (InvocationContext context) =>
        {
            var execution = GetExecutionOptions(context, workingDirectory, configOption, jsonOption, outputOption, echoSqlOption, verboseOption, noColorOption);
            await ExecuteWithHandlingAsync(context, execution, stdout, stderr, async () =>
            {
                var target = ResolveConfiguredTarget(execution, context.ParseResult.GetValueForArgument(targetArgument)!);
                using var session = OpenSession(execution);
                var attachSql = BuildAttachStatement(target);
                EchoSql(execution, stderr, attachSql);
                session.ExecuteNonQuery(attachSql);

                var querySql = BuildShowQuery(target);
                EchoSql(execution, stderr, querySql);
                var result = session.ExecuteQuery(querySql);

                var payload = new
                {
                    ok = true,
                    command = "show",
                    target = target.Alias,
                    columns = result.Columns,
                    rows = result.Rows,
                    rowCount = result.Rows.Count
                };

                await WriteQueryPayloadAsync(execution, stdout, payload, result);
            });
        });
        root.AddCommand(showCommand);

        var appendCommand = new Command("append",
            """
            Insert a row into a configured single-file target.

            Config-based usage:
              sqlitemd append notes --set id=3 --set title="Docs polish" --set stars=8

            JSON example:
              sqlitemd append intake --set id=11 --set item="Ship docs refresh" --set owner=govert --json

            Exit codes:
              0 success
              3 config or target resolution error
              4 SQLite or extension-load error
              5 semantic refusal or diagnostics rejection
            """);
        appendCommand.AddArgument(targetArgument);
        var setOption = new Option<string[]>("--set", "Column assignment in the form column=value.")
        {
            AllowMultipleArgumentsPerToken = true
        };
        appendCommand.AddOption(setOption);
        appendCommand.SetHandler(async (InvocationContext context) =>
        {
            var execution = GetExecutionOptions(context, workingDirectory, configOption, jsonOption, outputOption, echoSqlOption, verboseOption, noColorOption);
            await ExecuteWithHandlingAsync(context, execution, stdout, stderr, async () =>
            {
                var resolved = ResolveConfiguredTarget(execution, context.ParseResult.GetValueForArgument(targetArgument)!);
                if (resolved is not TableTarget target)
                {
                    throw new CliFailure(5, "append_requires_table_target", "The append command only works with single-file table targets.");
                }

                if (target.WriteMode.Equals("read_only", StringComparison.OrdinalIgnoreCase))
                {
                    throw new CliFailure(5, "append_rejected_read_only", $"Target '{target.Alias}' is configured as read_only.");
                }

                var assignments = ParseAssignments(context.ParseResult.GetValueForOption(setOption));
                if (!assignments.ContainsKey(target.KeyColumn))
                {
                    throw new CliFailure(2, "missing_key_assignment", $"Append requires --set {target.KeyColumn}=<value>.");
                }

                using var session = OpenSession(execution);
                await EnsureSingleFileDiagnosticsAcceptedAsync(session, execution, stderr, target);

                var attachSql = BuildAttachStatement(target);
                EchoSql(execution, stderr, attachSql);
                session.ExecuteNonQuery(attachSql);

                var schemaColumns = ParseSchemaColumns(target.Schema);
                var parameterValues = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                var columnNames = new List<string>();
                var placeholders = new List<string>();

                for (var i = 0; i < schemaColumns.Count; i++)
                {
                    var column = schemaColumns[i];
                    columnNames.Add(QuoteIdentifier(column));
                    var parameterName = $"@p{i}";
                    placeholders.Add(parameterName);
                    parameterValues[parameterName] = assignments.TryGetValue(column, out var value) ? value : null;
                }

                var insertSql = $"INSERT INTO {QuoteIdentifier(target.Alias)} ({string.Join(", ", columnNames)}) VALUES ({string.Join(", ", placeholders)})";
                EchoSql(execution, stderr, insertSql);
                var affected = session.ExecuteNonQuery(insertSql, parameterValues);

                var payload = new
                {
                    ok = true,
                    command = "append",
                    target = target.Alias,
                    affected,
                    row = schemaColumns.ToDictionary(column => column, column => assignments.TryGetValue(column, out var value) ? value : null)
                };

                await WriteResultAsync(execution, stdout, payload, $"Inserted {affected} row into {target.Alias}");
            });
        });
        root.AddCommand(appendCommand);

        var diagnoseCommand = new Command("diagnose",
            """
            Show unified diagnostics for a configured target.

            Config-based usage:
              sqlitemd diagnose notes

            JSON example:
              sqlitemd diagnose tasks --json

            Exit codes:
              0 success
              3 config or target resolution error
              4 SQLite or extension-load error
              5 diagnostics rejection
            """);
        diagnoseCommand.AddArgument(targetArgument);
        diagnoseCommand.SetHandler(async (InvocationContext context) =>
        {
            var execution = GetExecutionOptions(context, workingDirectory, configOption, jsonOption, outputOption, echoSqlOption, verboseOption, noColorOption);
            await ExecuteWithHandlingAsync(context, execution, stdout, stderr, async () =>
            {
                var target = ResolveConfiguredTarget(execution, context.ParseResult.GetValueForArgument(targetArgument)!);
                using var session = OpenSession(execution);
                var attachSql = BuildDiagnosticsStatement("diag", target);
                EchoSql(execution, stderr, attachSql);
                session.ExecuteNonQuery(attachSql);

                var querySql = "SELECT * FROM [diag] ORDER BY COALESCE(path, target), reason_code";
                EchoSql(execution, stderr, querySql);
                var result = session.ExecuteQuery(querySql);
                var rejected = result.Rows.Any(row => Convert.ToInt64(row["accepted"] ?? 0L) == 0L);

                var payload = new
                {
                    ok = !rejected,
                    command = "diagnose",
                    target = target.Alias,
                    diagnostics = result.Rows
                };

                await WriteDiagnosticsPayloadAsync(execution, stdout, payload, result);
                context.ExitCode = rejected ? 5 : 0;
            });
        });
        root.AddCommand(diagnoseCommand);

        var queryOption = new Option<string>("--query", "SQL query to execute.") { IsRequired = true };

        var sqlCommand = new Command("sql",
            """
            Run raw SQL against a configured target after attaching it.

            Config-based usage:
              sqlitemd sql notes --query "SELECT * FROM notes ORDER BY id"

            JSON example:
              sqlitemd sql tasks --query "SELECT title, _path FROM tasks ORDER BY _path" --json

            Exit codes:
              0 success
              3 config or target resolution error
              4 SQLite or extension-load error
            """);
        sqlCommand.AddArgument(targetArgument);
        sqlCommand.AddOption(queryOption);
        sqlCommand.SetHandler(async (InvocationContext context) =>
        {
            var execution = GetExecutionOptions(context, workingDirectory, configOption, jsonOption, outputOption, echoSqlOption, verboseOption, noColorOption);
            await ExecuteWithHandlingAsync(context, execution, stdout, stderr, async () =>
            {
                var target = ResolveConfiguredTarget(execution, context.ParseResult.GetValueForArgument(targetArgument)!);
                var sql = context.ParseResult.GetValueForOption(queryOption)!;

                using var session = OpenSession(execution);
                var attachSql = BuildAttachStatement(target);
                EchoSql(execution, stderr, attachSql);
                session.ExecuteNonQuery(attachSql);

                EchoSql(execution, stderr, sql);
                var result = ExecuteAdHocSql(session, sql);

                var payload = new
                {
                    ok = true,
                    command = "sql",
                    target = target.Alias,
                    columns = result.Columns,
                    rows = result.Rows,
                    rowCount = result.Rows.Count
                };

                await WriteQueryPayloadAsync(execution, stdout, payload, result);
            });
        });
        root.AddCommand(sqlCommand);

        var queryCommand = new Command("query",
            """
            Run raw SQL against a one-off table or repo target without a config file.

            One-off usage:
              sqlitemd query --kind table --path notes.md --title Notes --schema "id INTEGER, title TEXT" --key id --query "SELECT * FROM source"
              sqlitemd query --kind repo --glob "docs/**/*.md" --schema "id INTEGER, title TEXT, owner TEXT, status TEXT" --query "SELECT * FROM source"

            JSON example:
              sqlitemd query --kind repo --glob "examples/repo/docs/**/*.md" --schema "id INTEGER, title TEXT, owner TEXT, status TEXT" --query "SELECT title, _path FROM source ORDER BY _path" --json

            Exit codes:
              0 success
              2 usage or validation error
              4 SQLite or extension-load error
            """);
        var kindOption = new Option<string>("--kind", "Target kind: table or repo.") { IsRequired = true };
        var nameOption = new Option<string>("--name", () => "source", "Attached virtual table name.");
        var pathOption = new Option<string?>("--path", "Markdown path for --kind table.");
        var titleOption = new Option<string?>("--title", "Title to use when creating a single-file table.");
        var schemaOption = new Option<string?>("--schema", "Schema for the target.");
        var keyOption = new Option<string?>("--key", "Key column for --kind table.");
        var writeModeOption = new Option<string>("--write-mode", () => "read_write", "Write mode for --kind table.");
        var globOption = new Option<string?>("--glob", "Glob pattern for --kind repo.");
        queryCommand.AddOption(kindOption);
        queryCommand.AddOption(nameOption);
        queryCommand.AddOption(pathOption);
        queryCommand.AddOption(titleOption);
        queryCommand.AddOption(schemaOption);
        queryCommand.AddOption(keyOption);
        queryCommand.AddOption(writeModeOption);
        queryCommand.AddOption(globOption);
        queryCommand.AddOption(queryOption);
        queryCommand.SetHandler(async (InvocationContext context) =>
        {
            var execution = GetExecutionOptions(context, workingDirectory, configOption, jsonOption, outputOption, echoSqlOption, verboseOption, noColorOption);
            await ExecuteWithHandlingAsync(context, execution, stdout, stderr, async () =>
            {
                var target = ResolveOneOffTarget(
                    execution,
                    context.ParseResult.GetValueForOption(nameOption)!,
                    context.ParseResult.GetValueForOption(kindOption)!,
                    context.ParseResult.GetValueForOption(pathOption),
                    context.ParseResult.GetValueForOption(titleOption),
                    context.ParseResult.GetValueForOption(schemaOption),
                    context.ParseResult.GetValueForOption(keyOption),
                    context.ParseResult.GetValueForOption(writeModeOption)!,
                    context.ParseResult.GetValueForOption(globOption));

                var sql = context.ParseResult.GetValueForOption(queryOption)!;

                using var session = OpenSession(execution);
                var attachSql = BuildAttachStatement(target);
                EchoSql(execution, stderr, attachSql);
                session.ExecuteNonQuery(attachSql);

                EchoSql(execution, stderr, sql);
                var result = ExecuteAdHocSql(session, sql);

                var payload = new
                {
                    ok = true,
                    command = "query",
                    target = target.Alias,
                    columns = result.Columns,
                    rows = result.Rows,
                    rowCount = result.Rows.Count
                };

                await WriteQueryPayloadAsync(execution, stdout, payload, result);
            });
        });
        root.AddCommand(queryCommand);

        var helpCommand = new Command("help",
            """
            Show generated command help or topic guides.

            Topics:
              config
              diagnostics
              output
              modes
              skill
            """);
        var topicArgument = new Argument<string?>("topic", () => null, "Command name or topic.");
        helpCommand.AddArgument(topicArgument);
        helpCommand.SetHandler(async (InvocationContext context) =>
        {
            var execution = GetExecutionOptions(context, workingDirectory, configOption, jsonOption, outputOption, echoSqlOption, verboseOption, noColorOption);
            var topic = context.ParseResult.GetValueForArgument(topicArgument);
            if (string.IsNullOrWhiteSpace(topic))
            {
                await stdout.WriteLineAsync(CliTopics.HelpAll);
                context.ExitCode = 0;
                return;
            }

            switch (topic.Trim().ToLowerInvariant())
            {
                case "config":
                    await stdout.WriteLineAsync(CliTopics.Config);
                    context.ExitCode = 0;
                    return;
                case "diagnostics":
                    await stdout.WriteLineAsync(CliTopics.Diagnostics);
                    context.ExitCode = 0;
                    return;
                case "output":
                    await stdout.WriteLineAsync(CliTopics.Output);
                    context.ExitCode = 0;
                    return;
                case "modes":
                    await stdout.WriteLineAsync(CliTopics.Modes);
                    context.ExitCode = 0;
                    return;
                case "skill":
                    await stdout.WriteLineAsync(CliTopics.Skill);
                    context.ExitCode = 0;
                    return;
                default:
                    if (TryGetCommandHelp(topic, out var helpText))
                    {
                        await stdout.WriteLineAsync(helpText);
                        context.ExitCode = 0;
                        return;
                    }

                    await WriteFailureAsync(execution, stdout, stderr, new CliFailure(2, "unknown_help_topic", $"Unknown help topic '{topic}'."));
                    context.ExitCode = 2;
                    return;
            }
        });
        root.AddCommand(helpCommand);

        var completionCommand = new Command("completion",
            """
            Print a simple shell completion script.

            Usage:
              sqlitemd completion pwsh
              sqlitemd completion bash
              sqlitemd completion zsh
            """);
        var shellArgument = new Argument<string>("shell", "Shell name: pwsh, bash, or zsh.");
        completionCommand.AddArgument(shellArgument);
        completionCommand.SetHandler(async (InvocationContext context) =>
        {
            var shell = context.ParseResult.GetValueForArgument(shellArgument)!;
            var script = CliTopics.Completion(shell.Trim().ToLowerInvariant());
            if (script == "Unsupported shell.")
            {
                await WriteFailureAsync(
                    GetExecutionOptions(context, workingDirectory, configOption, jsonOption, outputOption, echoSqlOption, verboseOption, noColorOption),
                    stdout,
                    stderr,
                    new CliFailure(2, "unsupported_shell", $"Unsupported shell '{shell}'."));
                context.ExitCode = 2;
                return;
            }

            await stdout.WriteLineAsync(script);
            context.ExitCode = 0;
        });
        root.AddCommand(completionCommand);

        return root;
    }

    private static ExecutionOptions GetExecutionOptions(
        InvocationContext context,
        string workingDirectory,
        Option<string?> configOption,
        Option<bool> jsonOption,
        Option<string> outputOption,
        Option<bool> echoSqlOption,
        Option<bool> verboseOption,
        Option<bool> noColorOption)
    {
        var output = context.ParseResult.GetValueForOption(outputOption) ?? "table";
        if (context.ParseResult.GetValueForOption(jsonOption))
        {
            output = "json";
        }

        return new ExecutionOptions(
            workingDirectory,
            context.ParseResult.GetValueForOption(configOption),
            context.ParseResult.GetValueForOption(jsonOption),
            output,
            context.ParseResult.GetValueForOption(echoSqlOption),
            context.ParseResult.GetValueForOption(verboseOption),
            context.ParseResult.GetValueForOption(noColorOption));
    }

    private static async Task ExecuteWithHandlingAsync(
        InvocationContext context,
        ExecutionOptions execution,
        TextWriter stdout,
        TextWriter stderr,
        Func<Task> action)
    {
        try
        {
            await action();
            if (context.ExitCode == 0)
            {
                context.ExitCode = 0;
            }
        }
        catch (CliFailure failure)
        {
            await WriteFailureAsync(execution, stdout, stderr, failure);
            context.ExitCode = failure.ExitCode;
        }
        catch (Exception exception)
        {
            await WriteFailureAsync(execution, stdout, stderr, new CliFailure(4, "sqlite_error", exception.Message, new { exception = exception.GetType().Name }));
            context.ExitCode = 4;
        }
    }

    private static ConfigResolution LoadRequiredConfig(ExecutionOptions execution)
    {
        var discoveredPath = execution.ExplicitConfigPath is not null
            ? Path.GetFullPath(execution.ExplicitConfigPath, execution.WorkingDirectory)
            : DiscoverConfigPath(execution.WorkingDirectory);

        if (discoveredPath is null || !File.Exists(discoveredPath))
        {
            throw new CliFailure(
                3,
                "config_not_found",
                "No sqlitemd.json was found. Run `sqlitemd init` or use `sqlitemd query --kind ...` for a one-off target.",
                new { workingDirectory = execution.WorkingDirectory });
        }

        try
        {
            var json = File.ReadAllText(discoveredPath);
            var config = JsonSerializer.Deserialize<CliConfig>(json, JsonOptions);
            if (config is null)
            {
                throw new InvalidOperationException("Configuration file is empty.");
            }

            return new ConfigResolution(discoveredPath, config);
        }
        catch (Exception exception) when (exception is JsonException or IOException or InvalidOperationException)
        {
            throw new CliFailure(3, "config_invalid", $"Failed to load config '{discoveredPath}': {exception.Message}");
        }
    }

    private static string? DiscoverConfigPath(string startingDirectory)
    {
        var current = new DirectoryInfo(startingDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "sqlitemd.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        return null;
    }

    private static ResolvedTarget ResolveConfiguredTarget(ExecutionOptions execution, string alias)
    {
        var resolution = LoadRequiredConfig(execution);
        if (!resolution.Config.Targets.TryGetValue(alias, out var targetConfig))
        {
            throw new CliFailure(3, "target_not_found", $"Target '{alias}' was not found in '{resolution.Path}'.");
        }

        return ResolveTarget(alias, targetConfig, resolution.Path);
    }

    private static ResolvedTarget ResolveOneOffTarget(
        ExecutionOptions execution,
        string alias,
        string kind,
        string? path,
        string? title,
        string? schema,
        string? key,
        string writeMode,
        string? glob)
    {
        var targetConfig = new TargetConfig
        {
            Kind = kind,
            Path = path,
            Title = title,
            Schema = schema,
            Key = key,
            WriteMode = writeMode,
            Glob = glob
        };

        return ResolveTarget(alias, targetConfig, Path.Combine(execution.WorkingDirectory, "sqlitemd.json"));
    }

    private static ResolvedTarget ResolveTarget(string alias, TargetConfig targetConfig, string configPath)
    {
        var configDirectory = Path.GetDirectoryName(configPath)!;
        var kind = NormalizeKind(targetConfig.Kind);
        var schema = targetConfig.Schema?.Trim();
        if (string.IsNullOrWhiteSpace(schema))
        {
            throw new CliFailure(3, "schema_missing", $"Target '{alias}' is missing a schema.");
        }

        return kind switch
        {
            "table" => new TableTarget(
                alias,
                ResolvePath(configDirectory, targetConfig.Path, "path", alias),
                targetConfig.Title?.Trim() ?? string.Empty,
                schema,
                RequireValue(targetConfig.Key, "key", alias),
                NormalizeWriteMode(targetConfig.WriteMode, allowDefault: true)),
            "repo" => new RepoTarget(
                alias,
                ResolvePath(configDirectory, targetConfig.Glob, "glob", alias),
                schema),
            _ => throw new CliFailure(3, "target_kind_invalid", $"Target '{alias}' has unsupported kind '{targetConfig.Kind}'.")
        };
    }

    private static string ResolveTargetPreview(TargetConfig targetConfig, string configPath)
    {
        var configDirectory = Path.GetDirectoryName(configPath)!;
        var kind = NormalizeKind(targetConfig.Kind);
        return kind switch
        {
            "table" => ResolvePath(configDirectory, targetConfig.Path, "path", "preview"),
            "repo" => ResolvePath(configDirectory, targetConfig.Glob, "glob", "preview"),
            _ => "<invalid>"
        };
    }

    private static string NormalizeKind(string? kind)
    {
        return (kind ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "repo" => "repo",
            "glob" => "repo",
            "table" => "table",
            "" => throw new CliFailure(3, "target_kind_missing", "Target kind is required."),
            _ => throw new CliFailure(3, "target_kind_invalid", $"Unsupported target kind '{kind}'.")
        };
    }

    private static string NormalizeWriteMode(string? writeMode, bool allowDefault)
    {
        var normalized = (writeMode ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return allowDefault ? "read_write" : throw new CliFailure(2, "write_mode_missing", "Write mode is required.");
        }

        return normalized switch
        {
            "read_write" => "read_write",
            "read_only" => "read_only",
            "append_only" => "append_only",
            _ => throw new CliFailure(2, "write_mode_invalid", $"Unsupported write mode '{writeMode}'.")
        };
    }

    private static string RequireValue(string? value, string propertyName, string alias)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value.Trim();
        }

        throw new CliFailure(3, $"{propertyName}_missing", $"Target '{alias}' is missing '{propertyName}'.");
    }

    private static string ResolvePath(string baseDirectory, string? rawValue, string propertyName, string alias)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            throw new CliFailure(3, $"{propertyName}_missing", $"Target '{alias}' is missing '{propertyName}'.");
        }

        return Path.GetFullPath(rawValue, baseDirectory);
    }

    private static SqliteMdSession OpenSession(ExecutionOptions execution)
    {
        var extensionPath = ResolveExtensionPath(execution);
        if (execution.Verbose && !execution.Json)
        {
            Console.Error.WriteLine($"Loading extension: {extensionPath}");
        }

        try
        {
            return new SqliteMdSession(extensionPath);
        }
        catch (Exception exception)
        {
            throw new CliFailure(4, "extension_load_failed", $"Failed to load SqliteMd extension from '{extensionPath}': {exception.Message}");
        }
    }

    private static string ResolveExtensionPath(ExecutionOptions execution)
    {
        if (execution.ExplicitConfigPath is not null || DiscoverConfigPath(execution.WorkingDirectory) is not null)
        {
            var resolution = LoadRequiredConfig(execution);
            if (!string.IsNullOrWhiteSpace(resolution.Config.ExtensionPath))
            {
                var configuredPath = Path.GetFullPath(resolution.Config.ExtensionPath!, Path.GetDirectoryName(resolution.Path)!);
                if (File.Exists(configuredPath))
                {
                    return configuredPath;
                }

                throw new CliFailure(4, "extension_missing", $"Configured extension path '{configuredPath}' does not exist.");
            }
        }

        var defaultPath = Path.Combine(AppContext.BaseDirectory, "SqliteMd.dll");
        if (File.Exists(defaultPath))
        {
            return defaultPath;
        }

        throw new CliFailure(4, "extension_missing", $"SqliteMd.dll was not found next to the CLI executable at '{defaultPath}'.");
    }

    private static string BuildAttachStatement(ResolvedTarget target)
    {
        return target switch
        {
            TableTarget table when table.WriteMode.Equals("read_write", StringComparison.OrdinalIgnoreCase)
                => $"CREATE VIRTUAL TABLE {QuoteIdentifier(target.Alias)} USING markdown_table({EscapeLiteral(table.Path)}, {EscapeLiteral(table.Title)}, {EscapeLiteral(table.Schema)}, {EscapeLiteral(table.KeyColumn)})",
            TableTarget table
                => $"CREATE VIRTUAL TABLE {QuoteIdentifier(target.Alias)} USING markdown_table_mode({EscapeLiteral(table.Path)}, {EscapeLiteral(table.Title)}, {EscapeLiteral(table.Schema)}, {EscapeLiteral(table.KeyColumn)}, {EscapeLiteral(table.WriteMode)})",
            RepoTarget repo
                => $"CREATE VIRTUAL TABLE {QuoteIdentifier(target.Alias)} USING markdown_repo({EscapeLiteral(repo.Glob)}, {EscapeLiteral(repo.Schema)})",
            _ => throw new CliFailure(2, "attach_target_invalid", "Unsupported attach target.")
        };
    }

    private static string BuildDiagnosticsStatement(string alias, ResolvedTarget target)
    {
        return target switch
        {
            TableTarget table when table.WriteMode.Equals("read_write", StringComparison.OrdinalIgnoreCase)
                => $"CREATE VIRTUAL TABLE {QuoteIdentifier(alias)} USING markdown_table_diagnostics({EscapeLiteral(table.Path)}, {EscapeLiteral(table.Title)}, {EscapeLiteral(table.Schema)}, {EscapeLiteral(table.KeyColumn)})",
            TableTarget table
                => $"CREATE VIRTUAL TABLE {QuoteIdentifier(alias)} USING markdown_table_diagnostics_mode({EscapeLiteral(table.Path)}, {EscapeLiteral(table.Title)}, {EscapeLiteral(table.Schema)}, {EscapeLiteral(table.KeyColumn)}, {EscapeLiteral(table.WriteMode)})",
            RepoTarget repo
                => $"CREATE VIRTUAL TABLE {QuoteIdentifier(alias)} USING markdown_repo_diagnostics({EscapeLiteral(repo.Glob)}, {EscapeLiteral(repo.Schema)})",
            _ => throw new CliFailure(2, "diagnostics_target_invalid", "Unsupported diagnostics target.")
        };
    }

    private static string BuildShowQuery(ResolvedTarget target)
    {
        return target switch
        {
            TableTarget table => $"SELECT * FROM {QuoteIdentifier(table.Alias)} ORDER BY {QuoteIdentifier(table.KeyColumn)}",
            RepoTarget repo => $"SELECT * FROM {QuoteIdentifier(repo.Alias)} ORDER BY _path, _table_index",
            _ => $"SELECT * FROM {QuoteIdentifier(target.Alias)}"
        };
    }

    private static SqlQueryResult ExecuteAdHocSql(SqliteMdSession session, string sql)
    {
        var trimmed = sql.TrimStart();
        if (StartsWithQueryKeyword(trimmed))
        {
            return session.ExecuteQuery(sql);
        }

        var affected = session.ExecuteNonQuery(sql);
        return new SqlQueryResult(
            new[] { "affected" },
            new[] { new Dictionary<string, object?> { ["affected"] = affected } });
    }

    private static bool StartsWithQueryKeyword(string sql)
    {
        return sql.StartsWith("select ", StringComparison.OrdinalIgnoreCase)
            || sql.StartsWith("with ", StringComparison.OrdinalIgnoreCase)
            || sql.StartsWith("pragma ", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task EnsureSingleFileDiagnosticsAcceptedAsync(SqliteMdSession session, ExecutionOptions execution, TextWriter stderr, TableTarget target)
    {
        var attachSql = BuildDiagnosticsStatement("diag", target);
        EchoSql(execution, stderr, attachSql);
        session.ExecuteNonQuery(attachSql);
        var querySql = "SELECT accepted, reason_code, reason_detail FROM [diag]";
        EchoSql(execution, stderr, querySql);
        var result = session.ExecuteQuery(querySql);
        var row = result.Rows.SingleOrDefault();
        if (row is null)
        {
            throw new CliFailure(5, "diagnostics_empty", $"Diagnostics for '{target.Alias}' returned no rows.");
        }

        if (Convert.ToInt64(row["accepted"] ?? 0L) == 0L)
        {
            throw new CliFailure(
                5,
                Convert.ToString(row["reason_code"]) ?? "diagnostics_rejected",
                Convert.ToString(row["reason_detail"]) ?? $"Target '{target.Alias}' was rejected by diagnostics.",
                row);
        }

        await Task.CompletedTask;
    }

    private static Dictionary<string, object?> ParseAssignments(IEnumerable<string>? rawAssignments)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in rawAssignments ?? Array.Empty<string>())
        {
            var separator = entry.IndexOf('=');
            if (separator <= 0)
            {
                throw new CliFailure(2, "assignment_invalid", $"Invalid assignment '{entry}'. Expected column=value.");
            }

            var column = entry[..separator].Trim();
            var rawValue = entry[(separator + 1)..].Trim();
            if (string.IsNullOrWhiteSpace(column))
            {
                throw new CliFailure(2, "assignment_invalid", $"Invalid assignment '{entry}'. Column name is empty.");
            }

            result[column] = ParseScalarValue(rawValue);
        }

        return result;
    }

    private static object? ParseScalarValue(string rawValue)
    {
        if (string.Equals(rawValue, "null", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if ((rawValue.StartsWith('"') && rawValue.EndsWith('"')) || (rawValue.StartsWith('\'') && rawValue.EndsWith('\'')))
        {
            return rawValue[1..^1];
        }

        if (long.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue))
        {
            return longValue;
        }

        if (double.TryParse(rawValue, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var doubleValue))
        {
            return doubleValue;
        }

        if (bool.TryParse(rawValue, out var boolValue))
        {
            return boolValue;
        }

        return rawValue;
    }

    private static List<string> ParseSchemaColumns(string schema)
    {
        var columns = schema
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].Trim())
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .ToList();

        if (columns.Count == 0)
        {
            throw new CliFailure(2, "schema_invalid", "Schema must contain at least one column.");
        }

        return columns;
    }

    private static string QuoteIdentifier(string identifier)
    {
        return $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";
    }

    private static string EscapeLiteral(string value)
    {
        return $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
    }

    private static async Task WriteQueryPayloadAsync(ExecutionOptions execution, TextWriter stdout, object payload, SqlQueryResult result)
    {
        if (execution.Json || execution.Output.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            await WriteJsonAsync(stdout, payload);
            return;
        }

        await stdout.WriteLineAsync(execution.Output.ToLowerInvariant() switch
        {
            "csv" => FormatDelimited(result, ","),
            "tsv" => FormatDelimited(result, "\t"),
            _ => FormatTable(result)
        });
    }

    private static async Task WriteDiagnosticsPayloadAsync(ExecutionOptions execution, TextWriter stdout, object payload, SqlQueryResult result)
    {
        await WriteQueryPayloadAsync(execution, stdout, payload, result);
    }

    private static async Task WriteResultAsync(ExecutionOptions execution, TextWriter stdout, object payload, string text)
    {
        if (execution.Json || execution.Output.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            await WriteJsonAsync(stdout, payload);
            return;
        }

        await stdout.WriteLineAsync(text);
    }

    private static async Task WriteJsonAsync(TextWriter stdout, object payload)
    {
        await stdout.WriteLineAsync(JsonSerializer.Serialize(payload, JsonOptions));
    }

    private static async Task WriteFailureAsync(ExecutionOptions execution, TextWriter stdout, TextWriter stderr, CliFailure failure)
    {
        var payload = new
        {
            ok = false,
            code = failure.Code,
            message = failure.Message,
            details = failure.Details
        };

        if (execution.Json || execution.Output.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            await WriteJsonAsync(stdout, payload);
            return;
        }

        await stderr.WriteLineAsync($"Error [{failure.Code}]: {failure.Message}");
    }

    private static void EchoSql(ExecutionOptions execution, TextWriter stderr, string sql)
    {
        if (execution.EchoSql)
        {
            stderr.WriteLine($"SQL> {sql}");
        }
    }

    private static bool TryGetCommandHelp(string topic, out string helpText)
    {
        helpText = topic.Trim().ToLowerInvariant() switch
        {
            "init" => "sqlitemd init [--force]\n\nCreates a starter sqlitemd.json in the current directory.",
            "targets" => "sqlitemd targets list\n\nLists configured aliases from sqlitemd.json.",
            "show" => "sqlitemd show <target> [--json|--output]\n\nLoads the configured target and prints all rows.",
            "append" => "sqlitemd append <target> --set col=value [--set col=value ...]\n\nAppends a row to a single-file table target.",
            "diagnose" => "sqlitemd diagnose <target>\n\nReturns unified diagnostics for a configured target and exits 5 when any row is rejected.",
            "sql" => "sqlitemd sql <target> --query \"SELECT ...\"\n\nAttaches a configured target and runs raw SQL against it.",
            "query" => "sqlitemd query --kind table|repo ... --query \"SELECT ...\"\n\nRuns raw SQL against a one-off target without a config file.",
            "completion" => "sqlitemd completion pwsh|bash|zsh\n\nPrints a shell completion script.",
            _ => string.Empty
        };

        return helpText.Length > 0;
    }

    private static string FormatTable(SqlQueryResult result)
    {
        if (result.Columns.Count == 0)
        {
            return "(no columns)";
        }

        var widths = result.Columns
            .Select(column => column.Length)
            .ToArray();

        foreach (var row in result.Rows)
        {
            for (var i = 0; i < result.Columns.Count; i++)
            {
                var value = FormatCell(row[result.Columns[i]]);
                widths[i] = Math.Max(widths[i], value.Length);
            }
        }

        var builder = new StringBuilder();
        builder.AppendLine(string.Join(" | ", result.Columns.Select((column, index) => column.PadRight(widths[index]))));
        builder.AppendLine(string.Join("-+-", widths.Select(width => new string('-', width))));

        foreach (var row in result.Rows)
        {
            builder.AppendLine(string.Join(" | ", result.Columns.Select((column, index) => FormatCell(row[column]).PadRight(widths[index]))));
        }

        if (result.Rows.Count == 0)
        {
            builder.Append("(0 rows)");
        }

        return builder.ToString().TrimEnd();
    }

    private static string FormatDelimited(SqlQueryResult result, string separator)
    {
        var builder = new StringBuilder();
        builder.AppendLine(string.Join(separator, result.Columns.Select(EscapeDelimited)));
        foreach (var row in result.Rows)
        {
            builder.AppendLine(string.Join(separator, result.Columns.Select(column => EscapeDelimited(row[column]))));
        }

        return builder.ToString().TrimEnd();
    }

    private static string EscapeDelimited(object? value)
    {
        var text = value?.ToString() ?? string.Empty;
        if (text.Contains('"') || text.Contains(',') || text.Contains('\t') || text.Contains('\n') || text.Contains('\r'))
        {
            return $"\"{text.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
        }

        return text;
    }

    private static string FormatCell(object? value)
    {
        return value?.ToString() ?? "NULL";
    }

    private sealed class ConsoleRedirect : IDisposable
    {
        private readonly TextWriter originalOut;
        private readonly TextWriter originalError;

        public ConsoleRedirect(TextWriter stdout, TextWriter stderr)
        {
            originalOut = Console.Out;
            originalError = Console.Error;
            Console.SetOut(stdout);
            Console.SetError(stderr);
        }

        public void Dispose()
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }
}
