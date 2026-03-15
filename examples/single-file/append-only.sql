.load ./SqliteMd.dll

CREATE VIRTUAL TABLE status_items
USING markdown_table_mode(
  'examples/single-file/append-target.md',
  'Weekly Status',
  'id INTEGER, item TEXT, owner TEXT',
  'id',
  'append_only'
);

INSERT INTO status_items(id, item, owner)
VALUES
  (1, 'Ship SqliteMd examples', 'govert'),
  (2, 'Review diagnostics output', 'team');

SELECT id, item, owner
FROM status_items
ORDER BY id;
