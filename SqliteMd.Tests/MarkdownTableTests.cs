using System.IO;
using SqliteDna.Testing;

namespace SqliteMd.Tests;

public class MarkdownTableTests
{
    [Theory, MemberData(nameof(ConnectionData))]
    public void ReadSingleMarkdownFile(string extensionFile, SqliteProvider provider)
    {
        using var connection = SqliteConnection.Create("Data Source=:memory:", extensionFile, provider);
        Assert.Equal(0, connection.ExecuteNonQuery(
            "CREATE VIRTUAL TABLE Notes USING markdown_table('Fixtures/single/notes-table.md', 'Notes', 'id INTEGER, title TEXT, stars INTEGER', 'id')"));

        using var reader = connection.ExecuteReader("SELECT id, title, stars FROM Notes ORDER BY id");
        Assert.True(reader.Read());
        Assert.Equal(1L, reader.GetItem<long>("id"));
        Assert.Equal("Release notes", reader.GetItem<string>("title"));
        Assert.Equal(4L, reader.GetItem<long>("stars"));

        Assert.True(reader.Read());
        Assert.Equal(2L, reader.GetItem<long>("id"));
        Assert.Equal("Testing coverage", reader.GetItem<string>("title"));
        Assert.Equal(7L, reader.GetItem<long>("stars"));
        Assert.False(reader.Read());
    }

    [Theory, MemberData(nameof(ConnectionData))]
    public void ReadDuplicateHeadersWithExplicitSchema(string extensionFile, SqliteProvider provider)
    {
        using var connection = SqliteConnection.Create("Data Source=:memory:", extensionFile, provider);
        Assert.Equal(0, connection.ExecuteNonQuery(
            "CREATE VIRTUAL TABLE Headers USING markdown_table('Fixtures/single/dup-headers.md', '', 'id INTEGER, id_2 INTEGER, value TEXT', 'id')"));

        using var reader = connection.ExecuteReader("SELECT id, id_2, value FROM Headers ORDER BY id");
        Assert.True(reader.Read());
        Assert.Equal(10L, reader.GetItem<long>("id"));
        Assert.Equal(2L, reader.GetItem<long>("id_2"));
        Assert.Equal("left", reader.GetItem<string>("value"));

        Assert.True(reader.Read());
        Assert.Equal(11L, reader.GetItem<long>("id"));
        Assert.Equal(3L, reader.GetItem<long>("id_2"));
        Assert.Equal("right", reader.GetItem<string>("value"));
        Assert.False(reader.Read());
    }

    [Theory, MemberData(nameof(ConnectionData))]
    public void ReadFirstMarkdownTableAfterPreamble(string extensionFile, SqliteProvider provider)
    {
        using var connection = SqliteConnection.Create("Data Source=:memory:", extensionFile, provider);
        Assert.Equal(0, connection.ExecuteNonQuery(
            "CREATE VIRTUAL TABLE Notes USING markdown_table('Fixtures/single/preamble-notes.md', 'Ignored Title', 'id INTEGER, title TEXT, stars INTEGER', 'id')"));

        using var reader = connection.ExecuteReader("SELECT id, title, stars FROM Notes ORDER BY id");
        Assert.True(reader.Read());
        Assert.Equal(1L, reader.GetItem<long>("id"));
        Assert.Equal("Release notes", reader.GetItem<string>("title"));
        Assert.Equal(4L, reader.GetItem<long>("stars"));

        Assert.True(reader.Read());
        Assert.Equal(2L, reader.GetItem<long>("id"));
        Assert.Equal("Testing coverage", reader.GetItem<string>("title"));
        Assert.Equal(7L, reader.GetItem<long>("stars"));
        Assert.False(reader.Read());
    }

    [Theory, MemberData(nameof(ConnectionData))]
    public void InsertUpdateDeleteThroughVirtualTable(string extensionFile, SqliteProvider provider)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "SqliteMd", "Writable", Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        var pathNoExt = Path.Combine(tempDir, "notes");
        var path = pathNoExt + ".md";

        try
        {
            using var connection = SqliteConnection.Create("Data Source=:memory:", extensionFile, provider);
            var escaped = SqliteLiteral(pathNoExt);

            Assert.Equal(0, connection.ExecuteNonQuery(
                $"CREATE VIRTUAL TABLE notes USING markdown_table('{escaped}', 'Notes', 'id INTEGER, title TEXT, stars INTEGER', 'id')"));

            Assert.Equal(1, connection.ExecuteNonQuery("INSERT INTO notes(id, title, stars) VALUES (1, 'Alpha', 10)"));
            Assert.Equal(1, connection.ExecuteNonQuery("INSERT INTO notes(id, title, stars) VALUES (2, 'Beta', 20)"));

            Assert.True(File.Exists(path));
            Assert.Equal(2L, connection.ExecuteScalar<long>("SELECT COUNT(*) FROM notes"));

            Assert.Equal(1, connection.ExecuteNonQuery("UPDATE notes SET stars = 99 WHERE id = 2"));
            Assert.Equal(99L, connection.ExecuteScalar<long>("SELECT stars FROM notes WHERE id = 2"));

            Assert.Equal(1, connection.ExecuteNonQuery("DELETE FROM notes WHERE id = 1"));
            Assert.Equal(1L, connection.ExecuteScalar<long>("SELECT COUNT(*) FROM notes"));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
            Directory.Delete(tempDir, true);
        }
    }

    [Theory, MemberData(nameof(ConnectionData))]
    public void ReadOnlyModeAllowsReadsButRejectsWrites(string extensionFile, SqliteProvider provider)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "SqliteMd", "ReadOnly", Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        var path = Path.Combine(tempDir, "notes.md");

        try
        {
            File.WriteAllText(path,
                "## Notes\r\n\r\n| id | title | stars |\r\n| --- | --- | --- |\r\n| 1 | Alpha | 10 |\r\n");

            using var connection = SqliteConnection.Create("Data Source=:memory:", extensionFile, provider);
            var escaped = SqliteLiteral(path);

            Assert.Equal(0, connection.ExecuteNonQuery(
                $"CREATE VIRTUAL TABLE notes USING markdown_table_mode('{escaped}', 'Notes', 'id INTEGER, title TEXT, stars INTEGER', 'id', 'read_only')"));

            Assert.Equal(1L, connection.ExecuteScalar<long>("SELECT COUNT(*) FROM notes"));

            Assert.ThrowsAny<Exception>(() => connection.ExecuteNonQuery("INSERT INTO notes(id, title, stars) VALUES (2, 'Beta', 20)"));

            Assert.ThrowsAny<Exception>(() => connection.ExecuteNonQuery("UPDATE notes SET stars = 99 WHERE id = 1"));

            Assert.ThrowsAny<Exception>(() => connection.ExecuteNonQuery("DELETE FROM notes WHERE id = 1"));

            Assert.Equal(1L, connection.ExecuteScalar<long>("SELECT COUNT(*) FROM notes"));
            Assert.Contains("| 1 | Alpha | 10 |", File.ReadAllText(path));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Theory, MemberData(nameof(ConnectionData))]
    public void AppendOnlyModeCreatesAndRejectsUpdateAndDelete(string extensionFile, SqliteProvider provider)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "SqliteMd", "AppendOnly", Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        var path = Path.Combine(tempDir, "notes.md");

        try
        {
            File.WriteAllText(path, "# Intro\r\n\r\nNo table yet.\r\n");

            using var connection = SqliteConnection.Create("Data Source=:memory:", extensionFile, provider);
            var escaped = SqliteLiteral(path);

            Assert.Equal(0, connection.ExecuteNonQuery(
                $"CREATE VIRTUAL TABLE notes USING markdown_table_mode('{escaped}', 'Notes', 'id INTEGER, title TEXT, stars INTEGER', 'id', 'append_only')"));

            Assert.Equal(1, connection.ExecuteNonQuery("INSERT INTO notes(id, title, stars) VALUES (1, 'Alpha', 10)"));
            Assert.Equal(1L, connection.ExecuteScalar<long>("SELECT COUNT(*) FROM notes"));

            Assert.ThrowsAny<Exception>(() => connection.ExecuteNonQuery("UPDATE notes SET stars = 99 WHERE id = 1"));

            Assert.ThrowsAny<Exception>(() => connection.ExecuteNonQuery("DELETE FROM notes WHERE id = 1"));

            var content = File.ReadAllText(path);
            Assert.Contains("## Notes", content);
            Assert.Contains("| 1 | Alpha | 10 |", content);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Theory, MemberData(nameof(ConnectionData))]
    public void CreateNewTableAfterPreambleUsesDoubleHashHeading(string extensionFile, SqliteProvider provider)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "SqliteMd", "Create", Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        var path = Path.Combine(tempDir, "notes.md");

        try
        {
            File.WriteAllText(path, "# Intro\r\n\r\nSome preamble before any table.\r\n");

            using var connection = SqliteConnection.Create("Data Source=:memory:", extensionFile, provider);
            var escaped = SqliteLiteral(path);

            Assert.Equal(0, connection.ExecuteNonQuery(
                $"CREATE VIRTUAL TABLE notes USING markdown_table('{escaped}', 'Notes', 'id INTEGER, title TEXT, stars INTEGER', 'id')"));

            Assert.Equal(1, connection.ExecuteNonQuery("INSERT INTO notes(id, title, stars) VALUES (1, 'Alpha', 10)"));

            var content = File.ReadAllText(path);
            Assert.Contains("Some preamble before any table.", content);
            Assert.Contains("## Notes", content);
            Assert.Contains("| id | title | stars |", content);
            Assert.Contains("| 1 | Alpha | 10 |", content);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
            Directory.Delete(tempDir, true);
        }
    }

    [Theory, MemberData(nameof(ConnectionData))]
    public void ReadRepositoryTableAcrossMarkdownTree(string extensionFile, SqliteProvider provider)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "SqliteMd", "Repo", Path.GetRandomFileName());
        var subDir = Path.Combine(tempDir, "nested");
        Directory.CreateDirectory(subDir);

        var firstFile = Path.Combine(tempDir, "a.md");
        var secondFile = Path.Combine(subDir, "b.md");
        var skippedFile = Path.Combine(tempDir, "readme.md");

        try
        {
            File.WriteAllText(firstFile,
                "# Intro\r\n\r\nPreamble.\r\n\r\n## Notes\r\n\r\n| id | title | stars |\r\n| --- | --- | --- |\r\n| 1 | Alpha | 10 |\r\n| 2 | Beta | 20 |\r\n");
            File.WriteAllText(secondFile,
                "Some text first.\r\n\r\n## More Notes\r\n\r\n| id | title | stars |\r\n| --- | --- | --- |\r\n| 3 | Gamma | 30 |\r\n");
            File.WriteAllText(skippedFile, "# No table here\r\n\r\nJust prose.\r\n");

            using var connection = SqliteConnection.Create("Data Source=:memory:", extensionFile, provider);
            var escaped = SqliteLiteral(Path.Combine(tempDir, "**", "*.md"));

            Assert.Equal(0, connection.ExecuteNonQuery(
                $"CREATE VIRTUAL TABLE notes USING markdown_glob('{escaped}', 'id INTEGER, title TEXT, stars INTEGER')"));

            using var reader = connection.ExecuteReader(
                "SELECT id, title, stars, _path, _heading, _mtime, _table_index FROM notes ORDER BY _path, id");

            Assert.True(reader.Read());
            Assert.Equal(1L, reader.GetItem<long>("id"));
            Assert.Equal("Alpha", reader.GetItem<string>("title"));
            Assert.Equal(10L, reader.GetItem<long>("stars"));
            Assert.Equal(firstFile, reader.GetItem<string>("_path"));
            Assert.Equal("Notes", reader.GetItem<string>("_heading"));
            Assert.True(reader.GetItem<long>("_mtime") > 0);
            Assert.Equal(0L, reader.GetItem<long>("_table_index"));

            Assert.True(reader.Read());
            Assert.Equal(2L, reader.GetItem<long>("id"));
            Assert.Equal("Beta", reader.GetItem<string>("title"));
            Assert.Equal(20L, reader.GetItem<long>("stars"));
            Assert.Equal(firstFile, reader.GetItem<string>("_path"));

            Assert.True(reader.Read());
            Assert.Equal(3L, reader.GetItem<long>("id"));
            Assert.Equal("Gamma", reader.GetItem<string>("title"));
            Assert.Equal(30L, reader.GetItem<long>("stars"));
            Assert.Equal(secondFile, reader.GetItem<string>("_path"));
            Assert.Equal("More Notes", reader.GetItem<string>("_heading"));
            Assert.True(reader.GetItem<long>("_mtime") > 0);
            Assert.Equal(0L, reader.GetItem<long>("_table_index"));

            Assert.False(reader.Read());
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Theory, MemberData(nameof(ConnectionData))]
    public void DiagnosticsExplainSingleFileAcceptance(string extensionFile, SqliteProvider provider)
    {
        using var connection = SqliteConnection.Create("Data Source=:memory:", extensionFile, provider);
        Assert.Equal(0, connection.ExecuteNonQuery(
            "CREATE VIRTUAL TABLE Diagnostics USING markdown_diagnostics('table', 'Fixtures/single/preamble-notes.md', 'id INTEGER, title TEXT, stars INTEGER', 'id', 'Ignored Title')"));

        using var reader = connection.ExecuteReader(
            "SELECT accepted, reason_code, matched_table_count, selected_table_index, heading, preamble_line_count, table_start_line, table_end_line, create_on_write, can_insert, can_update, can_delete FROM Diagnostics");

        Assert.True(reader.Read());
        Assert.Equal(1L, reader.GetItem<long>("accepted"));
        Assert.Equal("ok", reader.GetItem<string>("reason_code"));
        Assert.Equal(1L, reader.GetItem<long>("matched_table_count"));
        Assert.Equal(0L, reader.GetItem<long>("selected_table_index"));
        Assert.Equal("Notes", reader.GetItem<string>("heading"));
        Assert.Equal(9L, reader.GetItem<long>("preamble_line_count"));
        Assert.Equal(10L, reader.GetItem<long>("table_start_line"));
        Assert.Equal(13L, reader.GetItem<long>("table_end_line"));
        Assert.Equal(0L, reader.GetItem<long>("create_on_write"));
        Assert.Equal(1L, reader.GetItem<long>("can_insert"));
        Assert.Equal(1L, reader.GetItem<long>("can_update"));
        Assert.Equal(1L, reader.GetItem<long>("can_delete"));
        Assert.False(reader.Read());
    }

    [Theory, MemberData(nameof(ConnectionData))]
    public void DiagnosticsExplainCreateOnWriteForMissingTable(string extensionFile, SqliteProvider provider)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "SqliteMd", "Diagnostics", Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        var path = Path.Combine(tempDir, "notes.md");

        try
        {
            File.WriteAllText(path, "# Intro\r\n\r\nNo table yet.\r\n");

            using var connection = SqliteConnection.Create("Data Source=:memory:", extensionFile, provider);
            var escaped = SqliteLiteral(path);

            Assert.Equal(0, connection.ExecuteNonQuery(
                $"CREATE VIRTUAL TABLE Diagnostics USING markdown_table_diagnostics('{escaped}', 'Notes', 'id INTEGER, title TEXT, stars INTEGER', 'id')"));

            using var reader = connection.ExecuteReader(
                "SELECT accepted, reason_code, create_on_write, can_read, can_insert, can_update, can_delete FROM Diagnostics");

            Assert.True(reader.Read());
            Assert.Equal(1L, reader.GetItem<long>("accepted"));
            Assert.Equal("no_markdown_table_create_on_write", reader.GetItem<string>("reason_code"));
            Assert.Equal(1L, reader.GetItem<long>("create_on_write"));
            Assert.Equal(1L, reader.GetItem<long>("can_read"));
            Assert.Equal(1L, reader.GetItem<long>("can_insert"));
            Assert.Equal(1L, reader.GetItem<long>("can_update"));
            Assert.Equal(1L, reader.GetItem<long>("can_delete"));
            Assert.False(reader.Read());
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Theory, MemberData(nameof(ConnectionData))]
    public void DiagnosticsReflectSingleFileWriteModes(string extensionFile, SqliteProvider provider)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "SqliteMd", "DiagnosticsModes", Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        var path = Path.Combine(tempDir, "notes.md");

        try
        {
            File.WriteAllText(path, "# Intro\r\n\r\nNo table yet.\r\n");

            var escaped = SqliteLiteral(path);

            using (var connection = SqliteConnection.Create("Data Source=:memory:", extensionFile, provider))
            {
                Assert.Equal(0, connection.ExecuteNonQuery(
                    $"CREATE VIRTUAL TABLE ReadOnlyDiagnostics USING markdown_table_diagnostics_mode('{escaped}', 'Notes', 'id INTEGER, title TEXT, stars INTEGER', 'id', 'read_only')"));

                using var reader = connection.ExecuteReader(
                    "SELECT write_mode, accepted, reason_code, can_read, can_insert, can_update, can_delete, create_on_write FROM ReadOnlyDiagnostics");
                Assert.True(reader.Read());
                Assert.Equal("read_only", reader.GetItem<string>("write_mode"));
                Assert.Equal(1L, reader.GetItem<long>("accepted"));
                Assert.Equal("no_markdown_table", reader.GetItem<string>("reason_code"));
                Assert.Equal(1L, reader.GetItem<long>("can_read"));
                Assert.Equal(0L, reader.GetItem<long>("can_insert"));
                Assert.Equal(0L, reader.GetItem<long>("can_update"));
                Assert.Equal(0L, reader.GetItem<long>("can_delete"));
                Assert.Equal(0L, reader.GetItem<long>("create_on_write"));
                Assert.False(reader.Read());
            }

            using (var connection = SqliteConnection.Create("Data Source=:memory:", extensionFile, provider))
            {
                Assert.Equal(0, connection.ExecuteNonQuery(
                    $"CREATE VIRTUAL TABLE AppendDiagnostics USING markdown_table_diagnostics_mode('{escaped}', 'Notes', 'id INTEGER, title TEXT, stars INTEGER', 'id', 'append_only')"));

                using var reader = connection.ExecuteReader(
                    "SELECT write_mode, accepted, reason_code, can_read, can_insert, can_update, can_delete, create_on_write FROM AppendDiagnostics");
                Assert.True(reader.Read());
                Assert.Equal("append_only", reader.GetItem<string>("write_mode"));
                Assert.Equal(1L, reader.GetItem<long>("accepted"));
                Assert.Equal("no_markdown_table_create_on_write", reader.GetItem<string>("reason_code"));
                Assert.Equal(1L, reader.GetItem<long>("can_read"));
                Assert.Equal(1L, reader.GetItem<long>("can_insert"));
                Assert.Equal(0L, reader.GetItem<long>("can_update"));
                Assert.Equal(0L, reader.GetItem<long>("can_delete"));
                Assert.Equal(1L, reader.GetItem<long>("create_on_write"));
                Assert.False(reader.Read());
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Theory, MemberData(nameof(ConnectionData))]
    public void DiagnosticsExplainWhyGlobFilesAreRejected(string extensionFile, SqliteProvider provider)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "SqliteMd", "DiagnosticsGlob", Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        var acceptedFile = Path.Combine(tempDir, "accepted.md");
        var missingTableFile = Path.Combine(tempDir, "no-table.md");
        var mismatchFile = Path.Combine(tempDir, "wrong-columns.md");

        try
        {
            File.WriteAllText(acceptedFile,
                "## Notes\r\n\r\n| id | title | stars |\r\n| --- | --- | --- |\r\n| 1 | Alpha | 10 |\r\n");
            File.WriteAllText(missingTableFile,
                "# Just prose\r\n\r\nNo markdown table here.\r\n");
            File.WriteAllText(mismatchFile,
                "## Wrong\r\n\r\n| id | title |\r\n| --- | --- |\r\n| 2 | Beta |\r\n");

            using var connection = SqliteConnection.Create("Data Source=:memory:", extensionFile, provider);
            var escaped = SqliteLiteral(Path.Combine(tempDir, "**", "*.md"));

            Assert.Equal(0, connection.ExecuteNonQuery(
                $"CREATE VIRTUAL TABLE Diagnostics USING markdown_diagnostics('glob', '{escaped}', 'id INTEGER, title TEXT, stars INTEGER', '', '')"));

            using var reader = connection.ExecuteReader(
                "SELECT path, accepted, reason_code, matched_table_count, detected_column_count FROM Diagnostics ORDER BY path");

            Assert.True(reader.Read());
            Assert.Equal(acceptedFile, reader.GetItem<string>("path"));
            Assert.Equal(1L, reader.GetItem<long>("accepted"));
            Assert.Equal("ok", reader.GetItem<string>("reason_code"));
            Assert.Equal(1L, reader.GetItem<long>("matched_table_count"));
            Assert.Equal(3L, reader.GetItem<long>("detected_column_count"));

            Assert.True(reader.Read());
            Assert.Equal(missingTableFile, reader.GetItem<string>("path"));
            Assert.Equal(0L, reader.GetItem<long>("accepted"));
            Assert.Equal("no_markdown_table", reader.GetItem<string>("reason_code"));
            Assert.Equal(0L, reader.GetItem<long>("matched_table_count"));

            Assert.True(reader.Read());
            Assert.Equal(mismatchFile, reader.GetItem<string>("path"));
            Assert.Equal(0L, reader.GetItem<long>("accepted"));
            Assert.Equal("column_count_mismatch", reader.GetItem<string>("reason_code"));
            Assert.Equal(1L, reader.GetItem<long>("matched_table_count"));
            Assert.Equal(2L, reader.GetItem<long>("detected_column_count"));

            Assert.False(reader.Read());
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    public static IEnumerable<object[]> ConnectionData =>
        SqliteConnection.GenerateConnectionParameters(new string[] { "SqliteMd" }, SqliteProvider.SQLiteCpp);

    private static string SqliteLiteral(string value)
    {
        return value.Replace("'", "''");
    }
}
