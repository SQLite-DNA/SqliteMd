Set-Location $PSScriptRoot

sqlitemd targets list
sqlitemd show notes
sqlitemd append intake --set id=1 --set item="Ship docs refresh" --set owner=govert
sqlitemd diagnose tasks --json
