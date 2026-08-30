using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TradingBot.FXCM.Data;
using TradingBot.FXCM.Models;

namespace TradingBot.FXCM.Import
{
    /// <summary>
    /// Downloads historical candles in fixed-size chunks and saves each one to a
    /// <see cref="CandleRepository"/> immediately after receiving it, rather than
    /// accumulating the whole range in memory and saving once at the end. Combined with the
    /// repository's unique-index idempotency, this makes the importer safe to interrupt and
    /// re-run any number of times: a re-run resumes from the latest stored candle instead of
    /// starting over, and any chunk that failed simply gets requested again next run, since
    /// it never advanced the resume point.
    ///
    /// Depends on a plain fetch delegate rather than <c>FXCMPriceHistory</c> directly, so the
    /// chunking/retry/resume/dedup policy here is fully unit-testable with a fake delegate,
    /// without a live FXCM session. In production, <c>FXCMPriceHistory.GetCandles</c> already
    /// matches this delegate's signature and can be passed straight in
    /// (<c>new FXCMHistoricalImporter(history.GetCandles, repository)</c>).
    /// </summary>
    public class FXCMHistoricalImporter
    {
        private readonly Func<string, string, DateTime, DateTime, List<Candle>> _fetchCandles;
        private readonly CandleRepository _repository;
        private readonly TimeSpan _requestDelay;
        private readonly TimeSpan _requestTimeout;
        private readonly int _maxRetriesPerChunk;

        public FXCMHistoricalImporter(
            Func<string, string, DateTime, DateTime, List<Candle>> fetchCandles,
            CandleRepository repository,
            TimeSpan? requestDelay = null,
            TimeSpan? requestTimeout = null,
            int maxRetriesPerChunk = 3)
        {
            _fetchCandles = fetchCandles ?? throw new ArgumentNullException(nameof(fetchCandles));
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));

            if (maxRetriesPerChunk <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxRetriesPerChunk), maxRetriesPerChunk, "Must be greater than zero.");
            }

            _requestDelay = requestDelay ?? TimeSpan.FromSeconds(1);
            _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(30);
            _maxRetriesPerChunk = maxRetriesPerChunk;
        }

        /// <summary>
        /// Determines where a download should actually start: the later of
        /// <paramref name="requestedFromUtc"/> and the latest candle already stored for
        /// (instrument, timeframe). Deliberately does NOT add a margin past the latest
        /// stored candle - re-requesting it is harmless (the repository's unique index
        /// silently ignores the duplicate) and this avoids ever risking a gap through an
        /// off-by-one in this calculation.
        /// </summary>
        public DateTime ResolveResumeStartUtc(string instrument, string timeframe, DateTime requestedFromUtc)
        {
            DateTime? latest = _repository.GetLatestTimestampUtc(instrument, timeframe);
            if (latest.HasValue && latest.Value > requestedFromUtc)
            {
                return latest.Value;
            }

            return requestedFromUtc;
        }

        /// <summary>
        /// Downloads and saves [fromUtc, toUtc) in chunks of <paramref name="chunkSize"/>,
        /// skipping any leading range already present (see <see cref="ResolveResumeStartUtc"/>).
        /// Each chunk is saved to the repository immediately upon receipt, so interrupting
        /// the run partway through never loses already-downloaded data. A chunk that fails
        /// (exception or timeout) is retried up to the configured retry budget with a delay
        /// between attempts; if it still fails, it is recorded in the result and the import
        /// continues with the next chunk rather than aborting the whole run.
        /// </summary>
        public ImportResult Import(string instrument, string timeframe, DateTime fromUtc, DateTime toUtc, TimeSpan chunkSize)
        {
            if (string.IsNullOrWhiteSpace(instrument))
            {
                throw new ArgumentException("Instrument must not be null or empty.", nameof(instrument));
            }

            if (string.IsNullOrWhiteSpace(timeframe))
            {
                throw new ArgumentException("Timeframe must not be null or empty.", nameof(timeframe));
            }

            if (chunkSize <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(chunkSize), chunkSize, "Must be greater than zero.");
            }

            DateTime resumeFromUtc = ResolveResumeStartUtc(instrument, timeframe, fromUtc);

            var result = new ImportResult
            {
                Instrument = instrument,
                Timeframe = timeframe,
                RequestedFromUtc = fromUtc,
                RequestedToUtc = toUtc,
                ActualFromUtc = resumeFromUtc,
                FailedChunks = new List<ImportChunkFailure>()
            };

            if (resumeFromUtc >= toUtc)
            {
                result.AlreadyUpToDate = true;
                return result;
            }

            var failedChunks = new List<ImportChunkFailure>();
            DateTime chunkStart = resumeFromUtc;

            while (chunkStart < toUtc)
            {
                DateTime chunkEnd = chunkStart + chunkSize;
                if (chunkEnd > toUtc)
                {
                    chunkEnd = toUtc;
                }

                ProcessSingleChunk(instrument, timeframe, chunkStart, chunkEnd, result, failedChunks);

                chunkStart = chunkEnd;

                if (chunkStart < toUtc)
                {
                    Sleep(_requestDelay);
                }
            }

            result.FailedChunks = failedChunks;
            return result;
        }

        /// <summary>
        /// Re-attempts exactly the chunk ranges recorded in a previous
        /// <see cref="ImportResult.FailedChunks"/> (from <see cref="Import"/> or a prior call
        /// to this method). Deliberately bypasses <see cref="ResolveResumeStartUtc"/> - those
        /// chunks are known, specific gaps to fill, not a linear continuation point. This
        /// matters because a middle chunk can fail while a later chunk succeeds (the later
        /// chunk's download doesn't depend on the earlier one), and the repository's "latest
        /// timestamp" would then already be past the earlier gap - <see cref="Import"/> alone
        /// would think that range is already covered and silently never retry it.
        /// </summary>
        public ImportResult RetryFailedChunks(string instrument, string timeframe, IEnumerable<ImportChunkFailure> failedChunks)
        {
            if (string.IsNullOrWhiteSpace(instrument))
            {
                throw new ArgumentException("Instrument must not be null or empty.", nameof(instrument));
            }

            if (string.IsNullOrWhiteSpace(timeframe))
            {
                throw new ArgumentException("Timeframe must not be null or empty.", nameof(timeframe));
            }

            List<ImportChunkFailure> chunksToRetry = (failedChunks ?? Enumerable.Empty<ImportChunkFailure>())
                .Where(f => f != null)
                .OrderBy(f => f.ChunkStartUtc)
                .ToList();

            var result = new ImportResult
            {
                Instrument = instrument,
                Timeframe = timeframe,
                RequestedFromUtc = chunksToRetry.Count > 0 ? chunksToRetry[0].ChunkStartUtc : default(DateTime),
                RequestedToUtc = chunksToRetry.Count > 0 ? chunksToRetry[chunksToRetry.Count - 1].ChunkEndUtc : default(DateTime),
                ActualFromUtc = chunksToRetry.Count > 0 ? chunksToRetry[0].ChunkStartUtc : default(DateTime),
                AlreadyUpToDate = chunksToRetry.Count == 0
            };

            var stillFailed = new List<ImportChunkFailure>();

            for (int i = 0; i < chunksToRetry.Count; i++)
            {
                ImportChunkFailure chunk = chunksToRetry[i];
                ProcessSingleChunk(instrument, timeframe, chunk.ChunkStartUtc, chunk.ChunkEndUtc, result, stillFailed);

                if (i < chunksToRetry.Count - 1)
                {
                    Sleep(_requestDelay);
                }
            }

            result.FailedChunks = stillFailed;
            return result;
        }

        private void ProcessSingleChunk(
            string instrument,
            string timeframe,
            DateTime chunkStart,
            DateTime chunkEnd,
            ImportResult result,
            List<ImportChunkFailure> failedChunks)
        {
            ChunkOutcome outcome = DownloadChunkWithRetry(instrument, timeframe, chunkStart, chunkEnd);

            if (outcome.Succeeded)
            {
                int inserted = _repository.SaveCandles(instrument, timeframe, outcome.Candles);
                result.ChunksSucceeded++;
                result.CandlesReceived += outcome.Candles.Count;
                result.CandlesInserted += inserted;
            }
            else
            {
                result.ChunksFailed++;
                failedChunks.Add(new ImportChunkFailure
                {
                    ChunkStartUtc = chunkStart,
                    ChunkEndUtc = chunkEnd,
                    Attempts = outcome.Attempts,
                    LastErrorMessage = outcome.LastErrorMessage
                });
            }
        }

        private ChunkOutcome DownloadChunkWithRetry(string instrument, string timeframe, DateTime chunkStart, DateTime chunkEnd)
        {
            string lastError = null;

            for (int attempt = 1; attempt <= _maxRetriesPerChunk; attempt++)
            {
                try
                {
                    List<Candle> candles = FetchWithTimeout(instrument, timeframe, chunkStart, chunkEnd);
                    return ChunkOutcome.Success(candles, attempt);
                }
                catch (Exception ex)
                {
                    lastError = DescribeException(ex);

                    if (attempt < _maxRetriesPerChunk)
                    {
                        Sleep(_requestDelay);
                    }
                }
            }

            return ChunkOutcome.Failure(_maxRetriesPerChunk, lastError);
        }

        /// <summary>
        /// Bounds the fetch call with a timeout. This is a safety net for a scenario the
        /// underlying FXCM listener itself cannot signal: if the connection dies mid-request,
        /// its wait loop has no timeout and would otherwise block this importer forever. The
        /// background task cannot actually be cancelled if it times out - it is simply no
        /// longer waited on - so this bounds THIS importer's progress, not the underlying
        /// call's lifetime; a genuinely hung request still leaks a thread-pool thread.
        /// </summary>
        private List<Candle> FetchWithTimeout(string instrument, string timeframe, DateTime chunkStart, DateTime chunkEnd)
        {
            Task<List<Candle>> task = Task.Run(() => _fetchCandles(instrument, timeframe, chunkStart, chunkEnd));

            if (!task.Wait(_requestTimeout))
            {
                throw new TimeoutException(
                    $"Timed out after {_requestTimeout} waiting for {instrument} {timeframe} " +
                    $"{chunkStart:yyyy-MM-dd HH:mm} - {chunkEnd:yyyy-MM-dd HH:mm}.");
            }

            return task.Result;
        }

        private static string DescribeException(Exception ex)
        {
            return ex is AggregateException aggregate ? aggregate.GetBaseException().Message : ex.Message;
        }

        private static void Sleep(TimeSpan delay)
        {
            if (delay > TimeSpan.Zero)
            {
                Thread.Sleep(delay);
            }
        }

        private struct ChunkOutcome
        {
            public bool Succeeded;
            public List<Candle> Candles;
            public int Attempts;
            public string LastErrorMessage;

            public static ChunkOutcome Success(List<Candle> candles, int attempts)
            {
                return new ChunkOutcome { Succeeded = true, Candles = candles ?? new List<Candle>(), Attempts = attempts };
            }

            public static ChunkOutcome Failure(int attempts, string lastErrorMessage)
            {
                return new ChunkOutcome { Succeeded = false, Attempts = attempts, LastErrorMessage = lastErrorMessage };
            }
        }
    }
}
