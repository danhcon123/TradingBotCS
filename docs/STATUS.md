# Current Status

Last updated after: building and wiring the backtester, fixing an integrity-check
reporting bug. See `PROJECT_OVERVIEW.md` for architecture and `INSTRUCTIONS.md` for
build/run/test commands.

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
