.load ./SqliteMd.dll

CREATE VIRTUAL TABLE notes
USING markdown_table_mode(
  'examples/single-file/release-notes.md',
  'Release Notes',
  'id INTEGER, title TEXT, stars INTEGER',
  'id',
  'read_only'
);

SELECT id, title, stars
FROM notes
ORDER BY id;

-- This would fail because the table is read_only:
-- INSERT INTO notes(id, title, stars) VALUES (3, 'Blocked write', 1);
