using System;
using System.Collections.Generic;

namespace TradingBot.FXCM.Models
{
    /// <summary>Full diagnostic result of one FXCMHistoricalImporter.Import call.</summary>
    public class ImportResult
    {
        public string Instrument { get; set; }

        public string Timeframe { get; set; }

        public DateTime RequestedFromUtc { get; set; }

        public DateTime RequestedToUtc { get; set; }

        /// <summary>
        /// The instant downloading actually started from, after skipping any range already
        /// present in the repository. Equal to RequestedFromUtc when nothing was skipped.
        /// </summary>
        public DateTime ActualFromUtc { get; set; }

        /// <summary>True when the requested range was already fully covered by stored data - nothing was downloaded.</summary>
        public bool AlreadyUpToDate { get; set; }

        public int ChunksSucceeded { get; set; }

        public int ChunksFailed { get; set; }

        /// <summary>Total candles received across all successful chunk requests, before de-duplication.</summary>
        public int CandlesReceived { get; set; }

        /// <summary>Total candles actually inserted (excludes rows the unique index silently skipped as duplicates).</summary>
        public int CandlesInserted { get; set; }

        public IReadOnlyList<ImportChunkFailure> FailedChunks { get; set; }
    }
}
