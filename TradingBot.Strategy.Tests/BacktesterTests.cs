using System;
using System.Collections.Generic;
using TradingBot.FXCM.Backtest;
using TradingBot.FXCM.Models;
using TradingBot.FXCM.Settings;
using Xunit;

namespace TradingBot.Strategy.Tests
{
    public class BacktesterTests
    {
        // Same small bullish Dragonfly shape used across Phase 2/4/6 tests:
        // body [2358.45, 2358.47], upper wick 0, lower wick [2358.10, 2358.45].
        private static Candle DragonflyM1(DateTime timeUtc)
        {
            return new Candle { Time = timeUtc, Open = 2358.45, High = 2358.47, Low = 2358.10, Close = 2358.47, Volume = 10 };
        }

        // Clearly NOT a Dragonfly: body is 50% of range, lower wick ratio well below 5x.
        private static Candle NonDragonflyM1(DateTime timeUtc)
        {
            return new Candle { Time = timeUtc, Open = 100.00, High = 110.00, Low = 90.00, Close = 110.00, Volume = 10 };
        }

        private static Candle H1(DateTime timeUtc, double high, double low)
        {
            return new Candle { Time = timeUtc, Open = (high + low) / 2.0, Close = (high + low) / 2.0, High = high, Low = low, Volume = 100 };
        }

        private static List<Candle> BuildH1History(int count, DateTime newestTimeUtc, double intersectingLow)
        {
            var candles = new List<Candle>();
            for (int i = 0; i < count; i++)
            {
                DateTime time = newestTimeUtc.AddDays(-i);
                double low = i == 0 ? intersectingLow : 9998.00;
                candles.Add(H1(time, high: 9999.00, low: low));
            }
            return candles;
        }

        private static DragonflyStrategySettings Settings(bool usePreviousRange = false, bool useBlockedHours = true)
        {
            return new DragonflyStrategySettings
            {
                H1ReferenceCount = 28,
                MinLowerToBody = 5.0,
                MaxBodyPct = 20.0,
                UpperWickToleranceTicks = 0,
                TickSize = 0.01,
                UsePreviousRangeComparison = usePreviousRange,
                MinRangeVsPrevious = 1.5,
                UseBlockedHours = useBlockedHours
            };
        }

        private static readonly DateTime NewestH1TimeUtc = new DateTime(2026, 6, 15, 4, 0, 0, DateTimeKind.Utc);
        // 09:00 UTC-4 = 13:00 UTC - well outside the 12:00-15:59 UTC-4 blocked window.
        private static readonly DateTime AllowedM1TimeUtc = new DateTime(2026, 6, 15, 13, 0, 0, DateTimeKind.Utc);

        // ---------------------------------------------------------------
        // Empty / trivial input
        // ---------------------------------------------------------------

        [Fact]
        public void Run_NullM1Candles_ReturnsZeroedResult_DoesNotThrow()
        {
            var result = Backtester.Run(null, new List<Candle>(), Settings());

            Assert.Equal(0, result.M1CandlesEvaluated);
            Assert.Equal(0, result.DragonfliesDetected);
            Assert.Empty(result.DragonflyResults);
        }

        [Fact]
        public void Run_EmptyM1Candles_ReturnsZeroedResult()
        {
            var result = Backtester.Run(new List<Candle>(), new List<Candle>(), Settings());

            Assert.Equal(0, result.M1CandlesEvaluated);
        }

        [Fact]
        public void Run_SingleM1Candle_EvaluatesNothing()
        {
            // No previous candle exists to pair with the only element - the loop starts at index 1.
            var m1 = new List<Candle> { DragonflyM1(AllowedM1TimeUtc) };

            var result = Backtester.Run(m1, new List<Candle>(), Settings());

            Assert.Equal(0, result.M1CandlesEvaluated);
        }

        [Fact]
        public void Run_NullEntryInM1List_IsSkipped_DoesNotThrow()
        {
            var m1 = new List<Candle>
            {
                DragonflyM1(AllowedM1TimeUtc.AddMinutes(-1)),
                null,
                DragonflyM1(AllowedM1TimeUtc)
            };

            var result = Backtester.Run(m1, new List<Candle>(), Settings());

            // index 1 (null) skipped; index 2 evaluated against index 1 (null previous) - fine.
            Assert.Equal(1, result.M1CandlesEvaluated);
        }

        // ---------------------------------------------------------------
        // Aggregate counting across the full gate sequence
        // ---------------------------------------------------------------

        [Fact]
        public void Run_MixedOutcomes_CountsEachGateCorrectly()
        {
            var h1Candles = BuildH1History(28, NewestH1TimeUtc, intersectingLow: 2358.30);
            var settings = Settings();

            var t0 = AllowedM1TimeUtc;
            var t1 = t0.AddMinutes(1);

            var m1 = new List<Candle>
            {
                NonDragonflyM1(t0), // seed / previous for the next candle
                DragonflyM1(t1),    // Dragonfly + full H1 + intersects -> BUY
            };

            var result = Backtester.Run(m1, h1Candles, settings);

            Assert.Equal(1, result.M1CandlesEvaluated); // only index 1 evaluated (index 0 is the seed)
            Assert.Equal(1, result.DragonfliesDetected);
            Assert.Equal(1, result.DragonfliesWithFullH1Set);
            Assert.Equal(1, result.DragonfliesWithIntersection);
            Assert.Equal(0, result.SignalsBlockedByTime);
            Assert.Equal(1, result.BuySignals);
        }

        [Fact]
        public void Run_BlockedTime_CountsAsDetectedAndIntersecting_ButNotBuy()
        {
            var h1Candles = BuildH1History(28, NewestH1TimeUtc, intersectingLow: 2358.30);
            var settings = Settings();

            var blockedTimeUtc = new DateTime(2026, 6, 15, 17, 30, 0, DateTimeKind.Utc); // 13:30 UTC-4 -> blocked
            var m1 = new List<Candle>
            {
                DragonflyM1(blockedTimeUtc.AddMinutes(-1)),
                DragonflyM1(blockedTimeUtc)
            };

            var result = Backtester.Run(m1, h1Candles, settings);

            Assert.Equal(1, result.DragonfliesDetected);
            Assert.Equal(1, result.DragonfliesWithFullH1Set);
            Assert.Equal(1, result.DragonfliesWithIntersection);
            Assert.Equal(1, result.SignalsBlockedByTime);
            Assert.Equal(0, result.BuySignals);
        }

        [Fact]
        public void Run_InsufficientH1History_CountsDetectedButNotFullH1Set()
        {
            var h1Candles = BuildH1History(27, NewestH1TimeUtc, intersectingLow: 2358.30); // one short
            var settings = Settings();

            var m1 = new List<Candle> { DragonflyM1(AllowedM1TimeUtc.AddMinutes(-1)), DragonflyM1(AllowedM1TimeUtc) };

            var result = Backtester.Run(m1, h1Candles, settings);

            Assert.Equal(1, result.DragonfliesDetected);
            Assert.Equal(0, result.DragonfliesWithFullH1Set);
            Assert.Equal(0, result.DragonfliesWithIntersection);
            Assert.Equal(0, result.BuySignals);
        }

        [Fact]
        public void Run_NonDragonflyCandles_AreNotCountedOrIncludedInDragonflyResults()
        {
            var m1 = new List<Candle>
            {
                NonDragonflyM1(AllowedM1TimeUtc.AddMinutes(-1)),
                NonDragonflyM1(AllowedM1TimeUtc)
            };

            var result = Backtester.Run(m1, new List<Candle>(), Settings());

            Assert.Equal(1, result.M1CandlesEvaluated);
            Assert.Equal(0, result.DragonfliesDetected);
            Assert.Empty(result.DragonflyResults);
        }

        // ---------------------------------------------------------------
        // Chronological pairing: candle i must be paired with candle i-1,
        // not some other candle.
        // ---------------------------------------------------------------

        [Fact]
        public void Run_PairsEachCandleWithTheImmediatelyPrecedingOne_NotSomeOtherCandle()
        {
            // Previous-range comparison enabled: current range must be >= 1.5x previous range.
            var settings = Settings(usePreviousRange: true);
            var h1Candles = BuildH1History(28, NewestH1TimeUtc, intersectingLow: 2358.30);

            // A "huge range" candle that would fail the comparison if used as the previous
            // candle for the Dragonfly, sitting BEFORE a small-range filler which is the
            // Dragonfly's real chronological predecessor.
            var hugeRange = new Candle
            {
                Time = AllowedM1TimeUtc.AddMinutes(-2),
                Open = 2359.00, High = 2500.00, Low = 2358.00, Close = 2359.00, Volume = 10
            };
            var smallRangeImmediatePredecessor = new Candle
            {
                Time = AllowedM1TimeUtc.AddMinutes(-1),
                Open = 2358.20, High = 2358.25, Low = 2358.15, Close = 2358.20, Volume = 10
            };
            var dragonfly = DragonflyM1(AllowedM1TimeUtc); // range 0.37

            var m1 = new List<Candle> { hugeRange, smallRangeImmediatePredecessor, dragonfly };

            var result = Backtester.Run(m1, h1Candles, settings);

            // Only the dragonfly candle (index 2) and the filler (index 1) get evaluated.
            // The dragonfly must be checked against smallRangeImmediatePredecessor (range
            // ~0.10), not hugeRange - so the 1.5x comparison passes and it qualifies.
            Assert.Equal(2, result.M1CandlesEvaluated);
            Assert.Equal(1, result.DragonfliesDetected);
            Assert.True(result.DragonflyResults[0].DragonflyAnalysis.PreviousRangeRulePassed);
        }

        // ---------------------------------------------------------------
        // DragonflyResults content and ordering
        // ---------------------------------------------------------------

        [Fact]
        public void Run_DragonflyResults_AreInChronologicalOrder_WithCorrectCandleTimes()
        {
            var h1Candles = BuildH1History(28, NewestH1TimeUtc, intersectingLow: 2358.30);
            var settings = Settings();

            var t1 = AllowedM1TimeUtc;
            var t2 = t1.AddMinutes(5);

            var m1 = new List<Candle>
            {
                DragonflyM1(t1.AddMinutes(-1)), // seed
                DragonflyM1(t1),
                NonDragonflyM1(t1.AddMinutes(1)), // gap, not a dragonfly
                DragonflyM1(t2)
            };

            var result = Backtester.Run(m1, h1Candles, settings);

            Assert.Equal(2, result.DragonfliesDetected);
            Assert.Equal(2, result.DragonflyResults.Count);
            Assert.Equal(t1, result.DragonflyResults[0].CandleTimeUtc);
            Assert.Equal(t2, result.DragonflyResults[1].CandleTimeUtc);
        }

        // ---------------------------------------------------------------
        // Variant comparison: same candles, config alone flips the outcome -
        // this is the whole point of building the backtester.
        // ---------------------------------------------------------------

        [Fact]
        public void Run_SameCandles_DifferentSettings_ProducesDifferentSignalCounts()
        {
            var h1Candles = BuildH1History(28, NewestH1TimeUtc, intersectingLow: 2358.30);

            var previous = new Candle
            {
                Time = AllowedM1TimeUtc.AddMinutes(-1),
                Open = 2359.00, High = 2360.00, Low = 2358.00, Close = 2359.00, Volume = 10
            };
            var dragonfly = DragonflyM1(AllowedM1TimeUtc); // range 0.37, far below 1.5x previous's range 2.00

            var m1 = new List<Candle> { previous, dragonfly };

            var variantA = Backtester.Run(m1, h1Candles, Settings(usePreviousRange: true));
            var variantB = Backtester.Run(m1, h1Candles, Settings(usePreviousRange: false));

            Assert.Equal(0, variantA.BuySignals);
            Assert.Equal(1, variantB.BuySignals);
        }

        // ---------------------------------------------------------------
        // Argument guards
        // ---------------------------------------------------------------

        [Fact]
        public void Run_NullSettings_Throws()
        {
            Assert.Throws<ArgumentNullException>(
                () => Backtester.Run(new List<Candle>(), new List<Candle>(), null));
        }

        [Fact]
        public void Run_InvalidSettings_ThrowsArgumentOutOfRangeException()
        {
            var settings = new DragonflyStrategySettings(); // TickSize defaults to 0

            Assert.Throws<ArgumentOutOfRangeException>(
                () => Backtester.Run(new List<Candle>(), new List<Candle>(), settings));
        }
    }
}
