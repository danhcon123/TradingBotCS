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
    /// Downloads historical candles in fixed-size chunks and saves each successful
    /// chunk immediately.
    ///
    /// The repository is responsible for idempotent writes:
    /// - new candles are inserted,
    /// - existing candles with changed OHLCV values are updated,
    /// - identical existing candles are ignored.
    ///
    /// This makes the importer safe to interrupt and re-run.
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
            _fetchCandles =
                fetchCandles ??
                throw new ArgumentNullException(nameof(fetchCandles));

            _repository =
                repository ??
                throw new ArgumentNullException(nameof(repository));

            if (maxRetriesPerChunk <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxRetriesPerChunk),
                    maxRetriesPerChunk,
                    "Must be greater than zero.");
            }

            _requestDelay =
                requestDelay ?? TimeSpan.FromSeconds(1);

            _requestTimeout =
                requestTimeout ?? TimeSpan.FromSeconds(30);

            _maxRetriesPerChunk =
                maxRetriesPerChunk;
        }

        /// <summary>
        /// Determines where an import should resume.
        ///
        /// We deliberately resume FROM the newest stored candle rather than from
        /// the candle after it. Re-requesting the latest candle is intentional:
        /// CandleRepository can update it if FXCM later provides corrected OHLCV
        /// values, while an identical candle becomes a no-op.
        /// </summary>
        public DateTime ResolveResumeStartUtc(
            string instrument,
            string timeframe,
            DateTime requestedFromUtc)
        {
            DateTime? latest =
                _repository.GetLatestTimestampUtc(
                    instrument,
                    timeframe);

            if (latest.HasValue &&
                latest.Value > requestedFromUtc)
            {
                return latest.Value;
            }

            return requestedFromUtc;
        }

        /// <summary>
        /// Downloads and stores [fromUtc, toUtc) in fixed-size chunks.
        ///
        /// Failed chunks are recorded instead of aborting the entire import.
        /// They can later be passed to RetryFailedChunks().
        /// </summary>
        public ImportResult Import(
            string instrument,
            string timeframe,
            DateTime fromUtc,
            DateTime toUtc,
            TimeSpan chunkSize)
        {
            if (string.IsNullOrWhiteSpace(instrument))
            {
                throw new ArgumentException(
                    "Instrument must not be null or empty.",
                    nameof(instrument));
            }

            if (string.IsNullOrWhiteSpace(timeframe))
            {
                throw new ArgumentException(
                    "Timeframe must not be null or empty.",
                    nameof(timeframe));
            }

            if (chunkSize <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(chunkSize),
                    chunkSize,
                    "Must be greater than zero.");
            }

            if (toUtc <= fromUtc)
            {
                throw new ArgumentException(
                    "toUtc must be later than fromUtc.");
            }

            DateTime resumeFromUtc =
                ResolveResumeStartUtc(
                    instrument,
                    timeframe,
                    fromUtc);

            var result = new ImportResult
            {
                Instrument = instrument,
                Timeframe = timeframe,
                RequestedFromUtc = fromUtc,
                RequestedToUtc = toUtc,
                ActualFromUtc = resumeFromUtc,
                FailedChunks =
                    new List<ImportChunkFailure>()
            };

            if (resumeFromUtc >= toUtc)
            {
                result.AlreadyUpToDate = true;
                return result;
            }

            var failedChunks =
                new List<ImportChunkFailure>();

            DateTime chunkStart =
                resumeFromUtc;

            while (chunkStart < toUtc)
            {
                DateTime chunkEnd =
                    chunkStart + chunkSize;

                if (chunkEnd > toUtc)
                {
                    chunkEnd = toUtc;
                }

                ProcessSingleChunk(
                    instrument,
                    timeframe,
                    chunkStart,
                    chunkEnd,
                    result,
                    failedChunks);

                chunkStart = chunkEnd;

                if (chunkStart < toUtc)
                {
                    Sleep(_requestDelay);
                }
            }

            result.FailedChunks =
                failedChunks;

            return result;
        }

        /// <summary>
        /// Retries specifically failed chunk ranges.
        ///
        /// This deliberately does not use ResolveResumeStartUtc(), because a failed
        /// chunk may be located before newer candles that were successfully imported.
        /// </summary>
        public ImportResult RetryFailedChunks(
            string instrument,
            string timeframe,
            IEnumerable<ImportChunkFailure> failedChunks)
        {
            if (string.IsNullOrWhiteSpace(instrument))
            {
                throw new ArgumentException(
                    "Instrument must not be null or empty.",
                    nameof(instrument));
            }

            if (string.IsNullOrWhiteSpace(timeframe))
            {
                throw new ArgumentException(
                    "Timeframe must not be null or empty.",
                    nameof(timeframe));
            }

            List<ImportChunkFailure> chunksToRetry =
                (failedChunks ??
                 Enumerable.Empty<ImportChunkFailure>())
                .Where(f => f != null)
                .OrderBy(f => f.ChunkStartUtc)
                .ToList();

            var result = new ImportResult
            {
                Instrument = instrument,
                Timeframe = timeframe,

                RequestedFromUtc =
                    chunksToRetry.Count > 0
                        ? chunksToRetry[0].ChunkStartUtc
                        : default(DateTime),

                RequestedToUtc =
                    chunksToRetry.Count > 0
                        ? chunksToRetry[
                            chunksToRetry.Count - 1]
                            .ChunkEndUtc
                        : default(DateTime),

                ActualFromUtc =
                    chunksToRetry.Count > 0
                        ? chunksToRetry[0].ChunkStartUtc
                        : default(DateTime),

                AlreadyUpToDate =
                    chunksToRetry.Count == 0
            };

            var stillFailed =
                new List<ImportChunkFailure>();

            for (int i = 0;
                 i < chunksToRetry.Count;
                 i++)
            {
                ImportChunkFailure chunk =
                    chunksToRetry[i];

                ProcessSingleChunk(
                    instrument,
                    timeframe,
                    chunk.ChunkStartUtc,
                    chunk.ChunkEndUtc,
                    result,
                    stillFailed);

                if (i < chunksToRetry.Count - 1)
                {
                    Sleep(_requestDelay);
                }
            }

            result.FailedChunks =
                stillFailed;

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
            ChunkOutcome outcome =
                DownloadChunkWithRetry(
                    instrument,
                    timeframe,
                    chunkStart,
                    chunkEnd);

            if (outcome.Succeeded)
            {
                /*
                 * FXCM may occasionally return candles around the requested
                 * boundaries. Only persist candles that actually belong to
                 * this chunk:
                 *
                 *      chunkStart <= Time < chunkEnd
                 */
                List<Candle> candlesInChunk =
                    outcome.Candles
                        .Where(c =>
                            c != null &&
                            c.Time >= chunkStart &&
                            c.Time < chunkEnd)
                        .OrderBy(c => c.Time)
                        .ToList();

                int written =
                    _repository.SaveCandles(
                        instrument,
                        timeframe,
                        candlesInChunk);

                result.ChunksSucceeded++;

                /*
                 * CandlesReceived intentionally counts what FXCM returned
                 * before boundary filtering/de-duplication.
                 */
                result.CandlesReceived +=
                    outcome.Candles.Count;

                /*
                 * Written means:
                 *   INSERTED new candle
                 *          OR
                 *   UPDATED existing changed candle
                 *
                 * Identical candles count as zero.
                 */
                result.CandlesWritten +=
                    written;
            }
            else
            {
                result.ChunksFailed++;

                failedChunks.Add(
                    new ImportChunkFailure
                    {
                        ChunkStartUtc =
                            chunkStart,

                        ChunkEndUtc =
                            chunkEnd,

                        Attempts =
                            outcome.Attempts,

                        LastErrorMessage =
                            outcome.LastErrorMessage
                    });
            }
        }

        private ChunkOutcome DownloadChunkWithRetry(
            string instrument,
            string timeframe,
            DateTime chunkStart,
            DateTime chunkEnd)
        {
            string lastError = null;

            for (int attempt = 1;
                 attempt <= _maxRetriesPerChunk;
                 attempt++)
            {
                try
                {
                    List<Candle> candles =
                        FetchWithTimeout(
                            instrument,
                            timeframe,
                            chunkStart,
                            chunkEnd);

                    return ChunkOutcome.Success(
                        candles,
                        attempt);
                }
                catch (Exception ex)
                {
                    lastError =
                        DescribeException(ex);

                    if (attempt <
                        _maxRetriesPerChunk)
                    {
                        Sleep(_requestDelay);
                    }
                }
            }

            return ChunkOutcome.Failure(
                _maxRetriesPerChunk,
                lastError);
        }

        /// <summary>
        /// Secondary safety timeout around the fetch delegate.
        ///
        /// PriceHistoryListener also has its own completion timeout. This
        /// importer-level timeout prevents a broken fetch implementation from
        /// blocking the entire historical import indefinitely.
        ///
        /// Task.Run cannot forcibly cancel the underlying FXCM request, so the
        /// listener itself must also have a timeout.
        /// </summary>
        private List<Candle> FetchWithTimeout(
            string instrument,
            string timeframe,
            DateTime chunkStart,
            DateTime chunkEnd)
        {
            Task<List<Candle>> task =
                Task.Run(
                    () => _fetchCandles(
                        instrument,
                        timeframe,
                        chunkStart,
                        chunkEnd));

            if (!task.Wait(_requestTimeout))
            {
                throw new TimeoutException(
                    $"Timed out after {_requestTimeout} waiting for " +
                    $"{instrument} {timeframe} " +
                    $"{chunkStart:yyyy-MM-dd HH:mm} - " +
                    $"{chunkEnd:yyyy-MM-dd HH:mm}.");
            }

            return task.Result;
        }

        private static string DescribeException(
            Exception ex)
        {
            AggregateException aggregate =
                ex as AggregateException;

            if (aggregate != null)
            {
                return aggregate
                    .GetBaseException()
                    .Message;
            }

            return ex.Message;
        }

        private static void Sleep(
            TimeSpan delay)
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

            public static ChunkOutcome Success(
                List<Candle> candles,
                int attempts)
            {
                return new ChunkOutcome
                {
                    Succeeded = true,

                    Candles =
                        candles ??
                        new List<Candle>(),

                    Attempts =
                        attempts
                };
            }

            public static ChunkOutcome Failure(
                int attempts,
                string lastErrorMessage)
            {
                return new ChunkOutcome
                {
                    Succeeded = false,

                    Attempts =
                        attempts,

                    LastErrorMessage =
                        lastErrorMessage
                };
            }
        }
    }
}