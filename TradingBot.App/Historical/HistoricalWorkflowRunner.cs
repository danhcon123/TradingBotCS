using System;
using System.Collections.Generic;
using System.Linq;
using TradingBot.FXCM;
using TradingBot.FXCM.Backtest;
using TradingBot.FXCM.Data;
using TradingBot.FXCM.Import;
using TradingBot.FXCM.Models;
using TradingBot.FXCM.Settings;
using TradingBot.FXCM.Strategy;

namespace TradingBot.App.Historical
{
    /// <summary>
    /// The original historical backfill + integrity-check + backtest workflow, extracted
    /// unchanged from Program.cs so it runs only on demand (--historical /
    /// --historical-then-live) instead of automatically before every live start. Every
    /// method body here is identical to its original Program.cs implementation - only the
    /// location and (for the previously-inline Main logic) grouping into <see cref="Run"/>
    /// changed.
    /// </summary>
    public static class HistoricalWorkflowRunner
    {
        private const string Instrument = "XAU/USD";

        // ForexConnect timeframe codes: minutes are lowercase ("m1"), hours are uppercase
        // ("H1") - uppercase "M1" means MONTHLY, not 1-minute. Easy to get backwards.
        private const string M1Timeframe = "m1";
        private const string H1Timeframe = "H1";

        // FXCMPriceHistory.GetCandles caps each response at 100 bars, so the importer pulls
        // a multi-day window in fixed-size chunks. Kept well under the 100-bar cap for each
        // timeframe as a safety margin.
        private static readonly TimeSpan M1ChunkSize = TimeSpan.FromMinutes(90);
        private static readonly TimeSpan H1ChunkSize = TimeSpan.FromDays(3);

        // Conservative validation-scale window: enough to exercise the full backfill +
        // integrity-check pipeline without a multi-hour live run. A full 6-month M1
        // backfill is many thousands of chunked requests and can take hours - only widen
        // these to the real target (e.g. 180 days M1 / 240 days H1, so H1 reference history
        // exists well before the M1 window even starts) once this smaller run has been
        // verified end to end. The importer is resumable, so widening later and re-running
        // simply continues from what's already stored - it never re-downloads anything.
        private static readonly TimeSpan M1LookbackWindow = TimeSpan.FromDays(90);
        private static readonly TimeSpan H1LookbackWindow = TimeSpan.FromDays(135);

        /// <summary>
        /// Runs the full historical workflow against an already-connected FXCM session and
        /// the given repository: backfill + integrity check, the one-off target-candle
        /// check, then a full backtest of everything stored. This is exactly what Program.cs
        /// used to run unconditionally before every live start.
        /// </summary>
        public static void Run(FXCMConnection fxcm, CandleRepository repository)
        {
            RunBackfillAndIntegrityCheck(fxcm, repository);

            // Optional check for the specific TradingView candle.
            DateTime targetCandleUtc =
                new DateTime(
                    2026, 8, 31,
                    14, 15, 0,
                    DateTimeKind.Utc);

            RunTargetCandleCheck(
                repository,
                targetCandleUtc);

            // Backtest the complete stored database.
            RunBacktest(repository);
        }

        private static void RunBackfillAndIntegrityCheck(FXCMConnection fxcm, CandleRepository repository)
        {
            FXCMPriceHistory history = fxcm.GetPriceHistory();
            var importer = new FXCMHistoricalImporter(history.GetCandles, repository, requestTimeout: TimeSpan.FromSeconds(40));

            DateTime nowUtc = DateTime.UtcNow;

            DateTime m1ToUtc = FloorToMinute(nowUtc);
            DateTime h1ToUtc = FloorToHour(nowUtc);

            DateTime m1FromUtc = m1ToUtc - M1LookbackWindow;
            DateTime h1FromUtc = h1ToUtc - H1LookbackWindow;

            BackfillTimeframe(importer, Instrument, M1Timeframe, m1FromUtc, m1ToUtc, M1ChunkSize);
            BackfillTimeframe(importer, Instrument, H1Timeframe, h1FromUtc, h1ToUtc, H1ChunkSize);

            RunIntegrityCheck(repository, Instrument, M1Timeframe, TimeSpan.FromMinutes(1));
            RunIntegrityCheck(repository, Instrument, H1Timeframe, TimeSpan.FromHours(1));
        }

        /// <summary>
        /// Runs the main chunked import, then explicitly retries any chunks that failed.
        /// This retry step is necessary (not just belt-and-braces): if a middle chunk fails
        /// while a later chunk still succeeds, the repository's "latest stored timestamp"
        /// ends up past the failed gap, so a second call to Import() alone would think that
        /// range is already covered and would never revisit it. RetryFailedChunks bypasses
        /// that resume logic and re-attempts exactly the recorded gaps.
        /// </summary>
        private static void BackfillTimeframe(
            FXCMHistoricalImporter importer,
            string instrument,
            string timeframe,
            DateTime fromUtc,
            DateTime toUtc,
            TimeSpan chunkSize)
        {
            Console.WriteLine();
            Console.WriteLine($"=== Backfilling {instrument} {timeframe}: {fromUtc:yyyy-MM-dd HH:mm} -> {toUtc:yyyy-MM-dd HH:mm} UTC ===");

            ImportResult result = importer.Import(instrument, timeframe, fromUtc, toUtc, chunkSize);
            PrintImportResult(result);

            const int maxRetryPasses = 3;
            int pass = 0;
            IReadOnlyList<ImportChunkFailure> outstanding = result.FailedChunks;

            while (outstanding != null && outstanding.Count > 0 && pass < maxRetryPasses)
            {
                pass++;
                Console.WriteLine($"Retrying {outstanding.Count} failed chunk(s) (pass {pass}/{maxRetryPasses})...");
                ImportResult retryResult = importer.RetryFailedChunks(instrument, timeframe, outstanding);
                PrintImportResult(retryResult);
                outstanding = retryResult.FailedChunks;
            }

            if (outstanding != null && outstanding.Count > 0)
            {
                Console.WriteLine($"WARNING: {outstanding.Count} chunk(s) still failed after {maxRetryPasses} retry pass(es):");
                foreach (ImportChunkFailure failure in outstanding)
                {
                    Console.WriteLine("  " + failure);
                }
            }
        }

        private static void PrintImportResult(ImportResult result)
        {
            if (result.AlreadyUpToDate)
            {
                Console.WriteLine("Already up to date - nothing to download.");
                return;
            }

            Console.WriteLine($"Actual start (after resume skip): {result.ActualFromUtc:yyyy-MM-dd HH:mm}");
            Console.WriteLine($"Chunks succeeded: {result.ChunksSucceeded}, failed: {result.ChunksFailed}");
            Console.WriteLine(
                $"Candles received: {result.CandlesReceived}, " +
                $"written or updated: {result.CandlesWritten}");
        }

        /// <summary>
        /// A lightweight sanity pass, not a full market-hours calendar: classifies the gap
        /// between every pair of consecutive stored candles as "normal" (exactly the
        /// expected interval), "weekend-sized" (a broad window around the typical ~48h
        /// Friday-close-to-Sunday-open closure), or "unexpected" (flagged for manual review
        /// against a chart before trusting this data for backtesting).
        ///
        /// Always scans the FULL actual stored range (via GetEarliestTimestampUtc /
        /// GetLatestTimestampUtc), not a now-relative rolling window - a rolling window
        /// drifts forward every run and can silently stop covering older data that is still
        /// perfectly valid and stored, making this report inconsistent with what
        /// RunBacktest (which already scans the full stored range) shows.
        /// </summary>
        private static void RunIntegrityCheck(
            CandleRepository repository,
            string instrument,
            string timeframe,
            TimeSpan expectedInterval)
        {
            Console.WriteLine();
            Console.WriteLine($"=== Integrity check: {instrument} {timeframe} ===");

            DateTime? fromUtc = repository.GetEarliestTimestampUtc(instrument, timeframe);
            DateTime? toUtc = repository.GetLatestTimestampUtc(instrument, timeframe);

            if (!fromUtc.HasValue || !toUtc.HasValue)
            {
                Console.WriteLine("No candles stored for this instrument/timeframe.");
                return;
            }

            // GetCandles is a half-open [from, to) range - extend by one second so the very
            // last stored candle is actually included.
            IReadOnlyList<Candle> candles = repository.GetCandles(instrument, timeframe, fromUtc.Value, toUtc.Value.AddSeconds(1));

            if (candles.Count == 0)
            {
                Console.WriteLine("No candles stored in this range.");
                return;
            }

            Console.WriteLine($"Stored candles: {candles.Count}");
            Console.WriteLine($"Earliest: {candles[0].Time:yyyy-MM-dd HH:mm} UTC");
            Console.WriteLine($"Latest:   {candles[candles.Count - 1].Time:yyyy-MM-dd HH:mm} UTC");

            var weekendGapMin = TimeSpan.FromHours(40);
            var weekendGapMax = TimeSpan.FromHours(60);

            int normalGaps = 0;
            int weekendGaps = 0;
            var unexpectedGaps = new List<(DateTime From, DateTime To, TimeSpan Gap)>();

            for (int i = 1; i < candles.Count; i++)
            {
                TimeSpan gap = candles[i].Time - candles[i - 1].Time;

                if (gap == expectedInterval)
                {
                    normalGaps++;
                }
                else if (gap >= weekendGapMin && gap <= weekendGapMax)
                {
                    weekendGaps++;
                }
                else
                {
                    unexpectedGaps.Add((candles[i - 1].Time, candles[i].Time, gap));
                }
            }

            Console.WriteLine($"Normal-interval gaps:  {normalGaps}");
            Console.WriteLine($"Weekend-sized gaps:    {weekendGaps}");
            Console.WriteLine($"Unexpected gaps:       {unexpectedGaps.Count}");

            const int maxGapsToPrint = 20;
            if (unexpectedGaps.Count > 0)
            {
                Console.WriteLine($"Showing up to {maxGapsToPrint} unexpected gap(s) for manual review:");
                foreach (var gap in unexpectedGaps.Take(maxGapsToPrint))
                {
                    Console.WriteLine($"  {gap.From:yyyy-MM-dd HH:mm} -> {gap.To:yyyy-MM-dd HH:mm} UTC (gap: {gap.Gap})");
                }

                if (unexpectedGaps.Count > maxGapsToPrint)
                {
                    Console.WriteLine($"  ... and {unexpectedGaps.Count - maxGapsToPrint} more.");
                }
            }
        }

        private static DateTime FloorToMinute(DateTime utc)
        {
            return new DateTime(
                utc.Year, utc.Month, utc.Day,
                utc.Hour, utc.Minute, 0,
                DateTimeKind.Utc);
        }

        private static DateTime FloorToHour(DateTime utc)
        {
            return new DateTime(utc.Year, utc.Month, utc.Day,
                utc.Hour, 0, 0,
                DateTimeKind.Utc);
        }

        private static void RunTargetCandleCheck(
            CandleRepository repository,
            DateTime targetUtc)
        {
            Console.WriteLine();
            Console.WriteLine("################################################");
            Console.WriteLine("Target M1 candle check");
            Console.WriteLine("################################################");

            Console.WriteLine(
                $"Requested UTC:   {targetUtc:yyyy-MM-dd HH:mm}");

            Console.WriteLine(
                $"Requested UTC-4: {targetUtc.AddHours(-4):yyyy-MM-dd HH:mm}");

            // An M1 candle is only known after its closing time.
            DateTime evaluationTimeUtc = targetUtc.AddMinutes(1);

            if (DateTime.UtcNow < evaluationTimeUtc)
            {
                Console.WriteLine(
                    $"The candle is not completed yet. It closes at " +
                    $"{evaluationTimeUtc:yyyy-MM-dd HH:mm} UTC.");

                return;
            }

            // Load enough M1 data to find the requested candle and its
            // immediately preceding available candle.
            IReadOnlyList<Candle> nearbyM1Candles =
                repository.GetCandles(
                    Instrument,
                    M1Timeframe,
                    targetUtc.AddHours(-1),
                    targetUtc.AddMinutes(1));

            Candle currentM1 = nearbyM1Candles
                .FirstOrDefault(c => c.Time == targetUtc);

            if (currentM1 == null)
            {
                Console.WriteLine(
                    "The requested M1 candle is not in the database.");

                return;
            }

            Candle previousM1 = nearbyM1Candles
                .Where(c => c.Time < targetUtc)
                .LastOrDefault();

            if (previousM1 == null)
            {
                Console.WriteLine(
                    "The previous M1 candle is not available.");

                return;
            }

            DateTime? h1FromUtc =
                repository.GetEarliestTimestampUtc(
                    Instrument,
                    H1Timeframe);

            if (!h1FromUtc.HasValue)
            {
                Console.WriteLine(
                    "No H1 history is available.");

                return;
            }

            // Load H1 candles available up to the target evaluation time.
            // DragonflyStrategy/H1LevelCalculator will perform the final
            // completed-H1 availability check.
            IReadOnlyList<Candle> h1Candles =
                repository.GetCandles(
                    Instrument,
                    H1Timeframe,
                    h1FromUtc.Value,
                    evaluationTimeUtc.AddSeconds(1));

            foreach (bool usePreviousRangeComparison in new[] { true, false })
            {
                Console.WriteLine();
                Console.WriteLine(
                    $"Target variant: UsePreviousRangeComparison = " +
                    $"{usePreviousRangeComparison}");

                var settings = new DragonflyStrategySettings
                {
                    TickSize = 0.01,
                    UsePreviousRangeComparison =
                        usePreviousRangeComparison
                };

                var strategy =
                    new DragonflyStrategy(settings);

                StrategyResult result =
                    strategy.Evaluate(
                        currentM1,
                        previousM1,
                        h1Candles);

                PrintDragonflyDiagnostic(
                    result,
                    settings.H1ReferenceCount);
            }
        }

        /// <summary>
        /// Reads whatever M1/H1 history is currently stored and replays it through
        /// DragonflyStrategy via Backtester, for both previous-range-comparison variants.
        /// Prints the full Phase 7 diagnostic block for every detected Dragonfly (not just
        /// final BUYs, so near-misses are visible too), plus a summary per variant.
        /// </summary>
        private static void RunBacktest(CandleRepository repository)
        {
            Console.WriteLine();
            Console.WriteLine("################################################");
            Console.WriteLine("Backtest (reads the local database only - no live connection needed)");
            Console.WriteLine("################################################");

            DateTime? m1FromUtc = repository.GetEarliestTimestampUtc(Instrument, M1Timeframe);
            DateTime? m1ToUtc = repository.GetLatestTimestampUtc(Instrument, M1Timeframe);
            DateTime? h1FromUtc = repository.GetEarliestTimestampUtc(Instrument, H1Timeframe);
            DateTime? h1ToUtc = repository.GetLatestTimestampUtc(Instrument, H1Timeframe);

            if (!m1FromUtc.HasValue || !m1ToUtc.HasValue || !h1FromUtc.HasValue || !h1ToUtc.HasValue)
            {
                Console.WriteLine("No stored M1/H1 data yet - run a backfill first.");
                return;
            }

            // Only include completed candles.
            //
            // Example at 12:34:27 UTC:
            // - The 12:34 M1 candle is still forming.
            // - completedM1Boundary is 12:34:00.
            // - Because GetCandles uses [from, to), the 12:34 candle is excluded.
            //
            // For H1:
            // - The 12:00 H1 candle is still forming until 13:00.
            // - completedH1Boundary is 12:00:00.
            // - Therefore, the 12:00 H1 candle is excluded.

            DateTime nowUtc = DateTime.UtcNow;

            DateTime completedM1Boundary = FloorToMinute(nowUtc);
            DateTime completedH1Boundary = FloorToHour(nowUtc);

            // Include the latest stored candle only when it is already completed.
            DateTime m1QueryToUtc =
                m1ToUtc.Value.AddSeconds(1) < completedM1Boundary
                    ? m1ToUtc.Value.AddSeconds(1)
                    : completedM1Boundary;

            DateTime h1QueryToUtc =
                h1ToUtc.Value.AddSeconds(1) < completedH1Boundary
                    ? h1ToUtc.Value.AddSeconds(1)
                    : completedH1Boundary;

            IReadOnlyList<Candle> m1Candles =
                repository.GetCandles(
                    Instrument,
                    M1Timeframe,
                    m1FromUtc.Value,
                    m1QueryToUtc);

            IReadOnlyList<Candle> h1Candles =
                repository.GetCandles(
                    Instrument,
                    H1Timeframe,
                    h1FromUtc.Value,
                    h1QueryToUtc);

            Console.WriteLine($"M1 range in database: {m1FromUtc:yyyy-MM-dd HH:mm} -> {m1ToUtc:yyyy-MM-dd HH:mm} UTC ({m1Candles.Count} candles)");
            Console.WriteLine($"H1 range in database: {h1FromUtc:yyyy-MM-dd HH:mm} -> {h1ToUtc:yyyy-MM-dd HH:mm} UTC ({h1Candles.Count} candles)");

            RunBacktestVariant(m1Candles, h1Candles, usePreviousRangeComparison: true);
            RunBacktestVariant(m1Candles, h1Candles, usePreviousRangeComparison: false);
        }

        private static void RunBacktestVariant(
            IReadOnlyList<Candle> m1Candles,
            IReadOnlyList<Candle> h1Candles,
            bool usePreviousRangeComparison)
        {
            Console.WriteLine();
            Console.WriteLine("==========================================");
            Console.WriteLine($"Variant: UsePreviousRangeComparison = {usePreviousRangeComparison}");
            Console.WriteLine("==========================================");

            var settings = new DragonflyStrategySettings
            {
                TickSize = 0.01, // XAU/USD
                UsePreviousRangeComparison = usePreviousRangeComparison
            };

            BacktestResult result = Backtester.Run(m1Candles, h1Candles, settings);

            foreach (StrategyResult dragonflyResult in result.DragonflyResults)
            {
                PrintDragonflyDiagnostic(dragonflyResult, settings.H1ReferenceCount);
            }

            Console.WriteLine();
            Console.WriteLine("Summary:");
            Console.WriteLine($"  M1 candles evaluated:              {result.M1CandlesEvaluated}");
            Console.WriteLine($"  Dragonflies detected:              {result.DragonfliesDetected}");
            Console.WriteLine($"  Dragonflies with full H1 history:  {result.DragonfliesWithFullH1Set}");
            Console.WriteLine($"  Dragonflies with intersection:     {result.DragonfliesWithIntersection}");
            Console.WriteLine($"  Signals blocked by time:           {result.SignalsBlockedByTime}");
            Console.WriteLine($"  Final BUY signals:                 {result.BuySignals}");
        }

        private static void PrintDragonflyDiagnostic(StrategyResult result, int requiredH1Count)
        {
            Candle candle = result.CurrentCandle;
            DragonflyAnalysis analysis = result.DragonflyAnalysis;

            Console.WriteLine("----------------------------------------");
            Console.WriteLine($"M1 UTC:    {result.CandleTimeUtc:yyyy-MM-dd HH:mm}");
            Console.WriteLine($"M1 UTC-4:  {result.CandleTimeUtcMinus4:yyyy-MM-dd HH:mm}");
            Console.WriteLine();
            Console.WriteLine($"Open:  {candle.Open}");
            Console.WriteLine($"High:  {candle.High}");
            Console.WriteLine($"Low:   {candle.Low}");
            Console.WriteLine($"Close: {candle.Close}");
            Console.WriteLine();
            Console.WriteLine($"Range:      {analysis.Range}");
            Console.WriteLine($"Body:       {analysis.Body}");
            Console.WriteLine($"Upper Wick: {analysis.UpperWick}");
            Console.WriteLine($"Lower Wick: {analysis.LowerWick}");
            Console.WriteLine();
            Console.WriteLine($"Lower Wick / Body: {analysis.LowerWickToBodyRatio}");
            Console.WriteLine();
            Console.WriteLine($"Valid OHLC:             {analysis.IsValidOhlc}");
            Console.WriteLine(
                $"Has positive range:     {analysis.HasRange}");

            Console.WriteLine(
                $"Body percentage:        {analysis.BodyPercentage:F2}%");

            Console.WriteLine(
                $"Lower-wick rule passed: {analysis.LowerWickRulePassed}");

            Console.WriteLine(
                $"Body rule passed:       {analysis.BodyRulePassed}");

            Console.WriteLine(
                $"Upper-wick rule passed: {analysis.UpperWickRulePassed}");
            Console.WriteLine($"Previous-range comparison enabled: {analysis.PreviousRangeRuleEnabled}");
            Console.WriteLine($"Previous M1 range:                 {FormatNullable(analysis.PreviousRange)}");
            Console.WriteLine($"Current / Previous ratio:          {FormatNullable(analysis.CurrentToPreviousRangeRatio)}");
            Console.WriteLine($"Previous rule passed:              {FormatNullableBool(analysis.PreviousRangeRulePassed)}");
            Console.WriteLine();
            Console.WriteLine($"Dragonfly: {analysis.IsDragonfly}");
            Console.WriteLine();
            Console.WriteLine($"Available H1 sets: {result.ReferenceSetCount}/{requiredH1Count}");
            Console.WriteLine();
            Console.WriteLine($"Lower-wick intersection: {result.HasIntersection}");
            Console.WriteLine($"Intersection count:      {result.Intersections.Count}");
            Console.WriteLine();

            if (result.PrimaryIntersection != null)
            {
                PriceLevel primary = result.PrimaryIntersection.Level;
                Console.WriteLine($"Primary level type:       {primary.LevelType}");
                Console.WriteLine($"Primary level price:      {primary.Price}");
                Console.WriteLine($"Primary H1 source UTC:    {primary.SourceCandleTimeUtc:yyyy-MM-dd HH:mm}");
                Console.WriteLine($"Primary H1 strategy date: {primary.StrategyDate:yyyy-MM-dd}");
            }
            else
            {
                Console.WriteLine("Primary level: none");
            }

            Console.WriteLine();
            Console.WriteLine($"Blocked by time: {result.BlockedByTime}");
            Console.WriteLine();
            Console.WriteLine($"FINAL SIGNAL: {(result.FinalSignal ? "BUY" : "NONE")}");
            Console.WriteLine("----------------------------------------");
        }

        private static string FormatNullable(double? value)
        {
            return value.HasValue ? value.Value.ToString("F5") : "n/a";
        }

        private static string FormatNullableBool(bool? value)
        {
            return value.HasValue ? value.Value.ToString() : "n/a (disabled)";
        }
    }
}
