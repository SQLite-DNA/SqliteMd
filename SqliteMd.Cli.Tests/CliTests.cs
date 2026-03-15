using System.Diagnostics;
using System.Text.Json;

namespace SqliteMd.Cli.Tests;

public sealed class CliTests : IDisposable
{
    private readonly string tempRoot;

    public CliTests()
    {
        tempRoot = Path.Combine(Path.GetTempPath(), "SqliteMd", "CliTests", Path.GetRandomFileName());
        Directory.CreateDirectory(tempRoot);
    }

    [Fact]
    public async Task HelpAllShowsPrimaryExamples()
    {
        var result = await InvokeAsync("--help-all");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("sqlitemd show notes", result.Stdout);
        Assert.Contains("sqlitemd append intake", result.Stdout);
        Assert.Contains("sqlitemd diagnose tasks --json", result.Stdout);
    }

    [Theory]
    [InlineData(new[] { "--help" }, "Usage:\n  sqlitemd [command] [options]")]
    [InlineData(new[] { "init", "--help" }, "sqlitemd init")]
    [InlineData(new[] { "targets", "list", "--help" }, "sqlitemd targets list")]
    [InlineData(new[] { "show", "--help" }, "sqlitemd show notes")]
    [InlineData(new[] { "append", "--help" }, "sqlitemd append notes")]
    [InlineData(new[] { "diagnose", "--help" }, "sqlitemd diagnose notes")]
    [InlineData(new[] { "sql", "--help" }, "sqlitemd sql notes")]
    [InlineData(new[] { "query", "--help" }, "sqlitemd query --kind table")]
    [InlineData(new[] { "completion", "--help" }, "sqlitemd completion pwsh")]
    public async Task HelpOutputCoversCommands(string[] args, string expected)
    {
        var result = await InvokeAsync(args);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(expected.Replace("\n", Environment.NewLine), result.Stdout);
    }

    [Fact]
    public async Task InitWritesStarterConfig()
    {
        var result = await InvokeAsync("init");
        var configPath = Path.Combine(tempRoot, "sqlitemd.json");

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(configPath));
        Assert.Contains("\"targets\"", File.ReadAllText(configPath));
    }

    [Fact]
    public async Task TargetsListReadsConfig()
    {
        var workspace = CreateWorkspaceWithConfig();

        var result = await InvokeInDirectoryAsync(workspace, "targets", "list", "--json");
        using var json = JsonDocument.Parse(result.Stdout);

        Assert.Equal(0, result.ExitCode);
        Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
        var targets = json.RootElement.GetProperty("targets");
        Assert.Equal(2, targets.GetArrayLength());
        Assert.Contains(targets.EnumerateArray(), element => element.GetProperty("name").GetString() == "notes");
        Assert.Contains(targets.EnumerateArray(), element => element.GetProperty("name").GetString() == "tasks");
    }

    [Fact]
    public async Task ShowReturnsRowsForConfiguredTableTarget()
    {
        var workspace = CreateWorkspaceWithConfig();

        var result = await InvokeInDirectoryAsync(workspace, "show", "notes", "--json");
        using var json = JsonDocument.Parse(result.Stdout);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(2, json.RootElement.GetProperty("rowCount").GetInt32());
        var rows = json.RootElement.GetProperty("rows");
        Assert.Equal("Release notes", rows[0].GetProperty("title").GetString());
        Assert.Equal(7, rows[1].GetProperty("stars").GetInt64());
    }

    [Fact]
    public async Task AppendCreatesTableForAppendOnlyTarget()
    {
        var workspace = CreateWorkspaceWithConfig(includeIntake: true);

        var result = await InvokeInDirectoryAsync(
            workspace,
            "append",
            "intake",
            "--set",
            "id=1",
            "--set",
            "item=Ship docs refresh",
            "--set",
            "owner=govert",
            "--json");

        var intakePath = Path.Combine(workspace, "append-target.md");
        using var json = JsonDocument.Parse(result.Stdout);

        Assert.Equal(0, result.ExitCode);
        Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("## Weekly Status", File.ReadAllText(intakePath));
        Assert.Contains("| 1 | Ship docs refresh | govert |", File.ReadAllText(intakePath));
    }

    [Fact]
    public async Task AppendRejectsReadOnlyTargets()
    {
        var workspace = CreateWorkspaceWithConfig(readOnly: true);

        var result = await InvokeInDirectoryAsync(
            workspace,
            "append",
            "notes",
            "--set",
            "id=3",
            "--set",
            "title=Blocked",
            "--json");

        using var json = JsonDocument.Parse(result.Stdout);
        Assert.Equal(5, result.ExitCode);
        Assert.False(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("append_rejected_read_only", json.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task DiagnoseReturnsExitCodeFiveForRejectedRepoFiles()
    {
        var workspace = CreateWorkspaceWithConfig();
        File.WriteAllText(Path.Combine(workspace, "docs", "bad.md"), "# No table here\r\n");

        var result = await InvokeInDirectoryAsync(workspace, "diagnose", "tasks", "--json");
        using var json = JsonDocument.Parse(result.Stdout);

        Assert.Equal(5, result.ExitCode);
        var diagnostics = json.RootElement.GetProperty("diagnostics");
        Assert.Contains(diagnostics.EnumerateArray(), element => element.GetProperty("reason_code").GetString() == "no_markdown_table");
    }

    [Fact]
    public async Task OneOffQueryWorksWithoutConfig()
    {
        var filePath = Path.Combine(tempRoot, "query-notes.md");
        File.WriteAllText(filePath,
            "## Notes\r\n\r\n| id | title | stars |\r\n| --- | --- | --- |\r\n| 1 | Alpha | 10 |\r\n| 2 | Beta | 20 |\r\n");

        var result = await InvokeAsync(
            "query",
            "--kind",
            "table",
            "--path",
            filePath,
            "--title",
            "Notes",
            "--schema",
            "id INTEGER, title TEXT, stars INTEGER",
            "--key",
            "id",
            "--query",
            "SELECT id, title FROM source ORDER BY id",
            "--json");

        using var json = JsonDocument.Parse(result.Stdout);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(2, json.RootElement.GetProperty("rowCount").GetInt32());
    }

    [Fact]
    public async Task SqlCommandRunsAgainstConfiguredTarget()
    {
        var workspace = CreateWorkspaceWithConfig();

        var result = await InvokeInDirectoryAsync(
            workspace,
            "sql",
            "notes",
            "--query",
            "SELECT COUNT(*) AS c FROM notes",
            "--json");

        using var json = JsonDocument.Parse(result.Stdout);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(2, json.RootElement.GetProperty("rows")[0].GetProperty("c").GetInt64());
    }

    [Fact]
    public async Task InvalidExtensionPathReturnsCodeFour()
    {
        var workspace = CreateWorkspaceWithConfig(extensionPath: ".\\missing\\SqliteMd.dll");

        var result = await InvokeInDirectoryAsync(workspace, "show", "notes", "--json");
        using var json = JsonDocument.Parse(result.Stdout);

        Assert.Equal(4, result.ExitCode);
        Assert.Equal("extension_missing", json.RootElement.GetProperty("code").GetString());
    }

    public void Dispose()
    {
        if (Directory.Exists(tempRoot))
        {
            Directory.Delete(tempRoot, true);
        }
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> InvokeAsync(params string[] args)
    {
        return await InvokeAsync(args, tempRoot);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> InvokeInDirectoryAsync(string workingDirectory, params string[] args)
    {
        return await InvokeAsync(args, workingDirectory);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> InvokeAsync(string[] args, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = CliExecutablePath,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private string CreateWorkspaceWithConfig(bool includeIntake = false, bool readOnly = false, string? extensionPath = null)
    {
        var workspace = Path.Combine(tempRoot, Path.GetRandomFileName());
        var docs = Path.Combine(workspace, "docs");
        Directory.CreateDirectory(docs);

        File.WriteAllText(Path.Combine(workspace, "release-notes.md"),
            "## Release Notes\r\n\r\n| id | title | stars |\r\n| --- | --- | --- |\r\n| 1 | Release notes | 4 |\r\n| 2 | Testing coverage | 7 |\r\n");
        File.WriteAllText(Path.Combine(docs, "tasks.md"),
            "## Tasks\r\n\r\n| id | title | owner | status |\r\n| --- | --- | --- | --- |\r\n| 1 | Ship docs | govert | open |\r\n");
        File.WriteAllText(Path.Combine(workspace, "append-target.md"), "# Intro\r\n\r\nNo table yet.\r\n");

        var config = $$"""
{
  "extensionPath": "{{(extensionPath ?? ExtensionPathForConfig).Replace("\\", "\\\\", StringComparison.Ordinal)}}",
  "defaults": {
    "output": "table"
  },
  "targets": {
    "notes": {
      "kind": "table",
      "path": "release-notes.md",
      "title": "Release Notes",
      "schema": "id INTEGER, title TEXT, stars INTEGER",
      "key": "id",
      "writeMode": "{{(readOnly ? "read_only" : "read_write")}}"
    },
    {{(includeIntake ? """
    "intake": {
      "kind": "table",
      "path": "append-target.md",
      "title": "Weekly Status",
      "schema": "id INTEGER, item TEXT, owner TEXT",
      "key": "id",
      "writeMode": "append_only"
    },
""" : string.Empty)}}
    "tasks": {
      "kind": "repo",
      "glob": "docs\\**\\*.md",
      "schema": "id INTEGER, title TEXT, owner TEXT, status TEXT"
    }
  }
}
""";

        File.WriteAllText(Path.Combine(workspace, "sqlitemd.json"), config);
        return workspace;
    }

    private static string ExtensionPathForConfig =>
        Path.Combine(AppContext.BaseDirectory, "SqliteMd.dll");

    private static string CliExecutablePath =>
        Path.Combine(RepositoryRoot, "SqliteMd.Cli", "bin", BuildConfiguration, "net7.0", "win-x64", "SqliteMd.Cli.exe");

    private static string RepositoryRoot =>
        new DirectoryInfo(AppContext.BaseDirectory).Parent!.Parent!.Parent!.Parent!.FullName;

    private static string BuildConfiguration =>
        new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
}
