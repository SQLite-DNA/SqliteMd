Set-Location $PSScriptRoot

sqlitemd query `
  --kind table `
  --path ..\single-file\release-notes.md `
  --title "Release Notes" `
  --schema "id INTEGER, title TEXT, stars INTEGER" `
  --key id `
  --query "SELECT id, title, stars FROM source ORDER BY id" `
  --json
