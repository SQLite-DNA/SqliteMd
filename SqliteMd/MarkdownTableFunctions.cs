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
    private const string DefaultPattern = "*.md";

    [SqliteTableFunction]
    public static DynamicTable markdown(string source, string searchPattern, long recursive)
    {
        var sourceFiles = ResolveSourceFiles(source, searchPattern, recursive).ToList();
        if (sourceFiles.Count == 0)
        {
            throw new FileNotFoundException($"No markdown source found for '{source}'.");
        }

        var rows = new List<ParsedRow>();
        var columnOrder = new List<string>
        {
            "source_path",
            "source_file_name",
            "source_table_index",
            "source_table_title",
            "source_row"
        };

        var usedColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "source_path",
            "source_file_name",
            "source_table_index",
            "source_table_title",
            "source_row"
        };

        var columnTypes = new Dictionary<string, ColumnType>(StringComparer.OrdinalIgnoreCase);

        foreach (var sourceFile in sourceFiles)
        {
            var fullPath = Path.GetFullPath(sourceFile);
            var fileName = Path.GetFileName(fullPath);
            var tables = ParseMarkdownTables(fullPath).ToList();
            var tableIndex = 0;

            foreach (var table in tables)
            {
                tableIndex++;
                var tableColumns = BuildTableColumns(table.Headers, table.Rows, usedColumns);

                foreach (var column in tableColumns)
                {
                    if (!columnOrder.Contains(column, StringComparer.OrdinalIgnoreCase))
                    {
                        columnOrder.Add(column);
                    }
                }

                foreach (var rowWithIndex in table.Rows.Select((row, index) => (row, index)))
                {
                    var rowValues = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

                    for (int i = 0; i < tableColumns.Count; i++)
                    {
                        var key = tableColumns[i];
                        var rawValue = i < rowWithIndex.row.Count ? rowWithIndex.row[i] : null;
                        rowValues[key] = rawValue;

                        if (!columnTypes.TryGetValue(key, out var existingType))
                        {
                            existingType = ColumnType.Unknown;
                        }

                        columnTypes[key] = MergeType(existingType, rawValue);
                    }

                    if (rowWithIndex.row.Count > tableColumns.Count)
                    {
                        for (int i = tableColumns.Count; i < rowWithIndex.row.Count; i++)
                        {
                            var overflowColumn = GetUniqueColumnName($"column_{i + 1}", usedColumns);
                            if (!columnOrder.Contains(overflowColumn, StringComparer.OrdinalIgnoreCase))
                            {
                                columnOrder.Add(overflowColumn);
                                tableColumns.Add(overflowColumn);
                            }

                            rowValues[overflowColumn] = rowWithIndex.row[i];
                            if (!columnTypes.TryGetValue(overflowColumn, out var existingType))
                            {
                                existingType = ColumnType.Unknown;
                            }

                            columnTypes[overflowColumn] = MergeType(existingType, rowWithIndex.row[i]);
                        }
                    }

                    rows.Add(new ParsedRow(
                        fullPath,
                        fileName,
                        tableIndex,
                        table.Title,
                        rowWithIndex.index + 1,
                        rowValues));
                }
            }
        }

        if (rows.Count == 0)
        {
            throw new InvalidOperationException($"No markdown tables found in '{source}'.");
        }

        var schemaBuilder = new StringBuilder();
        schemaBuilder.Append("\"source_path\" TEXT, ");
        schemaBuilder.Append("\"source_file_name\" TEXT, ");
        schemaBuilder.Append("\"source_table_index\" INTEGER, ");
        schemaBuilder.Append("\"source_table_title\" TEXT, ");
        schemaBuilder.Append("\"source_row\" INTEGER");

        var dynamicColumns = columnOrder
            .Where(c => c != "source_path"
                        && c != "source_file_name"
                        && c != "source_table_index"
                        && c != "source_table_title"
                        && c != "source_row")
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var column in dynamicColumns)
        {
            var sqlType = columnTypes.TryGetValue(column, out var type)
                ? type switch
                {
                    ColumnType.Integer => "INTEGER",
                    ColumnType.Real => "REAL",
                    _ => "TEXT"
                }
                : "TEXT";

            schemaBuilder.Append(", \"").Append(column).Append("\" ").Append(sqlType);
        }

        var data = new List<object[]>();
        foreach (var row in rows)
        {
            var values = new object[columnOrder.Count];
            values[0] = row.SourcePath;
            values[1] = row.SourceFileName;
            values[2] = row.SourceTableIndex;
            values[3] = row.SourceTableTitle ?? (object)DBNull.Value;
            values[4] = row.SourceRow;

            for (int i = 5; i < columnOrder.Count; i++)
            {
                var column = columnOrder[i];
                if (row.Values.TryGetValue(column, out var rawValue))
                {
                    values[i] = ConvertCellValue(rawValue, columnTypes.GetValueOrDefault(column));
                }
                else
                {
                    values[i] = DBNull.Value;
                }
            }

            data.Add(values);
        }

        return new DynamicTable(schemaBuilder.ToString(), data);
    }

    private static IEnumerable<string> ResolveSourceFiles(string source, string searchPattern, long recursive)
    {
        var path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(source));
        var pattern = string.IsNullOrWhiteSpace(searchPattern) ? DefaultPattern : searchPattern;

        if (File.Exists(path))
        {
            return new[] { path };
        }

        if (!Directory.Exists(path))
        {
            throw new FileNotFoundException($"Could not find file or directory '{source}'.");
        }

        var option = recursive != 0 ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        return Directory.EnumerateFiles(path, pattern, option).OrderBy(i => i, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<MarkdownTable> ParseMarkdownTables(string filePath)
    {
        var lines = File.ReadAllLines(filePath);
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

            if (!TryParsePipeLine(rawLine, out var potentialHeader))
            {
                index++;
                continue;
            }

            if (index + 1 >= lines.Length || !TryParsePipeSeparator(lines[index + 1], out _))
            {
                index++;
                continue;
            }

            var parsedHeader = potentialHeader.Select(h => h?.Trim() ?? string.Empty).ToList();
            index += 2;
            var parsedRows = new List<IReadOnlyList<string?>>();

            while (index < lines.Length)
            {
                if (!TryParsePipeLine(lines[index], out var rowValues))
                {
                    break;
                }

                if (TryParsePipeSeparator(lines[index], out int _))
                {
                    index++;
                    continue;
                }

                if (rowValues.All(string.IsNullOrWhiteSpace))
                {
                    index++;
                    continue;
                }

                parsedRows.Add(rowValues);
                index++;
            }

            yield return new MarkdownTable(currentHeading, parsedHeader, parsedRows);
            currentHeading = null;
        }
    }

    private static bool TryParseHeading(string line, out string heading)
    {
        heading = string.Empty;
        var trimmed = line.TrimStart();

        if (!trimmed.StartsWith("#", StringComparison.Ordinal))
            return false;

        int hashes = 0;
        while (hashes < trimmed.Length && trimmed[hashes] == '#')
        {
            hashes++;
        }

        if (hashes == 0 || hashes > 6)
            return false;

        if (hashes == trimmed.Length || (trimmed[hashes] != ' ' && trimmed[hashes] != '\t'))
            return false;

        heading = trimmed[(hashes + 1)..].Trim();
        return heading.Length > 0;
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

    private static List<string> BuildTableColumns(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string?>> rows, HashSet<string> usedColumns)
    {
        var columns = new List<string>();

        for (int i = 0; i < headers.Count; i++)
        {
            var header = headers[i];
            var rawName = string.IsNullOrWhiteSpace(header) ? $"column_{i + 1}" : SanitizeHeaderName(header);
            columns.Add(GetUniqueColumnName(rawName, usedColumns));
        }

        int maxColumns = rows.Count == 0 ? headers.Count : rows.Max(r => r.Count);
        for (int i = columns.Count; i < maxColumns; i++)
        {
            columns.Add(GetUniqueColumnName($"column_{i + 1}", usedColumns));
        }

        return columns;
    }

    private static string SanitizeHeaderName(string header)
    {
        var sanitized = new StringBuilder();
        foreach (char ch in header)
        {
            if (char.IsLetterOrDigit(ch) || ch == '_')
                sanitized.Append(ch);
            else
                sanitized.Append('_');
        }

        if (sanitized.Length == 0)
            return "column";

        return sanitized.ToString();
    }

    private static string GetUniqueColumnName(string name, HashSet<string> usedColumns)
    {
        var normalized = string.IsNullOrWhiteSpace(name) ? "column" : name;
        if (char.IsDigit(normalized[0]))
            normalized = "_" + normalized;

        var unique = normalized;
        int suffix = 2;
        while (usedColumns.Contains(unique))
        {
            unique = $"{normalized}_{suffix}";
            suffix++;
        }

        usedColumns.Add(unique);
        return unique;
    }

    private static ColumnType MergeType(ColumnType current, string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
            return current;

        if (current == ColumnType.Text)
            return ColumnType.Text;

        if (current == ColumnType.Real)
            return IsLong(rawValue) || IsDouble(rawValue) ? ColumnType.Real : ColumnType.Text;

        if (current == ColumnType.Integer)
        {
            return IsLong(rawValue)
                ? ColumnType.Integer
                : IsDouble(rawValue)
                    ? ColumnType.Real
                    : ColumnType.Text;
        }

        if (IsLong(rawValue))
            return ColumnType.Integer;

        if (IsDouble(rawValue))
            return ColumnType.Real;

        return ColumnType.Text;
    }

    private static bool IsLong(string value)
    {
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
    }

    private static bool IsDouble(string value)
    {
        return double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out _);
    }

    private static object ConvertCellValue(string? rawValue, ColumnType type)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
            return DBNull.Value;

        return type switch
        {
            ColumnType.Integer => long.Parse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture),
            ColumnType.Real => double.Parse(rawValue, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture),
            _ => rawValue!
        };
    }

    private sealed record ParsedRow(
        string SourcePath,
        string SourceFileName,
        int SourceTableIndex,
        string? SourceTableTitle,
        int SourceRow,
        Dictionary<string, string?> Values);

    private sealed record MarkdownTable(string? Title, IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string?>> Rows);

    private enum ColumnType
    {
        Unknown,
        Integer,
        Real,
        Text
    }
}
