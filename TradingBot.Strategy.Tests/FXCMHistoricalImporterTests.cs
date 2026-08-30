using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using TradingBot.FXCM.Data;
using TradingBot.FXCM.Import;
using TradingBot.FXCM.Models;
using Xunit;

namespace TradingBot.Strategy.Tests
{
    public class FXCMHistoricalImporterTests : IDisposable
    {
        private readonly string _dbPath;

        public FXCMHistoricalImporterTests()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"tradingbot_importer_test_{Guid.NewGuid():N}.db");
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

        private static Candle CandleAt(DateTime timeUtc) =>
            new Candle { Time = timeUtc, Open = 1, High = 2, Low = 0, Close = 1, Volume = 10 };

        /// <summary>
        /// Records every call made to it and delegates to a configurable behavior function,
        /// so tests can simulate transient failures, permanent failures, hangs, or normal
        /// responses per chunk.
        /// </summary>
        private class FakeFetcher
        {
            public List<(string Instrument, string Timeframe, DateTime From, DateTime To)> Calls { get; } = new List<(string, string, DateTime, DateTime)>();

            private readonly Func<DateTime, DateTime, List<Candle>> _behavior;

            public FakeFetcher(Func<DateTime, DateTime, List<Candle>> behavior)
            {
                _behavior = behavior;
            }

            public List<Candle> Fetch(string instrument, string timeframe, DateTime from, DateTime to)
            {
                Calls.Add((instrument, timeframe, from, to));
                return _behavior(from, to);
            }
        }

        // ---------------------------------------------------------------
        // Chunking
        // ---------------------------------------------------------------

        [Fact]
        public void Import_SplitsRangeIntoChunksOfConfiguredSize()
        {
            var repo = CreateRepository();
            var fetcher = new FakeFetcher((chunkFrom, chunkTo) => new List<Candle>());
            var importer = new FXCMHistoricalImporter(fetcher.Fetch, repo, requestDelay: TimeSpan.Zero);

            var from = new DateTime(2026, 6, 15, 4, 0, 0, DateTimeKind.Utc);
            var to = new DateTime(2026, 6, 15, 10, 0, 0, DateTimeKind.Utc);

            importer.Import("XAU/USD", "H1", from, to, TimeSpan.FromHours(2));

            Assert.Equal(3, fetcher.Calls.Count);
            Assert.Equal((from, from.AddHours(2)), (fetcher.Calls[0].From, fetcher.Calls[0].To));
            Assert.Equal((from.AddHours(2), from.AddHours(4)), (fetcher.Calls[1].From, fetcher.Calls[1].To));
            Assert.Equal((from.AddHours(4), to), (fetcher.Calls[2].From, fetcher.Calls[2].To));
        }

        [Fact]
        public void Import_LastChunk_IsClampedToRequestedEnd()
        {
            var repo = CreateRepository();
            var fetcher = new FakeFetcher((chunkFrom, chunkTo) => new List<Candle>());
            var importer = new FXCMHistoricalImporter(fetcher.Fetch, repo, requestDelay: TimeSpan.Zero);

            var from = new DateTime(2026, 6, 15, 4, 0, 0, DateTimeKind.Utc);
            var to = from.AddHours(5); // not an exact multiple of the 2h chunk size

            importer.Import("XAU/USD", "H1", from, to, TimeSpan.FromHours(2));

            Assert.Equal(3, fetcher.Calls.Count);
            Assert.Equal(to, fetcher.Calls[2].To); // clamped, not from+6h
        }

        // ---------------------------------------------------------------
        // Saving each chunk immediately
        // ---------------------------------------------------------------

        [Fact]
        public void Import_SavesCandlesFromEachChunk_ToRepository()
        {
            var repo = CreateRepository();
            var from = new DateTime(2026, 6, 15, 4, 0, 0, DateTimeKind.Utc);
            var to = from.AddHours(4);

            var fetcher = new FakeFetcher((chunkFrom, chunkTo) => new List<Candle> { CandleAt(chunkFrom) });
            var importer = new FXCMHistoricalImporter(fetcher.Fetch, repo, requestDelay: TimeSpan.Zero);

            ImportResult result = importer.Import("XAU/USD", "H1", from, to, TimeSpan.FromHours(2));

            Assert.Equal(2, result.ChunksSucceeded);
            Assert.Equal(2, result.CandlesReceived);
            Assert.Equal(2, result.CandlesInserted);

            var stored = repo.GetCandles("XAU/USD", "H1", from, to);
            Assert.Equal(2, stored.Count);
        }

        // ---------------------------------------------------------------
        // Resume / skip-existing-range
        // ---------------------------------------------------------------

        [Fact]
        public void Import_ResumesFromLatestStoredCandle_SkipsAlreadyDownloadedRange()
        {
            var repo = CreateRepository();
            var from = new DateTime(2026, 6, 15, 4, 0, 0, DateTimeKind.Utc);
            var alreadyStored = from.AddHours(3);
            var to = from.AddHours(6);

            repo.SaveCandles("XAU/USD", "H1", new[] { CandleAt(alreadyStored) });

            var fetcher = new FakeFetcher((chunkFrom, chunkTo) => new List<Candle> { CandleAt(chunkFrom) });
            var importer = new FXCMHistoricalImporter(fetcher.Fetch, repo, requestDelay: TimeSpan.Zero);

            ImportResult result = importer.Import("XAU/USD", "H1", from, to, TimeSpan.FromHours(1));

            Assert.Equal(alreadyStored, result.ActualFromUtc);
            Assert.False(result.AlreadyUpToDate);
            Assert.Equal(alreadyStored, fetcher.Calls.First().From);
            Assert.DoesNotContain(fetcher.Calls, c => c.From < alreadyStored);
        }

        [Fact]
        public void Import_RequestedRangeFullyCovered_ReturnsAlreadyUpToDate_MakesNoRequests()
        {
            var repo = CreateRepository();
            var from = new DateTime(2026, 6, 15, 4, 0, 0, DateTimeKind.Utc);
            var to = from.AddHours(2);

            repo.SaveCandles("XAU/USD", "H1", new[] { CandleAt(to.AddHours(5)) }); // well past "to"

            var fetcher = new FakeFetcher((chunkFrom, chunkTo) => new List<Candle>());
            var importer = new FXCMHistoricalImporter(fetcher.Fetch, repo, requestDelay: TimeSpan.Zero);

            ImportResult result = importer.Import("XAU/USD", "H1", from, to, TimeSpan.FromHours(1));

            Assert.True(result.AlreadyUpToDate);
            Assert.Empty(fetcher.Calls);
            Assert.Equal(0, result.ChunksSucceeded);
        }

        [Fact]
        public void ResolveResumeStartUtc_NoExistingData_ReturnsRequestedFrom()
        {
            var repo = CreateRepository();
            var importer = new FXCMHistoricalImporter((i, t, f, to) => new List<Candle>(), repo);
            var requestedFrom = new DateTime(2026, 6, 15, 4, 0, 0, DateTimeKind.Utc);

            var resumeFrom = importer.ResolveResumeStartUtc("XAU/USD", "H1", requestedFrom);

            Assert.Equal(requestedFrom, resumeFrom);
        }

        [Fact]
        public void ResolveResumeStartUtc_ExistingDataAfterRequestedFrom_ReturnsLatestStored()
        {
            var repo = CreateRepository();
            var requestedFrom = new DateTime(2026, 6, 15, 4, 0, 0, DateTimeKind.Utc);
            var latest = requestedFrom.AddDays(3);
            repo.SaveCandles("XAU/USD", "H1", new[] { CandleAt(latest) });

            var importer = new FXCMHistoricalImporter((i, t, f, to) => new List<Candle>(), repo);

            var resumeFrom = importer.ResolveResumeStartUtc("XAU/USD", "H1", requestedFrom);

            Assert.Equal(latest, resumeFrom);
        }

        [Fact]
        public void ResolveResumeStartUtc_ExistingDataBeforeRequestedFrom_DoesNotMoveBackward()
        {
            var repo = CreateRepository();
            var earlierStored = new DateTime(2026, 6, 10, 4, 0, 0, DateTimeKind.Utc);
            var requestedFrom = new DateTime(2026, 6, 15, 4, 0, 0, DateTimeKind.Utc); // ahead of stored data

            repo.SaveCandles("XAU/USD", "H1", new[] { CandleAt(earlierStored) });

            var importer = new FXCMHistoricalImporter((i, t, f, to) => new List<Candle>(), repo);

            var resumeFrom = importer.ResolveResumeStartUtc("XAU/USD", "H1", requestedFrom);

            Assert.Equal(requestedFrom, resumeFrom);
        }

        // ---------------------------------------------------------------
        // Retry on transient failure
        // ---------------------------------------------------------------

        [Fact]
        public void Import_TransientFailure_RetriesAndEventuallySucceeds()
        {
            var repo = CreateRepository();
            var from = new DateTime(2026, 6, 15, 4, 0, 0, DateTimeKind.Utc);
            var to = from.AddHours(1);

            int callCount = 0;
            var fetcher = new FakeFetcher((chunkFrom, chunkTo) =>
            {
                callCount++;
                if (callCount == 1)
                {
                    throw new InvalidOperationException("simulated transient failure");
                }
                return new List<Candle> { CandleAt(chunkFrom) };
            });

            var importer = new FXCMHistoricalImporter(fetcher.Fetch, repo, requestDelay: TimeSpan.Zero, maxRetriesPerChunk: 3);

            ImportResult result = importer.Import("XAU/USD", "H1", from, to, TimeSpan.FromHours(1));

            Assert.Equal(2, callCount); // failed once, succeeded on retry
            Assert.Equal(1, result.ChunksSucceeded);
            Assert.Equal(0, result.ChunksFailed);
            Assert.Empty(result.FailedChunks);
            Assert.Equal(1, result.CandlesInserted);
        }

        // ---------------------------------------------------------------
        // Permanent failure: recorded, does not abort the rest of the run
        // ---------------------------------------------------------------

        [Fact]
        public void Import_PermanentFailureOnOneChunk_RecordsFailure_StillProcessesOtherChunks()
        {
            var repo = CreateRepository();
            var from = new DateTime(2026, 6, 15, 4, 0, 0, DateTimeKind.Utc);
            var to = from.AddHours(3);
            var badChunkStart = from.AddHours(1);

            var fetcher = new FakeFetcher((chunkFrom, chunkTo) =>
            {
                if (chunkFrom == badChunkStart)
                {
                    throw new InvalidOperationException("simulated permanent failure");
                }
                return new List<Candle> { CandleAt(chunkFrom) };
            });

            var importer = new FXCMHistoricalImporter(fetcher.Fetch, repo, requestDelay: TimeSpan.Zero, maxRetriesPerChunk: 2);

            ImportResult result = importer.Import("XAU/USD", "H1", from, to, TimeSpan.FromHours(1));

            Assert.Equal(2, result.ChunksSucceeded);
            Assert.Equal(1, result.ChunksFailed);
            Assert.Single(result.FailedChunks);
            Assert.Equal(badChunkStart, result.FailedChunks[0].ChunkStartUtc);
            Assert.Equal(2, result.FailedChunks[0].Attempts);
            Assert.Contains("simulated permanent failure", result.FailedChunks[0].LastErrorMessage);

            // The two good chunks were still saved despite the bad one in between.
            var stored = repo.GetCandles("XAU/USD", "H1", from, to);
            Assert.Equal(2, stored.Count);
        }

        // ---------------------------------------------------------------
        // RetryFailedChunks: the fix for the "later chunk succeeds, resume logic
        // thinks the whole range is covered" gap.
        // ---------------------------------------------------------------

        [Fact]
        public void RetryFailedChunks_BypassesResumeSkipLogic_EvenWhenLaterDataAlreadyExists()
        {
            var repo = CreateRepository();
            var gapStart = new DateTime(2026, 6, 15, 5, 0, 0, DateTimeKind.Utc);
            var gapEnd = gapStart.AddHours(1);
            var laterAlreadyStored = gapEnd.AddHours(10); // simulates a later chunk that already succeeded

            repo.SaveCandles("XAU/USD", "H1", new[] { CandleAt(laterAlreadyStored) });

            var fetcher = new FakeFetcher((chunkFrom, chunkTo) => new List<Candle> { CandleAt(chunkFrom) });
            var importer = new FXCMHistoricalImporter(fetcher.Fetch, repo, requestDelay: TimeSpan.Zero);

            var failedChunks = new[]
            {
                new ImportChunkFailure { ChunkStartUtc = gapStart, ChunkEndUtc = gapEnd, Attempts = 3, LastErrorMessage = "boom" }
            };

            ImportResult result = importer.RetryFailedChunks("XAU/USD", "H1", failedChunks);

            // Proves the gap chunk was actually requested, not skipped because later data exists.
            Assert.Single(fetcher.Calls);
            Assert.Equal(gapStart, fetcher.Calls[0].From);
            Assert.Equal(1, result.ChunksSucceeded);
            Assert.Equal(1, result.CandlesInserted);

            var stored = repo.GetCandles("XAU/USD", "H1", gapStart, laterAlreadyStored.AddMinutes(1));
            Assert.Equal(2, stored.Count); // the filled gap + the pre-existing later candle
        }

        [Fact]
        public void RetryFailedChunks_StillFailingChunk_RemainsInResultFailedChunks()
        {
            var repo = CreateRepository();
            var gapStart = new DateTime(2026, 6, 15, 5, 0, 0, DateTimeKind.Utc);
            var gapEnd = gapStart.AddHours(1);

            var fetcher = new FakeFetcher((chunkFrom, chunkTo) =>
                throw new InvalidOperationException("still broken"));
            var importer = new FXCMHistoricalImporter(fetcher.Fetch, repo, requestDelay: TimeSpan.Zero, maxRetriesPerChunk: 2);

            var failedChunks = new[]
            {
                new ImportChunkFailure { ChunkStartUtc = gapStart, ChunkEndUtc = gapEnd, Attempts = 3, LastErrorMessage = "boom" }
            };

            ImportResult result = importer.RetryFailedChunks("XAU/USD", "H1", failedChunks);

            Assert.Equal(1, result.ChunksFailed);
            Assert.Single(result.FailedChunks);
            Assert.Equal(gapStart, result.FailedChunks[0].ChunkStartUtc);
            Assert.Contains("still broken", result.FailedChunks[0].LastErrorMessage);
        }

        [Fact]
        public void RetryFailedChunks_MultipleGaps_AreAllRetried_InOrder()
        {
            var repo = CreateRepository();
            var gap1 = new DateTime(2026, 6, 15, 5, 0, 0, DateTimeKind.Utc);
            var gap2 = new DateTime(2026, 6, 20, 5, 0, 0, DateTimeKind.Utc);

            var fetcher = new FakeFetcher((chunkFrom, chunkTo) => new List<Candle> { CandleAt(chunkFrom) });
            var importer = new FXCMHistoricalImporter(fetcher.Fetch, repo, requestDelay: TimeSpan.Zero);

            // Deliberately supplied out of order.
            var failedChunks = new[]
            {
                new ImportChunkFailure { ChunkStartUtc = gap2, ChunkEndUtc = gap2.AddHours(1) },
                new ImportChunkFailure { ChunkStartUtc = gap1, ChunkEndUtc = gap1.AddHours(1) }
            };

            ImportResult result = importer.RetryFailedChunks("XAU/USD", "H1", failedChunks);

            Assert.Equal(2, result.ChunksSucceeded);
            Assert.Equal(2, fetcher.Calls.Count);
            Assert.Equal(gap1, fetcher.Calls[0].From); // processed oldest-first regardless of input order
            Assert.Equal(gap2, fetcher.Calls[1].From);
        }

        [Fact]
        public void RetryFailedChunks_EmptyOrNullList_ReturnsAlreadyUpToDate_MakesNoRequests()
        {
            var repo = CreateRepository();
            var fetcher = new FakeFetcher((chunkFrom, chunkTo) => new List<Candle>());
            var importer = new FXCMHistoricalImporter(fetcher.Fetch, repo, requestDelay: TimeSpan.Zero);

            ImportResult emptyResult = importer.RetryFailedChunks("XAU/USD", "H1", new List<ImportChunkFailure>());
            ImportResult nullResult = importer.RetryFailedChunks("XAU/USD", "H1", null);

            Assert.True(emptyResult.AlreadyUpToDate);
            Assert.True(nullResult.AlreadyUpToDate);
            Assert.Empty(fetcher.Calls);
        }

        [Fact]
        public void RetryFailedChunks_NullOrEmptyInstrumentOrTimeframe_Throws()
        {
            var repo = CreateRepository();
            var importer = new FXCMHistoricalImporter((i, t, f, to) => new List<Candle>(), repo, requestDelay: TimeSpan.Zero);
            var chunk = new[] { new ImportChunkFailure { ChunkStartUtc = DateTime.UtcNow, ChunkEndUtc = DateTime.UtcNow.AddHours(1) } };

            Assert.Throws<ArgumentException>(() => importer.RetryFailedChunks(null, "H1", chunk));
            Assert.Throws<ArgumentException>(() => importer.RetryFailedChunks("XAU/USD", "", chunk));
        }

        // ---------------------------------------------------------------
        // End-to-end: the exact real-world workflow Program.cs will use.
        // ---------------------------------------------------------------

        [Fact]
        public void ImportThenRetryFailedChunks_FillsTheGap_LeavesNoHoleInStoredHistory()
        {
            var repo = CreateRepository();
            var from = new DateTime(2026, 6, 15, 4, 0, 0, DateTimeKind.Utc);
            var to = from.AddHours(3);
            var badChunkStart = from.AddHours(1);

            bool badChunkShouldFailNow = true;
            var fetcher = new FakeFetcher((chunkFrom, chunkTo) =>
            {
                if (chunkFrom == badChunkStart && badChunkShouldFailNow)
                {
                    throw new InvalidOperationException("transient outage");
                }
                return new List<Candle> { CandleAt(chunkFrom) };
            });

            var importer = new FXCMHistoricalImporter(fetcher.Fetch, repo, requestDelay: TimeSpan.Zero, maxRetriesPerChunk: 1);

            ImportResult firstPass = importer.Import("XAU/USD", "H1", from, to, TimeSpan.FromHours(1));
            Assert.Single(firstPass.FailedChunks);

            // Simulate the outage clearing before Program.cs's retry pass runs.
            badChunkShouldFailNow = false;
            ImportResult retryPass = importer.RetryFailedChunks("XAU/USD", "H1", firstPass.FailedChunks);

            Assert.Empty(retryPass.FailedChunks);
            var stored = repo.GetCandles("XAU/USD", "H1", from, to);
            Assert.Equal(3, stored.Count); // all three hours present - no hole left behind
        }

        // ---------------------------------------------------------------
        // Timeout on a hung request
        // ---------------------------------------------------------------

        [Fact]
        public void Import_HungRequest_TimesOutAndIsTreatedAsFailure()
        {
            var repo = CreateRepository();
            var from = new DateTime(2026, 6, 15, 4, 0, 0, DateTimeKind.Utc);
            var to = from.AddHours(1);

            var fetcher = new FakeFetcher((chunkFrom, chunkTo) =>
            {
                Thread.Sleep(TimeSpan.FromSeconds(5)); // simulate a dead connection that never responds
                return new List<Candle>();
            });

            var importer = new FXCMHistoricalImporter(
                fetcher.Fetch,
                repo,
                requestDelay: TimeSpan.Zero,
                requestTimeout: TimeSpan.FromMilliseconds(100),
                maxRetriesPerChunk: 1);

            ImportResult result = importer.Import("XAU/USD", "H1", from, to, TimeSpan.FromHours(1));

            Assert.Equal(1, result.ChunksFailed);
            Assert.Single(result.FailedChunks);
            Assert.Contains("Timed out", result.FailedChunks[0].LastErrorMessage);
        }

        // ---------------------------------------------------------------
        // Received vs inserted: re-running the same import is idempotent
        // ---------------------------------------------------------------

        [Fact]
        public void Import_RunTwiceOverSameRange_SecondRunReceivesButDoesNotReinsert()
        {
            var repo = CreateRepository();
            var from = new DateTime(2026, 6, 15, 4, 0, 0, DateTimeKind.Utc);
            var to = from.AddHours(1);

            var fetcher = new FakeFetcher((chunkFrom, chunkTo) => new List<Candle> { CandleAt(chunkFrom) });
            var importer = new FXCMHistoricalImporter(fetcher.Fetch, repo, requestDelay: TimeSpan.Zero);

            ImportResult first = importer.Import("XAU/USD", "H1", from, to, TimeSpan.FromHours(1));
            ImportResult second = importer.Import("XAU/USD", "H1", from, to, TimeSpan.FromHours(1));

            Assert.Equal(1, first.CandlesInserted);
            Assert.Equal(1, second.CandlesReceived);
            Assert.Equal(0, second.CandlesInserted); // already present - unique index ignored it

            Assert.Equal(1, repo.GetCandleCount("XAU/USD", "H1"));
        }

        // ---------------------------------------------------------------
        // Argument guards
        // ---------------------------------------------------------------

        [Fact]
        public void Constructor_NullFetcherOrRepository_Throws()
        {
            var repo = CreateRepository();

            Assert.Throws<ArgumentNullException>(() => new FXCMHistoricalImporter(null, repo));
            Assert.Throws<ArgumentNullException>(() => new FXCMHistoricalImporter((i, t, f, to) => new List<Candle>(), null));
        }

        [Fact]
        public void Constructor_NonPositiveMaxRetries_Throws()
        {
            var repo = CreateRepository();

            Assert.Throws<ArgumentOutOfRangeException>(
                () => new FXCMHistoricalImporter((i, t, f, to) => new List<Candle>(), repo, maxRetriesPerChunk: 0));
        }

        [Fact]
        public void Import_NullOrEmptyInstrumentOrTimeframe_Throws()
        {
            var repo = CreateRepository();
            var importer = new FXCMHistoricalImporter((i, t, f, to) => new List<Candle>(), repo, requestDelay: TimeSpan.Zero);
            var from = new DateTime(2026, 6, 15, 4, 0, 0, DateTimeKind.Utc);

            Assert.Throws<ArgumentException>(() => importer.Import(null, "H1", from, from.AddHours(1), TimeSpan.FromHours(1)));
            Assert.Throws<ArgumentException>(() => importer.Import("XAU/USD", "", from, from.AddHours(1), TimeSpan.FromHours(1)));
        }

        [Fact]
        public void Import_NonPositiveChunkSize_Throws()
        {
            var repo = CreateRepository();
            var importer = new FXCMHistoricalImporter((i, t, f, to) => new List<Candle>(), repo, requestDelay: TimeSpan.Zero);
            var from = new DateTime(2026, 6, 15, 4, 0, 0, DateTimeKind.Utc);

            Assert.Throws<ArgumentOutOfRangeException>(
                () => importer.Import("XAU/USD", "H1", from, from.AddHours(1), TimeSpan.Zero));
        }
    }
}
