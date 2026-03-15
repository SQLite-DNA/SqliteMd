using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
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
        return new MarkdownFileTable(path, tableTitle, schema, keyColumn, WriteMode.ReadWrite);
    }

    // Mode-aware, single-table view over a markdown pipe table.
    //
    // CREATE VIRTUAL TABLE t USING markdown_table_mode('path.md', 'Title', 'id INTEGER, title TEXT', 'id', 'append_only');
    [SqliteVirtualTable]
    public static ISqliteVirtualTable markdown_table_mode(string path, string tableTitle, string schema, string keyColumn, string writeMode)
    {
        return new MarkdownFileTable(path, tableTitle, schema, keyColumn, ParseWriteMode(writeMode));
    }

    // Read-only, repository-scoped view over the first markdown table in each matched file.
    //
    // CREATE VIRTUAL TABLE tasks USING markdown_glob('C:\Notes\**\*.md', 'id INTEGER, title TEXT');
    // Provenance is exposed via hidden columns: _path, _heading, _mtime, _table_index.
    [SqliteVirtualTable]
    public static ISqliteVirtualTable markdown_glob(string globPattern, string schema)
    {
        return new MarkdownGlobTable(globPattern, schema);
    }

    [SqliteVirtualTable]
    public static ISqliteVirtualTable markdown_repo(string globPattern, string schema)
    {
        return new MarkdownGlobTable(globPattern, schema);
    }

    [SqliteTableFunction]
    public static DynamicTable markdown_diagnostics(string mode, string target, string schema, string keyColumn, string tableTitle)
    {
        return BuildDiagnostics(mode, target, schema, keyColumn, tableTitle, null);
    }

    [SqliteTableFunction]
    public static DynamicTable markdown_diagnostics_mode(string mode, string target, string schema, string keyColumn, string tableTitle, string writeMode)
    {
        return BuildDiagnostics(mode, target, schema, keyColumn, tableTitle, writeMode);
    }

    [SqliteTableFunction]
    public static DynamicTable markdown_table_diagnostics(string path, string tableTitle, string schema, string keyColumn)
    {
        return BuildDiagnostics("table", path, schema, keyColumn, tableTitle, null);
    }

    [SqliteTableFunction]
    public static DynamicTable markdown_table_diagnostics_mode(string path, string tableTitle, string schema, string keyColumn, string writeMode)
    {
        return BuildDiagnostics("table", path, schema, keyColumn, tableTitle, writeMode);
    }

    [SqliteTableFunction]
    public static DynamicTable markdown_glob_diagnostics(string globPattern, string schema)
    {
        return BuildDiagnostics("glob", globPattern, schema, string.Empty, string.Empty, null);
    }

    [SqliteTableFunction]
    public static DynamicTable markdown_repo_diagnostics(string globPattern, string schema)
    {
        return BuildDiagnostics("repo", globPattern, schema, string.Empty, string.Empty, null);
    }

    private const string DiagnosticsSchema =
        "\"mode\" TEXT, \"target\" TEXT, \"path\" TEXT, \"exists\" INTEGER, \"readable\" INTEGER, \"accepted\" INTEGER, " +
        "\"reason_code\" TEXT, \"reason_detail\" TEXT, \"matched_table_count\" INTEGER, \"selected_table_index\" INTEGER, " +
        "\"heading\" TEXT, \"table_start_line\" INTEGER, \"table_end_line\" INTEGER, \"preamble_line_count\" INTEGER, " +
        "\"schema_column_count\" INTEGER, \"detected_column_count\" INTEGER, \"key_column\" TEXT, \"key_column_found\" INTEGER, " +
        "\"key_column_is_integer\" INTEGER, \"write_mode\" TEXT, \"can_read\" INTEGER, \"can_insert\" INTEGER, \"can_update\" INTEGER, " +
        "\"can_delete\" INTEGER, \"create_on_write\" INTEGER, \"mtime\" INTEGER";

    private sealed class MarkdownFileTable : ISqliteWritableVirtualTable, ISqliteVirtualTableTransaction, IDisposable
    {
        private readonly string filePath;
        private readonly string tableTitle;
        private readonly IReadOnlyList<SchemaColumn> columns;
        private readonly int keyIndex;
        private readonly WriteMode writeMode;

        private FileStream? lockedStream;
        private string[]? baseLines;
        private TableLocation? baseLocation;
        private List<Row>? workingRows;
        private bool dirty;

        public MarkdownFileTable(string inputPath, string tableTitle, string schema, string keyColumn, WriteMode writeMode)
        {
            filePath = ResolveTargetPath(inputPath, tableTitle);
            this.tableTitle = tableTitle ?? string.Empty;
            columns = ParseSchema(schema);
            if (columns.Count == 0)
                throw new ArgumentException("schema must contain at least one column.", nameof(schema));

            keyIndex = FindColumnIndex(columns, keyColumn);
            if (keyIndex < 0)
                throw new ArgumentException($"keyColumn '{keyColumn}' must match a column in schema.", nameof(keyColumn));

            if (columns[keyIndex].Affinity != TypeAffinity.Integer)
                throw new ArgumentException("keyColumn must be declared as INTEGER in schema.", nameof(keyColumn));

            this.writeMode = writeMode;
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
            EnsureWriteAllowed(update.Kind);
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

        private void EnsureWriteAllowed(SqliteVirtualTableUpdateKind kind)
        {
            switch (writeMode)
            {
                case WriteMode.ReadOnly:
                    throw new InvalidOperationException("This markdown table is read_only and does not allow INSERT, UPDATE, or DELETE.");

                case WriteMode.AppendOnly when kind != SqliteVirtualTableUpdateKind.Insert:
                    throw new InvalidOperationException("This markdown table is append_only and only allows INSERT.");
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
            var analysis = AnalyzeSelectedTable(lines, columns.Count);
            if (analysis.SelectedLocation == null)
                return new List<Row>();

            if (!analysis.Accepted)
                throw new InvalidOperationException(analysis.ReasonDetail ?? "Column count mismatch between schema and markdown table.");

            return ParseRows(lines, analysis.SelectedLocation.Value, columns, keyIndex);
        }
    }

    private sealed class MarkdownGlobTable : ISqliteVirtualTable
    {
        private readonly string globPattern;
        private readonly IReadOnlyList<SchemaColumn> columns;

        public MarkdownGlobTable(string globPattern, string schema)
        {
            this.globPattern = globPattern ?? throw new ArgumentNullException(nameof(globPattern));
            columns = ParseSchema(schema);
            if (columns.Count == 0)
                throw new ArgumentException("schema must contain at least one column.", nameof(schema));
        }

        public string Schema
        {
            get
            {
                var userSchema = string.Join(", ", columns.Select(c => $"\"{c.Name}\" {c.SqlType}"));
                return userSchema
                    + ", \"_path\" TEXT HIDDEN"
                    + ", \"_heading\" TEXT HIDDEN"
                    + ", \"_mtime\" INTEGER HIDDEN"
                    + ", \"_table_index\" INTEGER HIDDEN";
            }
        }

        public ISqliteVirtualTableCursor OpenCursor()
        {
            var rows = LoadRows();
            return new MarkdownGlobCursor(columns, rows);
        }

        private List<RepoRow> LoadRows()
        {
            var result = new List<RepoRow>();
            long nextRowId = 1;

            foreach (var filePath in ResolveGlobMatches(globPattern))
            {
                string[] lines;
                try
                {
                    lines = File.ReadAllLines(filePath);
                }
                catch
                {
                    continue;
                }

                var analysis = AnalyzeSelectedTable(lines, columns.Count);
                if (!analysis.Accepted || analysis.SelectedLocation == null)
                    continue;

                var rowCells = ParseRowCells(lines, analysis.SelectedLocation.Value);
                var mtime = new DateTimeOffset(File.GetLastWriteTimeUtc(filePath)).ToUnixTimeSeconds();
                foreach (var cells in rowCells)
                {
                    result.Add(new RepoRow(nextRowId++, cells, filePath, analysis.SelectedLocation.Value.Heading, mtime, analysis.SelectedTableIndex ?? 0));
                }
            }

            return result;
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
            return ConvertCellValue(raw, columns[columnIndex].Affinity);
        }
    }

    private sealed class MarkdownGlobCursor : ISqliteVirtualTableCursor
    {
        private readonly IReadOnlyList<SchemaColumn> columns;
        private readonly IReadOnlyList<RepoRow> rows;
        private int index;

        public MarkdownGlobCursor(IReadOnlyList<SchemaColumn> columns, IReadOnlyList<RepoRow> rows)
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
            if (columnIndex < columns.Count)
            {
                var raw = columnIndex < row.Cells.Length ? row.Cells[columnIndex] : null;
                return ConvertCellValue(raw, columns[columnIndex].Affinity);
            }

            return (columnIndex - columns.Count) switch
            {
                0 => (object?)row.Path,
                1 => row.Heading,
                2 => row.MTimeUnix,
                3 => (long)row.TableIndex,
                _ => null,
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

        var analysis = AnalyzeSelectedTable(lines, columns.Count);
        if (analysis.SelectedLocation == null)
            return (lines, null, new List<Row>());

        if (!analysis.Accepted)
            throw new InvalidOperationException(analysis.ReasonDetail ?? "Column count mismatch between schema and markdown table.");

        var rows = ParseRows(lines, analysis.SelectedLocation.Value, columns, keyIndex);
        return (lines, analysis.SelectedLocation, rows);
    }

    private static string[] SplitLines(string content)
    {
        return content
            .Replace("\r\n", "\n")
            .Replace("\r", "\n")
            .Split('\n', StringSplitOptions.None);
    }

    private static DynamicTable BuildDiagnostics(string mode, string target, string schema, string keyColumn, string tableTitle, string? writeModeText)
    {
        var normalizedMode = NormalizeDiagnosticsMode(mode);
        if (normalizedMode == null)
        {
            return new DynamicTable(
                DiagnosticsSchema,
                new[]
                {
                    BuildDiagnosticRow(
                        mode,
                        target,
                        null,
                        exists: null,
                        readable: null,
                        accepted: false,
                        reasonCode: "unsupported_mode",
                        reasonDetail: "Supported modes are 'table', 'glob', and 'repo'.",
                        matchedTableCount: 0,
                        selectedTableIndex: null,
                        heading: null,
                        tableStartLine: null,
                        tableEndLine: null,
                        preambleLineCount: null,
                        schemaColumnCount: null,
                        detectedColumnCount: null,
                        keyColumn: keyColumn,
                        keyColumnFound: null,
                        keyColumnIsInteger: null,
                        writeMode: null,
                        canRead: false,
                        canInsert: false,
                        canUpdate: false,
                        canDelete: false,
                        createOnWrite: false,
                        mtime: null)
                });
        }

        var requestedWriteMode = normalizedMode == "table"
            ? TryParseWriteMode(writeModeText ?? string.Empty)
            : WriteMode.ReadOnly;
        if (normalizedMode == "table" && requestedWriteMode == null)
        {
            return new DynamicTable(
                DiagnosticsSchema,
                new[]
                {
                    BuildDiagnosticRow(
                        normalizedMode,
                        target,
                        null,
                        exists: null,
                        readable: null,
                        accepted: false,
                        reasonCode: "unsupported_write_mode",
                        reasonDetail: $"Supported write modes are '{FormatWriteMode(WriteMode.ReadWrite)}', '{FormatWriteMode(WriteMode.ReadOnly)}', and '{FormatWriteMode(WriteMode.AppendOnly)}'.",
                        matchedTableCount: 0,
                        selectedTableIndex: null,
                        heading: null,
                        tableStartLine: null,
                        tableEndLine: null,
                        preambleLineCount: null,
                        schemaColumnCount: null,
                        detectedColumnCount: null,
                        keyColumn: keyColumn,
                        keyColumnFound: null,
                        keyColumnIsInteger: null,
                        writeMode: writeModeText,
                        canRead: false,
                        canInsert: false,
                        canUpdate: false,
                        canDelete: false,
                        createOnWrite: false,
                        mtime: null)
                });
        }

        var columns = ParseSchema(schema);
        if (columns.Count == 0)
        {
            return new DynamicTable(
                DiagnosticsSchema,
                new[]
                {
                    BuildDiagnosticRow(
                        normalizedMode,
                        target,
                        null,
                        exists: null,
                        readable: null,
                        accepted: false,
                        reasonCode: "invalid_schema",
                        reasonDetail: "schema must contain at least one column.",
                        matchedTableCount: 0,
                        selectedTableIndex: null,
                        heading: null,
                        tableStartLine: null,
                        tableEndLine: null,
                        preambleLineCount: null,
                        schemaColumnCount: 0,
                        detectedColumnCount: null,
                        keyColumn: keyColumn,
                        keyColumnFound: null,
                        keyColumnIsInteger: null,
                        writeMode: requestedWriteMode == null ? null : FormatWriteMode(requestedWriteMode.Value),
                        canRead: false,
                        canInsert: false,
                        canUpdate: false,
                        canDelete: false,
                        createOnWrite: false,
                        mtime: null)
                });
        }

        return normalizedMode == "table"
            ? BuildTableDiagnostics(target, tableTitle, columns, keyColumn, requestedWriteMode ?? WriteMode.ReadWrite)
            : BuildGlobDiagnostics(normalizedMode, target, columns);
    }

    private static DynamicTable BuildTableDiagnostics(string target, string tableTitle, IReadOnlyList<SchemaColumn> columns, string keyColumn, WriteMode writeMode)
    {
        int keyIndex = FindColumnIndex(columns, keyColumn);
        bool keyColumnFound = keyIndex >= 0;
        bool keyColumnIsInteger = keyColumnFound && columns[keyIndex].Affinity == TypeAffinity.Integer;
        var capabilities = GetWriteCapabilities(writeMode);
        var writeModeText = FormatWriteMode(writeMode);

        string resolvedPath;
        try
        {
            resolvedPath = ResolveTargetPath(target, tableTitle);
        }
        catch (Exception ex)
        {
            return new DynamicTable(
                DiagnosticsSchema,
                new[]
                {
                    BuildDiagnosticRow(
                        "table",
                        target,
                        null,
                        exists: null,
                        readable: null,
                        accepted: false,
                        reasonCode: "resolve_target_error",
                        reasonDetail: ex.Message,
                        matchedTableCount: 0,
                        selectedTableIndex: null,
                        heading: null,
                        tableStartLine: null,
                        tableEndLine: null,
                        preambleLineCount: null,
                        schemaColumnCount: columns.Count,
                        detectedColumnCount: null,
                        keyColumn: keyColumn,
                        keyColumnFound: keyColumnFound,
                        keyColumnIsInteger: keyColumnFound ? keyColumnIsInteger : null,
                        writeMode: writeModeText,
                        canRead: false,
                        canInsert: false,
                        canUpdate: false,
                        canDelete: false,
                        createOnWrite: false,
                        mtime: null)
                });
        }

        if (!keyColumnFound)
        {
            return new DynamicTable(
                DiagnosticsSchema,
                new[]
                {
                    BuildDiagnosticRow(
                        "table",
                        target,
                        resolvedPath,
                        exists: File.Exists(resolvedPath),
                        readable: File.Exists(resolvedPath),
                        accepted: false,
                        reasonCode: "key_column_not_found",
                        reasonDetail: $"keyColumn '{keyColumn}' must match a column in schema.",
                        matchedTableCount: 0,
                        selectedTableIndex: null,
                        heading: null,
                        tableStartLine: null,
                        tableEndLine: null,
                        preambleLineCount: null,
                        schemaColumnCount: columns.Count,
                        detectedColumnCount: null,
                        keyColumn: keyColumn,
                        keyColumnFound: false,
                        keyColumnIsInteger: null,
                        writeMode: writeModeText,
                        canRead: false,
                        canInsert: false,
                        canUpdate: false,
                        canDelete: false,
                        createOnWrite: false,
                        mtime: TryGetLastWriteTimeUnix(resolvedPath))
                });
        }

        if (!keyColumnIsInteger)
        {
            return new DynamicTable(
                DiagnosticsSchema,
                new[]
                {
                    BuildDiagnosticRow(
                        "table",
                        target,
                        resolvedPath,
                        exists: File.Exists(resolvedPath),
                        readable: File.Exists(resolvedPath),
                        accepted: false,
                        reasonCode: "key_column_not_integer",
                        reasonDetail: "keyColumn must be declared as INTEGER in schema.",
                        matchedTableCount: 0,
                        selectedTableIndex: null,
                        heading: null,
                        tableStartLine: null,
                        tableEndLine: null,
                        preambleLineCount: null,
                        schemaColumnCount: columns.Count,
                        detectedColumnCount: null,
                        keyColumn: keyColumn,
                        keyColumnFound: true,
                        keyColumnIsInteger: false,
                        writeMode: writeModeText,
                        canRead: false,
                        canInsert: false,
                        canUpdate: false,
                        canDelete: false,
                        createOnWrite: false,
                        mtime: TryGetLastWriteTimeUnix(resolvedPath))
                });
        }

        if (!File.Exists(resolvedPath))
        {
            return new DynamicTable(
                DiagnosticsSchema,
                new[]
                {
                    BuildDiagnosticRow(
                        "table",
                        target,
                        resolvedPath,
                        exists: false,
                        readable: false,
                        accepted: true,
                        reasonCode: capabilities.CreateOnWrite ? "missing_file_create_on_write" : "missing_file",
                        reasonDetail: capabilities.CreateOnWrite
                            ? "File does not exist; markdown_table can create a table on first write."
                            : "File does not exist; markdown_table_mode opens as an empty read_only table.",
                        matchedTableCount: 0,
                        selectedTableIndex: null,
                        heading: null,
                        tableStartLine: null,
                        tableEndLine: null,
                        preambleLineCount: null,
                        schemaColumnCount: columns.Count,
                        detectedColumnCount: null,
                        keyColumn: keyColumn,
                        keyColumnFound: true,
                        keyColumnIsInteger: true,
                        canRead: true,
                        writeMode: writeModeText,
                        canInsert: capabilities.CanInsert,
                        canUpdate: capabilities.CanUpdate,
                        canDelete: capabilities.CanDelete,
                        createOnWrite: capabilities.CreateOnWrite,
                        mtime: null)
                });
        }

        try
        {
            var lines = File.ReadAllLines(resolvedPath);
            var analysis = AnalyzeSelectedTable(lines, columns.Count);
            bool accepted = analysis.Accepted || analysis.ReasonCode == "no_markdown_table";
            bool createOnWrite = accepted && analysis.SelectedLocation == null && capabilities.CreateOnWrite;
            string reasonCode = createOnWrite && analysis.ReasonCode == "no_markdown_table"
                ? "no_markdown_table_create_on_write"
                : analysis.ReasonCode;
            string? reasonDetail = createOnWrite && analysis.ReasonCode == "no_markdown_table"
                ? "No markdown table found; markdown_table can create one on first write."
                : analysis.ReasonDetail;

            return new DynamicTable(
                DiagnosticsSchema,
                new[]
                {
                    BuildDiagnosticRow(
                        "table",
                        target,
                        resolvedPath,
                        exists: true,
                        readable: true,
                        accepted: accepted,
                        reasonCode: reasonCode,
                        reasonDetail: reasonDetail,
                        matchedTableCount: analysis.MatchedTableCount,
                        selectedTableIndex: analysis.SelectedTableIndex,
                        heading: analysis.SelectedLocation?.Heading,
                        tableStartLine: analysis.SelectedLocation == null ? null : analysis.SelectedLocation.Value.HeaderLineIndex + 1,
                        tableEndLine: analysis.SelectedLocation == null ? null : analysis.SelectedLocation.Value.AfterTableLineIndex,
                        preambleLineCount: analysis.SelectedLocation == null ? null : analysis.SelectedLocation.Value.HeaderLineIndex,
                        schemaColumnCount: columns.Count,
                        detectedColumnCount: analysis.DetectedColumnCount,
                        keyColumn: keyColumn,
                        keyColumnFound: true,
                        keyColumnIsInteger: true,
                        writeMode: writeModeText,
                        canRead: accepted,
                        canInsert: accepted && capabilities.CanInsert,
                        canUpdate: accepted && capabilities.CanUpdate,
                        canDelete: accepted && capabilities.CanDelete,
                        createOnWrite: createOnWrite,
                        mtime: TryGetLastWriteTimeUnix(resolvedPath))
                });
        }
        catch (Exception ex)
        {
            return new DynamicTable(
                DiagnosticsSchema,
                new[]
                {
                    BuildDiagnosticRow(
                        "table",
                        target,
                        resolvedPath,
                        exists: true,
                        readable: false,
                        accepted: false,
                        reasonCode: "file_read_error",
                        reasonDetail: ex.Message,
                        matchedTableCount: 0,
                        selectedTableIndex: null,
                        heading: null,
                        tableStartLine: null,
                        tableEndLine: null,
                        preambleLineCount: null,
                        schemaColumnCount: columns.Count,
                        detectedColumnCount: null,
                        keyColumn: keyColumn,
                        keyColumnFound: true,
                        keyColumnIsInteger: true,
                        writeMode: writeModeText,
                        canRead: false,
                        canInsert: false,
                        canUpdate: false,
                        canDelete: false,
                        createOnWrite: false,
                        mtime: TryGetLastWriteTimeUnix(resolvedPath))
                });
        }
    }

    private static DynamicTable BuildGlobDiagnostics(string mode, string target, IReadOnlyList<SchemaColumn> columns)
    {
        const string writeModeText = "read_only";

        List<string> matches;
        try
        {
            matches = ResolveGlobMatches(target);
        }
        catch (Exception ex)
        {
            return new DynamicTable(
                DiagnosticsSchema,
                new[]
                {
                    BuildDiagnosticRow(
                        mode,
                        target,
                        null,
                        exists: null,
                        readable: null,
                        accepted: false,
                        reasonCode: "resolve_target_error",
                        reasonDetail: ex.Message,
                        matchedTableCount: 0,
                        selectedTableIndex: null,
                        heading: null,
                        tableStartLine: null,
                        tableEndLine: null,
                        preambleLineCount: null,
                        schemaColumnCount: columns.Count,
                        detectedColumnCount: null,
                        keyColumn: null,
                        keyColumnFound: null,
                        keyColumnIsInteger: null,
                        writeMode: writeModeText,
                        canRead: false,
                        canInsert: false,
                        canUpdate: false,
                        canDelete: false,
                        createOnWrite: false,
                        mtime: null)
                });
        }

        if (matches.Count == 0)
        {
            return new DynamicTable(
                DiagnosticsSchema,
                new[]
                {
                    BuildDiagnosticRow(
                        mode,
                        target,
                        null,
                        exists: false,
                        readable: false,
                        accepted: false,
                        reasonCode: "no_files_matched",
                        reasonDetail: "No markdown files matched the target.",
                        matchedTableCount: 0,
                        selectedTableIndex: null,
                        heading: null,
                        tableStartLine: null,
                        tableEndLine: null,
                        preambleLineCount: null,
                        schemaColumnCount: columns.Count,
                        detectedColumnCount: null,
                        keyColumn: null,
                        keyColumnFound: null,
                        keyColumnIsInteger: null,
                        writeMode: writeModeText,
                        canRead: false,
                        canInsert: false,
                        canUpdate: false,
                        canDelete: false,
                        createOnWrite: false,
                        mtime: null)
                });
        }

        var rows = new List<object[]>();
        foreach (var filePath in matches)
        {
            try
            {
                var lines = File.ReadAllLines(filePath);
                var analysis = AnalyzeSelectedTable(lines, columns.Count);

                rows.Add(BuildDiagnosticRow(
                    mode,
                    target,
                    filePath,
                    exists: true,
                    readable: true,
                    accepted: analysis.Accepted,
                    reasonCode: analysis.ReasonCode,
                    reasonDetail: analysis.ReasonDetail,
                    matchedTableCount: analysis.MatchedTableCount,
                    selectedTableIndex: analysis.SelectedTableIndex,
                    heading: analysis.SelectedLocation?.Heading,
                    tableStartLine: analysis.SelectedLocation == null ? null : analysis.SelectedLocation.Value.HeaderLineIndex + 1,
                    tableEndLine: analysis.SelectedLocation == null ? null : analysis.SelectedLocation.Value.AfterTableLineIndex,
                    preambleLineCount: analysis.SelectedLocation == null ? null : analysis.SelectedLocation.Value.HeaderLineIndex,
                    schemaColumnCount: columns.Count,
                    detectedColumnCount: analysis.DetectedColumnCount,
                    keyColumn: null,
                    keyColumnFound: null,
                    keyColumnIsInteger: null,
                    writeMode: writeModeText,
                    canRead: analysis.Accepted,
                    canInsert: false,
                    canUpdate: false,
                    canDelete: false,
                    createOnWrite: false,
                    mtime: TryGetLastWriteTimeUnix(filePath)));
            }
            catch (Exception ex)
            {
                rows.Add(BuildDiagnosticRow(
                    mode,
                    target,
                    filePath,
                    exists: true,
                    readable: false,
                    accepted: false,
                    reasonCode: "file_read_error",
                    reasonDetail: ex.Message,
                    matchedTableCount: 0,
                    selectedTableIndex: null,
                    heading: null,
                    tableStartLine: null,
                    tableEndLine: null,
                    preambleLineCount: null,
                    schemaColumnCount: columns.Count,
                    detectedColumnCount: null,
                    keyColumn: null,
                    keyColumnFound: null,
                    keyColumnIsInteger: null,
                    writeMode: writeModeText,
                    canRead: false,
                    canInsert: false,
                    canUpdate: false,
                    canDelete: false,
                    createOnWrite: false,
                    mtime: TryGetLastWriteTimeUnix(filePath)));
            }
        }

        return new DynamicTable(DiagnosticsSchema, rows);
    }

    private static string? NormalizeDiagnosticsMode(string mode)
    {
        if (string.Equals(mode, "table", StringComparison.OrdinalIgnoreCase))
            return "table";
        if (string.Equals(mode, "glob", StringComparison.OrdinalIgnoreCase))
            return "glob";
        if (string.Equals(mode, "repo", StringComparison.OrdinalIgnoreCase))
            return "repo";
        return null;
    }

    private static object[] BuildDiagnosticRow(
        string mode,
        string target,
        string? path,
        bool? exists,
        bool? readable,
        bool accepted,
        string reasonCode,
        string? reasonDetail,
        int matchedTableCount,
        int? selectedTableIndex,
        string? heading,
        int? tableStartLine,
        int? tableEndLine,
        int? preambleLineCount,
        int? schemaColumnCount,
        int? detectedColumnCount,
        string? keyColumn,
        bool? keyColumnFound,
        bool? keyColumnIsInteger,
        string? writeMode,
        bool canRead,
        bool canInsert,
        bool canUpdate,
        bool canDelete,
        bool createOnWrite,
        long? mtime)
    {
        return new object?[]
        {
            mode,
            target,
            path,
            ToSqliteInteger(exists),
            ToSqliteInteger(readable),
            ToSqliteInteger(accepted),
            reasonCode,
            reasonDetail,
            matchedTableCount,
            selectedTableIndex,
            heading,
            tableStartLine,
            tableEndLine,
            preambleLineCount,
            schemaColumnCount,
            detectedColumnCount,
            keyColumn,
            ToSqliteInteger(keyColumnFound),
            ToSqliteInteger(keyColumnIsInteger),
            writeMode,
            ToSqliteInteger(canRead),
            ToSqliteInteger(canInsert),
            ToSqliteInteger(canUpdate),
            ToSqliteInteger(canDelete),
            ToSqliteInteger(createOnWrite),
            mtime
        }!;
    }

    private static long? ToSqliteInteger(bool? value)
    {
        if (value == null)
            return null;
        return value.Value ? 1L : 0L;
    }

    private static WriteMode ParseWriteMode(string? writeMode)
    {
        return TryParseWriteMode(writeMode)
            ?? throw new ArgumentException($"Unsupported writeMode '{writeMode}'. Supported values are '{FormatWriteMode(WriteMode.ReadWrite)}', '{FormatWriteMode(WriteMode.ReadOnly)}', and '{FormatWriteMode(WriteMode.AppendOnly)}'.", nameof(writeMode));
    }

    private static WriteMode? TryParseWriteMode(string? writeMode)
    {
        if (string.IsNullOrWhiteSpace(writeMode))
            return WriteMode.ReadWrite;

        return writeMode.Trim().ToLowerInvariant() switch
        {
            "read_write" or "readwrite" or "rw" => WriteMode.ReadWrite,
            "read_only" or "readonly" or "ro" => WriteMode.ReadOnly,
            "append_only" or "appendonly" or "append" => WriteMode.AppendOnly,
            _ => null
        };
    }

    private static string FormatWriteMode(WriteMode writeMode)
    {
        return writeMode switch
        {
            WriteMode.ReadWrite => "read_write",
            WriteMode.ReadOnly => "read_only",
            WriteMode.AppendOnly => "append_only",
            _ => "read_write"
        };
    }

    private static WriteCapabilities GetWriteCapabilities(WriteMode writeMode)
    {
        return writeMode switch
        {
            WriteMode.ReadOnly => new WriteCapabilities(CanInsert: false, CanUpdate: false, CanDelete: false, CreateOnWrite: false),
            WriteMode.AppendOnly => new WriteCapabilities(CanInsert: true, CanUpdate: false, CanDelete: false, CreateOnWrite: true),
            _ => new WriteCapabilities(CanInsert: true, CanUpdate: true, CanDelete: true, CreateOnWrite: true)
        };
    }

    private static long? TryGetLastWriteTimeUnix(string path)
    {
        try
        {
            return File.Exists(path)
                ? new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeSeconds()
                : null;
        }
        catch
        {
            return null;
        }
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
            result.Add("## " + tableTitle.Trim());
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

    private static int FindColumnIndex(IReadOnlyList<SchemaColumn> schemaColumns, string keyColumn)
    {
        for (int i = 0; i < schemaColumns.Count; i++)
        {
            if (string.Equals(schemaColumns[i].Name, keyColumn, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
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
        foreach (var normalized in ParseRowCells(lines, location))
        {
            var keyCell = normalized[keyIndex];
            if (string.IsNullOrWhiteSpace(keyCell))
                throw new InvalidOperationException("Key column must not be NULL.");

            var rowId = long.Parse(keyCell!, NumberStyles.Integer, CultureInfo.InvariantCulture);
            rows.Add(new Row(rowId, normalized));
        }

        return rows;
    }

    private static List<string?[]> ParseRowCells(string[] lines, TableLocation location)
    {
        var rows = new List<string?[]>();
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

            rows.Add(normalized);
        }

        return rows;
    }

    private static TableAnalysis AnalyzeSelectedTable(string[] lines, int expectedColumnCount)
    {
        var tables = ParseMarkdownTablesWithLineNumbers(lines);
        if (tables.Count == 0)
        {
            return new TableAnalysis(
                Accepted: false,
                ReasonCode: "no_markdown_table",
                ReasonDetail: "No markdown table found.",
                MatchedTableCount: 0,
                SelectedLocation: null,
                DetectedColumnCount: null);
        }

        var selected = tables[0];
        if (selected.ColumnCount != expectedColumnCount)
        {
            return new TableAnalysis(
                Accepted: false,
                ReasonCode: "column_count_mismatch",
                ReasonDetail: $"Expected {expectedColumnCount} columns, found {selected.ColumnCount} in the first markdown table.",
                MatchedTableCount: tables.Count,
                SelectedLocation: selected,
                DetectedColumnCount: selected.ColumnCount);
        }

        return new TableAnalysis(
            Accepted: true,
            ReasonCode: "ok",
            ReasonDetail: "Accepted.",
            MatchedTableCount: tables.Count,
            SelectedLocation: selected,
            DetectedColumnCount: selected.ColumnCount);
    }

    private static TableLocation? LocateMarkdownTable(string[] lines, string tableTitle)
    {
        var tables = ParseMarkdownTablesWithLineNumbers(lines);
        if (tables.Count == 0)
            return null;

        return tables[0];
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

    private static object? ConvertCellValue(string? raw, TypeAffinity affinity)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        return affinity switch
        {
            TypeAffinity.Integer => long.Parse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture),
            TypeAffinity.Real => double.Parse(raw, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture),
            _ => raw
        };
    }

    private static List<string> ResolveGlobMatches(string globPattern)
    {
        var expanded = Environment.ExpandEnvironmentVariables(globPattern);
        if (!HasWildcards(expanded))
        {
            var fullPath = Path.GetFullPath(expanded);
            if (Directory.Exists(fullPath))
            {
                return Directory
                    .EnumerateFiles(fullPath, "*.md", SearchOption.AllDirectories)
                    .Select(Path.GetFullPath)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            return File.Exists(fullPath) ? new List<string> { fullPath } : new List<string>();
        }

        var fullPattern = Path.GetFullPath(expanded);
        var baseDirectory = GetGlobBaseDirectory(fullPattern);
        if (!Directory.Exists(baseDirectory))
            return new List<string>();

        var relativePattern = Path.GetRelativePath(baseDirectory, fullPattern).Replace('\\', '/');
        var patternRegex = new Regex("^" + GlobToRegex(relativePattern) + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        return Directory
            .EnumerateFiles(baseDirectory, "*", SearchOption.AllDirectories)
            .Select(Path.GetFullPath)
            .Where(path => patternRegex.IsMatch(Path.GetRelativePath(baseDirectory, path).Replace('\\', '/')))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool HasWildcards(string path)
    {
        return path.IndexOf('*') >= 0 || path.IndexOf('?') >= 0;
    }

    private static string GetGlobBaseDirectory(string fullPattern)
    {
        int wildcardIndex = fullPattern.IndexOfAny(new[] { '*', '?' });
        if (wildcardIndex < 0)
            return Path.GetDirectoryName(fullPattern) ?? Directory.GetCurrentDirectory();

        int separatorIndex = fullPattern.LastIndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, wildcardIndex);
        if (separatorIndex < 0)
            return Directory.GetCurrentDirectory();

        var baseDirectory = fullPattern[..separatorIndex];
        if (string.IsNullOrWhiteSpace(baseDirectory))
            return Path.GetPathRoot(fullPattern) ?? Directory.GetCurrentDirectory();

        return Path.GetFullPath(baseDirectory);
    }

    private static string GlobToRegex(string globPattern)
    {
        var normalized = globPattern.Replace('\\', '/');
        var regex = new StringBuilder();

        for (int i = 0; i < normalized.Length; i++)
        {
            char ch = normalized[i];
            if (ch == '*')
            {
                bool isDoubleStar = i + 1 < normalized.Length && normalized[i + 1] == '*';
                if (isDoubleStar)
                {
                    bool followedBySlash = i + 2 < normalized.Length && normalized[i + 2] == '/';
                    regex.Append(followedBySlash ? "(?:.*/)?" : ".*");
                    i += followedBySlash ? 2 : 1;
                    continue;
                }

                regex.Append("[^/]*");
                continue;
            }

            if (ch == '?')
            {
                regex.Append("[^/]");
                continue;
            }

            regex.Append(Regex.Escape(ch.ToString()));
        }

        return regex.ToString();
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

    private enum WriteMode
    {
        ReadWrite,
        ReadOnly,
        AppendOnly
    }

    private readonly record struct WriteCapabilities(
        bool CanInsert,
        bool CanUpdate,
        bool CanDelete,
        bool CreateOnWrite);

    private sealed record Row(long RowId, string?[] Cells);

    private sealed record RepoRow(long RowId, string?[] Cells, string Path, string? Heading, long MTimeUnix, int TableIndex);

    private sealed record TableAnalysis(
        bool Accepted,
        string ReasonCode,
        string? ReasonDetail,
        int MatchedTableCount,
        TableLocation? SelectedLocation,
        int? DetectedColumnCount)
    {
        public int? SelectedTableIndex => SelectedLocation == null ? null : 0;
    }

    private readonly record struct TableLocation(
        string? Heading,
        int HeaderLineIndex,
        int ColumnCount,
        IReadOnlyList<int> DataRowLineIndices,
        int AfterTableLineIndex);
}
