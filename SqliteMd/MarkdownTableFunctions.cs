using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using SqliteDna.Integration;

namespace SqliteMd;

public static class MarkdownTableFunctions
{
    // Writable, single-table view over a markdown pipe table.
    //
    // CREATE VIRTUAL TABLE t USING markdown_table('path.md', 'Title', 'id INTEGER, title TEXT', 'id');
    // INSERT/UPDATE/DELETE then go through xUpdate and mutate the backing .md file.
    [SqliteVirtualTable]
    public static ISqliteVirtualTable markdown_table(string path, string tableTitle, string schema, string keyColumn)
    {
        return new MarkdownFileTable(path, tableTitle, schema, keyColumn);
    }

    private sealed class MarkdownFileTable : ISqliteWritableVirtualTable, ISqliteVirtualTableTransaction, IDisposable
    {
        private readonly string filePath;
        private readonly string tableTitle;
        private readonly IReadOnlyList<SchemaColumn> columns;
        private readonly int keyIndex;

        private FileStream? lockedStream;
        private string[]? baseLines;
        private TableLocation? baseLocation;
        private List<Row>? workingRows;
        private bool dirty;

        public MarkdownFileTable(string inputPath, string tableTitle, string schema, string keyColumn)
        {
            filePath = ResolveTargetPath(inputPath, tableTitle);
            this.tableTitle = tableTitle ?? string.Empty;
            columns = ParseSchema(schema);
            if (columns.Count == 0)
                throw new ArgumentException("schema must contain at least one column.", nameof(schema));

            keyIndex = columns.FindIndex(c => string.Equals(c.Name, keyColumn, StringComparison.OrdinalIgnoreCase));
            if (keyIndex < 0)
                throw new ArgumentException($"keyColumn '{keyColumn}' must match a column in schema.", nameof(keyColumn));

            if (columns[keyIndex].Affinity != TypeAffinity.Integer)
                throw new ArgumentException("keyColumn must be declared as INTEGER in schema.", nameof(keyColumn));
        }

        public string Schema => string.Join(", ", columns.Select(c => $"\"{c.Name}\" {c.SqlType}"));

        public ISqliteVirtualTableCursor OpenCursor()
        {
            // If there's an active transaction, expose the staged view.
            var snapshot = workingRows != null ? workingRows.ToList() : LoadRowsFromDisk();
            return new MarkdownFileCursor(columns, snapshot);
        }

        public void Begin()
        {
            if (lockedStream != null)
                return;

            EnsureOutputDirectory(filePath);
            lockedStream = new FileStream(filePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            (baseLines, baseLocation, workingRows) = LoadTableFromStream(lockedStream, tableTitle, columns, keyIndex);
            dirty = false;
        }

        public void Commit()
        {
            if (lockedStream == null)
                return;

            try
            {
                if (dirty)
                {
                    var lines = baseLines ?? Array.Empty<string>();
                    var location = baseLocation;
                    var rows = workingRows ?? new List<Row>();

                    var newLines = RewriteDocument(lines, location, tableTitle, columns, rows);
                    lockedStream.Position = 0;
                    lockedStream.SetLength(0);
                    using var writer = new StreamWriter(lockedStream, new UTF8Encoding(false), 4096, leaveOpen: true);
                    for (int i = 0; i < newLines.Count; i++)
                    {
                        writer.WriteLine(newLines[i]);
                    }
                    writer.Flush();
                }
            }
            finally
            {
                lockedStream.Dispose();
                lockedStream = null;
                baseLines = null;
                baseLocation = null;
                workingRows = null;
                dirty = false;
            }
        }

        public void Rollback()
        {
            if (lockedStream == null)
                return;

            lockedStream.Dispose();
            lockedStream = null;
            baseLines = null;
            baseLocation = null;
            workingRows = null;
            dirty = false;
        }

        public void Dispose()
        {
            Rollback();
        }

        public SqliteVirtualTableUpdateResult Update(SqliteVirtualTableUpdate update)
        {
            // Ensure an implicit transaction around a single xUpdate call.
            if (lockedStream == null)
            {
                Begin();
                try
                {
                    var result = ApplyUpdate(update);
                    Commit();
                    return result;
                }
                catch
                {
                    Rollback();
                    throw;
                }
            }

            return ApplyUpdate(update);
        }

        private SqliteVirtualTableUpdateResult ApplyUpdate(SqliteVirtualTableUpdate update)
        {
            workingRows ??= new List<Row>();

            switch (update.Kind)
            {
                case SqliteVirtualTableUpdateKind.Delete:
                {
                    if (update.OldRowId == null)
                        throw new InvalidOperationException("DELETE requires old rowid.");

                    var idx = workingRows.FindIndex(r => r.RowId == update.OldRowId.Value);
                    if (idx >= 0)
                    {
                        workingRows.RemoveAt(idx);
                        dirty = true;
                    }

                    return new SqliteVirtualTableUpdateResult(update.OldRowId.Value);
                }

                case SqliteVirtualTableUpdateKind.Insert:
                {
                    var cells = BuildCells(update.ColumnValues);
                    var rowId = GetRowIdFromCells(cells, update.NewRowId);

                    if (workingRows.Any(r => r.RowId == rowId))
                        throw new InvalidOperationException("Rowid already exists.");

                    workingRows.Add(new Row(rowId, cells));
                    dirty = true;
                    return new SqliteVirtualTableUpdateResult(rowId);
                }

                case SqliteVirtualTableUpdateKind.Update:
                {
                    if (update.OldRowId == null)
                        throw new InvalidOperationException("UPDATE requires old rowid.");

                    var idx = workingRows.FindIndex(r => r.RowId == update.OldRowId.Value);
                    if (idx < 0)
                        throw new InvalidOperationException("Row not found.");

                    var cells = BuildCells(update.ColumnValues);
                    var newRowId = GetRowIdFromCells(cells, update.NewRowId ?? update.OldRowId);

                    if (newRowId != update.OldRowId.Value && workingRows.Any(r => r.RowId == newRowId))
                        throw new InvalidOperationException("Rowid already exists.");

                    workingRows[idx] = new Row(newRowId, cells);
                    dirty = true;
                    return new SqliteVirtualTableUpdateResult(newRowId);
                }

                default:
                    throw new InvalidOperationException("Unknown update kind.");
            }
        }

        private string?[] BuildCells(IReadOnlyList<SqliteValue> values)
        {
            if (values.Count != columns.Count)
                throw new InvalidOperationException($"Expected {columns.Count} column values, got {values.Count}.");

            var result = new string?[columns.Count];
            for (int i = 0; i < columns.Count; i++)
            {
                result[i] = FormatCell(values[i], columns[i].Affinity);
            }
            return result;
        }

        private long GetRowIdFromCells(string?[] cells, long? explicitRowId)
        {
            var keyCell = cells[keyIndex];
            long? keyValue = string.IsNullOrWhiteSpace(keyCell) ? null : long.Parse(keyCell!, NumberStyles.Integer, CultureInfo.InvariantCulture);
            var rowId = explicitRowId ?? keyValue;
            if (rowId == null)
                throw new InvalidOperationException("Rowid (key column) must not be NULL.");

            if (keyValue != null && keyValue.Value != rowId.Value)
                throw new InvalidOperationException("rowid must match key column value.");

            if (keyValue == null)
                cells[keyIndex] = rowId.Value.ToString(CultureInfo.InvariantCulture);

            return rowId.Value;
        }

        private List<Row> LoadRowsFromDisk()
        {
            if (!File.Exists(filePath))
                return new List<Row>();

            var lines = File.ReadAllLines(filePath);
            var location = LocateMarkdownTable(lines, tableTitle);
            if (location == null)
                return new List<Row>();

            if (location.Value.ColumnCount != columns.Count)
                throw new InvalidOperationException("Column count mismatch between schema and markdown table.");

            return ParseRows(lines, location.Value, columns, keyIndex);
        }
    }

    private sealed class MarkdownFileCursor : ISqliteVirtualTableCursor
    {
        private readonly IReadOnlyList<SchemaColumn> columns;
        private readonly IReadOnlyList<Row> rows;
        private int index;

        public MarkdownFileCursor(IReadOnlyList<SchemaColumn> columns, IReadOnlyList<Row> rows)
        {
            this.columns = columns;
            this.rows = rows;
            index = 0;
        }

        public void Dispose()
        {
        }

        public void Filter(SqliteVirtualTableFilter filter)
        {
            index = 0;
        }

        public void Next()
        {
            index++;
        }

        public bool Eof => index >= rows.Count;

        public long RowId => rows[index].RowId;

        public object? GetColumnValue(int columnIndex)
        {
            var row = rows[index];
            var raw = columnIndex < row.Cells.Length ? row.Cells[columnIndex] : null;
            var affinity = columns[columnIndex].Affinity;

            if (string.IsNullOrWhiteSpace(raw))
                return null;

            return affinity switch
            {
                TypeAffinity.Integer => long.Parse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture),
                TypeAffinity.Real => double.Parse(raw, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture),
                _ => raw
            };
        }
    }

    private static (string[] lines, TableLocation? location, List<Row> rows) LoadTableFromStream(
        FileStream stream,
        string tableTitle,
        IReadOnlyList<SchemaColumn> columns,
        int keyIndex)
    {
        stream.Position = 0;
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
        var content = reader.ReadToEnd();
        var lines = SplitLines(content);

        var location = LocateMarkdownTable(lines, tableTitle);
        if (location == null)
            return (lines, null, new List<Row>());

        if (location.Value.ColumnCount != columns.Count)
            throw new InvalidOperationException("Column count mismatch between schema and markdown table.");

        var rows = ParseRows(lines, location.Value, columns, keyIndex);
        return (lines, location, rows);
    }

    private static string[] SplitLines(string content)
    {
        return content
            .Replace("\r\n", "\n")
            .Replace("\r", "\n")
            .Split('\n', StringSplitOptions.None);
    }

    private static List<string> RewriteDocument(string[] lines, TableLocation? location, string tableTitle, IReadOnlyList<SchemaColumn> schema, List<Row> rows)
    {
        var result = lines.ToList();
        var newBlock = BuildMarkdownTableBlock(tableTitle, schema, rows);

        if (location == null)
        {
            if (result.Count > 0 && !string.IsNullOrWhiteSpace(result.Last()))
                result.Add(string.Empty);

            result.AddRange(newBlock);
            return result;
        }

        int start = location.Value.HeaderLineIndex;
        int count = Math.Max(0, location.Value.AfterTableLineIndex - start);
        result.RemoveRange(start, count);

        // newBlock includes heading+blank line; keep existing heading, replace only the table.
        var tableOnly = newBlock;
        if (!string.IsNullOrWhiteSpace(tableTitle) && tableOnly.Count >= 2 && IsHeading(tableOnly[0]))
            tableOnly = tableOnly.Skip(2).ToList();

        result.InsertRange(start, tableOnly);
        return result;
    }

    private static List<string> BuildMarkdownTableBlock(string tableTitle, IReadOnlyList<SchemaColumn> schema, List<Row> rows)
    {
        var result = new List<string>();
        if (!string.IsNullOrWhiteSpace(tableTitle))
        {
            result.Add("# " + tableTitle.Trim());
            result.Add(string.Empty);
        }

        result.Add(BuildHeaderLine(schema.Select(c => c.Name)));
        result.Add(BuildSeparatorLine(schema.Count));
        foreach (var row in rows)
        {
            result.Add(BuildRowLine(row.Cells));
        }
        return result;
    }

    private static string BuildHeaderLine(IEnumerable<string> names)
    {
        return "| " + string.Join(" | ", names.Select(EscapeMarkdownValue)) + " |";
    }

    private static string BuildSeparatorLine(int columnCount)
    {
        return "| " + string.Join(" | ", Enumerable.Repeat("---", columnCount)) + " |";
    }

    private static string BuildRowLine(string?[] cells)
    {
        return "| " + string.Join(" | ", cells.Select(EscapeMarkdownValue)) + " |";
    }

    private static string EscapeMarkdownValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return value
            .Replace("|", "\\|")
            .Replace("\r", " ")
            .Replace("\n", " ");
    }

    private static string? FormatCell(SqliteValue value, TypeAffinity affinity)
    {
        if (value.IsNull)
            return null;

        return affinity switch
        {
            TypeAffinity.Integer => FormatInteger(value),
            TypeAffinity.Real => FormatReal(value),
            _ => value.GetString() ?? value.ToObject()?.ToString()
        };
    }

    private static string FormatInteger(SqliteValue value)
    {
        return value.Kind switch
        {
            SqliteValueKind.Integer => value.GetInt64().ToString(CultureInfo.InvariantCulture),
            SqliteValueKind.Float => ((long)value.GetDouble()).ToString(CultureInfo.InvariantCulture),
            SqliteValueKind.Text => long.Parse(value.GetString() ?? "0", NumberStyles.Integer, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
            _ => throw new InvalidOperationException("Invalid INTEGER value.")
        };
    }

    private static string FormatReal(SqliteValue value)
    {
        return value.Kind switch
        {
            SqliteValueKind.Integer => ((double)value.GetInt64()).ToString(CultureInfo.InvariantCulture),
            SqliteValueKind.Float => value.GetDouble().ToString(CultureInfo.InvariantCulture),
            SqliteValueKind.Text => double.Parse(value.GetString() ?? "0", NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
            _ => throw new InvalidOperationException("Invalid REAL value.")
        };
    }

    private static IReadOnlyList<SchemaColumn> ParseSchema(string schema)
    {
        var parts = SplitCommaSeparated(schema);
        var columns = new List<SchemaColumn>();

        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (trimmed.Length == 0)
                continue;

            var tokens = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Length == 0)
                continue;

            var name = Unquote(tokens[0]);
            var type = tokens.Length >= 2 ? tokens[1] : "TEXT";
            columns.Add(new SchemaColumn(name, type));
        }

        return columns;
    }

    private static List<string> SplitCommaSeparated(string value)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;
        char quote = '\0';

        foreach (var ch in value)
        {
            if (inQuotes)
            {
                current.Append(ch);
                if (ch == quote)
                    inQuotes = false;
                continue;
            }

            if (ch == '\'' || ch == '"')
            {
                inQuotes = true;
                quote = ch;
                current.Append(ch);
                continue;
            }

            if (ch == ',')
            {
                result.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(ch);
        }

        result.Add(current.ToString());
        return result;
    }

    private static string Unquote(string token)
    {
        if (token.Length >= 2)
        {
            if ((token.StartsWith("\"") && token.EndsWith("\"")) || (token.StartsWith("'") && token.EndsWith("'")))
                return token.Substring(1, token.Length - 2);
        }
        return token;
    }

    private static string ResolveTargetPath(string destinationPath, string tableTitle)
    {
        var expandedPath = Environment.ExpandEnvironmentVariables(destinationPath);
        var fullPath = Path.GetFullPath(expandedPath);
        var isDirectoryHint = Directory.Exists(fullPath)
                             || fullPath.EndsWith(Path.DirectorySeparatorChar)
                             || fullPath.EndsWith(Path.AltDirectorySeparatorChar);

        if (isDirectoryHint)
        {
            var safeFileName = MakeSafeFileName(string.IsNullOrWhiteSpace(tableTitle) ? "table" : tableTitle);
            return Path.Combine(fullPath, safeFileName);
        }

        if (string.IsNullOrWhiteSpace(Path.GetExtension(fullPath)))
            return $"{fullPath}.md";

        return fullPath;
    }

    private static string MakeSafeFileName(string sourceName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new StringBuilder();
        foreach (var ch in sourceName)
        {
            sanitized.Append(invalid.Contains(ch) ? '_' : ch);
        }

        var value = sanitized.ToString().Trim();
        return string.IsNullOrWhiteSpace(value) ? "table.md" : $"{value}.md";
    }

    private static void EnsureOutputDirectory(string outputPath)
    {
        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDirectory))
            Directory.CreateDirectory(outputDirectory);
    }

    private static List<Row> ParseRows(string[] lines, TableLocation location, IReadOnlyList<SchemaColumn> schema, int keyIndex)
    {
        var rows = new List<Row>();
        var columnCount = location.ColumnCount;

        foreach (var rowLineIndex in location.DataRowLineIndices)
        {
            if (!TryParsePipeLine(lines[rowLineIndex], out var cells))
                continue;

            var normalized = new string?[columnCount];
            for (int i = 0; i < columnCount; i++)
            {
                normalized[i] = i < cells.Count ? (cells[i] ?? string.Empty).Trim() : null;
            }

            var keyCell = normalized[keyIndex];
            if (string.IsNullOrWhiteSpace(keyCell))
                throw new InvalidOperationException("Key column must not be NULL.");

            var rowId = long.Parse(keyCell!, NumberStyles.Integer, CultureInfo.InvariantCulture);
            rows.Add(new Row(rowId, normalized));
        }

        return rows;
    }

    private static TableLocation? LocateMarkdownTable(string[] lines, string tableTitle)
    {
        var tables = ParseMarkdownTablesWithLineNumbers(lines);
        if (tables.Count == 0)
            return null;

        if (string.IsNullOrWhiteSpace(tableTitle))
        {
            if (tables.Count != 1)
                throw new InvalidOperationException("tableTitle is required when a file has multiple tables.");
            return tables[0];
        }

        var matches = tables.Where(t => string.Equals(t.Heading, tableTitle, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0)
            return null;
        if (matches.Count > 1)
            throw new InvalidOperationException("Multiple matching tables found for title.");
        return matches[0];
    }

    private static List<TableLocation> ParseMarkdownTablesWithLineNumbers(string[] lines)
    {
        var tables = new List<TableLocation>();
        bool inCodeFence = false;
        string? currentHeading = null;
        int index = 0;

        while (index < lines.Length)
        {
            var rawLine = lines[index];
            if (IsCodeFence(rawLine))
            {
                inCodeFence = !inCodeFence;
                index++;
                continue;
            }

            if (inCodeFence)
            {
                index++;
                continue;
            }

            if (TryParseHeading(rawLine, out var heading))
            {
                currentHeading = heading;
                index++;
                continue;
            }

            if (!TryParsePipeLine(rawLine, out _))
            {
                index++;
                continue;
            }

            if (index + 1 >= lines.Length || !TryParsePipeSeparator(lines[index + 1], out int cellCount))
            {
                index++;
                continue;
            }

            var rowLineIndices = new List<int>();
            int tableLine = index + 2;
            while (tableLine < lines.Length)
            {
                if (!TryParsePipeLine(lines[tableLine], out var rowValues))
                    break;

                if (TryParsePipeSeparator(lines[tableLine], out _))
                {
                    tableLine++;
                    continue;
                }

                if (rowValues.All(string.IsNullOrWhiteSpace))
                {
                    tableLine++;
                    continue;
                }

                rowLineIndices.Add(tableLine);
                tableLine++;
            }

            tables.Add(new TableLocation(currentHeading?.Trim(), index, cellCount, rowLineIndices, tableLine));
            currentHeading = null;
            index = tableLine;
        }

        return tables;
    }

    private static bool TryParseHeading(string line, out string heading)
    {
        heading = string.Empty;
        var trimmed = line.TrimStart();

        if (!trimmed.StartsWith("#", StringComparison.Ordinal))
            return false;

        int hashes = 0;
        while (hashes < trimmed.Length && trimmed[hashes] == '#')
            hashes++;

        if (hashes == 0 || hashes > 6)
            return false;

        if (hashes == trimmed.Length || (trimmed[hashes] != ' ' && trimmed[hashes] != '\t'))
            return false;

        heading = trimmed[(hashes + 1)..].Trim();
        return heading.Length > 0;
    }

    private static bool IsHeading(string line)
    {
        return TryParseHeading(line, out _);
    }

    private static bool TryParsePipeLine(string line, out IReadOnlyList<string?> values)
    {
        values = Array.Empty<string?>();
        if (string.IsNullOrWhiteSpace(line))
            return false;
        if (!line.Contains('|', StringComparison.Ordinal))
            return false;

        values = SplitPipeLine(line);
        return values.Count > 0;
    }

    private static bool TryParsePipeSeparator(string line, out int cellCount)
    {
        cellCount = 0;
        if (!TryParsePipeLine(line, out var cells))
            return false;

        foreach (string? cell in cells)
        {
            var marker = (cell ?? string.Empty).Trim();
            if (marker.Length == 0)
                return false;

            int dashCount = marker.Count(ch => ch == '-');
            if (dashCount < 3)
                return false;

            foreach (char ch in marker)
            {
                if (ch != '-' && ch != ':')
                    return false;
            }
        }

        cellCount = cells.Count;
        return true;
    }

    private static bool IsCodeFence(string line)
    {
        var trimmed = line.Trim();
        return trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal);
    }

    private static List<string> SplitPipeLine(string line)
    {
        var content = line.Trim();
        if (content.StartsWith("|", StringComparison.Ordinal))
            content = content[1..];
        if (content.EndsWith("|", StringComparison.Ordinal))
            content = content[..^1];

        var values = new List<string>();
        var current = new StringBuilder();
        bool escaped = false;

        for (int i = 0; i < content.Length; i++)
        {
            char ch = content[i];
            if (escaped)
            {
                current.Append(ch);
                escaped = false;
            }
            else if (ch == '\\')
            {
                escaped = true;
            }
            else if (ch == '|')
            {
                values.Add(current.ToString().Trim());
                current.Clear();
            }
            else
            {
                current.Append(ch);
            }
        }

        values.Add(current.ToString().Trim());
        return values;
    }

    private readonly record struct SchemaColumn(string Name, string SqlType)
    {
        public TypeAffinity Affinity => SqlType.StartsWith("INT", StringComparison.OrdinalIgnoreCase)
            ? TypeAffinity.Integer
            : SqlType.StartsWith("REAL", StringComparison.OrdinalIgnoreCase)
              || SqlType.StartsWith("FLOA", StringComparison.OrdinalIgnoreCase)
              || SqlType.StartsWith("DOUB", StringComparison.OrdinalIgnoreCase)
                ? TypeAffinity.Real
                : TypeAffinity.Text;
    }

    private enum TypeAffinity
    {
        Integer,
        Real,
        Text
    }

    private sealed record Row(long RowId, string?[] Cells);

    private readonly record struct TableLocation(
        string? Heading,
        int HeaderLineIndex,
        int ColumnCount,
        IReadOnlyList<int> DataRowLineIndices,
        int AfterTableLineIndex);
}

