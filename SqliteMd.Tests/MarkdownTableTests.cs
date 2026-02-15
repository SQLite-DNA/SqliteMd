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

            Assert.Equal(0, connection.ExecuteNonQuery("INSERT INTO notes(id, title, stars) VALUES (1, 'Alpha', 10)"));
            Assert.Equal(0, connection.ExecuteNonQuery("INSERT INTO notes(id, title, stars) VALUES (2, 'Beta', 20)"));

            Assert.True(File.Exists(path));
            Assert.Equal(2L, connection.ExecuteScalar<long>("SELECT COUNT(*) FROM notes"));

            Assert.Equal(0, connection.ExecuteNonQuery("UPDATE notes SET stars = 99 WHERE id = 2"));
            Assert.Equal(99L, connection.ExecuteScalar<long>("SELECT stars FROM notes WHERE id = 2"));

            Assert.Equal(0, connection.ExecuteNonQuery("DELETE FROM notes WHERE id = 1"));
            Assert.Equal(1L, connection.ExecuteScalar<long>("SELECT COUNT(*) FROM notes"));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
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

