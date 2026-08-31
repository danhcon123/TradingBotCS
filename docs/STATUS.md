# Current Status

Last updated after: building `LiveSignalRunner` (continuous live signal streaming) and
refactoring `Program.cs` into a thin, three-mode entry point. See `PROJECT_OVERVIEW.md` for
architecture, `INSTRUCTIONS.md` for build/run/test commands, and
`features/live-signal-streaming.md` for the full write-up of everything in this section.

## What was just built: live signal streaming

`TradingBot.App/Live/LiveSignalRunner.cs` runs the existing (unchanged) `DragonflyStrategy`
continuously against live FXCM data — one full loop of
`FXCM → closed M1 candle → strategy → NONE/BUY → continue`, on a UTC-minute schedule, with
error/no-data backoff, until Ctrl+C. Nothing in the strategy, `Backtester`, or the H1/M1
rules was touched to build this.

Three real bugs were found and fixed during review (not just at first-draft time):
1. A long FXCM outage could silently lose candles — the fetch window now widens back to
   `lastProcessedM1Utc` after any gap, instead of a fixed 5-minute lookback that could skip
   over an extended outage.
2. A partially-failed H1 startup backfill was silently accepted as sufficient — it now
   fails startup and retries until the reference history is genuinely complete.
3. The very first data seen after starting with no baseline (e.g. bot started while the
   market was closed) could evaluate a whole backlog of candles as live signals instead of
   just marking a baseline — fixed to always establish-only, never replay, exactly like the
   normal startup path.

`Program.cs` was also refactored from "always backfill 3 months, then backtest, then go
live" into a thin entry point with three explicit modes (`dotnet run`,
`dotnet run -- --historical`, `dotnet run -- --historical-then-live`), each opening its own
database (`data/candles.db` for live, `data/candles-3months.db` for historical — never
mixed). The old inline historical/backtest code moved, unchanged, into
`TradingBot.App/Historical/HistoricalWorkflowRunner.cs`.

**Open issue:** Ctrl+C during live mode still doesn't reliably return control to the shell
on its own. A 15-second watchdog now force-terminates the process regardless, so the
process is guaranteed to exit, but the actual stall hasn't been root-caused yet (no live
FXCM session available to debug it further in this environment). See
`features/live-signal-streaming.md` for the full investigation log.

**201/201 tests passing**, `dotnet build TradingBotCS.sln -c Debug -p:Platform=x64` → 3
succeeded, 0 warnings, 0 errors.

## What works right now

- **Full strategy layer** (H1 reference levels, Dragonfly detection, lower-wick
  intersection, blocked-hours filter, orchestration) is complete and unit/integration
  tested.
- **SQLite storage** (`CandleRepository`) and a **resumable, retrying importer**
  (`FXCMHistoricalImporter`) are complete and tested, including the fix for a real bug
  (a failed middle chunk being silently un-retryable once a later chunk succeeds).
- **`Backtester`** replays stored M1 candles through `DragonflyStrategy` offline (no FXCM
  connection needed) and is wired into `Program.cs`, which now runs it *before* attempting
  any live connection.
- **184/184 tests passing.** Full solution builds clean via
  `dotnet build TradingBotCS.sln -c Debug -p:Platform=x64`.
- No order execution exists anywhere in this codebase.

## What's actually in `data/candles.db` right now

| Timeframe | Range | Candle count |
|---|---|---|
| M1 | 2026-08-28 19:12 → 20:43 UTC | 92 |
| H1 | 2026-07-16 19:00 → 2026-08-28 20:00 UTC | 715 |

**This is not enough data to draw any conclusion about the strategy.** The M1 window is
only ~1.5 hours because `M1LookbackWindow` in `Program.cs` is still set to a conservative
`2 days`, which landed almost entirely on a weekend market closure. Backtesting this data
correctly produces **0 Dragonflies detected** in both configuration variants — that is the
backtester working correctly on a too-small sample, not a sign the strategy doesn't work.

## Immediate next decision (paused, awaiting go-ahead)

Widen `M1LookbackWindow`/`H1LookbackWindow` in `Program.cs` and re-run the backfill so
there's enough real trading-day data to actually evaluate Dragonfly frequency and signal
quality. Two options were discussed and are still open:

- Full 6-month target (`180`/`240` days) — matches the original plan, but is a genuinely
  multi-hour live run (`FXCMPriceHistory.GetCandles` caps at 100 bars/request, so this is
  many thousands of chunked requests).
- Intermediate 14-day step first (`14`/`60` days) — crosses two full trading weeks, gives a
  first real read in well under an hour, before committing to the full pull.

The importer is resumable, so whichever is chosen, re-running never re-downloads what's
already stored - it only fetches what's missing.

## Recently fixed

- `RunIntegrityCheck` in `Program.cs` used to scan a now-relative rolling window
  (`now - lookback`), which drifts forward on every run and could silently stop covering
  older data that was still validly stored - it once under-reported the actual M1 candle
  count (46 vs. the real 92) for exactly this reason. It now always scans the full stored
  range via `CandleRepository.GetEarliestTimestampUtc`/`GetLatestTimestampUtc`, matching
  what `Backtester` already did, so the two can never disagree again.
- `data/candles.db` was moved from inside `TradingBot.App/bin/...` (would be silently
  wiped by `dotnet clean`) to `<solution root>/data/candles.db`, and is intentionally
  **tracked in git** (not ignored) for now, so the backfilled history is available for
  backtesting from any clone without re-hitting the live FXCM API. Revisit this as the
  file grows.
