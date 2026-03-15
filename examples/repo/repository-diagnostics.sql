.load ./SqliteMd.dll

CREATE VIRTUAL TABLE diag
USING markdown_glob_diagnostics(
  'examples/repo/docs/**/*.md',
  'id INTEGER, title TEXT, owner TEXT, status TEXT'
);

SELECT path, accepted, reason_code, reason_detail, detected_column_count
FROM diag
ORDER BY path;
