# Feature: Live Signal Streaming (`LiveSignalRunner`)

See `PROJECT_OVERVIEW.md` for the strategy itself (unchanged by this feature) and
`STATUS.md` for the current overall project state. This document is the deep dive on one
feature: turning the existing, already-tested `DragonflyStrategy` from an offline
backtest-only tool into a process that runs continuously against live FXCM data.

No strategy rule, threshold, or the `DragonflyStrategy`/`Backtester` classes themselves
were changed to build this. Telegram, chart generation, and automatic order execution are
all still explicitly out of scope.

## The flow

```
FXCM (live)
   |
   |  small recent-window request (~5 min lookback, wider after a gap - see Bug 1 below)
   v
[filter: candle.Time < currentMinuteStartUtc]   <- only CLOSED candles pass, no exceptions
   |
[filter: candle.Time > lastProcessedM1Utc]      <- de-dup, ascending order
   |
   v
CandleRepository.SaveCandles                     (persist the M1 candle)
   |
   v
DragonflyStrategy.Evaluate(candle, previous, h1Candles)
   |                                              (same class Backtester uses - untouched)
   v
 NONE ---> log "Result: NONE", continue
 BUY  ---> log the "LIVE BUY SIGNAL" block, continue
```

Every tick does exactly this, in this order:

1. Compute `currentMinuteStartUtc` — "now" floored down to the minute.
2. Fetch a small recent window from FXCM.
3. Keep only candles strictly before `currentMinuteStartUtc`.
4. Keep only candles after `lastProcessedM1Utc`, sorted ascending.
5. Process each survivor in order: save to SQLite, evaluate through the strategy, log the
   result, advance `lastProcessedM1Utc` to that candle's timestamp.
6. Sleep until the next UTC minute boundary (+ a small safety delay) and repeat — or back
   off, if the last tick errored or found nothing new (see "Two independent backoff
   tracks" below).

## Why "closed candle" is the one rule everything else is built around

The single most important invariant in this whole feature: **an M1 candle must never be
evaluated before it has actually closed.** FXCM's price-history API does not promise it
will only ever return finished candles — the currently-forming minute can come back in the
same response as everything else. So this is enforced as a hard filter on the *response*,
not as a property of the *request*: every fetch result is filtered by
`candle.Time < currentMinuteStartUtc` regardless of what window was asked for. Widening the
lookback window (normal operation, or the outage-recovery widening below) can never smuggle
an unclosed candle past this check, because the filter doesn't care what was requested —
only what time each returned candle actually claims to be.

## Three bugs found during review — and why each mattered

The first draft of `LiveSignalRunner` looked correct and passed its own tests, but a
review pass against realistic failure scenarios (not just happy-path data) turned up three
real bugs. All three are about the same underlying theme: **a "successful" tick that quietly
does less than it should is worse than an obviously failed one**, because nothing downstream
notices.

### Bug 1 — a long FXCM outage could silently lose candles

The original fetch always asked for a fixed window: `currentMinuteStartUtc - 5 minutes`.
That's fine for recovering a *short* delay (one slow request, one dropped tick), but if
FXCM/network was unavailable for, say, 12 minutes, the very next successful fetch would
still only ask for the last 5 — the candles from minutes 6–12 of the outage were gone for
good, with no error, no log, nothing to indicate a gap had occurred. That directly
contradicts the point of streaming signals: a missed candle is a missed potential BUY, and
it would have failed silently.

**Fix:** once a baseline exists, the fetch window is the *earlier* of the fixed 5-minute
lookback and `lastProcessedM1Utc - 1 minute`. In steady state this is identical to before
(the fixed window is always closer). After any gap wider than that, it automatically
widens back to exactly where processing left off, so recovery is complete rather than
partial. This is why `lastProcessedM1Utc` being persisted (planned, not yet built — see
"Known limitations" below) matters so much: right now a *process restart* still only
recovers from the newest closed candle, by design (see Bug 3) — only a live, in-memory gap
during a single run is auto-recovered by this fix.

### Bug 2 — a partially failed H1 backfill was silently accepted as "good enough"

Startup ensures 40 days of H1 history exists via the same resumable `FXCMHistoricalImporter`
already used for the historical workflow. `Import()` doesn't throw when a chunk fails — it
records it in `ImportResult.FailedChunks` and moves on, which is exactly right for a
one-off historical backfill (partial progress is still progress, and it's retried later).
But `LiveSignalRunner`'s original startup code just logged
`"X chunk(s) failed"` and proceeded straight into live mode anyway — meaning a network
blip during startup could leave the strategy running against an *incomplete* 28-candle H1
reference set for hours, silently producing fewer (or zero) intersections than it should,
with no signal that anything was ever wrong.

**Fix:** startup now treats `ChunksFailed > 0` as a failure, throws, and lets the existing
retry/backoff loop try again — `LiveSignalRunner` will not proceed into live processing
until the H1 history it needs is genuinely complete.

### Bug 3 — first-ever data after "no baseline" processed a backlog instead of just marking a baseline

Startup normally finds the newest already-closed M1 candle and uses it purely as a
reference point — it is deliberately never evaluated itself, so starting the bot doesn't
retroactively fire signals for old candles. But if the market happened to be closed during
that very first lookback window (e.g. starting the bot on a weekend), no baseline could be
found at startup, and the *first* tick after data appeared would fall through to
"no baseline yet → treat everything returned as new" — meaning however many candles came
back in that first live response (e.g. 5 candles right at market open) would *all* be
evaluated as live signals, not just the newest one.

**Fix:** the "no baseline yet" case is now handled explicitly and identically everywhere it
can occur: the newest closed candle in the response becomes the baseline, and the tick
returns having evaluated *nothing* — exactly the same non-replaying behavior as the normal
startup path.

## What's interesting: two independent backoff tracks

A failed request and a successful-but-empty request are treated as different situations on
purpose, because they mean different things and call for different responses:

| Outcome | Meaning | Response |
|---|---|---|
| `Error` | the fetch (or a downstream step) threw | error backoff: 5s → 15s → 30s → 60s, capped, resets to 5s the instant a request succeeds |
| `NoNewCandle` | the fetch succeeded, nothing newer than `lastProcessedM1Utc` | after 3 consecutive occurrences, no-data backoff: 1m → 2m → 5m, capped — likely a closed market or broker maintenance, not a problem to hammer |
| `Processed` | at least one candle evaluated | both counters reset; next tick goes right back to strict one-minute scheduling |

Conflating these would either retry a genuine market closure every second forever (wasteful,
looks like a bug in the logs), or treat an actual connectivity problem as "market's probably
just closed" and wait five minutes before trying again (slow to recover from a real outage).

## What's interesting: H1 caching without ever risking look-ahead

The strategy needs 28 completed H1 reference candles per evaluation, but re-downloading
that whole window from FXCM every single minute would be wasteful and pointless — new H1
data only exists once per hour. So:

- **Startup:** ensure ~40 days of H1 history exists via the *same* resumable importer the
  historical workflow uses (no duplicate download/chunking logic — see Bug 2 above for why
  this must succeed, not just attempt).
- **Live refresh:** only re-fetch from FXCM once a new H1 *hour boundary* has actually
  passed — never on every M1 tick.
- **Per-candle read:** every M1 evaluation reads H1 candles fresh from the local SQLite
  repository (cheap), never from an in-memory cache — so a stale in-memory copy can never
  be the reason a signal is wrong.

Look-ahead safety doesn't depend on any of this being timed perfectly, though — it comes
from a property `H1LevelCalculator` (unchanged) already guarantees: it only ever uses H1
candles that had fully completed as of the specific M1 candle's own evaluation instant,
regardless of what else happens to be sitting in the cache or repository at the time. A
slow H1 refresh can only ever produce a *smaller* reference set (or zero signal), never a
look-ahead one.

## App modes (added alongside the `Program.cs` refactor)

`Program.cs` is a thin entry point that picks one of three modes and never runs the
historical workflow automatically before live mode (it used to):

| Command | Database | What runs |
|---|---|---|
| `dotnet run` | `data/candles.db` | `LiveSignalRunner` only, until Ctrl+C |
| `dotnet run -- --historical` | `data/candles-3months.db` | the old backfill+backtest workflow, then exit |
| `dotnet run -- --historical-then-live` | historical DB first, then a **separate** `CandleRepository` on the live DB | historical workflow, then `LiveSignalRunner` |

The two databases are never shared or cross-contaminated — live mode always opens
`candles.db`, historical mode always opens `candles-3months.db`, and the combined mode
explicitly constructs two independent `CandleRepository` instances rather than reusing one.

## Ctrl+C shutdown — status: mitigated, root cause not yet confirmed

**Symptom:** pressing Ctrl+C correctly cancels `LiveSignalRunner` (its own
`"[LIVE] Cancellation requested - live signal loop stopped."` log line prints), but the
process then does not return control to the shell — it just hangs.

**Working hypothesis:** the ForexConnect SDK's session/table-manager machinery is known to
start internal worker threads that the managed API surface (`logout()`, `Dispose()`) gives
no way to explicitly join. Even a fully successful, clean shutdown sequence in our own code
can leave the CLR process alive if one of those threads isn't a background thread.

**What's been done about it, in order:**
1. Fixed a real bug in `FXCMConnection.IsConnected` (declared, but never actually set) so
   `Disconnect()` can correctly tell whether a real logout is needed at all.
2. Reworked `Disconnect()` to log every step (`logout()` → wait for confirmation, capped at
   5s, was previously reusing the 30s *login* timeout → `unsubscribeSessionStatus` →
   `Dispose()`), so a hang has visible before/after log lines pinpointing exactly where it
   happened.
3. Added the same bracketing logs around `await runner.RunAsync(...)` and around
   `fxcm.Disconnect()` in `Program.cs`.
4. **A live test after steps 1–3 still hung, with none of the new log lines appearing at
   all** — meaning whatever is stalling is upstream of `Disconnect()` even being reached,
   which the hypothesis in point 1 doesn't fully explain on its own. This is genuinely
   unresolved without a live FXCM session to attach a debugger to, which isn't available in
   the environment this code was written in.
5. **Guardrail added regardless of root cause:** the Ctrl+C handler now arms a 15-second
   watchdog (`Task.Delay` → `Environment.Exit(1)`) the moment Ctrl+C is pressed. If the
   normal clean-shutdown pipeline hasn't finished within 15 seconds for *any* reason, the
   watchdog force-terminates the process. This guarantees "the process must actually exit"
   holds unconditionally, independent of ever fully diagnosing the stall.

**Next diagnostic step:** on the next live run, whichever bracketing log line fails to
print its "returned"/"completed" half tells us exactly which await is the one not
resuming — that's the next thing to chase if the 15s watchdog is still the thing actually
ending the process rather than the clean path finishing on its own.

## Known limitations / next phase

- `lastProcessedM1Utc` and `previousM1Candle` are **in-memory only** for this version — a
  process restart re-establishes a fresh baseline from the newest closed candle (safe: it
  never replays history) but forgets exactly where it left off. Moving this to SQLite is
  the deliberately-deferred next phase.
- Telegram alerting, chart generation, and automatic order placement are all still
  unimplemented by design — this feature is scoped to
  `FXCM → closed M1 → existing strategy → NONE/BUY → continue`, nothing more.
- The Ctrl+C shutdown stall is mitigated (guaranteed termination within 15s) but not yet
  root-caused — see above.
