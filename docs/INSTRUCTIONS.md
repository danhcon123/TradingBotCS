# Instructions

Practical how-to for building, testing, and running TradingBotCS. See
`PROJECT_OVERVIEW.md` for what the code actually does.

## Prerequisites

- .NET SDK (build/test were done against the `dotnet` CLI, SDK 9.0.x).
- ForexConnect API installed at `C:\Program Files\Candleworks\ForexConnectAPIx64\` (the
  `.csproj` files reference its DLLs via `HintPath`; both `TradingBot.FXCM` and
  `TradingBot.App` copy its native DLLs into their own build output automatically).
- Windows, x64. Every project is pinned to `<PlatformTarget>x64</PlatformTarget>` because
  the ForexConnect native libraries are x64-only.

## Build

```
dotnet build TradingBotCS.sln -c Debug -p:Platform=x64
```

All three projects (`TradingBot.FXCM`, `TradingBot.App`, `TradingBot.Strategy.Tests`) are
SDK-style `.csproj` files, so `dotnet build`/`dotnet restore` work directly — no Visual
Studio required, even though `TradingBot.FXCM` and `TradingBot.App` reference native FXCM
DLLs via `<Reference HintPath="...">`.

## Test

```
dotnet test TradingBot.Strategy.Tests/TradingBot.Strategy.Tests.csproj -c Debug -p:Platform=x64 -s TradingBot.Strategy.Tests/tests.runsettings
```

**The `-s ...tests.runsettings` flag is required**, not optional. Without it, every test
that touches `CandleRepository` or `FXCMHistoricalImporter` fails with
`Library e_sqlite3 not found`. This is a known VSTest quirk on .NET Framework: the test
host shadow-copies the test assembly into a temp folder but doesn't preserve the nested
`runtimes\win-x64\native\` subfolder the SQLite native library lives in.
`tests.runsettings` sets `DisableAppDomain: true`, which makes tests run directly from the
build output directory instead, where that folder actually exists.

As of this writing there are 171 tests, all passing.

## Run

```
dotnet build TradingBotCS.sln -c Debug -p:Platform=x64
"TradingBot.App/bin/x64/Debug/net48/TradingBot.App.exe"
```

**⚠️ `Program.cs` currently contains hardcoded live FXCM login credentials, and the
connection type is `"Real"` — a live account, not a demo.** Running the app connects to
that real account and pulls historical price data (read-only; no orders are ever placed
anywhere in this codebase). Treat running it with the same care as any other action that
touches a real external account.

### What running it does right now

`Program.cs`'s `Main` connects to FXCM, then calls `RunBackfillAndIntegrityCheck`, which:

1. Creates/opens a SQLite database at `TradingBotData/candles.db` next to the built exe.
2. Backfills XAU/USD `m1` and `H1` candles into it via `FXCMHistoricalImporter`, using the
   window sizes defined as constants near the top of `Program.cs`
   (`M1LookbackWindow`, `H1LookbackWindow` — currently 2 days / 45 days, a conservative
   validation-scale window, not the eventual 6-month target).
3. Explicitly retries any chunks that failed (up to 3 retry passes) — see
   *"Why the retry pass matters"* below.
4. Prints a per-timeframe integrity report: candle count, earliest/latest timestamp, and a
   gap classification (normal-interval / weekend-sized / unexpected — the last one printed
   individually, capped at 20, for manual review).

Because the importer is resumable, re-running the app after widening the lookback windows
does **not** re-download anything already stored — it continues from the latest stored
candle. A full 6-month M1 backfill is many thousands of chunked requests (the underlying
`FXCMPriceHistory.GetCandles` caps each response at 100 bars) and can take a long time —
widen the window constants deliberately when you're ready for that, rather than by
accident.

### Timeframe codes — easy to get backwards

ForexConnect's timeframe strings are case-sensitive and not what you'd guess:
- 1-minute candles: lowercase `"m1"`.
- 1-hour candles: uppercase `"H1"`.
- Uppercase `"M1"` means **Monthly**, not 1-minute.

`Program.cs` defines these as named constants (`M1Timeframe`, `H1Timeframe`) specifically
to avoid this trap — always go through those constants rather than typing the string
literal again.

### Why the retry pass matters

`FXCMHistoricalImporter.Import`'s resume logic looks at the *latest* stored timestamp to
decide where to continue from. If chunk 2 of 3 fails but chunk 3 still succeeds, the
latest stored timestamp ends up *past* chunk 2's gap — a second call to `Import` alone
would think that range is already fully covered and would never revisit it.
`RetryFailedChunks` exists specifically to re-attempt exactly the ranges recorded as
failed, bypassing the resume-skip logic entirely. `Program.cs` always calls it
automatically after the main `Import` pass — this isn't optional cleanup, it's required
for correctness whenever a failure happens to land in the middle of a range.

## Known limitations / things to keep in mind

- **No order execution anywhere.** Everything through the current phase is signal
  generation and data infrastructure only.
- **`FXCMPriceHistory.GetCandles` caps at 100 bars per request.** This is existing
  infrastructure and hasn't been modified — `FXCMHistoricalImporter` works around it from
  the caller side via chunked requests.
- **The gap-integrity check is a sanity pass, not a market-hours calendar.** It classifies
  gaps as normal-interval / roughly-weekend-sized (40–60h) / unexpected, using simple
  thresholds — it doesn't model actual trading-session hours. A recurring ~2h gap around
  20:00–22:00 UTC on many days is expected (FXCM's daily metals rollover halt), not a bug.
- **Credentials are hardcoded in `Program.cs`.** Fine for this stage of development; would
  need to move to configuration/secrets before this code is shared or goes further.
