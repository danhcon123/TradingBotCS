using System;

namespace TradingBot.FXCM.Models
{
    /// <summary>
    /// One chunk that could not be downloaded within the importer's configured retry
    /// budget. Kept as diagnostic information rather than silently swallowed - re-running
    /// the importer will naturally retry it, since only successfully saved candles ever
    /// advance the resume point.
    /// </summary>
    public class ImportChunkFailure
    {
        public DateTime ChunkStartUtc { get; set; }

        public DateTime ChunkEndUtc { get; set; }

        public int Attempts { get; set; }

        public string LastErrorMessage { get; set; }

        public override string ToString()
        {
            return
                $"{ChunkStartUtc:yyyy-MM-dd HH:mm} - {ChunkEndUtc:yyyy-MM-dd HH:mm} " +
                $"failed after {Attempts} attempt(s): {LastErrorMessage}";
        }
    }
}
