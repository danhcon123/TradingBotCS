using System;
using System.Collections.Generic;
using System.Linq;
using TradingBot.FXCM.Models;
using TradingBot.FXCM.Strategy;
using Xunit;

namespace TradingBot.Strategy.Tests
{
    public class H1LevelCalculatorTests
    {
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

        // ---------------------------------------------------------------
        // 1-3: qualifying-candle boundary detection (00:00 UTC-4 == 04:00 UTC)
        // ---------------------------------------------------------------

        [Fact]
        public void Test01_0400Utc_QualifiesAs_0000UtcMinus4()
        {
            var candleTimeUtc = new DateTime(2026, 6, 15, 4, 0, 0, DateTimeKind.Utc);

            Assert.True(H1LevelCalculator.IsQualifyingH1Candle(candleTimeUtc));
        }

        [Fact]
        public void Test02_0300Utc_DoesNotQualify()
        {
            var candleTimeUtc = new DateTime(2026, 6, 15, 3, 0, 0, DateTimeKind.Utc);

            Assert.False(H1LevelCalculator.IsQualifyingH1Candle(candleTimeUtc));
        }

        [Fact]
        public void Test03_0500Utc_DoesNotQualify()
        {
            var candleTimeUtc = new DateTime(2026, 6, 15, 5, 0, 0, DateTimeKind.Utc);

            Assert.False(H1LevelCalculator.IsQualifyingH1Candle(candleTimeUtc));
        }

        // ---------------------------------------------------------------
        // 4-6: High / Low preserved, Midpoint calculated correctly
        // ---------------------------------------------------------------

        [Fact]
        public void Test04_HighIsPreserved()
        {
            var calculator = new H1LevelCalculator(referenceCount: 1);
            var candle = H1(new DateTime(2026, 6, 15, 4, 0, 0, DateTimeKind.Utc), high: 2365.40, low: 2350.10);
            var evalTime = candle.Time.AddHours(2);

            var sets = calculator.CalculateReferenceSets(new[] { candle }, evalTime);

            Assert.Equal(2365.40, sets[0].High, 5);
        }

        [Fact]
        public void Test05_LowIsPreserved()
        {
            var calculator = new H1LevelCalculator(referenceCount: 1);
            var candle = H1(new DateTime(2026, 6, 15, 4, 0, 0, DateTimeKind.Utc), high: 2365.40, low: 2350.10);
            var evalTime = candle.Time.AddHours(2);

            var sets = calculator.CalculateReferenceSets(new[] { candle }, evalTime);

            Assert.Equal(2350.10, sets[0].Low, 5);
        }

        [Fact]
        public void Test06_MidpointIsAverageOfHighAndLow()
        {
            var calculator = new H1LevelCalculator(referenceCount: 1);
            var candle = H1(new DateTime(2026, 6, 15, 4, 0, 0, DateTimeKind.Utc), high: 2365.40, low: 2350.10);
            var evalTime = candle.Time.AddHours(2);

            var sets = calculator.CalculateReferenceSets(new[] { candle }, evalTime);

            Assert.Equal((2365.40 + 2350.10) / 2.0, sets[0].Midpoint, 5);
        }

        // ---------------------------------------------------------------
        // 7: one H1 candle -> exactly High, Midpoint, Low
        // ---------------------------------------------------------------

        [Fact]
        public void Test07_OneReferenceSet_GeneratesExactlyThreeLevels_InHighMidLowOrder()
        {
            var referenceSet = new H1ReferenceSet
            {
                SourceCandleTimeUtc = new DateTime(2026, 6, 15, 4, 0, 0, DateTimeKind.Utc),
                StrategyDate = new DateTime(2026, 6, 15),
                High = 2365.40,
                Midpoint = 2357.75,
                Low = 2350.10
            };

            var levels = referenceSet.ToPriceLevels();

            Assert.Equal(3, levels.Count);
            Assert.Equal(PriceLevelType.High, levels[0].LevelType);
            Assert.Equal(PriceLevelType.Midpoint, levels[1].LevelType);
            Assert.Equal(PriceLevelType.Low, levels[2].LevelType);
            Assert.Equal(2365.40, levels[0].Price, 5);
            Assert.Equal(2357.75, levels[1].Price, 5);
            Assert.Equal(2350.10, levels[2].Price, 5);
        }

        // ---------------------------------------------------------------
        // 8: 28 valid H1 candles -> 28 reference sets, 84 levels
        // ---------------------------------------------------------------

        [Fact]
        public void Test08_TwentyEightValidCandles_ProduceTwentyEightSets_AndEightyFourLevels()
        {
            var calculator = new H1LevelCalculator(referenceCount: 28);
            var candles = BuildConsecutiveDailyQualifyingCandles(startDate: new DateTime(2026, 1, 1), count: 28);
            var evalTime = candles.Last().Time.AddHours(2);

            var sets = calculator.CalculateReferenceSets(candles, evalTime);

            Assert.Equal(28, sets.Count);
            Assert.Equal(84, sets.Sum(s => s.ToPriceLevels().Count));
        }

        // ---------------------------------------------------------------
        // 9-10: HasFullReferenceSet
        // ---------------------------------------------------------------

        [Fact]
        public void Test09_TwentySevenSets_HasFullReferenceSetIsFalse()
        {
            var calculator = new H1LevelCalculator(referenceCount: 28);
            var candles = BuildConsecutiveDailyQualifyingCandles(startDate: new DateTime(2026, 1, 1), count: 27);
            var evalTime = candles.Last().Time.AddHours(2);

            var sets = calculator.CalculateReferenceSets(candles, evalTime);

            Assert.Equal(27, sets.Count);
            Assert.False(calculator.HasFullReferenceSet(sets));
        }

        [Fact]
        public void Test10_TwentyEightSets_HasFullReferenceSetIsTrue()
        {
            var calculator = new H1LevelCalculator(referenceCount: 28);
            var candles = BuildConsecutiveDailyQualifyingCandles(startDate: new DateTime(2026, 1, 1), count: 28);
            var evalTime = candles.Last().Time.AddHours(2);

            var sets = calculator.CalculateReferenceSets(candles, evalTime);

            Assert.True(calculator.HasFullReferenceSet(sets));
        }

        // ---------------------------------------------------------------
        // 11: newest -> oldest ordering
        // ---------------------------------------------------------------

        [Fact]
        public void Test11_ReferenceSetsAreOrderedNewestToOldest()
        {
            var calculator = new H1LevelCalculator(referenceCount: 3);
            var day1 = H1(new DateTime(2026, 6, 1, 4, 0, 0, DateTimeKind.Utc), 2360, 2350);
            var day2 = H1(new DateTime(2026, 6, 2, 4, 0, 0, DateTimeKind.Utc), 2361, 2351);
            var day3 = H1(new DateTime(2026, 6, 3, 4, 0, 0, DateTimeKind.Utc), 2362, 2352);
            var evalTime = day3.Time.AddHours(2);

            // Deliberately supplied out of order.
            var sets = calculator.CalculateReferenceSets(new[] { day2, day3, day1 }, evalTime);

            Assert.Equal(day3.Time, sets[0].SourceCandleTimeUtc);
            Assert.Equal(day2.Time, sets[1].SourceCandleTimeUtc);
            Assert.Equal(day1.Time, sets[2].SourceCandleTimeUtc);
        }

        // ---------------------------------------------------------------
        // 12: missing/null entries do not crash
        // ---------------------------------------------------------------

        [Fact]
        public void Test12_NullEntriesInInput_DoNotCrash_AndAreIgnored()
        {
            var calculator = new H1LevelCalculator(referenceCount: 5);
            var day1 = H1(new DateTime(2026, 6, 1, 4, 0, 0, DateTimeKind.Utc), 2360, 2350);
            var evalTime = day1.Time.AddHours(2);

            var candles = new List<Candle> { null, day1, null };

            var sets = calculator.CalculateReferenceSets(candles, evalTime);

            Assert.Single(sets);
        }

        // ---------------------------------------------------------------
        // 13: empty input -> empty result
        // ---------------------------------------------------------------

        [Fact]
        public void Test13_EmptyInput_ReturnsEmptyResult()
        {
            var calculator = new H1LevelCalculator(referenceCount: 28);

            var sets = calculator.CalculateReferenceSets(
                new List<Candle>(),
                DateTime.UtcNow);

            Assert.Empty(sets);
        }

        [Fact]
        public void Test13b_NullInput_ReturnsEmptyResult()
        {
            var calculator = new H1LevelCalculator(referenceCount: 28);

            var sets = calculator.CalculateReferenceSets(null, DateTime.UtcNow);

            Assert.Empty(sets);
        }

        // ---------------------------------------------------------------
        // 14: future reference candles cannot be used
        // ---------------------------------------------------------------

        [Fact]
        public void Test14_FutureCandle_CannotBeUsed()
        {
            var calculator = new H1LevelCalculator(referenceCount: 5);
            var pastCandle = H1(new DateTime(2026, 6, 10, 4, 0, 0, DateTimeKind.Utc), 2360, 2350);
            var futureCandle = H1(new DateTime(2026, 6, 20, 4, 0, 0, DateTimeKind.Utc), 2370, 2360);

            // Evaluation happens before the "future" candle has even opened.
            var evalTime = new DateTime(2026, 6, 11, 0, 0, 0, DateTimeKind.Utc);

            var sets = calculator.CalculateReferenceSets(new[] { pastCandle, futureCandle }, evalTime);

            Assert.Single(sets);
            Assert.Equal(pastCandle.Time, sets[0].SourceCandleTimeUtc);
        }

        // ---------------------------------------------------------------
        // 15: an H1 candle that has not completed at evaluation time cannot be used
        // ---------------------------------------------------------------

        [Fact]
        public void Test15_IncompleteCandleAtEvaluationTime_CannotBeUsed()
        {
            var candle = H1(new DateTime(2026, 6, 10, 4, 0, 0, DateTimeKind.Utc), 2360, 2350);

            // Evaluation time falls inside the candle's own hour - it has not closed yet.
            var evalTimeMidCandle = candle.Time.AddMinutes(30);
            var evalTimeExactlyAtClose = candle.Time.AddHours(1);

            var calculator = new H1LevelCalculator(referenceCount: 5);

            var setsBeforeClose = calculator.CalculateReferenceSets(new[] { candle }, evalTimeMidCandle);
            var setsAtClose = calculator.CalculateReferenceSets(new[] { candle }, evalTimeExactlyAtClose);

            Assert.Empty(setsBeforeClose);
            Assert.Single(setsAtClose);
        }

        // ---------------------------------------------------------------
        // 16: weekends/missing days do not incorrectly count
        // ---------------------------------------------------------------

        [Fact]
        public void Test16_MissingDays_DoNotCountAsReferenceCandles()
        {
            var calculator = new H1LevelCalculator(referenceCount: 5);

            // Friday, then a weekend gap (Sat/Sun have no qualifying candle), then Monday.
            var friday = H1(new DateTime(2026, 6, 5, 4, 0, 0, DateTimeKind.Utc), 2360, 2350);
            var monday = H1(new DateTime(2026, 6, 8, 4, 0, 0, DateTimeKind.Utc), 2362, 2352);
            var evalTime = monday.Time.AddHours(2);

            var sets = calculator.CalculateReferenceSets(new[] { friday, monday }, evalTime);

            // Only the two real candles count - no synthesized Saturday/Sunday entries.
            Assert.Equal(2, sets.Count);
            Assert.False(calculator.HasFullReferenceSet(sets));
        }

        // ---------------------------------------------------------------
        // 17: 28 most recent VALID candles, not blindly 28 calendar days
        // ---------------------------------------------------------------

        [Fact]
        public void Test17_ReturnsTwentyEightMostRecentValidCandles_EvenAcrossGapsSpanningMoreThan28CalendarDays()
        {
            var calculator = new H1LevelCalculator(referenceCount: 28);

            // 35 calendar days, but every 5th day is missing (holiday/no data) -
            // so reaching 28 qualifying candles requires looking back further than
            // 28 calendar days.
            var allCandles = new List<Candle>();
            var start = new DateTime(2026, 1, 1, 4, 0, 0, DateTimeKind.Utc);
            for (int day = 0; day < 35; day++)
            {
                if (day % 5 == 4)
                {
                    continue; // simulate a missing/holiday day
                }

                var time = start.AddDays(day);
                allCandles.Add(H1(time, 2350 + day, 2340 + day));
            }

            var evalTime = allCandles.Last().Time.AddHours(2);

            var sets = calculator.CalculateReferenceSets(allCandles, evalTime);

            Assert.Equal(28, sets.Count);
            Assert.True(calculator.HasFullReferenceSet(sets));

            // The oldest of the 28 returned sets must be further back than 28 calendar
            // days from the newest, proving gaps were skipped rather than assumed valid.
            var newest = sets.First().SourceCandleTimeUtc;
            var oldest = sets.Last().SourceCandleTimeUtc;
            Assert.True((newest - oldest).TotalDays > 27);
        }

        // ---------------------------------------------------------------
        // 18: duplicate qualifying H1 timestamps are counted only once
        // ---------------------------------------------------------------

        [Fact]
        public void Test18_DuplicateSourceTimestamps_AreCountedOnlyOnce()
        {
            var calculator = new H1LevelCalculator(referenceCount: 28);

            var day1 = H1(new DateTime(2026, 6, 15, 4, 0, 0, DateTimeKind.Utc), 2360, 2350);
            var day1Duplicate = H1(new DateTime(2026, 6, 15, 4, 0, 0, DateTimeKind.Utc), 2360, 2350);
            var day2 = H1(new DateTime(2026, 6, 16, 4, 0, 0, DateTimeKind.Utc), 2361, 2351);

            var evalTime = day2.Time.AddHours(2);

            var sets = calculator.CalculateReferenceSets(new[] { day1, day1Duplicate, day2 }, evalTime);

            Assert.Equal(2, sets.Count);
            Assert.Single(sets, s => s.SourceCandleTimeUtc == day1.Time);
        }

        // ---------------------------------------------------------------
        // 19: invalid reference count is rejected defensively
        // ---------------------------------------------------------------

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void Test19_NonPositiveReferenceCount_ThrowsArgumentOutOfRangeException(int invalidCount)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new H1LevelCalculator(invalidCount));
        }

        // ---------------------------------------------------------------
        // 20: Candle.Time UTC contract
        // ---------------------------------------------------------------

        [Fact]
        public void Test20_UnspecifiedKindTimestamp_IsTrustedAsUtc()
        {
            var calculator = new H1LevelCalculator(referenceCount: 5);
            var candle = H1(new DateTime(2026, 6, 15, 4, 0, 0, DateTimeKind.Unspecified), 2360, 2350);
            var evalTime = candle.Time.AddHours(2);

            var sets = calculator.CalculateReferenceSets(new[] { candle }, evalTime);

            Assert.Single(sets);
        }

        [Fact]
        public void Test20b_LocalKindTimestamp_ThrowsArgumentException()
        {
            var calculator = new H1LevelCalculator(referenceCount: 5);
            var candle = H1(new DateTime(2026, 6, 15, 4, 0, 0, DateTimeKind.Local), 2360, 2350);
            var evalTime = candle.Time.AddHours(2);

            Assert.Throws<ArgumentException>(
                () => calculator.CalculateReferenceSets(new[] { candle }, evalTime));
        }

        // ---------------------------------------------------------------
        // 21: the UTC contract also applies to the evaluation timestamp itself,
        // not just candle timestamps - otherwise UTC candles could silently be
        // compared against a local-time evaluation instant.
        // ---------------------------------------------------------------

        [Fact]
        public void Test21_LocalKindEvaluationTime_ThrowsArgumentException()
        {
            var calculator = new H1LevelCalculator(referenceCount: 5);
            var candle = H1(new DateTime(2026, 6, 15, 4, 0, 0, DateTimeKind.Utc), 2360, 2350);
            var localEvalTime = DateTime.SpecifyKind(candle.Time.AddHours(2), DateTimeKind.Local);

            Assert.Throws<ArgumentException>(
                () => calculator.CalculateReferenceSets(new[] { candle }, localEvalTime));
        }

        [Fact]
        public void Test22_UnspecifiedKindEvaluationTime_IsTrustedAsUtc()
        {
            var calculator = new H1LevelCalculator(referenceCount: 5);
            var candle = H1(new DateTime(2026, 6, 15, 4, 0, 0, DateTimeKind.Utc), 2360, 2350);
            var unspecifiedEvalTime = DateTime.SpecifyKind(candle.Time.AddHours(2), DateTimeKind.Unspecified);

            var sets = calculator.CalculateReferenceSets(new[] { candle }, unspecifiedEvalTime);

            Assert.Single(sets);
        }

        [Fact]
        public void Test23_LocalKindEvaluationTime_ThrowsEvenWithEmptyCandleList()
        {
            var calculator = new H1LevelCalculator(referenceCount: 5);
            var localEvalTime = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Local);

            Assert.Throws<ArgumentException>(
                () => calculator.CalculateReferenceSets(new List<Candle>(), localEvalTime));
        }

        private static List<Candle> BuildConsecutiveDailyQualifyingCandles(DateTime startDate, int count)
        {
            var candles = new List<Candle>();
            for (int i = 0; i < count; i++)
            {
                var time = new DateTime(startDate.Year, startDate.Month, startDate.Day, 4, 0, 0, DateTimeKind.Utc)
                    .AddDays(i);
                candles.Add(H1(time, high: 2350 + i, low: 2340 + i));
            }

            return candles;
        }
    }
}
