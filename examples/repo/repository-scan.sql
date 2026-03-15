.load ./SqliteMd.dll

CREATE VIRTUAL TABLE tasks
USING markdown_repo(
  'examples/repo/docs/**/*.md',
  'id INTEGER, title TEXT, owner TEXT, status TEXT'
);

SELECT id, title, owner, status, _path, _heading
FROM tasks
ORDER BY _path, id;
