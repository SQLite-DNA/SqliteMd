using Microsoft.Data.Sqlite;

namespace SqliteMd.Cli;

internal sealed class SqliteMdSession : IDisposable
{
    private readonly SqliteConnection connection;
    private bool disposed;

    public SqliteMdSession(string extensionPath)
    {
        connection = new SqliteConnection("Data Source=:memory:");

        try
        {
            connection.Open();
            connection.LoadExtension(extensionPath);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        connection.Dispose();
        disposed = true;
    }

    public int ExecuteNonQuery(string sql, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        using var command = CreateCommand(sql, parameters);
        return command.ExecuteNonQuery();
    }

    public object? ExecuteScalar(string sql, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        using var command = CreateCommand(sql, parameters);
        return command.ExecuteScalar();
    }

    public SqlQueryResult ExecuteQuery(string sql, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        using var command = CreateCommand(sql, parameters);
        using var reader = command.ExecuteReader();

        var columns = Enumerable.Range(0, reader.FieldCount)
            .Select(reader.GetName)
            .ToArray();
        var rows = new List<IReadOnlyDictionary<string, object?>>();

        while (reader.Read())
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var column in columns)
            {
                var value = reader[column];
                row[column] = value is DBNull ? null : value;
            }

            rows.Add(row);
        }

        return new SqlQueryResult(columns, rows);
    }

    private SqliteCommand CreateCommand(string sql, IReadOnlyDictionary<string, object?>? parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;

        if (parameters is not null)
        {
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            }
        }

        return command;
    }
}
