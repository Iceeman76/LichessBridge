# LichessBridge

LichessBridge connects a WinBoard/2 chess engine to a Lichess Bot account. The MVP accepts one casual standard blitz or rapid game at a time.

## Configuration

1. Copy `Bridge/appsettings.Local.example.json` to `Bridge/appsettings.Local.json` and set the path to `alarm.exe`.
2. Set a Lichess personal access token with the `bot:play` scope in the `LICHESS_TOKEN` environment variable. A token may instead be placed in `appsettings.Local.json`, which is ignored by Git.
3. The Lichess account must already have been upgraded to a Bot account.

PowerShell example:

```powershell
$env:LICHESS_TOKEN = "your-token"
dotnet run --project Bridge
```

Completed games are saved as PGN files in the configured `Games:PgnDirectory` directory.
