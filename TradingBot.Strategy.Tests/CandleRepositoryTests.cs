using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using TradingBot.FXCM.Data;
using TradingBot.FXCM.Models;
using Xunit;

namespace TradingBot.Strategy.Tests
{
    public class CandleRepositoryTests : IDisposable
    {
        private readonly string _dbPath;

        public CandleRepositoryTests()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"tradingbot_test_{Guid.NewGuid():N}.db");
        }

        public void Dispose()
        {
            // Microsoft.Data.Sqlite pools connections by default, so a closed SqliteConnection
            // doesn't necessarily release its underlying file handle right away. Clear the
            // pool first so the temp file can actually be deleted here - production code
            // never needs this, since it never deletes the database file mid-run.
            SqliteConnection.ClearAllPools();

            if (File.Exists(_dbPath))
            {
                File.Delete(_dbPath);
            }
        }

        private CandleRepository CreateRepository() => new CandleRepository(_dbPath);

        private static Candle M1(DateTime timeUtc, double open, double high, double low, double close, long volume = 100)
        {
            return new Candle { Time = timeUtc, Open = open, High = high, Low = low, Close = close, Volume = volume };
        }

        // ---------------------------------------------------------------
        // Save + retrieve round trip
        // ---------------------------------------------------------------

        [Fact]
        public void SaveCandles_ThenGetCandles_ReturnsThemOrderedAscending()
        {
            var repo = CreateRepository();
            var day1 = M1(new DateTime(2026, 6, 15, 10, 0, 0, DateTimeKind.Utc), 100, 101, 99, 100.5);
            var day2 = M1(new DateTime(2026, 6, 15, 10, 1, 0, DateTimeKind.Utc), 100.5, 102, 100, 101.5);

            // Insert out of order deliberately.
            repo.SaveCandles("XAU/USD", "m1", new[] { day2, day1 });

            var result = repo.GetCandles(
                "XAU/USD", "m1",
                new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 6, 16, 0, 0, 0, DateTimeKind.Utc));

            Assert.Equal(2, result.Count);
            Assert.Equal(day1.Time, result[0].Time);
            Assert.Equal(day2.Time, result[1].Time);
        }

        [Fact]
        public void SaveCandles_ThenGetCandles_PreservesOhlcvValuesExactly()
        {
            var repo = CreateRepository();
            var candle = M1(new DateTime(2026, 6, 15, 10, 0, 0, DateTimeKind.Utc), 2358.45, 2358.47, 2358.10, 2358.47, 1234);

            repo.SaveCandles("XAU/USD", "m1", new[] { candle });

            var result = repo.GetCandles(
                "XAU/USD", "m1",
                candle.Time, candle.Time.AddMinutes(1));

            Assert.Single(result);
            Assert.Equal(candle.Open, result[0].Open, 10);
            Assert.Equal(candle.High, result[0].High, 10);
            Assert.Equal(candle.Low, result[0].Low, 10);
            Assert.Equal(candle.Close, result[0].Close, 10);
            Assert.Equal(candle.Volume, result[0].Volume);
            Assert.Equal(candle.Time, result[0].Time);
        }

        // ---------------------------------------------------------------
        // Idempotency via the unique index - the core requirement for a
        // resumable importer that may request overlapping ranges.
        // ---------------------------------------------------------------

        [Fact]
        public void SaveCandles_CalledTwiceWithOverlappingRange_DoesNotCreateDuplicates()
        {
            var repo = CreateRepository();
            var t1 = new DateTime(2026, 6, 15, 10, 0, 0, DateTimeKind.Utc);
            var t2 = new DateTime(2026, 6, 15, 10, 1, 0, DateTimeKind.Utc);
            var t3 = new DateTime(2026, 6, 15, 10, 2, 0, DateTimeKind.Utc);

            var firstBatch = new[] { M1(t1, 1, 2, 0, 1), M1(t2, 1, 2, 0, 1) };
            var secondBatchOverlapping = new[] { M1(t2, 1, 2, 0, 1), M1(t3, 1, 2, 0, 1) };

            int firstInserted = repo.SaveCandles("XAU/USD", "m1", firstBatch);
            int secondInserted = repo.SaveCandles("XAU/USD", "m1", secondBatchOverlapping);

            Assert.Equal(2, firstInserted);
            Assert.Equal(1, secondInserted); // t2 was already present - only t3 is new

            long total = repo.GetCandleCount("XAU/USD", "m1");
            Assert.Equal(3, total);
        }

        [Fact]
        public void SaveCandles_ExactSameCandleSavedMultipleTimes_IsSafeToRepeat()
        {
            var repo = CreateRepository();
            var candle = M1(new DateTime(2026, 6, 15, 10, 0, 0, DateTimeKind.Utc), 1, 2, 0, 1);

            repo.SaveCandles("XAU/USD", "m1", new[] { candle });
            repo.SaveCandles("XAU/USD", "m1", new[] { candle });
            repo.SaveCandles("XAU/USD", "m1", new[] { candle });

            Assert.Equal(1, repo.GetCandleCount("XAU/USD", "m1"));
        }

        // ---------------------------------------------------------------
        // Instrument/timeframe scoping
        // ---------------------------------------------------------------

        [Fact]
        public void GetCandles_DoesNotLeakAcrossInstruments()
        {
            var repo = CreateRepository();
            var time = new DateTime(2026, 6, 15, 10, 0, 0, DateTimeKind.Utc);

            repo.SaveCandles("XAU/USD", "m1", new[] { M1(time, 1, 2, 0, 1) });
            repo.SaveCandles("EUR/USD", "m1", new[] { M1(time, 3, 4, 2, 3) });

            var xauResults = repo.GetCandles("XAU/USD", "m1", time, time.AddMinutes(1));
            var eurResults = repo.GetCandles("EUR/USD", "m1", time, time.AddMinutes(1));

            Assert.Single(xauResults);
            Assert.Single(eurResults);
            Assert.Equal(1.0, xauResults[0].Open, 5);
            Assert.Equal(3.0, eurResults[0].Open, 5);
        }

        [Fact]
        public void GetCandles_DoesNotLeakAcrossTimeframes()
        {
            var repo = CreateRepository();
            var time = new DateTime(2026, 6, 15, 4, 0, 0, DateTimeKind.Utc);

            repo.SaveCandles("XAU/USD", "m1", new[] { M1(time, 1, 2, 0, 1) });
            repo.SaveCandles("XAU/USD", "H1", new[] { M1(time, 5, 6, 4, 5) });

            var m1Results = repo.GetCandles("XAU/USD", "m1", time, time.AddHours(1));
            var h1Results = repo.GetCandles("XAU/USD", "H1", time, time.AddHours(1));

            Assert.Single(m1Results);
            Assert.Single(h1Results);
            Assert.Equal(1.0, m1Results[0].Open, 5);
            Assert.Equal(5.0, h1Results[0].Open, 5);
        }

        // ---------------------------------------------------------------
        // Range boundary: [fromUtc, toUtc)
        // ---------------------------------------------------------------

        [Fact]
        public void GetCandles_RangeIsInclusiveOfFrom_ExclusiveOfTo()
        {
            var repo = CreateRepository();
            var from = new DateTime(2026, 6, 15, 10, 0, 0, DateTimeKind.Utc);
            var to = new DateTime(2026, 6, 15, 10, 5, 0, DateTimeKind.Utc);

            repo.SaveCandles("XAU/USD", "m1", new[]
            {
                M1(from, 1, 2, 0, 1),               // exactly at "from" -> included
                M1(from.AddMinutes(2), 1, 2, 0, 1),  // inside range -> included
                M1(to, 1, 2, 0, 1)                   // exactly at "to" -> excluded
            });

            var result = repo.GetCandles("XAU/USD", "m1", from, to);

            Assert.Equal(2, result.Count);
            Assert.DoesNotContain(result, c => c.Time == to);
        }

        // ---------------------------------------------------------------
        // Latest / earliest timestamp lookups (used by a resumable importer)
        // ---------------------------------------------------------------

        [Fact]
        public void GetLatestTimestampUtc_ReturnsMostRecentStoredCandle()
        {
            var repo = CreateRepository();
            var earliest = new DateTime(2026, 6, 10, 4, 0, 0, DateTimeKind.Utc);
            var latest = new DateTime(2026, 6, 15, 4, 0, 0, DateTimeKind.Utc);

            repo.SaveCandles("XAU/USD", "H1", new[] { M1(earliest, 1, 2, 0, 1), M1(latest, 1, 2, 0, 1) });

            Assert.Equal(latest, repo.GetLatestTimestampUtc("XAU/USD", "H1"));
        }

        [Fact]
        public void GetEarliestTimestampUtc_ReturnsOldestStoredCandle()
        {
            var repo = CreateRepository();
            var earliest = new DateTime(2026, 6, 10, 4, 0, 0, DateTimeKind.Utc);
            var latest = new DateTime(2026, 6, 15, 4, 0, 0, DateTimeKind.Utc);

            repo.SaveCandles("XAU/USD", "H1", new[] { M1(earliest, 1, 2, 0, 1), M1(latest, 1, 2, 0, 1) });

            Assert.Equal(earliest, repo.GetEarliestTimestampUtc("XAU/USD", "H1"));
        }

        [Fact]
        public void GetLatestTimestampUtc_NoCandlesStored_ReturnsNull()
        {
            var repo = CreateRepository();

            Assert.Null(repo.GetLatestTimestampUtc("XAU/USD", "H1"));
        }

        [Fact]
        public void GetEarliestTimestampUtc_NoCandlesStored_ReturnsNull()
        {
            var repo = CreateRepository();

            Assert.Null(repo.GetEarliestTimestampUtc("XAU/USD", "H1"));
        }

        // ---------------------------------------------------------------
        // Resumability simulation: two sequential imports covering adjoining
        // ranges must not create gaps or duplicates.
        // ---------------------------------------------------------------

        [Fact]
        public void SequentialImports_FromLatestTimestamp_ProduceContiguousGapFreeHistory()
        {
            var repo = CreateRepository();
            var day1 = new DateTime(2026, 6, 15, 4, 0, 0, DateTimeKind.Utc);
            var day2 = day1.AddDays(1);
            var day3 = day1.AddDays(2);

            // First "download" covers day1 and day2.
            repo.SaveCandles("XAU/USD", "H1", new[] { M1(day1, 1, 2, 0, 1), M1(day2, 1, 2, 0, 1) });

            // Importer resumes from the latest stored timestamp + 1 tick and re-requests an
            // overlapping window (day2 again) plus new data (day3).
            DateTime? resumeFrom = repo.GetLatestTimestampUtc("XAU/USD", "H1");
            Assert.Equal(day2, resumeFrom);

            repo.SaveCandles("XAU/USD", "H1", new[] { M1(day2, 1, 2, 0, 1), M1(day3, 1, 2, 0, 1) });

            var all = repo.GetCandles("XAU/USD", "H1", day1, day3.AddDays(1));
            Assert.Equal(3, all.Count);
            Assert.Equal(new[] { day1, day2, day3 }, new[] { all[0].Time, all[1].Time, all[2].Time });
        }

        // ---------------------------------------------------------------
        // Null / empty input handling
        // ---------------------------------------------------------------

        [Fact]
        public void SaveCandles_NullEnumerable_ReturnsZero_DoesNotThrow()
        {
            var repo = CreateRepository();

            int inserted = repo.SaveCandles("XAU/USD", "m1", null);

            Assert.Equal(0, inserted);
        }

        [Fact]
        public void SaveCandles_NullEntriesInList_AreSkipped()
        {
            var repo = CreateRepository();
            var candle = M1(new DateTime(2026, 6, 15, 10, 0, 0, DateTimeKind.Utc), 1, 2, 0, 1);

            int inserted = repo.SaveCandles("XAU/USD", "m1", new List<Candle> { null, candle, null });

            Assert.Equal(1, inserted);
        }

        // ---------------------------------------------------------------
        // Argument guards
        // ---------------------------------------------------------------

        [Fact]
        public void Constructor_NullOrEmptyPath_Throws()
        {
            Assert.Throws<ArgumentException>(() => new CandleRepository(null));
            Assert.Throws<ArgumentException>(() => new CandleRepository(""));
            Assert.Throws<ArgumentException>(() => new CandleRepository("   "));
        }

        [Fact]
        public void SaveCandles_NullOrEmptyInstrumentOrTimeframe_Throws()
        {
            var repo = CreateRepository();
            var candles = new[] { M1(DateTime.UtcNow, 1, 2, 0, 1) };

            Assert.Throws<ArgumentException>(() => repo.SaveCandles(null, "m1", candles));
            Assert.Throws<ArgumentException>(() => repo.SaveCandles("XAU/USD", null, candles));
            Assert.Throws<ArgumentException>(() => repo.SaveCandles("", "m1", candles));
        }

        [Fact]
        public void GetCandles_NullOrEmptyInstrumentOrTimeframe_Throws()
        {
            var repo = CreateRepository();

            Assert.Throws<ArgumentException>(() => repo.GetCandles(null, "m1", DateTime.UtcNow, DateTime.UtcNow));
            Assert.Throws<ArgumentException>(() => repo.GetCandles("XAU/USD", null, DateTime.UtcNow, DateTime.UtcNow));
        }

        // ---------------------------------------------------------------
        // Data survives across repository instances (i.e. it's actually on disk)
        // ---------------------------------------------------------------

        [Fact]
        public void DataPersists_AcrossSeparateRepositoryInstances()
        {
            var candle = M1(new DateTime(2026, 6, 15, 10, 0, 0, DateTimeKind.Utc), 1, 2, 0, 1);

            var writer = new CandleRepository(_dbPath);
            writer.SaveCandles("XAU/USD", "m1", new[] { candle });

            var reader = new CandleRepository(_dbPath);
            var result = reader.GetCandles("XAU/USD", "m1", candle.Time, candle.Time.AddMinutes(1));

            Assert.Single(result);
        }
    }
}
