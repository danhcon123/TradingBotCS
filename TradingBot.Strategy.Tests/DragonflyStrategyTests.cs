using System;
using System.Collections.Generic;
using TradingBot.FXCM.Models;
using TradingBot.FXCM.Settings;
using TradingBot.FXCM.Strategy;
using Xunit;

namespace TradingBot.Strategy.Tests
{
    public class DragonflyStrategyTests
    {
        // Small bullish Dragonfly (also used across Phase 2/4 tests):
        // body [2358.45, 2358.47], upper wick 0, lower wick [2358.10, 2358.45].
        private static Candle DragonflyM1(DateTime timeUtc)
        {
            return new Candle
            {
                Time = timeUtc,
                Open = 2358.45,
                High = 2358.47,
                Low = 2358.10,
                Close = 2358.47,
                Volume = 10
            };
        }

        // Clearly NOT a Dragonfly: body is 50% of range, lower wick ratio well below 5x.
        private static Candle NonDragonflyM1(DateTime timeUtc)
        {
            return new Candle
            {
                Time = timeUtc,
                Open = 100.00,
                High = 110.00,
                Low = 90.00,
                Close = 110.00,
                Volume = 10
            };
        }

        private static Candle H1(DateTime timeUtc, double high, double low)
        {
            return new Candle
            {
                Time = timeUtc,
                Open = (high + low) / 2.0,
                Close = (high + low) / 2.0,
                High = high,
                Low = low,
                Volume = 100
            };
        }

        /// <summary>
        /// Builds <paramref name="count"/> consecutive daily qualifying H1 candles (00:00
        /// UTC-4 = 04:00 UTC), newest at <paramref name="newestTimeUtc"/>. The newest
        /// candle's Low is set to <paramref name="intersectingLow"/> so it falls inside the
        /// DragonflyM1 lower wick above; every other level across all sets is placed far
        /// away (9998/9999) so it never intersects.
        /// </summary>
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

        private static DragonflyStrategySettings Settings(
            int h1ReferenceCount = 28,
            bool usePreviousRange = false,
            bool useBlockedHours = true)
        {
            return new DragonflyStrategySettings
            {
                H1ReferenceCount = h1ReferenceCount,
                MinLowerToBody = 5.0,
                MaxBodyPct = 20.0,
                UpperWickToleranceTicks = 0,
                TickSize = 0.01,
                UsePreviousRangeComparison = usePreviousRange,
                MinRangeVsPrevious = 1.5,
                UseBlockedHours = useBlockedHours
            };
        }

        // A same-day H1 candle that has clearly completed before any of the M1 candles
        // below (all at 13:00 UTC or later on the same day).
        private static readonly DateTime NewestH1TimeUtc = new DateTime(2026, 6, 15, 4, 0, 0, DateTimeKind.Utc);

        // 09:00 UTC-4 = 13:00 UTC - well outside the 12:00-15:59 UTC-4 blocked window.
        private static readonly DateTime AllowedM1TimeUtc = new DateTime(2026, 6, 15, 13, 0, 0, DateTimeKind.Utc);

        // ---------------------------------------------------------------
        // Valid signal: full pipeline -> BUY
        // ---------------------------------------------------------------

        [Fact]
        public void ValidDragonfly_FullH1Set_Intersection_AllowedTime_ProducesBuy()
        {
            var strategy = new DragonflyStrategy(Settings());
            var current = DragonflyM1(AllowedM1TimeUtc);
            var h1Candles = BuildH1History(28, NewestH1TimeUtc, intersectingLow: 2358.30);

            var result = strategy.Evaluate(current, previousM1: null, h1Candles: h1Candles);

            Assert.True(result.DragonflyAnalysis.IsDragonfly);
            Assert.True(result.H1Evaluated);
            Assert.Equal(28, result.ReferenceSetCount);
            Assert.True(result.HasFullH1Set);
            Assert.True(result.IntersectionEvaluated);
            Assert.True(result.HasIntersection);
            Assert.NotNull(result.PrimaryIntersection);
            Assert.True(result.TimeFilterEvaluated);
            Assert.False(result.BlockedByTime);
            Assert.Equal(SignalType.Buy, result.Signal);
            Assert.True(result.FinalSignal);
            Assert.Equal(current.Time.AddMinutes(1), result.EvaluationTimeUtc);
        }

        // ---------------------------------------------------------------
        // Invalid Dragonfly: H1 level would cross the candle, but it must never matter,
        // and H1 must never even be computed.
        // ---------------------------------------------------------------

        [Fact]
        public void InvalidDragonfly_H1LevelWouldCross_ProducesNoSignal_AndSkipsH1Entirely()
        {
            var strategy = new DragonflyStrategy(Settings());
            var current = NonDragonflyM1(AllowedM1TimeUtc);
            // A full, valid 28-set history whose newest Low (95.00) sits squarely inside
            // what would be this candle's [90, 100] range, if it were ever checked.
            var h1Candles = BuildH1History(28, NewestH1TimeUtc, intersectingLow: 95.00);

            var result = strategy.Evaluate(current, previousM1: null, h1Candles: h1Candles);

            Assert.False(result.DragonflyAnalysis.IsDragonfly);
            Assert.False(result.H1Evaluated); // proves H1 was never even queried
            Assert.Equal(0, result.ReferenceSetCount);
            Assert.False(result.HasFullH1Set);
            Assert.False(result.IntersectionEvaluated);
            Assert.False(result.HasIntersection);
            Assert.Empty(result.Intersections);
            Assert.Null(result.PrimaryIntersection);
            Assert.False(result.TimeFilterEvaluated);
            Assert.Equal(SignalType.None, result.Signal);
            Assert.False(result.FinalSignal);
        }

        // ---------------------------------------------------------------
        // Intersection only inside the candle body -> NO SIGNAL
        // ---------------------------------------------------------------

        [Fact]
        public void ValidDragonfly_LevelInsideBodyOnly_ProducesNoSignal()
        {
            var strategy = new DragonflyStrategy(Settings());
            var current = DragonflyM1(AllowedM1TimeUtc);
            // Body spans [2358.45, 2358.47]; 2358.46 is inside the body, not the lower wick.
            var h1Candles = BuildH1History(28, NewestH1TimeUtc, intersectingLow: 2358.46);

            var result = strategy.Evaluate(current, previousM1: null, h1Candles: h1Candles);

            Assert.True(result.DragonflyAnalysis.IsDragonfly);
            Assert.True(result.HasFullH1Set);
            Assert.True(result.IntersectionEvaluated);
            Assert.False(result.HasIntersection);
            Assert.Equal(SignalType.None, result.Signal);
        }

        // ---------------------------------------------------------------
        // Only 27 H1 reference sets -> NO SIGNAL
        // ---------------------------------------------------------------

        [Fact]
        public void ValidDragonfly_Intersection_OnlyTwentySevenH1Sets_ProducesNoSignal()
        {
            var strategy = new DragonflyStrategy(Settings());
            var current = DragonflyM1(AllowedM1TimeUtc);
            var h1Candles = BuildH1History(27, NewestH1TimeUtc, intersectingLow: 2358.30);

            var result = strategy.Evaluate(current, previousM1: null, h1Candles: h1Candles);

            Assert.True(result.DragonflyAnalysis.IsDragonfly);
            Assert.True(result.H1Evaluated);
            Assert.Equal(27, result.ReferenceSetCount);
            Assert.False(result.HasFullH1Set);
            Assert.False(result.IntersectionEvaluated); // intersection scan never even ran
            Assert.False(result.HasIntersection);
            Assert.Equal(SignalType.None, result.Signal);
        }

        // ---------------------------------------------------------------
        // Blocked time -> NO SIGNAL, even with everything else valid
        // ---------------------------------------------------------------

        [Fact]
        public void ValidDragonfly_Intersection_FullH1Set_BlockedTime_ProducesNoSignal()
        {
            var strategy = new DragonflyStrategy(Settings());
            // 13:30 UTC-4 = 17:30 UTC -> inside the 12:00-15:59 UTC-4 blocked window.
            var blockedTimeUtc = new DateTime(2026, 6, 15, 17, 30, 0, DateTimeKind.Utc);
            var current = DragonflyM1(blockedTimeUtc);
            var h1Candles = BuildH1History(28, NewestH1TimeUtc, intersectingLow: 2358.30);

            var result = strategy.Evaluate(current, previousM1: null, h1Candles: h1Candles);

            Assert.True(result.DragonflyAnalysis.IsDragonfly);
            Assert.True(result.HasFullH1Set);
            Assert.True(result.HasIntersection);
            Assert.True(result.TimeFilterEvaluated);
            Assert.True(result.BlockedByTime);
            Assert.Equal(SignalType.None, result.Signal);
            Assert.False(result.FinalSignal);
        }

        // ---------------------------------------------------------------
        // Previous-M1 comparison: Variant A (enabled) vs Variant B (disabled),
        // same candles, configuration alone flips the outcome.
        // ---------------------------------------------------------------

        [Fact]
        public void VariantA_PreviousRangeComparisonEnabled_SmallerRangeThanRequired_ProducesNoSignal()
        {
            var strategy = new DragonflyStrategy(Settings(usePreviousRange: true));
            var current = DragonflyM1(AllowedM1TimeUtc); // range 0.37
            // Previous candle's huge range (2.00) makes 1.5x requirement (3.00) unreachable.
            var previous = new Candle
            {
                Time = AllowedM1TimeUtc.AddMinutes(-1),
                Open = 2359.00,
                High = 2360.00,
                Low = 2358.00,
                Close = 2359.00,
                Volume = 10
            };
            var h1Candles = BuildH1History(28, NewestH1TimeUtc, intersectingLow: 2358.30);

            var result = strategy.Evaluate(current, previous, h1Candles);

            Assert.False(result.DragonflyAnalysis.IsDragonfly);
            Assert.False(result.DragonflyAnalysis.PreviousRangeRulePassed);
            Assert.Equal(SignalType.None, result.Signal);
        }

        [Fact]
        public void VariantB_PreviousRangeComparisonDisabled_SameCandles_ProducesBuy()
        {
            var strategy = new DragonflyStrategy(Settings(usePreviousRange: false));
            var current = DragonflyM1(AllowedM1TimeUtc);
            var previous = new Candle
            {
                Time = AllowedM1TimeUtc.AddMinutes(-1),
                Open = 2359.00,
                High = 2360.00,
                Low = 2358.00,
                Close = 2359.00,
                Volume = 10
            };
            var h1Candles = BuildH1History(28, NewestH1TimeUtc, intersectingLow: 2358.30);

            var result = strategy.Evaluate(current, previous, h1Candles);

            Assert.True(result.DragonflyAnalysis.IsDragonfly);
            Assert.False(result.DragonflyAnalysis.PreviousRangeRuleEnabled);
            Assert.Null(result.DragonflyAnalysis.PreviousRangeRulePassed);
            Assert.Equal(SignalType.Buy, result.Signal);
        }

        // ---------------------------------------------------------------
        // The critical H1-close boundary: evaluation time is currentM1.Time + 1 minute,
        // never currentM1.Time itself.
        // ---------------------------------------------------------------

        [Fact]
        public void H1Boundary_M1At0458_EvaluatesAt0459_TodaysH1NotYetCompleted_ProducesNoSignal()
        {
            // H1ReferenceCount = 1 isolates the boundary: the only candidate is today's
            // 04:00-05:00 UTC H1 candle.
            var strategy = new DragonflyStrategy(Settings(h1ReferenceCount: 1));
            var current = DragonflyM1(new DateTime(2026, 6, 15, 4, 58, 0, DateTimeKind.Utc));
            var h1Candles = new List<Candle>
            {
                H1(new DateTime(2026, 6, 15, 4, 0, 0, DateTimeKind.Utc), high: 9999.00, low: 2358.30)
            };

            var result = strategy.Evaluate(current, previousM1: null, h1Candles: h1Candles);

            Assert.True(result.DragonflyAnalysis.IsDragonfly);
            Assert.Equal(0, result.ReferenceSetCount);
            Assert.False(result.HasFullH1Set);
            Assert.Equal(SignalType.None, result.Signal);
        }

        [Fact]
        public void H1Boundary_M1At0459_EvaluatesAt0500_TodaysH1NowCompleted_ProducesBuy()
        {
            var strategy = new DragonflyStrategy(Settings(h1ReferenceCount: 1));
            var current = DragonflyM1(new DateTime(2026, 6, 15, 4, 59, 0, DateTimeKind.Utc));
            var h1Candles = new List<Candle>
            {
                H1(new DateTime(2026, 6, 15, 4, 0, 0, DateTimeKind.Utc), high: 9999.00, low: 2358.30)
            };

            var result = strategy.Evaluate(current, previousM1: null, h1Candles: h1Candles);

            Assert.True(result.DragonflyAnalysis.IsDragonfly);
            Assert.Equal(1, result.ReferenceSetCount);
            Assert.True(result.HasFullH1Set);
            Assert.True(result.HasIntersection);
            Assert.Equal(SignalType.Buy, result.Signal);
        }

        // ---------------------------------------------------------------
        // The critical time-filter boundary: IsBlocked uses the candle's OPEN timestamp,
        // never the +1 minute H1-evaluation instant.
        // ---------------------------------------------------------------

        [Fact]
        public void TimeFilter_UsesCandleOpenTime_NotCloseTime()
        {
            var strategy = new DragonflyStrategy(Settings());
            // Open = 19:59 UTC = 15:59 UTC-4 -> blocked using open time.
            // Close (+1 min) = 20:00 UTC = 16:00 UTC-4 -> would be ALLOWED if the strategy
            // mistakenly used the close/H1-evaluation instant for the time filter instead.
            var current = DragonflyM1(new DateTime(2026, 6, 15, 19, 59, 0, DateTimeKind.Utc));
            var h1Candles = BuildH1History(28, NewestH1TimeUtc, intersectingLow: 2358.30);

            var result = strategy.Evaluate(current, previousM1: null, h1Candles: h1Candles);

            Assert.True(result.DragonflyAnalysis.IsDragonfly);
            Assert.True(result.HasFullH1Set);
            Assert.True(result.HasIntersection);
            Assert.True(result.BlockedByTime); // would be false if close time were used
            Assert.Equal(SignalType.None, result.Signal);
            Assert.Equal(current.Time, result.CandleTimeUtc);
            Assert.Equal(current.Time.AddMinutes(1), result.EvaluationTimeUtc);
        }

        // ---------------------------------------------------------------
        // Diagnostic fields
        // ---------------------------------------------------------------

        [Fact]
        public void CandleTimeUtcMinus4_ReflectsStrategyLocalConversion()
        {
            var strategy = new DragonflyStrategy(Settings());
            var current = DragonflyM1(AllowedM1TimeUtc); // 13:00 UTC = 09:00 UTC-4
            var h1Candles = BuildH1History(28, NewestH1TimeUtc, intersectingLow: 2358.30);

            var result = strategy.Evaluate(current, previousM1: null, h1Candles: h1Candles);

            Assert.Equal(new DateTime(2026, 6, 15, 9, 0, 0), result.CandleTimeUtcMinus4);
        }

        [Fact]
        public void EvaluationTimeUtc_IsOneMinuteAfterCandleOpenTime()
        {
            var strategy = new DragonflyStrategy(Settings());
            var current = DragonflyM1(AllowedM1TimeUtc);
            var h1Candles = BuildH1History(28, NewestH1TimeUtc, intersectingLow: 2358.30);

            var result = strategy.Evaluate(current, previousM1: null, h1Candles: h1Candles);

            Assert.Equal(AllowedM1TimeUtc, result.CandleTimeUtc);
            Assert.Equal(AllowedM1TimeUtc.AddMinutes(1), result.EvaluationTimeUtc);
            Assert.NotEqual(result.CandleTimeUtc, result.EvaluationTimeUtc);
        }

        // ---------------------------------------------------------------
        // Constructor / argument guards
        // ---------------------------------------------------------------

        [Fact]
        public void Constructor_NullSettings_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new DragonflyStrategy(null));
        }

        [Fact]
        public void Constructor_InvalidSettings_ThrowsArgumentOutOfRangeException()
        {
            // TickSize defaults to 0, which fails full settings validation.
            var settings = new DragonflyStrategySettings();

            Assert.Throws<ArgumentOutOfRangeException>(() => new DragonflyStrategy(settings));
        }

        [Fact]
        public void Evaluate_NullCurrentCandle_Throws()
        {
            var strategy = new DragonflyStrategy(Settings());

            Assert.Throws<ArgumentNullException>(() => strategy.Evaluate(null, null, null));
        }

        [Fact]
        public void Evaluate_NullH1Candles_DoesNotThrow_ProducesNoSignalForConfirmedDragonfly()
        {
            var strategy = new DragonflyStrategy(Settings());
            var current = DragonflyM1(AllowedM1TimeUtc);

            var result = strategy.Evaluate(current, previousM1: null, h1Candles: null);

            Assert.True(result.DragonflyAnalysis.IsDragonfly);
            Assert.Equal(0, result.ReferenceSetCount);
            Assert.False(result.HasFullH1Set);
            Assert.Equal(SignalType.None, result.Signal);
        }

        // ---------------------------------------------------------------
        // Settings are snapshotted at construction: mutating the caller's original
        // settings object afterward must not affect an already-running strategy.
        // ---------------------------------------------------------------

        [Fact]
        public void MutatingOriginalSettingsAfterConstruction_DoesNotAffectRunningStrategy()
        {
            var originalSettings = Settings(); // UsePreviousRangeComparison = false
            var strategy = new DragonflyStrategy(originalSettings);
            var current = DragonflyM1(AllowedM1TimeUtc);
            var h1Candles = BuildH1History(28, NewestH1TimeUtc, intersectingLow: 2358.30);

            // Mutate the original object into something that would reject this exact
            // candle (previous-range comparison now required, but no previous candle is
            // supplied to Evaluate below) and even into an outright invalid configuration.
            originalSettings.UsePreviousRangeComparison = true;
            originalSettings.MinLowerToBody = -100;

            var result = strategy.Evaluate(current, previousM1: null, h1Candles: h1Candles);

            // The strategy must still behave per its original, validated configuration
            // (UsePreviousRangeComparison = false) - the mutation above must be invisible.
            Assert.True(result.DragonflyAnalysis.IsDragonfly);
            Assert.False(result.DragonflyAnalysis.PreviousRangeRuleEnabled);
            Assert.Equal(SignalType.Buy, result.Signal);
        }

        [Fact]
        public void CopyConstructor_NullOther_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new DragonflyStrategySettings(null));
        }

        [Fact]
        public void CopyConstructor_CopiesEveryField()
        {
            var original = new DragonflyStrategySettings
            {
                H1ReferenceCount = 14,
                MinLowerToBody = 4.0,
                MaxBodyPct = 25.0,
                UpperWickToleranceTicks = 2,
                TickSize = 0.05,
                UsePreviousRangeComparison = false,
                MinRangeVsPrevious = 2.0,
                UseBlockedHours = false
            };

            var copy = new DragonflyStrategySettings(original);

            Assert.Equal(original.H1ReferenceCount, copy.H1ReferenceCount);
            Assert.Equal(original.MinLowerToBody, copy.MinLowerToBody, 10);
            Assert.Equal(original.MaxBodyPct, copy.MaxBodyPct, 10);
            Assert.Equal(original.UpperWickToleranceTicks, copy.UpperWickToleranceTicks);
            Assert.Equal(original.TickSize, copy.TickSize, 10);
            Assert.Equal(original.UsePreviousRangeComparison, copy.UsePreviousRangeComparison);
            Assert.Equal(original.MinRangeVsPrevious, copy.MinRangeVsPrevious, 10);
            Assert.Equal(original.UseBlockedHours, copy.UseBlockedHours);
        }
    }
}
