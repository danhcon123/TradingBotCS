using System;
using System.Collections.Generic;

namespace TradingBot.FXCM.Models
{
    /// <summary>
    /// Full diagnostic result of one FXCMHistoricalImporter.Import call.
    /// </summary>
    public class ImportResult
    {
        public string Instrument { get; set; }

        public string Timeframe { get; set; }

        public DateTime RequestedFromUtc { get; set; }

        public DateTime RequestedToUtc { get; set; }

        /// <summary>
        /// The instant downloading actually started from, after skipping any range
        /// already present in the repository.
        /// Equal to RequestedFromUtc when nothing was skipped.
        /// </summary>
        public DateTime ActualFromUtc { get; set; }

        /// <summary>
        /// True when the requested range was already fully covered by stored data
        /// and nothing needed to be downloaded.
        /// </summary>
        public bool AlreadyUpToDate { get; set; }

        public int ChunksSucceeded { get; set; }

        public int ChunksFailed { get; set; }

        /// <summary>
        /// Total candles received across all successful FXCM chunk requests,
        /// before boundary filtering and repository de-duplication.
        /// </summary>
        public int CandlesReceived { get; set; }

        /// <summary>
        /// Total candles actually written to the repository.
        ///
        /// Includes:
        /// - newly inserted candles
        /// - existing candles whose OHLCV values changed
        ///
        /// Identical existing candles are not counted.
        /// </summary>
        public int CandlesWritten { get; set; }

        public IReadOnlyList<ImportChunkFailure> FailedChunks { get; set; }
    }
}