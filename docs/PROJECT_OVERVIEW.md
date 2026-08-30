# TradingBotCS — Project Overview

A C#/.NET Framework 4.8 automated trading bot for XAU/USD, built on the FXCM ForexConnect
API. The strategy layer is complete and fully tested; historical data now persists to
SQLite via a resumable importer. No order execution exists yet.

## What the strategy does

On every completed M1 (1-minute) candle:

1. **Detect a Dragonfly Doji** on the M1 candle, using configurable geometry rules (not
   just `Open == Close`): a long lower wick relative to the body, a small body relative to
   the candle's range, little-to-no upper wick, and (optionally) a range comparison against
   the previous M1 candle.
2. If it's a Dragonfly, **look up 28 H1 reference levels** — the High, Midpoint, and Low of
   the 28 most recent *completed* H1 candles whose open time is 00:00 in a **fixed UTC-4**
   timezone (no DST), i.e. 04:00 UTC. Look-ahead bias is prevented throughout: only candles
   that had actually completed as of the evaluation instant are ever used.
3. Check whether the Dragonfly's **lower wick** (not the whole candle) intersects any of
   those 84 possible levels.
4. Apply a **blocked-hours filter** (12:00–15:59 UTC-4, optional).
5. If all of the above pass, emit a `SignalType.Buy`; otherwise `SignalType.None`. Every
   step's outcome is preserved in a rich `StrategyResult` for diagnostics — nothing is
   collapsed down to a bare boolean.

Two independently valuable behaviors are configuration, not hardcoded: the previous-M1
range comparison can be switched on/off (Variant A/B) without touching any code, and every
numeric threshold lives in `DragonflyStrategySettings`.

## Solution structure

```
TradingBotCS.sln
├── TradingBot.App/                  Console entry point (SDK-style, net48, x64)
│   └── Program.cs                   Currently: FXCM connect -> SQLite backfill -> integrity check
│
├── TradingBot.FXCM/                 Core library (SDK-style, net48, x64)
│   ├── FXCMConnection.cs            Session login (existing infra - not modified)
│   ├── FXCMPriceHistory.cs          GetCandles(instrument, timeframe, from, to) - caps at 100 bars/request
│   ├── FXCMMarketData.cs, FXCMChartData.cs, *Listener.cs   Existing FXCM infra
│   │
│   ├── Models/
│   │   ├── Candle.cs, Tick.cs                 Existing FXCM data models
│   │   ├── PriceLevelType.cs, PriceLevel.cs   One H1 High/Mid/Low level
│   │   ├── H1ReferenceSet.cs                  One H1 candle -> its 3 levels
│   │   ├── DragonflyAnalysis.cs               Full Dragonfly geometry diagnostic
│   │   ├── LevelIntersection.cs               One matched H1 level + the wick it matched
│   │   ├── SignalType.cs, StrategyResult.cs   Final per-candle verdict + full diagnostics
│   │   └── ImportResult.cs, ImportChunkFailure.cs   Historical-download diagnostics
│   │
│   ├── Settings/
│   │   └── DragonflyStrategySettings.cs   All thresholds + Validate() + copy constructor
│   │
│   ├── Strategy/
│   │   ├── StrategyUtcContract.cs      Shared "what counts as UTC" contract (internal)
│   │   ├── StrategyTime.cs             Shared UTC-4 conversion (internal)
│   │   ├── H1LevelCalculator.cs        28 qualifying H1 reference sets, no look-ahead
│   │   ├── DragonflyDojiDetector.cs    Candle geometry -> DragonflyAnalysis
│   │   ├── LevelIntersectionDetector.cs Lower-wick vs H1 levels -> LevelIntersection list
│   │   ├── StrategyTimeFilter.cs       12:00-15:59 UTC-4 blocked-hours check
│   │   └── DragonflyStrategy.cs        Orchestrates all of the above -> StrategyResult
│   │
│   ├── Data/
│   │   └── CandleRepository.cs         SQLite candle store, idempotent via unique index
│   │
│   └── Import/
│       └── FXCMHistoricalImporter.cs   Chunked, resumable, retrying historical downloader
│
└── TradingBot.Strategy.Tests/        xUnit test project (SDK-style, net48, x64)
    └── *Tests.cs                     One file per component above, 171 tests total
```

## Key design decisions (and why)

- **UTC contract, enforced centrally.** Every timestamp entering the strategy layer must
  represent UTC. `DateTimeKind.Utc` and `.Unspecified` are both trusted as UTC (FXCM
  candles arrive as `Unspecified`); `.Local` is rejected outright via
  `StrategyUtcContract.Ensure(...)`, since a Local-kind value signals an upstream
  conversion bug and its true UTC instant is now ambiguous.
- **Fixed UTC-4, never DST-aware.** The strategy's "local" timezone is a constant offset,
  not a real timezone — `StrategyTime.ToStrategyLocal` never applies daylight saving.
- **Two different timestamps per M1 candle, used for two different things.**
  `Candle.Time` is the candle's *open* time. H1 reference availability is judged as of the
  candle's *close* (`+1 minute`) — an H1 candle can only be "completed" once the M1 candle
  that shares its closing instant has also closed. The blocked-hours filter, by contrast,
  uses the candle's *open* time, matching the original Pine-script logic's bar timestamp.
  Mixing these two up shifts either the H1-availability boundary or the time-filter
  boundary by a minute — both are covered by dedicated regression tests.
- **Settings are validated once, then frozen.** `DragonflyStrategy`'s constructor validates
  the full settings object, then takes an internal copy via `DragonflyStrategySettings`'s
  copy constructor, so a caller mutating the original settings object after construction
  can never reach into an already-running strategy.
- **Every component validates only what it actually uses.** `StrategyTimeFilter` only reads
  a boolean flag, so it deliberately does *not* call full settings validation — that stays
  with the components (`DragonflyDojiDetector`, `DragonflyStrategy`) that actually consume
  the numeric thresholds.
- **Idempotent storage.** `CandleRepository`'s unique index on
  `(Instrument, Timeframe, TimestampUtc)` means `INSERT OR IGNORE` makes every save
  naturally deduplicated — this is what makes the importer safe to interrupt and re-run.
- **Resumable, not just retryable.** `FXCMHistoricalImporter.Import` resumes from the
  latest stored candle rather than restarting a whole range. A failed *middle* chunk
  followed by a successful *later* chunk is a real edge case that plain "resume from
  latest" cannot self-heal — `RetryFailedChunks` exists specifically to re-attempt exactly
  those known gaps.
- **Minimal-FXCM-dependency, fully testable classes.** Every strategy class and the
  importer depend on plain interfaces/delegates/models, never on live FXCM session
  objects — the entire strategy and import pipeline is unit-testable without ForexConnect
  running.

## Status (development order)

| Phase | What | Status |
|---|---|---|
| 1 | H1 reference levels (`H1LevelCalculator`) | Done |
| 2 | Dragonfly detection (`DragonflyDojiDetector`) | Done |
| 3 | Lower-wick intersection (`LevelIntersectionDetector`) | Done |
| 5 | Blocked-hours filter (`StrategyTimeFilter`) | Done |
| 6 | Orchestration + integration tests (`DragonflyStrategy`, `StrategyResult`) | Done |
| 7 | Real FXCM validation (small live pull, diagnostic printout) | Done |
| 7A | SQLite candle storage (`CandleRepository`) | Done |
| 7B | Resumable historical importer (`FXCMHistoricalImporter`) | Done |
| 7C | Wired into `Program.cs`; live backfill + integrity check run | Done |
| 8 | Manual chart validation of real signals | Not started |
| 9/10 | 6-month backfill, backtester, parameter comparisons | Not started |

No order execution exists anywhere in this codebase yet.
