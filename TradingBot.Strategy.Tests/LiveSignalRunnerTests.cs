using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using TradingBot.App.Live;
using TradingBot.FXCM.Data;
using TradingBot.FXCM.Models;
using TradingBot.FXCM.Settings;
using TradingBot.FXCM.Strategy;
using Xunit;

namespace TradingBot.Strategy.Tests
{
    public class LiveSignalRunnerTests : IDisposable
    {
        private const string Instrument = "XAU/USD";
        private const string M1 = "m1";
        private const string H1Timeframe = "H1";

        private readonly string _dbPath;

        public LiveSignalRunnerTests()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"tradingbot_live_test_{Guid.NewGuid():N}.db");
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(_dbPath))
            {
                File.Delete(_dbPath);
            }
        }

        private CandleRepository CreateRepository() => new CandleRepository(_dbPath);

        private static Candle M1Candle(DateTime timeUtc, double close = 100) =>
            new Candle { Time = timeUtc, Open = close, High = close, Low = close, Close = close, Volume = 10 };

        // Same small bullish Dragonfly used in DragonflyStrategyTests: body [2358.45, 2358.47],
        // upper wick 0, lower wick [2358.10, 2358.45].
        private static Candle DragonflyM1(DateTime timeUtc) => new Candle
        {
            Time = timeUtc,
            Open = 2358.45,
            High = 2358.47,
            Low = 2358.10,
            Close = 2358.47,
            Volume = 10
        };

        private static Candle H1Candle(DateTime timeUtc, double high, double low) => new Candle
        {
            Time = timeUtc,
            Open = (high + low) / 2.0,
            Close = (high + low) / 2.0,
            High = high,
            Low = low,
            Volume = 100
        };

        /// <summary>Mirrors DragonflyStrategyTests.BuildH1History: 28 qualifying daily H1 candles, only the newest intersects.</summary>
        private static List<Candle> BuildH1History(int count, DateTime newestTimeUtc, double intersectingLow)
        {
            var candles = new List<Candle>();
            for (int i = 0; i < count; i++)
            {
                DateTime time = newestTimeUtc.AddDays(-i);
                double low = i == 0 ? intersectingLow : 9998.00;
                candles.Add(H1Candle(time, high: 9999.00, low: low));
            }

            return candles;
        }

        private static DragonflyStrategySettings DefaultSettings() => new DragonflyStrategySettings
        {
            TickSize = 0.01,
            UsePreviousRangeComparison = false
        };

        /// <summary>Wraps an M1-only behavior function so any H1 (or other timeframe) call is a harmless no-op.</summary>
        private static Func<string, DateTime, DateTime, List<Candle>> M1Only(Func<DateTime, DateTime, List<Candle>> m1Behavior)
        {
            return (timeframe, from, to) => timeframe == M1 ? m1Behavior(from, to) : new List<Candle>();
        }

        /// <summary>
        /// Records every call and delegates to a mutable behavior function, so tests can
        /// change what subsequent calls return (e.g. simulate a transient failure followed
        /// by recovery) without reconstructing the runner.
        /// </summary>
        private class FakeFetcher
        {
            public int CallCount { get; private set; }
            public List<(string Timeframe, DateTime From, DateTime To)> Calls { get; } = new List<(string, DateTime, DateTime)>();
            public Func<string, DateTime, DateTime, List<Candle>> Behavior { get; set; }

            public FakeFetcher(Func<string, DateTime, DateTime, List<Candle>> behavior)
            {
                Behavior = behavior;
            }

            public List<Candle> Fetch(string instrument, string timeframe, DateTime from, DateTime to)
            {
                CallCount++;
                Calls.Add((timeframe, from, to));
                return Behavior(timeframe, from, to);
            }
        }

        private LiveSignalRunner CreateRunner(
            FakeFetcher fetcher,
            CandleRepository repository,
            List<string> logs = null,
            LiveSignalRunnerOptions options = null,
            DragonflyStrategySettings settings = null)
        {
            return new LiveSignalRunner(
                fetcher.Fetch,
                repository,
                settings ?? DefaultSettings(),
                Instrument,
                M1,
                H1Timeframe,
                options,
                log: logs != null ? (Action<string>)logs.Add : (_ => { }));
        }

        // ---------------------------------------------------------------
        // 1. An open/current M1 candle is never processed.
        // ---------------------------------------------------------------

        [Fact]
        public void ProcessTick_OpenCandleAtCurrentMinute_IsNeverProcessed()
        {
            var repo = CreateRepository();
            var currentMinuteStart = new DateTime(2026, 6, 15, 18, 43, 0, DateTimeKind.Utc);
            var nowUtc = currentMinuteStart.AddSeconds(37); // 18:43:37, still inside the open minute
            var openCandle = M1Candle(currentMinuteStart);
            var fetcher = new FakeFetcher(M1Only((from, to) => new List<Candle> { openCandle }));
            var runner = CreateRunner(fetcher, repo);

            TickResult result = runner.ProcessTick(nowUtc);

            Assert.Equal(TickOutcome.NoNewCandle, result.Outcome);
            Assert.Empty(result.ProcessedResults);
            Assert.Null(runner.LastProcessedM1Utc);
        }

        // ---------------------------------------------------------------
        // 2. One newly closed candle is processed exactly once.
        // 3. Re-fetching the same candle does not process it again.
        // ---------------------------------------------------------------

        [Fact]
        public void ProcessTick_OneClosedCandle_ProcessedExactlyOnce_ThenNotReprocessedOnRefetch()
        {
            var repo = CreateRepository();
            var baselineTime = new DateTime(2026, 6, 15, 18, 40, 0, DateTimeKind.Utc);
            var candleTime = baselineTime.AddMinutes(2); // 18:42
            var candle = M1Candle(candleTime);

            var fetcher = new FakeFetcher(M1Only((from, to) => new List<Candle> { M1Candle(baselineTime) }));
            var runner = CreateRunner(fetcher, repo);

            // Establish the baseline first - a call with no prior state never evaluates what
            // it sees, exactly like the Phase 7 startup baseline (see the dedicated baseline
            // test below).
            runner.ProcessTick(baselineTime.AddMinutes(1).AddSeconds(3));
            Assert.Equal(baselineTime, runner.LastProcessedM1Utc);

            fetcher.Behavior = M1Only((from, to) => new List<Candle> { candle });
            var nowUtc = candleTime.AddMinutes(1).AddSeconds(3);

            TickResult first = runner.ProcessTick(nowUtc);

            Assert.Equal(TickOutcome.Processed, first.Outcome);
            Assert.Single(first.ProcessedResults);
            Assert.Equal(candleTime, runner.LastProcessedM1Utc);

            // Same candle returned again on the next tick (simulating FXCM re-returning it
            // inside the lookback window) - must not be processed a second time.
            TickResult second = runner.ProcessTick(nowUtc.AddMinutes(1));

            Assert.Equal(TickOutcome.NoNewCandle, second.Outcome);
            Assert.Equal(candleTime, runner.LastProcessedM1Utc);
        }

        // ---------------------------------------------------------------
        // 4. Multiple missed closed candles are processed in ascending chronological order.
        // ---------------------------------------------------------------

        [Fact]
        public void ProcessTick_MultipleUnprocessedClosedCandles_ProcessedInAscendingOrder()
        {
            var repo = CreateRepository();
            var t39 = new DateTime(2026, 6, 15, 18, 39, 0, DateTimeKind.Utc); // baseline
            var t40 = t39.AddMinutes(1);
            var t41 = t39.AddMinutes(2);
            var t42 = t39.AddMinutes(3);
            var t43 = t39.AddMinutes(4);

            var fetcher = new FakeFetcher(M1Only((from, to) => new List<Candle> { M1Candle(t39) }));
            var runner = CreateRunner(fetcher, repo);
            runner.ProcessTick(t39.AddMinutes(1).AddSeconds(3));
            Assert.Equal(t39, runner.LastProcessedM1Utc);

            // Deliberately returned out of order to prove the runner sorts before processing.
            fetcher.Behavior = M1Only((from, to) => new List<Candle>
            {
                M1Candle(t43), M1Candle(t40), M1Candle(t42), M1Candle(t41)
            });

            TickResult result = runner.ProcessTick(t43.AddMinutes(1).AddSeconds(3));

            Assert.Equal(TickOutcome.Processed, result.Outcome);
            Assert.Equal(new[] { t40, t41, t42, t43 }, result.ProcessedResults.Select(r => r.CandleTimeUtc).ToArray());
            Assert.Equal(t43, runner.LastProcessedM1Utc);
        }

        // ---------------------------------------------------------------
        // 5. 18:40/18:41/18:42 with LastProcessed=18:41 processes only 18:42.
        // ---------------------------------------------------------------

        [Fact]
        public void ProcessTick_LastProcessedSet_OnlyProcessesCandlesStrictlyAfterIt()
        {
            var repo = CreateRepository();
            var t40 = new DateTime(2026, 6, 15, 18, 40, 0, DateTimeKind.Utc);
            var t41 = t40.AddMinutes(1);
            var t42 = t40.AddMinutes(2);

            var fetcher = new FakeFetcher(M1Only((from, to) => new List<Candle> { M1Candle(t40), M1Candle(t41) }));
            var runner = CreateRunner(fetcher, repo);

            runner.ProcessTick(t41.AddMinutes(1).AddSeconds(3));
            Assert.Equal(t41, runner.LastProcessedM1Utc);

            // FXCM now returns all three (40/41/42) in the lookback window - only 42 is new.
            fetcher.Behavior = M1Only((from, to) => new List<Candle> { M1Candle(t40), M1Candle(t41), M1Candle(t42) });

            TickResult second = runner.ProcessTick(t42.AddMinutes(1).AddSeconds(3));

            Assert.Equal(TickOutcome.Processed, second.Outcome);
            Assert.Single(second.ProcessedResults);
            Assert.Equal(t42, second.ProcessedResults[0].CandleTimeUtc);
            Assert.Equal(t42, runner.LastProcessedM1Utc);
        }

        // ---------------------------------------------------------------
        // 6. No-new-candle response does not throw.
        // 9. Market-closed/no-data state does not create fake candles.
        // ---------------------------------------------------------------

        [Fact]
        public void ProcessTick_NoDataAvailable_DoesNotThrow_AndCreatesNoFakeCandles()
        {
            var repo = CreateRepository();
            var fetcher = new FakeFetcher(M1Only((from, to) => new List<Candle>()));
            var runner = CreateRunner(fetcher, repo);

            TickResult result = runner.ProcessTick(new DateTime(2026, 6, 15, 18, 43, 3, DateTimeKind.Utc));

            Assert.Equal(TickOutcome.NoNewCandle, result.Outcome);
            Assert.Empty(result.ProcessedResults);
            Assert.Null(runner.LastProcessedM1Utc);
            Assert.Equal(0, repo.GetCandleCount(Instrument, M1));
        }

        // ---------------------------------------------------------------
        // No baseline yet (e.g. Initialize's own lookback window found nothing because the
        // market was closed at startup): the first closed candle(s) FXCM returns establish
        // the baseline only - none of them are evaluated as live signals, no matter how many
        // are sitting in that first response.
        // ---------------------------------------------------------------

        [Fact]
        public void ProcessTick_NoBaselineYet_FirstClosedCandlesEstablishBaseline_WithoutEvaluatingAny()
        {
            var repo = CreateRepository();
            var t1 = new DateTime(2026, 6, 15, 23, 0, 0, DateTimeKind.Utc);
            var t2 = t1.AddMinutes(1);
            var t3 = t1.AddMinutes(2);

            var fetcher = new FakeFetcher(M1Only((from, to) => new List<Candle> { M1Candle(t1), M1Candle(t2), M1Candle(t3) }));
            var runner = CreateRunner(fetcher, repo);

            TickResult result = runner.ProcessTick(t3.AddMinutes(1).AddSeconds(3));

            Assert.Equal(TickOutcome.NoNewCandle, result.Outcome);
            Assert.Empty(result.ProcessedResults);
            Assert.Equal(t3, runner.LastProcessedM1Utc); // newest closed candle becomes the baseline
            Assert.Equal(0, repo.GetCandleCount(Instrument, M1)); // none of them were saved/evaluated
        }

        // ---------------------------------------------------------------
        // A gap since the last processed candle wider than the normal lookback window must
        // widen the fetch, not silently lose the candles in between.
        // ---------------------------------------------------------------

        [Fact]
        public void ProcessTick_LongGapSinceLastProcessed_WidensFetchWindowToRecoverMissedCandles()
        {
            var repo = CreateRepository();
            var options = new LiveSignalRunnerOptions { M1LookbackWindow = TimeSpan.FromMinutes(5) };
            var baselineTime = new DateTime(2026, 6, 15, 18, 0, 0, DateTimeKind.Utc);

            var fetcher = new FakeFetcher(M1Only((from, to) => new List<Candle> { M1Candle(baselineTime) }));
            var runner = CreateRunner(fetcher, repo, options: options);
            runner.ProcessTick(baselineTime.AddMinutes(1).AddSeconds(3));
            Assert.Equal(baselineTime, runner.LastProcessedM1Utc);

            // "Now" has advanced 13 minutes past the baseline - far beyond the 5-minute
            // normal lookback window, simulating a long outage.
            DateTime nowUtc = baselineTime.AddMinutes(13).AddSeconds(3);
            fetcher.Behavior = M1Only((from, to) => new List<Candle>());
            runner.ProcessTick(nowUtc);

            DateTime requestedFrom = fetcher.Calls.Last(c => c.Timeframe == M1).From;
            DateTime expectedRecoveryFrom = baselineTime.AddMinutes(-1); // lastProcessed - 1 minute
            Assert.Equal(expectedRecoveryFrom, requestedFrom);
        }

        [Fact]
        public void ProcessTick_NormalOperation_UsesFixedLookbackWindow_NotWidened()
        {
            var repo = CreateRepository();
            var options = new LiveSignalRunnerOptions { M1LookbackWindow = TimeSpan.FromMinutes(5) };
            var baselineTime = new DateTime(2026, 6, 15, 18, 40, 0, DateTimeKind.Utc);

            var fetcher = new FakeFetcher(M1Only((from, to) => new List<Candle> { M1Candle(baselineTime) }));
            var runner = CreateRunner(fetcher, repo, options: options);
            runner.ProcessTick(baselineTime.AddMinutes(1).AddSeconds(3));

            DateTime nowUtc = baselineTime.AddMinutes(2).AddSeconds(3); // one minute later - normal cadence
            fetcher.Behavior = M1Only((from, to) => new List<Candle>());
            runner.ProcessTick(nowUtc);

            DateTime requestedFrom = fetcher.Calls.Last(c => c.Timeframe == M1).From;
            DateTime currentMinuteStartUtc = new DateTime(2026, 6, 15, 18, 42, 0, DateTimeKind.Utc);
            Assert.Equal(currentMinuteStartUtc - options.M1LookbackWindow, requestedFrom);
        }

        // ---------------------------------------------------------------
        // A partially-failed H1 startup backfill must not be treated as sufficient history -
        // InitializeAsync (and therefore RunAsync) must not proceed into live processing with
        // an incomplete reference set.
        // ---------------------------------------------------------------

        [Fact]
        public async Task InitializeAsync_H1BackfillHasFailedChunks_KeepsRetrying_NeverProceedsWithIncompleteHistory()
        {
            var repo = CreateRepository();
            int h1CallCount = 0;

            var fetcher = new FakeFetcher((timeframe, from, to) =>
            {
                if (timeframe != H1Timeframe)
                {
                    return new List<Candle> { M1Candle(new DateTime(2026, 6, 15, 18, 0, 0, DateTimeKind.Utc)) };
                }

                // Every H1 chunk request fails, forever - the fix must keep retrying rather
                // than ever accepting this partial (here: entirely empty) history as
                // sufficient. ProcessSingleChunk records this as a failed chunk rather than
                // letting it propagate out of Import() itself.
                h1CallCount++;
                throw new InvalidOperationException("simulated H1 chunk failure");
            });

            var options = new LiveSignalRunnerOptions
            {
                H1BackfillWindow = TimeSpan.FromDays(3),
                H1BackfillChunkSize = TimeSpan.FromDays(3), // exactly one chunk per Import() attempt
                H1BackfillRequestDelay = TimeSpan.Zero,
                ErrorBackoffSteps = new[] { TimeSpan.FromMilliseconds(5) }
            };

            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
            {
                var runner = new LiveSignalRunner(
                    fetcher.Fetch, repo, DefaultSettings(), Instrument, M1, H1Timeframe,
                    options, log: _ => { });

                // Every Import() attempt has a failed chunk -> InitializeAsync must keep
                // retrying rather than proceeding into live mode; the 2s cancellation above
                // is what ends this test once that has been observed repeatedly.
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.InitializeAsync(cts.Token));
            }

            Assert.True(h1CallCount >= 2, "Expected InitializeAsync to keep retrying the H1 backfill instead of giving up or proceeding.");
        }

        // ---------------------------------------------------------------
        // 7. A transient fetch exception does not permanently terminate the runner.
        // ---------------------------------------------------------------

        [Fact]
        public void ProcessTick_TransientFetchException_ReturnsError_AndNextTickRecovers()
        {
            var repo = CreateRepository();
            var baselineTime = new DateTime(2026, 6, 15, 18, 40, 0, DateTimeKind.Utc);
            var candleTime = baselineTime.AddMinutes(1); // 18:41

            var fetcher = new FakeFetcher(M1Only((from, to) => new List<Candle> { M1Candle(baselineTime) }));
            var runner = CreateRunner(fetcher, repo);
            runner.ProcessTick(baselineTime.AddMinutes(1).AddSeconds(3));
            Assert.Equal(baselineTime, runner.LastProcessedM1Utc);

            fetcher.Behavior = M1Only((from, to) => throw new InvalidOperationException("simulated transient failure"));
            var nowUtc = candleTime.AddMinutes(1).AddSeconds(3);

            TickResult failed = runner.ProcessTick(nowUtc);
            Assert.Equal(TickOutcome.Error, failed.Outcome);
            Assert.Equal(baselineTime, runner.LastProcessedM1Utc); // unchanged by the failed attempt

            fetcher.Behavior = M1Only((from, to) => new List<Candle> { M1Candle(baselineTime), M1Candle(candleTime) });
            TickResult recovered = runner.ProcessTick(nowUtc);
            Assert.Equal(TickOutcome.Processed, recovered.Outcome);
            Assert.Equal(candleTime, runner.LastProcessedM1Utc);
        }

        // ---------------------------------------------------------------
        // 8. After a successful request, error backoff state resets.
        //
        // Two isolated failures separated by a success: if the error counter reset after the
        // first success, the second failure reuses the fast first backoff step (20ms) rather
        // than escalating to the much slower second step (2s). The 100x margin between the
        // two makes this reliable to assert on wall-clock time.
        // ---------------------------------------------------------------

        [Fact]
        public async Task RunAsync_ResetsErrorBackoff_AfterASuccessfulTick()
        {
            var repo = CreateRepository();

            using (var cts = new CancellationTokenSource())
            {
                var baseline = new DateTime(2026, 6, 15, 18, 40, 0, DateTimeKind.Utc);
                var candleA = baseline.AddMinutes(1);
                var candleB = baseline.AddMinutes(2);

                // Just before a minute boundary, so ComputeDelayToNextBoundary is near-zero
                // for every "normal" (non-backoff) schedule decision in this test.
                DateTime fixedNow = candleB.AddMinutes(2).AddMilliseconds(-1);

                int m1CallCount = 0;

                var fetcher = new FakeFetcher((timeframe, from, to) =>
                {
                    if (timeframe != M1)
                    {
                        return new List<Candle>();
                    }

                    m1CallCount++;
                    switch (m1CallCount)
                    {
                        case 1: // InitializeAsync baseline fetch
                            return new List<Candle> { M1Candle(baseline) };
                        case 2:
                            throw new InvalidOperationException("simulated failure 1");
                        case 3:
                            return new List<Candle> { M1Candle(baseline), M1Candle(candleA) };
                        case 4:
                            throw new InvalidOperationException("simulated failure 2");
                        case 5:
                            cts.Cancel();
                            return new List<Candle> { M1Candle(baseline), M1Candle(candleA), M1Candle(candleB) };
                        default:
                            cts.Cancel();
                            return new List<Candle>();
                    }
                });

                var options = new LiveSignalRunnerOptions
                {
                    SafetyDelay = TimeSpan.Zero,
                    ErrorBackoffSteps = new[] { TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(2) },
                    NoDataBackoffThreshold = 1000,
                    H1BackfillRequestDelay = TimeSpan.Zero
                };

                var runner = new LiveSignalRunner(
                    fetcher.Fetch, repo, DefaultSettings(), Instrument, M1, H1Timeframe,
                    options, log: _ => { }, utcNowProvider: () => fixedNow);

                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                await runner.RunAsync(cts.Token);
                stopwatch.Stop();

                Assert.True(
                    stopwatch.ElapsedMilliseconds < 1000,
                    $"Expected the 2nd failure to reuse the fast backoff step (proving the counter reset after the intervening success); took {stopwatch.ElapsedMilliseconds}ms.");
                Assert.Equal(candleB, runner.LastProcessedM1Utc);
            }
        }

        // ---------------------------------------------------------------
        // 10. BUY output happens only when the existing strategy returns BUY.
        // ---------------------------------------------------------------

        [Fact]
        public void ProcessTick_StrategyProducesBuy_ReturnsBuySignal_AndSavesCandle()
        {
            var repo = CreateRepository();
            var newestH1TimeUtc = new DateTime(2026, 6, 15, 4, 0, 0, DateTimeKind.Utc);
            var allowedM1TimeUtc = new DateTime(2026, 6, 15, 13, 0, 0, DateTimeKind.Utc); // 09:00 UTC-4, outside blocked hours
            var baselineTime = allowedM1TimeUtc.AddMinutes(-1); // established first, so the dragonfly candle itself gets evaluated rather than swallowed as the baseline

            repo.SaveCandles(Instrument, H1Timeframe, BuildH1History(28, newestH1TimeUtc, intersectingLow: 2358.30));

            var fetcher = new FakeFetcher(M1Only((from, to) => new List<Candle> { M1Candle(baselineTime) }));
            var runner = CreateRunner(fetcher, repo);
            runner.ProcessTick(baselineTime.AddMinutes(1).AddSeconds(3));
            Assert.Equal(baselineTime, runner.LastProcessedM1Utc);

            Candle dragonfly = DragonflyM1(allowedM1TimeUtc);
            fetcher.Behavior = M1Only((from, to) => new List<Candle> { dragonfly });

            TickResult result = runner.ProcessTick(allowedM1TimeUtc.AddMinutes(1).AddSeconds(3));

            Assert.Equal(TickOutcome.Processed, result.Outcome);
            Assert.Single(result.ProcessedResults);
            Assert.Equal(SignalType.Buy, result.ProcessedResults[0].Signal);
            Assert.Equal(1, repo.GetCandleCount(Instrument, M1));
        }

        // ---------------------------------------------------------------
        // 11. NONE causes the runner to continue normally.
        // ---------------------------------------------------------------

        [Fact]
        public void ProcessTick_StrategyProducesNone_AdvancesLastProcessed_AndSavesCandle()
        {
            var repo = CreateRepository();
            var baselineTime = new DateTime(2026, 6, 15, 18, 41, 0, DateTimeKind.Utc);
            var candleTime = baselineTime.AddMinutes(1); // 18:42
            var candle = M1Candle(candleTime); // flat candle - never a Dragonfly, no H1 seeded either

            var fetcher = new FakeFetcher(M1Only((from, to) => new List<Candle> { M1Candle(baselineTime) }));
            var runner = CreateRunner(fetcher, repo);
            runner.ProcessTick(baselineTime.AddMinutes(1).AddSeconds(3));
            Assert.Equal(baselineTime, runner.LastProcessedM1Utc);

            fetcher.Behavior = M1Only((from, to) => new List<Candle> { candle });

            TickResult result = runner.ProcessTick(candleTime.AddMinutes(1).AddSeconds(3));

            Assert.Equal(TickOutcome.Processed, result.Outcome);
            Assert.Equal(SignalType.None, result.ProcessedResults[0].Signal);
            Assert.Equal(candleTime, runner.LastProcessedM1Utc);
            Assert.Equal(1, repo.GetCandleCount(Instrument, M1));
        }

        // ---------------------------------------------------------------
        // 12. Current/open candles are never written as completed strategy evaluations.
        // ---------------------------------------------------------------

        [Fact]
        public void ProcessTick_OpenCandle_NeverSavedToRepository()
        {
            var repo = CreateRepository();
            var currentMinuteStart = new DateTime(2026, 6, 15, 18, 43, 0, DateTimeKind.Utc);
            var openCandle = M1Candle(currentMinuteStart);
            var fetcher = new FakeFetcher(M1Only((from, to) => new List<Candle> { openCandle }));
            var runner = CreateRunner(fetcher, repo);

            runner.ProcessTick(currentMinuteStart.AddSeconds(37));

            Assert.Equal(0, repo.GetCandleCount(Instrument, M1));
        }

        // ---------------------------------------------------------------
        // Scheduling / backoff table helpers (pure functions - deterministic to verify directly).
        // ---------------------------------------------------------------

        [Fact]
        public void ComputeDelayToNextBoundary_MidMinute_ReturnsDelayToBoundaryPlusSafetyDelay()
        {
            var repo = CreateRepository();
            var options = new LiveSignalRunnerOptions { SafetyDelay = TimeSpan.FromSeconds(3) };
            var runner = CreateRunner(new FakeFetcher(M1Only((f, t) => new List<Candle>())), repo, options: options);

            var nowUtc = new DateTime(2026, 6, 15, 18, 43, 37, DateTimeKind.Utc);

            // Next M1 boundary is 18:44:00 + 3s safety delay = 18:44:03; now is 18:43:37.
            Assert.Equal(TimeSpan.FromSeconds(26), runner.ComputeDelayToNextBoundary(nowUtc));
        }

        [Fact]
        public void GetErrorBackoff_FollowsConfiguredStepsThenCaps()
        {
            var repo = CreateRepository();
            var runner = CreateRunner(new FakeFetcher(M1Only((f, t) => new List<Candle>())), repo);

            Assert.Equal(TimeSpan.FromSeconds(5), runner.GetErrorBackoff(1));
            Assert.Equal(TimeSpan.FromSeconds(15), runner.GetErrorBackoff(2));
            Assert.Equal(TimeSpan.FromSeconds(30), runner.GetErrorBackoff(3));
            Assert.Equal(TimeSpan.FromSeconds(60), runner.GetErrorBackoff(4));
            Assert.Equal(TimeSpan.FromSeconds(60), runner.GetErrorBackoff(10)); // capped
        }

        [Fact]
        public void GetNoDataBackoff_FollowsConfiguredStepsThenCaps()
        {
            var repo = CreateRepository();
            var runner = CreateRunner(new FakeFetcher(M1Only((f, t) => new List<Candle>())), repo);

            Assert.Equal(TimeSpan.FromMinutes(1), runner.GetNoDataBackoff(1));
            Assert.Equal(TimeSpan.FromMinutes(2), runner.GetNoDataBackoff(2));
            Assert.Equal(TimeSpan.FromMinutes(5), runner.GetNoDataBackoff(3));
            Assert.Equal(TimeSpan.FromMinutes(5), runner.GetNoDataBackoff(20)); // capped
        }
    }
}
