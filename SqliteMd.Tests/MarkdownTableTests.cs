using System.IO;
using SqliteDna.Testing;

namespace SqliteMd.Tests;

public class MarkdownTableTests
{
    [Theory, MemberData(nameof(ConnectionData))]
    public void ReadSingleMarkdownFile(string extensionFile, SqliteProvider provider)
    {
        using var connection = SqliteConnection.Create("Data Source=:memory:", extensionFile, provider);
        Assert.Equal(0, connection.ExecuteNonQuery("CREATE VIRTUAL TABLE Notes USING markdown('Fixtures/single/notes-table.md', '', 0)"));

        using var reader = connection.ExecuteReader("SELECT source_path, source_file_name, source_table_index, source_table_title, source_row, title, stars, id FROM Notes ORDER BY source_row");
        Assert.True(reader.Read());
        Assert.Equal("notes-table.md", reader.GetItem<string>("source_file_name"));
        Assert.Equal(1, reader.GetItem<long>("source_row"));
        Assert.Equal("Release notes", reader.GetItem<string>("title"));
        Assert.Equal(4, reader.GetItem<long>("stars"));

        Assert.True(reader.Read());
        Assert.Equal(2, reader.GetItem<long>("source_row"));
        Assert.Equal("Testing coverage", reader.GetItem<string>("title"));
        Assert.Equal(7, reader.GetItem<long>("stars"));
        Assert.Equal(2, reader.GetItem<long>("id"));
    }

    [Theory, MemberData(nameof(ConnectionData))]
    public void SanitizeAndTypeDuplicateHeaders(string extensionFile, SqliteProvider provider)
    {
        using var connection = SqliteConnection.Create("Data Source=:memory:", extensionFile, provider);
        Assert.Equal(0, connection.ExecuteNonQuery("CREATE VIRTUAL TABLE Headers USING markdown('Fixtures/single/dup-headers.md', '', 0)"));

        using var reader = connection.ExecuteReader("SELECT id, id_2 FROM Headers ORDER BY source_row");
        Assert.True(reader.Read());
        Assert.Equal(10, reader.GetItem<long>("id"));
        Assert.Equal(2, reader.GetItem<long>("id_2"));

        Assert.True(reader.Read());
        Assert.Equal(11, reader.GetItem<long>("id"));
        Assert.Equal(3, reader.GetItem<long>("id_2"));
    }

    [Theory, MemberData(nameof(ConnectionData))]
    public void ReadFolderAndNestedFolder(string extensionFile, SqliteProvider provider)
    {
        using var connection = SqliteConnection.Create("Data Source=:memory:", extensionFile, provider);
        Assert.Equal(0, connection.ExecuteNonQuery("CREATE VIRTUAL TABLE Root USING markdown('Fixtures/tree', '*.md', 0)"));

        using (var reader = connection.ExecuteReader("SELECT DISTINCT source_file_name FROM Root ORDER BY source_file_name"))
        {
            var files = new List<string>();
            while (reader.Read())
            {
                files.Add(reader.GetItem<string>("source_file_name"));
            }

            Assert.Single(files);
            Assert.Contains("root.md", files);
        }

        Assert.Equal(0, connection.ExecuteNonQuery("CREATE VIRTUAL TABLE Nested USING markdown('Fixtures/tree', '*.md', 1)"));
        using var recursive = connection.ExecuteReader("SELECT DISTINCT source_file_name FROM Nested ORDER BY source_file_name");
        var recursiveFiles = new List<string>();
        while (recursive.Read())
        {
            recursiveFiles.Add(recursive.GetItem<string>("source_file_name"));
        }

        Assert.Equal(2, recursiveFiles.Count);
        Assert.Contains("root.md", recursiveFiles);
        Assert.Contains("nested.md", recursiveFiles);
    }

    public static IEnumerable<object[]> ConnectionData =>
        SqliteConnection.GenerateConnectionParameters(new string[] { "SqliteMd" }, SqliteProvider.SQLiteCpp);

    [Theory, MemberData(nameof(ConnectionData))]
    public void WriteMarkdownFile(string extensionFile, SqliteProvider provider)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "SqliteMd", "Writes", Path.GetRandomFileName());
        var targetPath = tempPath + ".md";
        Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);
        try
        {
            using var connection = SqliteConnection.Create("Data Source=:memory:", extensionFile, provider);
            var escapedPath = SqliteLiteral(tempPath);
            var escapedTarget = SqliteLiteral(targetPath);
            var rowsJson = "[[1, \"Release notes\", 4], [2, \"Testing coverage\", 7]]";
            var writeSql = $"SELECT write_markdown('{escapedPath}', 'Notes', 'id,title,stars', '{rowsJson}') AS rows_written";

            Assert.Equal(2, ExecuteScalarLong(connection, writeSql));
            Assert.True(File.Exists(targetPath));

            Assert.Equal(0, connection.ExecuteNonQuery($"CREATE VIRTUAL TABLE notes USING markdown('{escapedTarget}', '', 0)"));

            using var reader = connection.ExecuteReader("SELECT source_row, id, title, stars FROM notes ORDER BY source_row");
            Assert.True(reader.Read());
            Assert.Equal(1L, reader.GetItem<long>("source_row"));
            Assert.Equal(1L, reader.GetItem<long>("id"));
            Assert.Equal("Release notes", reader.GetItem<string>("title"));
            Assert.Equal(4L, reader.GetItem<long>("stars"));

            Assert.True(reader.Read());
            Assert.Equal(2L, reader.GetItem<long>("source_row"));
            Assert.Equal(2L, reader.GetItem<long>("id"));
        }
        finally
        {
            File.Delete(targetPath);
            Directory.Delete(Path.GetDirectoryName(tempPath)!, true);
        }
    }

    [Theory, MemberData(nameof(ConnectionData))]
    public void WriteMarkdownDirectoryTarget(string extensionFile, SqliteProvider provider)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "SqliteMd", "Writes", Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        var tableTitle = "Folder Notes";
        try
        {
            using var connection = SqliteConnection.Create("Data Source=:memory:", extensionFile, provider);
            var escapedDirectory = SqliteLiteral(tempDir);
            var safeName = $"{tableTitle}.md";

            Assert.Equal(1, ExecuteScalarLong(connection, $"SELECT write_markdown('{escapedDirectory}', '{tableTitle}', 'id,title', '[[1, \"Folder\"]]') AS rows_written"));
            Assert.Equal(1, ExecuteScalarLong(connection, $"SELECT write_markdown('{escapedDirectory}', '{tableTitle}', 'id,title', '[[2, \"Append\"]]', 0) AS rows_written"));
            Assert.Equal(1, ExecuteScalarLong(connection, $"SELECT write_markdown('{escapedDirectory}', '{tableTitle}', 'id,title', '[[3, \"Overwrite\"]]', 1) AS rows_written"));

            var files = Directory.GetFiles(tempDir, "*.md");
            Assert.Single(files);
            Assert.Equal(Path.Combine(tempDir, safeName), files.Single());

            var content = File.ReadAllText(files.Single());
            Assert.Contains("# " + tableTitle, content);
            Assert.Equal(1, content.Split("# " + tableTitle, StringSplitOptions.None).Length - 1);

            Assert.Equal(0, connection.ExecuteNonQuery($"CREATE VIRTUAL TABLE folder_notes USING markdown('{SqliteLiteral(files.Single())}', '', 0)"));
            using var reader = connection.ExecuteReader("SELECT source_row, id, title FROM folder_notes ORDER BY source_row");

            Assert.True(reader.Read());
            Assert.Equal(1L, reader.GetItem<long>("source_row"));
            Assert.Equal(3L, reader.GetItem<long>("id"));
            Assert.False(reader.Read());
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    private static string SqliteLiteral(string value)
    {
        return value.Replace("'", "''");
    }

    private static long ExecuteScalarLong(SqliteConnection connection, string sql)
    {
        using var reader = connection.ExecuteReader(sql);
        Assert.True(reader.Read());
        return reader.GetItem<long>("rows_written");
    }
}
