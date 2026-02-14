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
        SqliteConnection.GenerateConnectionParameters(new string[] { "SqliteMd" }, SqliteProvider.System);
}
