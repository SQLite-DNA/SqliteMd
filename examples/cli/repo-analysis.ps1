Set-Location $PSScriptRoot

sqlitemd diagnose tasks
sqlitemd sql tasks --query "SELECT owner, COUNT(*) AS open_items FROM tasks WHERE status <> 'done' GROUP BY owner ORDER BY open_items DESC" --json
