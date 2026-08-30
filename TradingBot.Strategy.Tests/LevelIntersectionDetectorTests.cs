using System;
using System.Collections.Generic;
using TradingBot.FXCM.Models;
using TradingBot.FXCM.Strategy;
using Xunit;

namespace TradingBot.Strategy.Tests
{
    public class LevelIntersectionDetectorTests
    {
        private static readonly DateTime BaseTime = new DateTime(2026, 6, 15, 10, 0, 0, DateTimeKind.Utc);

        // Small bullish Dragonfly: body [99.00, 100.00], upper wick (100.00, 100.50],
        // lower wick [90.00, 99.00].
        private static Candle DragonflyCandle()
        {
            return new Candle
            {
                Time = BaseTime,
                Open = 99.00,
                High = 100.50,
                Low = 90.00,
                Close = 100.00,
                Volume = 10
            };
        }

        private static DragonflyAnalysis Analysis(bool isDragonfly)
        {
            return new DragonflyAnalysis { IsDragonfly = isDragonfly };
        }

        private static H1ReferenceSet Set(DateTime sourceTimeUtc, double high, double midpoint, double low)
        {
            return new H1ReferenceSet
            {
                SourceCandleTimeUtc = sourceTimeUtc,
                StrategyDate = sourceTimeUtc.Date,
                High = high,
                Midpoint = midpoint,
                Low = low
            };
        }

        // ---------------------------------------------------------------
        // 1-3: intersecting positions within the lower wick
        // ---------------------------------------------------------------

        [Fact]
        public void Test01_LevelExactlyAtCandleLow_Intersects()
        {
            var detector = new LevelIntersectionDetector();
            var candle = DragonflyCandle();
            var sets = new List<H1ReferenceSet> { Set(BaseTime.AddDays(-1), high: 200, midpoint: 300, low: 90.00) };

            var result = detector.FindIntersections(Analysis(true), candle, sets);

            Assert.Single(result);
            Assert.Equal(PriceLevelType.Low, result[0].Level.LevelType);
        }

        [Fact]
        public void Test02_LevelExactlyAtLowerWickTop_BottomOfBody_Intersects()
        {
            var detector = new LevelIntersectionDetector();
            var candle = DragonflyCandle();
            // lowerWickTop == Math.Min(Open, Close) == 99.00
            var sets = new List<H1ReferenceSet> { Set(BaseTime.AddDays(-1), high: 99.00, midpoint: 200, low: 300) };

            var result = detector.FindIntersections(Analysis(true), candle, sets);

            Assert.Single(result);
            Assert.Equal(PriceLevelType.High, result[0].Level.LevelType);
        }

        [Fact]
        public void Test03_LevelInMiddleOfLowerWick_Intersects()
        {
            var detector = new LevelIntersectionDetector();
            var candle = DragonflyCandle();
            var sets = new List<H1ReferenceSet> { Set(BaseTime.AddDays(-1), high: 200, midpoint: 95.00, low: 300) };

            var result = detector.FindIntersections(Analysis(true), candle, sets);

            Assert.Single(result);
            Assert.Equal(PriceLevelType.Midpoint, result[0].Level.LevelType);
        }

        // ---------------------------------------------------------------
        // 4-7: non-intersecting positions
        // ---------------------------------------------------------------

        [Fact]
        public void Test04_LevelBelowCandleLow_DoesNotIntersect()
        {
            var detector = new LevelIntersectionDetector();
            var candle = DragonflyCandle();
            var sets = new List<H1ReferenceSet> { Set(BaseTime.AddDays(-1), high: 200, midpoint: 300, low: 89.00) };

            var result = detector.FindIntersections(Analysis(true), candle, sets);

            Assert.Empty(result);
        }

        [Fact]
        public void Test05_LevelInsideCandleBody_DoesNotIntersect()
        {
            var detector = new LevelIntersectionDetector();
            var candle = DragonflyCandle();
            // Body spans [99.00, 100.00]; 99.50 is inside it, not the lower wick.
            var sets = new List<H1ReferenceSet> { Set(BaseTime.AddDays(-1), high: 99.50, midpoint: 200, low: 300) };

            var result = detector.FindIntersections(Analysis(true), candle, sets);

            Assert.Empty(result);
        }

        [Fact]
        public void Test06_LevelInsideUpperWick_DoesNotIntersect()
        {
            var detector = new LevelIntersectionDetector();
            var candle = DragonflyCandle();
            // Upper wick spans (100.00, 100.50]; 100.25 is inside it.
            var sets = new List<H1ReferenceSet> { Set(BaseTime.AddDays(-1), high: 100.25, midpoint: 200, low: 300) };

            var result = detector.FindIntersections(Analysis(true), candle, sets);

            Assert.Empty(result);
        }

        [Fact]
        public void Test07_LevelAboveCandleHigh_DoesNotIntersect()
        {
            var detector = new LevelIntersectionDetector();
            var candle = DragonflyCandle();
            var sets = new List<H1ReferenceSet> { Set(BaseTime.AddDays(-1), high: 101.00, midpoint: 200, low: 300) };

            var result = detector.FindIntersections(Analysis(true), candle, sets);

            Assert.Empty(result);
        }

        // ---------------------------------------------------------------
        // 8-9: multiple intersections are all preserved
        // ---------------------------------------------------------------

        [Fact]
        public void Test08And09_MultipleIntersections_AreAllPreserved()
        {
            var detector = new LevelIntersectionDetector();
            var candle = DragonflyCandle();

            // setA (newest): High=97 (in), Mid=200 (out), Low=91 (in) -> 2 intersections
            var setA = Set(BaseTime.AddDays(-1), high: 97.00, midpoint: 200.00, low: 91.00);
            // setB (middle): High=300 (out), Mid=93 (in), Low=400 (out) -> 1 intersection
            var setB = Set(BaseTime.AddDays(-2), high: 300.00, midpoint: 93.00, low: 400.00);
            // setC (oldest): all outside -> 0 intersections
            var setC = Set(BaseTime.AddDays(-3), high: 500.00, midpoint: 600.00, low: 700.00);

            var result = detector.FindIntersections(Analysis(true), candle, new List<H1ReferenceSet> { setA, setB, setC });

            Assert.Equal(3, result.Count);
            Assert.Equal(97.00, result[0].Level.Price, 5);
            Assert.Equal(PriceLevelType.High, result[0].Level.LevelType);
            Assert.Equal(91.00, result[1].Level.Price, 5);
            Assert.Equal(PriceLevelType.Low, result[1].Level.LevelType);
            Assert.Equal(93.00, result[2].Level.Price, 5);
            Assert.Equal(PriceLevelType.Midpoint, result[2].Level.LevelType);
        }

        // ---------------------------------------------------------------
        // 10-11: primary intersection priority
        // ---------------------------------------------------------------

        [Fact]
        public void Test10_PrimaryIntersection_UsesNewestReferenceSetFirst()
        {
            var detector = new LevelIntersectionDetector();
            var candle = DragonflyCandle();

            // Older set: only High intersects.
            var olderSet = Set(BaseTime.AddDays(-5), high: 95.00, midpoint: 200.00, low: 300.00);
            // Newer set: only Low intersects. Newest-set-first must still win, even though
            // Low is the lowest type-priority within its own set.
            var newerSet = Set(BaseTime.AddDays(-1), high: 500.00, midpoint: 600.00, low: 92.00);

            var intersections = detector.FindIntersections(
                Analysis(true), candle, new List<H1ReferenceSet> { olderSet, newerSet });
            var primary = detector.GetPrimaryIntersection(intersections);

            Assert.Equal(2, intersections.Count);
            Assert.Equal(newerSet.SourceCandleTimeUtc, primary.Level.SourceCandleTimeUtc);
            Assert.Equal(PriceLevelType.Low, primary.Level.LevelType);
        }

        [Fact]
        public void Test11_PrimaryIntersection_WithinSameSet_PrefersHighThenMidThenLow()
        {
            var detector = new LevelIntersectionDetector();
            var candle = DragonflyCandle();

            // All three levels of one set intersect the wick.
            var set = Set(BaseTime.AddDays(-1), high: 95.00, midpoint: 94.00, low: 93.00);

            var intersections = detector.FindIntersections(Analysis(true), candle, new List<H1ReferenceSet> { set });
            var primary = detector.GetPrimaryIntersection(intersections);

            Assert.Equal(3, intersections.Count);
            Assert.Equal(PriceLevelType.High, primary.Level.LevelType);
            Assert.Equal(PriceLevelType.High, intersections[0].Level.LevelType);
            Assert.Equal(PriceLevelType.Midpoint, intersections[1].Level.LevelType);
            Assert.Equal(PriceLevelType.Low, intersections[2].Level.LevelType);
        }

        // ---------------------------------------------------------------
        // 12: intersection is never evaluated for a non-Dragonfly candle
        // ---------------------------------------------------------------

        [Fact]
        public void Test12_NotADragonfly_ReturnsNoIntersections_EvenIfLevelWouldOtherwiseMatch()
        {
            var detector = new LevelIntersectionDetector();
            var candle = DragonflyCandle();
            // This level would clearly intersect the wick if the candle were a Dragonfly.
            var sets = new List<H1ReferenceSet> { Set(BaseTime.AddDays(-1), high: 200, midpoint: 95.00, low: 300) };

            var result = detector.FindIntersections(Analysis(false), candle, sets);

            Assert.Empty(result);
        }

        // ---------------------------------------------------------------
        // Additional guards
        // ---------------------------------------------------------------

        [Fact]
        public void GetPrimaryIntersection_EmptyList_ReturnsNull()
        {
            var detector = new LevelIntersectionDetector();

            var primary = detector.GetPrimaryIntersection(new List<LevelIntersection>());

            Assert.Null(primary);
        }

        [Fact]
        public void GetPrimaryIntersection_NullList_ReturnsNull()
        {
            var detector = new LevelIntersectionDetector();

            var primary = detector.GetPrimaryIntersection(null);

            Assert.Null(primary);
        }

        [Fact]
        public void FindIntersections_NullReferenceSets_ReturnsEmpty()
        {
            var detector = new LevelIntersectionDetector();
            var candle = DragonflyCandle();

            var result = detector.FindIntersections(Analysis(true), candle, null);

            Assert.Empty(result);
        }

        [Fact]
        public void FindIntersections_NullEntriesInReferenceSets_AreSkipped_ValidIntersectionsStillReturned()
        {
            var detector = new LevelIntersectionDetector();
            var candle = DragonflyCandle();
            var validSet = Set(BaseTime.AddDays(-1), high: 200, midpoint: 95.00, low: 300);
            var anotherValidSet = Set(BaseTime.AddDays(-2), high: 400, midpoint: 500, low: 91.00);

            var sets = new List<H1ReferenceSet> { validSet, null, anotherValidSet };

            var result = detector.FindIntersections(Analysis(true), candle, sets);

            Assert.Equal(2, result.Count);
            Assert.Equal(PriceLevelType.Midpoint, result[0].Level.LevelType);
            Assert.Equal(PriceLevelType.Low, result[1].Level.LevelType);
        }

        [Fact]
        public void FindIntersections_EmptyReferenceSets_ReturnsEmpty()
        {
            var detector = new LevelIntersectionDetector();
            var candle = DragonflyCandle();

            var result = detector.FindIntersections(Analysis(true), candle, new List<H1ReferenceSet>());

            Assert.Empty(result);
        }

        [Fact]
        public void FindIntersections_NullAnalysis_Throws()
        {
            var detector = new LevelIntersectionDetector();
            var candle = DragonflyCandle();

            Assert.Throws<ArgumentNullException>(
                () => detector.FindIntersections(null, candle, new List<H1ReferenceSet>()));
        }

        [Fact]
        public void FindIntersections_NullCandle_Throws()
        {
            var detector = new LevelIntersectionDetector();

            Assert.Throws<ArgumentNullException>(
                () => detector.FindIntersections(Analysis(true), null, new List<H1ReferenceSet>()));
        }
    }
}
