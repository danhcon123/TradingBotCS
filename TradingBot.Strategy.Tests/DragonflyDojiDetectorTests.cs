using System;
using TradingBot.FXCM.Models;
using TradingBot.FXCM.Settings;
using TradingBot.FXCM.Strategy;
using Xunit;

namespace TradingBot.Strategy.Tests
{
    public class DragonflyDojiDetectorTests
    {
        private static readonly DateTime BaseTime = new DateTime(2026, 6, 15, 10, 0, 0, DateTimeKind.Utc);

        private static Candle M1(double open, double high, double low, double close)
        {
            return new Candle
            {
                Time = BaseTime,
                Open = open,
                High = high,
                Low = low,
                Close = close,
                Volume = 10
            };
        }

        private static DragonflyStrategySettings DefaultSettings(bool usePreviousRange = false)
        {
            return new DragonflyStrategySettings
            {
                MinLowerToBody = 5.0,
                MaxBodyPct = 20.0,
                UpperWickToleranceTicks = 0,
                TickSize = 0.01,
                UsePreviousRangeComparison = usePreviousRange,
                MinRangeVsPrevious = 1.5,
                UseBlockedHours = true
            };
        }

        // ---------------------------------------------------------------
        // 1-3: shape variants
        // ---------------------------------------------------------------

        [Fact]
        public void Test01_PerfectDragonfly_IsDetected()
        {
            var settings = DefaultSettings();
            var detector = new DragonflyDojiDetector(settings);
            var candle = M1(open: 100.00, high: 100.00, low: 95.00, close: 100.00);

            var result = detector.Analyze(candle, previous: null);

            Assert.True(result.HasRange);
            Assert.Equal(0.0, result.Body, 5);
            Assert.True(result.IsDragonfly);
        }

        [Fact]
        public void Test02_SmallBullishBody_IsDetected()
        {
            var settings = DefaultSettings();
            var detector = new DragonflyDojiDetector(settings);
            var candle = M1(open: 99.99, high: 100.00, low: 95.00, close: 100.00);

            var result = detector.Analyze(candle, previous: null);

            Assert.True(result.IsDragonfly);
        }

        [Fact]
        public void Test03_SmallBearishBody_IsDetected()
        {
            var settings = DefaultSettings();
            var detector = new DragonflyDojiDetector(settings);
            var candle = M1(open: 100.00, high: 100.00, low: 95.00, close: 99.99);

            var result = detector.Analyze(candle, previous: null);

            Assert.True(result.IsDragonfly);
        }

        // ---------------------------------------------------------------
        // 4-5: lower wick / body boundary
        // ---------------------------------------------------------------

        [Fact]
        public void Test04_LowerWickExactlyFiveTimesBody_Passes()
        {
            var settings = DefaultSettings();
            var detector = new DragonflyDojiDetector(settings);
            // body=1.00, lowerWick=5.00 (exactly 5x), upperWick=0, range=6.00
            var candle = M1(open: 100.00, high: 101.00, low: 95.00, close: 101.00);

            var result = detector.Analyze(candle, previous: null);

            Assert.Equal(1.00, result.Body, 5);
            Assert.Equal(5.00, result.LowerWick, 5);
            Assert.True(result.LowerWickRulePassed);
            Assert.True(result.IsDragonfly);
        }

        [Fact]
        public void Test05_LowerWickSlightlyBelowFiveTimesBody_Fails()
        {
            var settings = DefaultSettings();
            var detector = new DragonflyDojiDetector(settings);
            // body=1.00, lowerWick=4.99 (just under 5x)
            var candle = M1(open: 100.00, high: 101.00, low: 95.01, close: 101.00);

            var result = detector.Analyze(candle, previous: null);

            Assert.False(result.LowerWickRulePassed);
            Assert.False(result.IsDragonfly);
        }

        // ---------------------------------------------------------------
        // 6-7: body percentage boundary
        // ---------------------------------------------------------------

        [Fact]
        public void Test06_BodyExactlyTwentyPercentOfRange_Passes()
        {
            var settings = DefaultSettings();
            var detector = new DragonflyDojiDetector(settings);
            // body=1.00, range=5.00 => body% == 20% exactly. (lowerWick=4 here fails
            // rule B, which is fine - this test isolates rule C only.)
            var candle = M1(open: 100.00, high: 101.00, low: 96.00, close: 101.00);

            var result = detector.Analyze(candle, previous: null);

            Assert.Equal(20.0, result.BodyPercentage, 5);
            Assert.True(result.BodyRulePassed);
        }

        [Fact]
        public void Test07_BodyGreaterThanTwentyPercent_Fails()
        {
            var settings = DefaultSettings();
            var detector = new DragonflyDojiDetector(settings);
            // body=1.20, range=5.00 => body% == 24%
            var candle = M1(open: 100.00, high: 101.20, low: 96.20, close: 101.20);

            var result = detector.Analyze(candle, previous: null);

            Assert.Equal(24.0, result.BodyPercentage, 5);
            Assert.False(result.BodyRulePassed);
            Assert.False(result.IsDragonfly);
        }

        // ---------------------------------------------------------------
        // 8-9: upper wick tolerance
        // ---------------------------------------------------------------

        [Fact]
        public void Test08_ZeroUpperWick_WithZeroTolerance_Passes()
        {
            var settings = DefaultSettings();
            var detector = new DragonflyDojiDetector(settings);
            var candle = M1(open: 100.00, high: 100.00, low: 95.00, close: 100.00);

            var result = detector.Analyze(candle, previous: null);

            Assert.Equal(0.0, result.UpperWick, 5);
            Assert.True(result.UpperWickRulePassed);
        }

        [Fact]
        public void Test09_UpperWickAboveTolerance_Fails()
        {
            var settings = DefaultSettings();
            var detector = new DragonflyDojiDetector(settings);
            // upperWick = 0.01 with zero tolerance
            var candle = M1(open: 100.00, high: 100.01, low: 95.00, close: 100.00);

            var result = detector.Analyze(candle, previous: null);

            Assert.False(result.UpperWickRulePassed);
            Assert.False(result.IsDragonfly);
        }

        // ---------------------------------------------------------------
        // 10: zero range
        // ---------------------------------------------------------------

        [Fact]
        public void Test10_ZeroRange_Fails()
        {
            var settings = DefaultSettings();
            var detector = new DragonflyDojiDetector(settings);
            var candle = M1(open: 100.00, high: 100.00, low: 100.00, close: 100.00);

            var result = detector.Analyze(candle, previous: null);

            Assert.False(result.HasRange);
            Assert.False(result.LowerWickRulePassed);
            Assert.False(result.BodyRulePassed);
            Assert.False(result.UpperWickRulePassed);
            Assert.False(result.IsDragonfly);
        }

        // ---------------------------------------------------------------
        // 11: realistic XAU/USD decimal values
        // ---------------------------------------------------------------

        [Fact]
        public void Test11_RealisticXauUsdValues_IsDetected()
        {
            var settings = DefaultSettings();
            var detector = new DragonflyDojiDetector(settings);
            // body=0.02, upperWick=0, lowerWick=0.35, range=0.37
            var candle = M1(open: 2358.45, high: 2358.47, low: 2358.10, close: 2358.47);

            var result = detector.Analyze(candle, previous: null);

            Assert.True(result.IsDragonfly);
        }

        // ---------------------------------------------------------------
        // 12: tick-size / tolerance behavior
        // ---------------------------------------------------------------

        [Theory]
        [InlineData(100.02, true)]  // upperWick = 0.02 == 2 ticks * 0.01 -> passes (inclusive)
        [InlineData(100.03, false)] // upperWick = 0.03 -> fails
        public void Test12_UpperWickToleranceTicks_BoundaryBehavior(double high, bool expectedPass)
        {
            var settings = DefaultSettings();
            settings.UpperWickToleranceTicks = 2;
            settings.TickSize = 0.01;
            var detector = new DragonflyDojiDetector(settings);
            var candle = M1(open: 100.00, high: high, low: 95.00, close: 100.00);

            var result = detector.Analyze(candle, previous: null);

            Assert.Equal(expectedPass, result.UpperWickRulePassed);
        }

        // ---------------------------------------------------------------
        // 13-17: previous-range comparison ENABLED
        // ---------------------------------------------------------------

        [Fact]
        public void Test13_PreviousRangeComparisonEnabled_IsReflectedInAnalysis()
        {
            var settings = DefaultSettings(usePreviousRange: true);
            var detector = new DragonflyDojiDetector(settings);
            var previous = M1(open: 100.00, high: 104.00, low: 100.00, close: 100.00);
            var current = M1(open: 104.00, high: 104.00, low: 98.00, close: 104.00);

            var result = detector.Analyze(current, previous);

            Assert.True(result.PreviousRangeRuleEnabled);
        }

        [Fact]
        public void Test14_CurrentRangeExactlyOnePointFiveTimesPrevious_Passes()
        {
            var settings = DefaultSettings(usePreviousRange: true);
            var detector = new DragonflyDojiDetector(settings);
            var previous = M1(open: 100.00, high: 104.00, low: 100.00, close: 100.00); // range 4.00
            var current = M1(open: 104.00, high: 104.00, low: 98.00, close: 104.00);   // range 6.00 == 1.5x

            var result = detector.Analyze(current, previous);

            Assert.Equal(4.00, result.PreviousRange.Value, 5);
            Assert.True(result.PreviousRangeRulePassed);
            Assert.True(result.IsDragonfly);
        }

        [Fact]
        public void Test15_CurrentRangeBelowOnePointFiveTimesPrevious_Fails()
        {
            var settings = DefaultSettings(usePreviousRange: true);
            var detector = new DragonflyDojiDetector(settings);
            var previous = M1(open: 100.00, high: 104.00, low: 100.00, close: 100.00); // range 4.00
            var current = M1(open: 104.00, high: 104.00, low: 98.01, close: 104.00);   // range 5.99 < 6.00

            var result = detector.Analyze(current, previous);

            Assert.False(result.PreviousRangeRulePassed);
            Assert.False(result.IsDragonfly);
        }

        [Fact]
        public void Test16_PreviousRangeZero_Fails()
        {
            var settings = DefaultSettings(usePreviousRange: true);
            var detector = new DragonflyDojiDetector(settings);
            var previous = M1(open: 100.00, high: 100.00, low: 100.00, close: 100.00); // range 0
            var current = M1(open: 104.00, high: 104.00, low: 98.00, close: 104.00);

            var result = detector.Analyze(current, previous);

            Assert.Equal(0.0, result.PreviousRange.Value, 5);
            Assert.False(result.PreviousRangeRulePassed);
            Assert.False(result.IsDragonfly);
        }

        [Fact]
        public void Test17_MissingPreviousCandle_FailsSafely()
        {
            var settings = DefaultSettings(usePreviousRange: true);
            var detector = new DragonflyDojiDetector(settings);
            var current = M1(open: 104.00, high: 104.00, low: 98.00, close: 104.00);

            var result = detector.Analyze(current, previous: null);

            Assert.Null(result.PreviousRange);
            Assert.False(result.PreviousRangeRulePassed);
            Assert.False(result.IsDragonfly);
        }

        // ---------------------------------------------------------------
        // 18-21: previous-range comparison DISABLED
        // ---------------------------------------------------------------

        [Fact]
        public void Test18_PreviousRangeComparisonDisabled_IsReflectedInAnalysis()
        {
            var settings = DefaultSettings(usePreviousRange: false);
            var detector = new DragonflyDojiDetector(settings);
            var candle = M1(open: 100.00, high: 100.00, low: 95.00, close: 100.00);

            var result = detector.Analyze(candle, previous: null);

            Assert.False(result.PreviousRangeRuleEnabled);
            Assert.Null(result.PreviousRangeRulePassed);
        }

        [Fact]
        public void Test19_ValidDragonfly_PassesEvenWithSmallerRangeThanPrevious_WhenComparisonDisabled()
        {
            var settings = DefaultSettings(usePreviousRange: false);
            var detector = new DragonflyDojiDetector(settings);
            var previous = M1(open: 100.00, high: 200.00, low: 100.00, close: 100.00); // huge range 100.00
            var current = M1(open: 100.00, high: 100.00, low: 95.00, close: 100.00);   // small range 5.00

            var result = detector.Analyze(current, previous);

            Assert.Equal(100.00, result.PreviousRange.Value, 5); // still recorded for diagnostics
            Assert.True(result.IsDragonfly);
        }

        [Fact]
        public void Test20_MissingPreviousCandle_DoesNotFail_WhenComparisonDisabled()
        {
            var settings = DefaultSettings(usePreviousRange: false);
            var detector = new DragonflyDojiDetector(settings);
            var current = M1(open: 100.00, high: 100.00, low: 95.00, close: 100.00);

            var result = detector.Analyze(current, previous: null);

            Assert.Null(result.PreviousRange);
            Assert.Null(result.PreviousRangeRulePassed);
            Assert.True(result.IsDragonfly);
        }

        [Fact]
        public void Test21_OtherRulesStillApply_WhenComparisonDisabled()
        {
            var settings = DefaultSettings(usePreviousRange: false);
            var detector = new DragonflyDojiDetector(settings);
            // Lower wick rule deliberately fails (4.99 < 5x body of 1.00).
            var current = M1(open: 100.00, high: 101.00, low: 95.01, close: 101.00);

            var result = detector.Analyze(current, previous: null);

            Assert.False(result.LowerWickRulePassed);
            Assert.False(result.IsDragonfly);
        }

        // ---------------------------------------------------------------
        // Invalid OHLC protection
        // ---------------------------------------------------------------

        [Fact]
        public void Test22_HighBelowOpen_IsInvalidOhlc_AndNotDragonfly()
        {
            var settings = DefaultSettings();
            var detector = new DragonflyDojiDetector(settings);
            // High = 3300 but Open = 3301 -> High < Open, malformed.
            var candle = M1(open: 3301.00, high: 3300.00, low: 3295.00, close: 3300.50);

            var result = detector.Analyze(candle, previous: null);

            Assert.False(result.IsValidOhlc);
            Assert.False(result.IsDragonfly);
        }

        [Fact]
        public void Test23_LowAboveClose_IsInvalidOhlc_AndNotDragonfly()
        {
            var settings = DefaultSettings();
            var detector = new DragonflyDojiDetector(settings);
            // Low = 3300 but Close = 3299 -> Low > Close, malformed.
            var candle = M1(open: 3300.00, high: 3305.00, low: 3300.00, close: 3299.00);

            var result = detector.Analyze(candle, previous: null);

            Assert.False(result.IsValidOhlc);
            Assert.False(result.IsDragonfly);
        }

        [Fact]
        public void Test24_HighBelowLow_IsInvalidOhlc_AndNotDragonfly()
        {
            var settings = DefaultSettings();
            var detector = new DragonflyDojiDetector(settings);
            var candle = M1(open: 100.00, high: 90.00, low: 95.00, close: 100.00);

            var result = detector.Analyze(candle, previous: null);

            Assert.False(result.IsValidOhlc);
            Assert.False(result.IsDragonfly);
        }

        [Theory]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        [InlineData(double.NegativeInfinity)]
        public void Test25_NonFiniteOhlcValue_IsInvalidOhlc_AndNotDragonfly(double malformedHigh)
        {
            var settings = DefaultSettings();
            var detector = new DragonflyDojiDetector(settings);
            var candle = M1(open: 100.00, high: malformedHigh, low: 95.00, close: 100.00);

            var result = detector.Analyze(candle, previous: null);

            Assert.False(result.IsValidOhlc);
            Assert.False(result.IsDragonfly);
        }

        [Fact]
        public void Test26_ValidOhlc_PerfectDragonfly_IsValidOhlcTrue()
        {
            var settings = DefaultSettings();
            var detector = new DragonflyDojiDetector(settings);
            // High/Open/Close = 3400, Low = 3390 - the ideal geometric Dragonfly.
            var candle = M1(open: 3400.00, high: 3400.00, low: 3390.00, close: 3400.00);

            var result = detector.Analyze(candle, previous: null);

            Assert.True(result.IsValidOhlc);
            Assert.Equal(0.0, result.Body, 5);
            Assert.Equal(0.0, result.UpperWick, 5);
            Assert.Equal(10.0, result.LowerWick, 5);
            Assert.True(result.IsDragonfly);
        }

        [Fact]
        public void Test27_InvalidPreviousOhlc_TreatedLikePreviousRangeUnavailable()
        {
            var settings = DefaultSettings(usePreviousRange: true);
            var detector = new DragonflyDojiDetector(settings);
            var invalidPrevious = M1(open: 105.00, high: 100.00, low: 95.00, close: 102.00); // High < Open
            var current = M1(open: 104.00, high: 104.00, low: 98.00, close: 104.00);

            var result = detector.Analyze(current, invalidPrevious);

            Assert.Null(result.PreviousRange);
            Assert.False(result.PreviousRangeRulePassed);
            Assert.False(result.IsDragonfly);
        }

        // ---------------------------------------------------------------
        // CurrentToPreviousRangeRatio
        // ---------------------------------------------------------------

        [Fact]
        public void Test28_CurrentToPreviousRangeRatio_IsComputedCorrectly()
        {
            var settings = DefaultSettings(usePreviousRange: false);
            var detector = new DragonflyDojiDetector(settings);
            var previous = M1(open: 100.00, high: 101.80, low: 100.00, close: 100.00); // range 1.80
            var current = M1(open: 100.00, high: 100.00, low: 96.75, close: 100.00);   // range 3.25

            var result = detector.Analyze(current, previous);

            Assert.NotNull(result.CurrentToPreviousRangeRatio);
            Assert.Equal(3.25 / 1.80, result.CurrentToPreviousRangeRatio.Value, 5);
        }

        [Fact]
        public void Test29_CurrentToPreviousRangeRatio_IsComputed_EvenWhenComparisonDisabled()
        {
            var settings = DefaultSettings(usePreviousRange: false);
            var detector = new DragonflyDojiDetector(settings);
            var previous = M1(open: 100.00, high: 108.00, low: 100.00, close: 100.00); // range 8.00
            var current = M1(open: 100.00, high: 100.00, low: 95.00, close: 100.00);   // range 5.00

            var result = detector.Analyze(current, previous);

            Assert.False(result.PreviousRangeRuleEnabled);
            Assert.NotNull(result.CurrentToPreviousRangeRatio);
            Assert.Equal(5.00 / 8.00, result.CurrentToPreviousRangeRatio.Value, 5);
        }

        [Fact]
        public void Test30_CurrentToPreviousRangeRatio_IsNull_WhenPreviousMissing()
        {
            var detector = new DragonflyDojiDetector(DefaultSettings());
            var current = M1(open: 100.00, high: 100.00, low: 95.00, close: 100.00);

            var result = detector.Analyze(current, previous: null);

            Assert.Null(result.CurrentToPreviousRangeRatio);
        }

        [Fact]
        public void Test31_CurrentToPreviousRangeRatio_IsNull_WhenPreviousRangeIsZero()
        {
            var detector = new DragonflyDojiDetector(DefaultSettings());
            var previous = M1(open: 100.00, high: 100.00, low: 100.00, close: 100.00); // range 0
            var current = M1(open: 100.00, high: 100.00, low: 95.00, close: 100.00);

            var result = detector.Analyze(current, previous);

            Assert.Null(result.CurrentToPreviousRangeRatio);
        }

        // ---------------------------------------------------------------
        // Constructor / argument guards
        // ---------------------------------------------------------------

        [Fact]
        public void Constructor_NullSettings_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new DragonflyDojiDetector(null));
        }

        [Fact]
        public void Constructor_InvalidSettings_ThrowsArgumentOutOfRangeException()
        {
            // TickSize defaults to 0, which fails settings validation.
            var settings = new DragonflyStrategySettings();

            Assert.Throws<ArgumentOutOfRangeException>(() => new DragonflyDojiDetector(settings));
        }

        [Fact]
        public void Analyze_NullCurrentCandle_Throws()
        {
            var detector = new DragonflyDojiDetector(DefaultSettings());

            Assert.Throws<ArgumentNullException>(() => detector.Analyze(null, null));
        }
    }
}
