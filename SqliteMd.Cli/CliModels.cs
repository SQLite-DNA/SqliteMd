using System.Text.Json.Serialization;

namespace SqliteMd.Cli;

internal sealed record ExecutionOptions(
    string WorkingDirectory,
    string? ExplicitConfigPath,
    bool Json,
    string Output,
    bool EchoSql,
    bool Verbose,
    bool NoColor);

internal sealed class CliConfig
{
    [JsonPropertyName("extensionPath")]
    public string? ExtensionPath { get; set; }

    [JsonPropertyName("defaults")]
    public CliDefaults Defaults { get; set; } = new();

    [JsonPropertyName("targets")]
    public Dictionary<string, TargetConfig> Targets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class CliDefaults
{
    [JsonPropertyName("output")]
    public string? Output { get; set; }
}

internal sealed class TargetConfig
{
    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("schema")]
    public string? Schema { get; set; }

    [JsonPropertyName("key")]
    public string? Key { get; set; }

    [JsonPropertyName("writeMode")]
    public string? WriteMode { get; set; }

    [JsonPropertyName("glob")]
    public string? Glob { get; set; }
}

internal sealed record ConfigResolution(string Path, CliConfig Config);

internal abstract record ResolvedTarget(string Alias, string Kind, string Schema);

internal sealed record TableTarget(
    string Alias,
    string Path,
    string Title,
    string Schema,
    string KeyColumn,
    string WriteMode) : ResolvedTarget(Alias, "table", Schema);

internal sealed record RepoTarget(
    string Alias,
    string Glob,
    string Schema) : ResolvedTarget(Alias, "repo", Schema);

internal sealed record SqlQueryResult(IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows);

internal sealed class CliFailure : Exception
{
    public CliFailure(int exitCode, string code, string message, object? details = null)
        : base(message)
    {
        ExitCode = exitCode;
        Code = code;
        Details = details;
    }

    public int ExitCode { get; }

    public string Code { get; }

    public object? Details { get; }
}
