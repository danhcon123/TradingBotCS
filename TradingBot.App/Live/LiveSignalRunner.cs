using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TradingBot.FXCM.Data;
using TradingBot.FXCM.Import;
using TradingBot.FXCM.Models;
using TradingBot.FXCM.Settings;
using TradingBot.FXCM.Strategy;

namespace TradingBot.App.Live
{
    public enum TickOutcome
    {
        /// <summary>The fetch succeeded and at least one newly-closed candle was evaluated.</summary>
        Processed,

        /// <summary>The fetch succeeded but returned nothing newer than the last processed candle.</summary>
        NoNewCandle,

        /// <summary>The fetch (or a downstream step) threw. Not a strategy verdict.</summary>
        Error
    }

    /// <summary>
    /// Result of one <see cref="LiveSignalRunner.ProcessTick"/> call. Kept separate from the
    /// strategy's own <see cref="StrategyResult"/> so "did the request succeed" and "what did
    /// the strategy decide" can be inspected independently.
    /// </summary>
    public class TickResult
    {
        public TickOutcome Outcome { get; set; }

        public IReadOnlyList<StrategyResult> ProcessedResults { get; set; } = new List<StrategyResult>();

        public string ErrorMessage { get; set; }
    }

    /// <summary>
    /// Drives the live loop: FXCM -&gt; closed M1 candle -&gt; existing <see cref="DragonflyStrategy"/>
    /// -&gt; NONE/BUY -&gt; continue.
    ///
    /// This class owns scheduling, closed-candle filtering, catch-up processing, in-process
    /// duplicate protection, and H1 caching/backoff. It never re-implements or tweaks a
    /// strategy rule - every decision is delegated to <see cref="DragonflyStrategy"/>, the
    /// same class <c>Backtester</c> uses, via <see cref="DragonflyStrategy.Evaluate"/>.
    /// </summary>
    public class LiveSignalRunner
    {
        private readonly Func<string, string, DateTime, DateTime, List<Candle>> _fetchCandles;
        private readonly CandleRepository _repository;
        private readonly DragonflyStrategy _strategy;
        private readonly FXCMHistoricalImporter _h1Importer;
        private readonly string _instrument;
        private readonly string _m1Timeframe;
        private readonly string _h1Timeframe;
        private readonly LiveSignalRunnerOptions _options;
        private readonly Action<string> _log;
        private readonly Func<DateTime> _utcNow;

        /// <summary>
        /// In-process only for this first version (Phase 6 of the spec this was built
        /// against) - deliberately not yet persisted to SQLite. A restart re-runs
        /// InitializeAsync and re-establishes a fresh baseline from the newest closed candle,
        /// which is safe (never replays history) but does forget exactly where it left off.
        /// Structured as a single nullable field so swapping this for repository-backed state
        /// later is a localized change.
        /// </summary>
        private DateTime? _lastProcessedM1Utc;

        /// <summary>
        /// The actual candle immediately preceding the next one to be evaluated - not
        /// necessarily at Time - 1 minute (weekends/gaps). Mirrors how Backtester supplies
        /// m1Candles[i-1] to DragonflyStrategy.
        /// </summary>
        private Candle _previousM1Candle;

        private DateTime? _lastH1RefreshHourUtc;

        public LiveSignalRunner(
            Func<string, string, DateTime, DateTime, List<Candle>> fetchCandles,
            CandleRepository repository,
            DragonflyStrategySettings strategySettings,
            string instrument,
            string m1Timeframe = "m1",
            string h1Timeframe = "H1",
            LiveSignalRunnerOptions options = null,
            Action<string> log = null,
            Func<DateTime> utcNowProvider = null)
        {
            if (fetchCandles == null)
            {
                throw new ArgumentNullException(nameof(fetchCandles));
            }

            if (repository == null)
            {
                throw new ArgumentNullException(nameof(repository));
            }

            if (strategySettings == null)
            {
                throw new ArgumentNullException(nameof(strategySettings));
            }

            if (string.IsNullOrWhiteSpace(instrument))
            {
                throw new ArgumentException("Instrument must not be null or empty.", nameof(instrument));
            }

            _fetchCandles = fetchCandles;
            _repository = repository;
            _strategy = new DragonflyStrategy(strategySettings);
            _instrument = instrument;
            _m1Timeframe = m1Timeframe;
            _h1Timeframe = h1Timeframe;
            _options = options ?? new LiveSignalRunnerOptions();
            _log = log ?? Console.WriteLine;
            _utcNow = utcNowProvider ?? (() => DateTime.UtcNow);

            // Reused as-is for the one-time startup H1 backfill below - the importer already
            // handles chunking (FXCM caps a response at 100 bars) and is resumable, so there
            // is no reason to re-implement any of that here.
            _h1Importer = new FXCMHistoricalImporter(_fetchCandles, _repository, requestDelay: _options.H1BackfillRequestDelay);
        }

        public DateTime? LastProcessedM1Utc => _lastProcessedM1Utc;

        // ------------------------------------------------------------------
        // Startup (Phase 7/9: sufficient H1 history + a non-replaying baseline)
        // ------------------------------------------------------------------

        /// <summary>
        /// Ensures sufficient H1 history exists, then establishes the live M1 baseline: the
        /// newest already-closed M1 candle becomes the starting point, so starting (or
        /// restarting) live mode never replays months of history as live signals. Retries
        /// transient failures with the same backoff table as the main loop rather than
        /// throwing - a slow/unavailable FXCM session at startup isn't fundamentally
        /// different from one that drops mid-run.
        /// </summary>
        public async Task InitializeAsync(CancellationToken cancellationToken)
        {
            await EnsureH1HistoryAsync(cancellationToken).ConfigureAwait(false);

            int attempt = 0;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    DateTime nowUtc = _utcNow();
                    DateTime currentMinuteStartUtc = FloorToMinute(nowUtc);
                    DateTime fromUtc = currentMinuteStartUtc - _options.M1LookbackWindow;

                    List<Candle> fetched = _fetchCandles(_instrument, _m1Timeframe, fromUtc, nowUtc) ?? new List<Candle>();

                    Candle baseline = fetched
                        .Where(c => c != null && c.Time < currentMinuteStartUtc)
                        .OrderByDescending(c => c.Time)
                        .FirstOrDefault();

                    if (baseline != null)
                    {
                        _lastProcessedM1Utc = baseline.Time;
                        _previousM1Candle = baseline;

                        _log(
                            $"[LIVE INIT] Baseline M1 candle: {baseline.Time:yyyy-MM-dd HH:mm} UTC. " +
                            "Live processing initialized - no historical candles will be replayed as live signals.");
                    }
                    else
                    {
                        _log(
                            "[LIVE INIT] No closed M1 candle found in the initial lookback window " +
                            "(market may be closed). Live processing initialized with no baseline - " +
                            "the next closed candle FXCM returns will be treated as the starting point.");
                    }

                    return;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    attempt++;
                    TimeSpan backoff = GetErrorBackoff(attempt);
                    _log($"[LIVE INIT] Startup M1 fetch failed ({ex.Message}). Retrying in {backoff.TotalSeconds:F0}s.");
                    await DelaySafe(backoff, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private async Task EnsureH1HistoryAsync(CancellationToken cancellationToken)
        {
            int attempt = 0;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    DateTime nowUtc = _utcNow();
                    DateTime toUtc = FloorToHour(nowUtc);
                    DateTime fromUtc = toUtc - _options.H1BackfillWindow;

                    ImportResult result = _h1Importer.Import(_instrument, _h1Timeframe, fromUtc, toUtc, _options.H1BackfillChunkSize);

                    // A partially-failed backfill must not be treated as "sufficient history
                    // exists" - the strategy needs a genuinely complete reference set, not an
                    // incomplete one that happens not to have thrown. Throwing here routes
                    // this through the same retry/backoff loop as any other startup failure.
                    // TODO: once this matters enough to optimize, retry only
                    // result.FailedChunks via _h1Importer.RetryFailedChunks(...) instead of
                    // re-running the whole Import() (which the resumable import already makes
                    // cheap for the already-succeeded range, but is still wasted work).
                    if (result.ChunksFailed > 0)
                    {
                        throw new InvalidOperationException(
                            $"H1 initialization incomplete: {result.ChunksFailed} chunk(s) failed to download.");
                    }

                    if (result.AlreadyUpToDate)
                    {
                        _log("[LIVE INIT] H1 history already sufficient - no backfill needed.");
                    }
                    else
                    {
                        _log($"[LIVE INIT] H1 backfill ensured: {result.CandlesWritten} candle(s) written/updated.");
                    }

                    _lastH1RefreshHourUtc = toUtc;
                    return;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    attempt++;
                    TimeSpan backoff = GetErrorBackoff(attempt);
                    _log($"[LIVE INIT] H1 backfill attempt failed ({ex.Message}). Retrying in {backoff.TotalSeconds:F0}s.");
                    await DelaySafe(backoff, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        // ------------------------------------------------------------------
        // Main loop
        // ------------------------------------------------------------------

        /// <summary>
        /// Runs until <paramref name="cancellationToken"/> is cancelled. Schedules around UTC
        /// minute boundaries during normal operation, backs off on errors, and backs off
        /// (separately) on repeated NO_NEW_CANDLE results - returning to normal one-minute
        /// scheduling as soon as a fresh candle appears again.
        /// </summary>
        public async Task RunAsync(CancellationToken cancellationToken)
        {
            await InitializeAsync(cancellationToken).ConfigureAwait(false);

            int consecutiveErrors = 0;
            int consecutiveNoNewCandle = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                TimeSpan delay;

                if (consecutiveErrors > 0)
                {
                    delay = GetErrorBackoff(consecutiveErrors);
                }
                else if (consecutiveNoNewCandle >= _options.NoDataBackoffThreshold)
                {
                    delay = GetNoDataBackoff(consecutiveNoNewCandle - _options.NoDataBackoffThreshold + 1);
                }
                else
                {
                    delay = ComputeDelayToNextBoundary(_utcNow());
                }

                bool waited = await DelaySafe(delay, cancellationToken).ConfigureAwait(false);
                if (!waited)
                {
                    break;
                }

                TickResult tick = ProcessTick(_utcNow());

                if (tick.Outcome == TickOutcome.Error)
                {
                    consecutiveErrors++;
                }
                else if (tick.Outcome == TickOutcome.NoNewCandle)
                {
                    consecutiveErrors = 0;
                    consecutiveNoNewCandle++;
                }
                else
                {
                    consecutiveErrors = 0;
                    consecutiveNoNewCandle = 0;
                }
            }

            _log("[LIVE] Cancellation requested - live signal loop stopped.");
        }

        // ------------------------------------------------------------------
        // Per-tick processing
        // ------------------------------------------------------------------

        /// <summary>
        /// Fetches a small recent window, filters to CLOSED candles (strictly before the
        /// current open minute), and evaluates every not-yet-processed candle in ascending
        /// order through the existing strategy. Never throws for a normal fetch/processing
        /// failure - callers see it via <see cref="TickResult.Outcome"/> instead, so one bad
        /// tick can never take down the loop. Public (and takes <paramref name="nowUtc"/>
        /// explicitly, rather than reading the clock itself) so tests can drive it
        /// deterministically without waiting on real time.
        /// </summary>
        public TickResult ProcessTick(DateTime nowUtc)
        {
            DateTime currentMinuteStartUtc = FloorToMinute(nowUtc);

            RefreshH1IfNeeded(currentMinuteStartUtc);

            List<Candle> fetched;

            try
            {
                DateTime normalLookbackFromUtc = currentMinuteStartUtc - _options.M1LookbackWindow;

                // After an outage longer than the normal lookback window, a fixed 5-minute
                // window would silently skip everything missed in between. Once a baseline
                // exists, always widen the request back to (at least) one minute before the
                // last processed candle, so a long gap is recovered rather than lost - the
                // fixed lookback then only matters for covering short/normal delays.
                DateTime fromUtc = normalLookbackFromUtc;

                if (_lastProcessedM1Utc.HasValue)
                {
                    DateTime recoveryFromUtc = _lastProcessedM1Utc.Value.AddMinutes(-1);
                    if (recoveryFromUtc < fromUtc)
                    {
                        fromUtc = recoveryFromUtc;
                    }
                }

                fetched = _fetchCandles(_instrument, _m1Timeframe, fromUtc, nowUtc) ?? new List<Candle>();
            }
            catch (Exception ex)
            {
                _log($"[LIVE] M1 fetch failed: {ex.Message}");
                return new TickResult { Outcome = TickOutcome.Error, ErrorMessage = ex.Message };
            }

            // Never evaluate a candle whose timestamp is not strictly before the current open
            // minute - regardless of what FXCM actually returned.
            List<Candle> closed = fetched
                .Where(c => c != null && c.Time < currentMinuteStartUtc)
                .OrderBy(c => c.Time)
                .ToList();

            if (!_lastProcessedM1Utc.HasValue)
            {
                // No baseline yet - e.g. Initialize's own lookback window found nothing
                // because the market was closed at startup. The first closed candle to
                // appear becomes the baseline, exactly like the Phase 7 startup baseline: it
                // must never itself be evaluated as a live signal, so this returns without
                // processing anything even though closed candles were found.
                Candle newestClosed = closed.Count > 0 ? closed[closed.Count - 1] : null;

                if (newestClosed == null)
                {
                    _log($"[{nowUtc:yyyy-MM-dd HH:mm:ss} UTC] NO_NEW_CANDLE. Last processed: none");
                    return new TickResult { Outcome = TickOutcome.NoNewCandle };
                }

                _lastProcessedM1Utc = newestClosed.Time;
                _previousM1Candle = newestClosed;

                _log(
                    $"[LIVE] Baseline established from first available closed candle: " +
                    $"{newestClosed.Time:yyyy-MM-dd HH:mm} UTC. No historical candles were evaluated.");

                return new TickResult { Outcome = TickOutcome.NoNewCandle };
            }

            List<Candle> unprocessed = closed.Where(c => c.Time > _lastProcessedM1Utc.Value).ToList();

            if (unprocessed.Count == 0)
            {
                _log(
                    $"[{nowUtc:yyyy-MM-dd HH:mm:ss} UTC] NO_NEW_CANDLE. Last processed: " +
                    $"{_lastProcessedM1Utc:yyyy-MM-dd HH:mm} UTC");

                return new TickResult { Outcome = TickOutcome.NoNewCandle };
            }

            var results = new List<StrategyResult>();

            foreach (Candle candle in unprocessed)
            {
                StrategyResult result;

                try
                {
                    result = ProcessClosedCandle(candle);
                }
                catch (Exception ex)
                {
                    // Only the candles fully processed before this failure advance
                    // _lastProcessedM1Utc, so the failed one (and anything still queued after
                    // it in this same catch-up batch) is retried on the next tick.
                    _log($"[LIVE] Failed to process candle {candle.Time:yyyy-MM-dd HH:mm} UTC: {ex.Message}");
                    return new TickResult { Outcome = TickOutcome.Error, ErrorMessage = ex.Message, ProcessedResults = results };
                }

                results.Add(result);
                _previousM1Candle = candle;
                _lastProcessedM1Utc = candle.Time;
            }

            return new TickResult { Outcome = TickOutcome.Processed, ProcessedResults = results };
        }

        private StrategyResult ProcessClosedCandle(Candle candle)
        {
            _repository.SaveCandles(_instrument, _m1Timeframe, new[] { candle });

            DateTime evaluationTimeUtc = candle.Time.AddMinutes(1);
            DateTime h1FromUtc = FloorToHour(evaluationTimeUtc) - _options.H1StrategyWindow;

            IReadOnlyList<Candle> h1Candles = _repository.GetCandles(
                _instrument, _h1Timeframe, h1FromUtc, evaluationTimeUtc.AddSeconds(1));

            StrategyResult result = _strategy.Evaluate(candle, _previousM1Candle, h1Candles);

            LogResult(result);

            return result;
        }

        /// <summary>
        /// Refreshes H1 data from FXCM only once per new H1 hour boundary - never every
        /// minute. A failed refresh must not abort M1 processing: the strategy simply keeps
        /// using whatever H1 data is already stored (H1LevelCalculator only ever uses
        /// completed, qualifying candles anyway, so slightly stale cached data cannot produce
        /// a look-ahead signal - only a temporarily smaller reference set).
        /// </summary>
        private void RefreshH1IfNeeded(DateTime currentMinuteStartUtc)
        {
            DateTime currentHourUtc = FloorToHour(currentMinuteStartUtc);

            if (_lastH1RefreshHourUtc.HasValue && currentHourUtc <= _lastH1RefreshHourUtc.Value)
            {
                return;
            }

            try
            {
                DateTime fromUtc = currentHourUtc - _options.H1RefreshLookback;
                List<Candle> fresh = _fetchCandles(_instrument, _h1Timeframe, fromUtc, currentMinuteStartUtc) ?? new List<Candle>();

                if (fresh.Count > 0)
                {
                    int written = _repository.SaveCandles(_instrument, _h1Timeframe, fresh);
                    _log($"[LIVE] H1 refresh: {written} candle(s) written/updated.");
                }

                _lastH1RefreshHourUtc = currentHourUtc;
            }
            catch (Exception ex)
            {
                // _lastH1RefreshHourUtc is deliberately left unadvanced on failure, so this
                // actually retries on the very next M1 tick (still within the same hour),
                // not "next new hour" as an earlier version of this message claimed.
                _log($"[LIVE] H1 refresh failed; will retry on the next live cycle: {ex.Message}");
            }
        }

        private void LogResult(StrategyResult result)
        {
            if (result.Signal == SignalType.Buy)
            {
                _log("====================================");
                _log("LIVE BUY SIGNAL");
                _log(_instrument);
                _log($"M1 UTC:    {result.CandleTimeUtc:yyyy-MM-dd HH:mm}");
                _log($"M1 UTC-4:  {result.CandleTimeUtcMinus4:yyyy-MM-dd HH:mm}");
                _log($"Close:     {result.CurrentCandle.Close}");

                if (result.PrimaryIntersection != null)
                {
                    _log($"Level type:  {result.PrimaryIntersection.Level.LevelType}");
                    _log($"Level price: {result.PrimaryIntersection.Level.Price}");
                }

                _log("====================================");
            }
            else
            {
                _log(
                    $"[{result.EvaluationTimeUtc:yyyy-MM-dd HH:mm:ss} UTC] " +
                    $"Processed {_instrument} M1 {result.CandleTimeUtc:HH:mm} -> Result: NONE");
            }
        }

        // ------------------------------------------------------------------
        // Scheduling helpers
        // ------------------------------------------------------------------

        /// <summary>Public (not just internal) so scheduling can be verified directly by tests without real-time waits.</summary>
        public TimeSpan ComputeDelayToNextBoundary(DateTime nowUtc)
        {
            DateTime nextBoundaryUtc = FloorToMinute(nowUtc).AddMinutes(1) + _options.SafetyDelay;
            TimeSpan delay = nextBoundaryUtc - nowUtc;
            return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        }

        public TimeSpan GetErrorBackoff(int consecutiveErrorCount)
        {
            TimeSpan[] steps = _options.ErrorBackoffSteps;
            if (steps == null || steps.Length == 0)
            {
                return TimeSpan.FromSeconds(5);
            }

            int index = Math.Min(consecutiveErrorCount, steps.Length) - 1;
            return steps[Math.Max(index, 0)];
        }

        public TimeSpan GetNoDataBackoff(int stepsPastThreshold)
        {
            TimeSpan[] steps = _options.NoDataBackoffSteps;
            if (steps == null || steps.Length == 0)
            {
                return TimeSpan.FromMinutes(1);
            }

            int index = Math.Min(stepsPastThreshold, steps.Length) - 1;
            return steps[Math.Max(index, 0)];
        }

        /// <summary>Task.Delay wrapper returning false instead of throwing on cancellation.</summary>
        private static async Task<bool> DelaySafe(TimeSpan delay, CancellationToken cancellationToken)
        {
            if (delay < TimeSpan.Zero)
            {
                delay = TimeSpan.Zero;
            }

            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        private static DateTime FloorToMinute(DateTime utc)
        {
            return new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, 0, DateTimeKind.Utc);
        }

        private static DateTime FloorToHour(DateTime utc)
        {
            return new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, DateTimeKind.Utc);
        }
    }
}
