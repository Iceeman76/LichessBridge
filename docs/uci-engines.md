# UCI engines

Select `Engine:Protocol = Uci` and configure `Engine:Path` in `appsettings.Local.json`. The shared `Bridge/appsettings.json` retains `WinBoard` as the default. Both protocols implement `IChessEngine` and use the same process transport and per-move timing. No `InternalsVisibleTo` or direct tests of internal parsers are used.

## Configuration

```json
{
  "Engine": {
    "Protocol": "Uci",
    "Path": "C:\\ChessEngines\\stockfish.exe",
    "Name": "Stockfish",
    "Ponder": true,
    "Uci": {
      "ResponseTimeoutSeconds": 10,
      "Options": {
        "Threads": "4",
        "Hash": "256",
        "MultiPV": "1",
        "SyzygyPath": "D:\\Tablebases\\Syzygy"
      }
    }
  }
}
```

These example options are supported by Stockfish; another engine may advertise different names or limits. Bridge reads the engine's `option` declarations during the `uci` handshake. It validates check, spin, combo, string, and button options before sending `setoption`. Buttons require an empty value. Invalid option values and unsupported names fail startup before connecting to Lichess. Option names may contain spaces.

`Engine:Ponder` controls the engine's `Ponder` option when it is advertised. An engine without that option can still play normally. Engines without a predicted reply use background analysis when pondering is enabled. `StartupCommands` is specific to WinBoard and must be empty for UCI; use `Uci:Options` instead. `SearchStatusPollMilliseconds` has no effect on UCI.

## Lifecycle

Startup sends `uci`, consumes `id` and `option` declarations until `uciok`, applies the configured options, then waits for `readyok` after `isready`. The first position of the game sends `ucinewgame` followed by `isready`. Reconnected snapshots within the same engine instance do not clear its hash with another `ucinewgame`.

Normal searches send the full history in `position startpos moves ...`, then `go wtime ... btime ... winc ... binc ...`. Clock and increment values are milliseconds. The configured bridge overhead is subtracted only from our side's clock. Full history preserves repetition information inside the engine; it is not converted to SAN by Bridge.

`bestmove` is read and timestamped by the transport before logging and callbacks. The returned coordinate move goes directly back to `GameController`; no `hint`, status poll, or other post-response motor command delays the HTTP submission. `bestmove 0000`, `(none)`, SAN, and malformed moves cannot be submitted as playable moves and raise an error. `MoveTiming` includes the UCI command measurements, stdout timestamp, `EngineMoveFormat=uci`, and `MoveConversionMs`.

The ordinary search deadline follows the bridge's existing safety reserve. If reached, Bridge sends `stop` and allows a 750 ms emergency grace for `bestmove`. `Uci:ResponseTimeoutSeconds` controls startup/readiness and draining stopped searches, not ordinary thinking time. An unresponsive stopped search makes the engine unusable, so it must be restarted before another search.

## Pondering and analysis

Pondering does not start before returning the move for HTTP submission. When a subsequent server update confirms our move, Bridge uses its reported clocks to start work during the opponent's turn:

- If `bestmove` supplied a `ponder` move, Bridge sends a position including that predicted opponent move and starts `go ponder` with clocks and increments.
- If the opponent plays the predicted move, Bridge sends `ponderhit` and consumes that search's result.
- On a mismatch, Bridge sends `stop`, consumes and discards the old `bestmove`, then sends the actual position and starts a new search.
- Without a prediction, Bridge can run `go infinite` on the current position. It stops and drains that analysis before requesting a playable response.

Cancelled searches are also stopped and drained before reuse. `isready` is not used as a substitute for the terminating `bestmove`. On game completion, background work is stopped; UCI has no standard `result` command. This adapter's `SetRatingsAsync` has no effect because the existing interface supplies only two Elo values, not the full opponent metadata required by the optional `UCI_Opponent` setting.

## Analysis and status

`info` lines update depth, centipawn or mate score, elapsed time, nodes, principal variation, current move, move number, hash fullness, and tablebase hits when present. Partial updates retain earlier fields within a search; new searches clear those metrics. Secondary MultiPV lines and `info string` do not replace the main analysis. PV and hint moves remain in coordinate notation; displaying them does not replay history for SAN conversion. UCI does not report the same total/remaining-move count as WinBoard, so the dashboard omits that fraction when it is unavailable.

The console's Search row automatically includes, for example, `Hash 37.5% • TB 1 234`. UCI `hashfull` is reported in permille, so 375 becomes 37.5%. This is the engine's estimate of hash occupancy. `TB` is the engine-reported tablebase-hit count for the current search, not an accumulated total for the game. Values that have not been reported are omitted; an explicitly reported zero is shown. No additional `appsettings.json` setting or polling command is required. WinBoard's standard `stat01` output does not supply these metrics.

Enable Debug for `DotNetLab.LichessBridge.Engine.UciEngine` to see `UCI >` / `UCI <` protocol lines and detailed transport timing. The regular Information summary remains available with Debug disabled. For the timing boundaries see [move timing](move-timing.md).

## Local verification

```powershell
dotnet build LichessBridge.slnx --no-restore
dotnet test LichessBridge.Tests/LichessBridge.Tests.csproj --no-restore --logger "trx;LogFileName=uci-verification.trx"
```

The local test engine supports scripted UCI options, normal and ponder searches, delayed stop replies, malformed moves, and missing acknowledgements. Tests use the public factory, `IChessEngine`, dependency injection, and a complete `GameController` flow with simulated HTTP. No live Lichess game or external network connection is needed.

Verified locally: all 204 NUnit tests passed. The complete solution also built in Release with zero warnings and errors. The test report is `LichessBridge.Tests/TestResults/uci-verification.trx`.

Protocol references: [UCI specification](https://backscattering.de/chess/uci/), [Stockfish UCI commands and options](https://official-stockfish.github.io/docs/stockfish-wiki/UCI-Protocol-and-Stockfish-Commands.html).
