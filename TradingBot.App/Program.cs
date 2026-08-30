using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TradingBot.FXCM;
using TradingBot.FXCM.Backtest;
using TradingBot.FXCM.Data;
using TradingBot.FXCM.Import;
using TradingBot.FXCM.Models;
using TradingBot.FXCM.Settings;

namespace TradingBot.App
{
    class Program
    {
        private const string Instrument = "XAU/USD";

        // ForexConnect timeframe codes: minutes are lowercase ("m1"), hours are uppercase
        // ("H1") - uppercase "M1" means MONTHLY, not 1-minute. Easy to get backwards.
        private const string M1Timeframe = "m1";
        private const string H1Timeframe = "H1";

        // FXCMPriceHistory.GetCandles caps each response at 100 bars, so the importer pulls
        // a multi-day window in fixed-size chunks. Kept well under the 100-bar cap for each
        // timeframe as a safety margin.
        private static readonly TimeSpan M1ChunkSize = TimeSpan.FromMinutes(60);
        private static readonly TimeSpan H1ChunkSize = TimeSpan.FromDays(3);

        // Conservative validation-scale window: enough to exercise the full backfill +
        // integrity-check pipeline without a multi-hour live run. A full 6-month M1
        // backfill is many thousands of chunked requests and can take hours - only widen
        // these to the real target (e.g. 180 days M1 / 240 days H1, so H1 reference history
        // exists well before the M1 window even starts) once this smaller run has been
        // verified end to end. The importer is resumable, so widening later and re-running
        // simply continues from what's already stored - it never re-downloads anything.
        private static readonly TimeSpan M1LookbackWindow = TimeSpan.FromDays(2);
        private static readonly TimeSpan H1LookbackWindow = TimeSpan.FromDays(45);

        private static readonly string DatabasePath = ResolveDatabasePath();

        /// <summary>
        /// Places candles.db at &lt;solution root&gt;/data/candles.db rather than inside the
        /// build output directory. Living under bin/ would mean a `dotnet clean` or a
        /// plain deletion of bin/obj - both completely normal things to do - would silently
        /// wipe out downloaded history along with build artifacts. The solution root is
        /// found by walking up from the running exe's directory looking for the .sln file,
        /// so this works regardless of configuration/platform/TFM subfolder depth.
        /// </summary>
        private static string ResolveDatabasePath()
        {
            string root = FindSolutionRootDirectory() ?? AppDomain.CurrentDomain.BaseDirectory;
            return Path.Combine(root, "data", "candles.db");
        }

        private static string FindSolutionRootDirectory()
        {
            var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);

            while (directory != null)
            {
                if (directory.GetFiles("*.sln").Length > 0)
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            return null;
        }

        static void Main(string[] args)
        {
            Console.WriteLine("Trading Bot Starting");

            string dbDirectory = Path.GetDirectoryName(DatabasePath);
            if (!string.IsNullOrEmpty(dbDirectory))
            {
                Directory.CreateDirectory(dbDirectory);
            }

            Console.WriteLine($"Database: {DatabasePath}");

            var repository = new CandleRepository(DatabasePath);

            // Backtesting only reads what has already been downloaded - it needs no live
            // FXCM connection at all. This is the entire point of Phase 7A/7B: download
            // once, then iterate on the strategy against stored data as many times as you
            // like without touching the API again.
            RunBacktest(repository);

            FXCMConnection fxcm = new FXCMConnection();

            bool connected = fxcm.Connect(
                "86082266",
                "Hoang1997!",
                "http://www.fxcorporate.com/Hosts.jsp",
                "Real"
            );

            Console.WriteLine(
               "Connected: " + connected
            );

            if (connected)
            {
                RunBackfillAndIntegrityCheck(fxcm, repository);
            }

            Console.WriteLine("Bot running. Press ENTER to stop.");

            Console.ReadLine();
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

            // GetCandles is a half-open [from, to) range - extend by one second so the very
            // last stored candle of each timeframe is actually included.
            IReadOnlyList<Candle> m1Candles = repository.GetCandles(Instrument, M1Timeframe, m1FromUtc.Value, m1ToUtc.Value.AddSeconds(1));
            IReadOnlyList<Candle> h1Candles = repository.GetCandles(Instrument, H1Timeframe, h1FromUtc.Value, h1ToUtc.Value.AddSeconds(1));

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

        private static void RunBackfillAndIntegrityCheck(FXCMConnection fxcm, CandleRepository repository)
        {
            FXCMPriceHistory history = fxcm.GetPriceHistory();
            var importer = new FXCMHistoricalImporter(history.GetCandles, repository);

            DateTime nowUtc = DateTime.UtcNow;
            DateTime m1FromUtc = nowUtc - M1LookbackWindow;
            DateTime h1FromUtc = nowUtc - H1LookbackWindow;

            BackfillTimeframe(importer, Instrument, M1Timeframe, m1FromUtc, nowUtc, M1ChunkSize);
            BackfillTimeframe(importer, Instrument, H1Timeframe, h1FromUtc, nowUtc, H1ChunkSize);

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
            Console.WriteLine($"Candles received: {result.CandlesReceived}, inserted (new): {result.CandlesInserted}");
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
    }
}
