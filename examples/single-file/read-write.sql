.load ./SqliteMd.dll

CREATE VIRTUAL TABLE notes
USING markdown_table(
  'examples/single-file/release-notes.md',
  'Release Notes',
  'id INTEGER, title TEXT, stars INTEGER',
  'id'
);

SELECT id, title, stars
FROM notes
ORDER BY id;

UPDATE notes
SET stars = 10
WHERE id = 2;

INSERT INTO notes(id, title, stars)
VALUES (3, 'Examples folder', 9);
