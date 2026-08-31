using System;

namespace TradingBot.App.Live
{
    /// <summary>
    /// Tunable knobs for <see cref="LiveSignalRunner"/>. Defaults are conservative starting
    /// points for the first live version - adjust based on what the soak test actually shows.
    /// </summary>
    public class LiveSignalRunnerOptions
    {
        /// <summary>
        /// Extra delay added after each UTC minute boundary before requesting the just-closed
        /// candle. The just-closed FXCM candle may not be immediately available at exactly
        /// xx:xx:00.000.
        /// </summary>
        public TimeSpan SafetyDelay { get; set; } = TimeSpan.FromSeconds(3);

        /// <summary>
        /// How far back each live M1 request looks. Requesting a small window (not exactly
        /// one candle) lets a short API/network delay be recovered on the very next tick
        /// instead of silently losing a candle.
        /// </summary>
        public TimeSpan M1LookbackWindow { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>
        /// How far back the H1 candle list handed to the existing strategy reaches. Must be
        /// generous enough to contain at least H1ReferenceCount qualifying (00:00 UTC-4)
        /// candles even across weekends/holidays. The strategy itself (H1LevelCalculator)
        /// still filters this down to only completed, qualifying candles - this window just
        /// has to be wide enough to contain them.
        /// </summary>
        public TimeSpan H1StrategyWindow { get; set; } = TimeSpan.FromDays(35);

        /// <summary>
        /// History depth ensured in the repository once at startup via the existing resumable
        /// FXCMHistoricalImporter (a cheap no-op if the database already covers this).
        /// </summary>
        public TimeSpan H1BackfillWindow { get; set; } = TimeSpan.FromDays(40);

        /// <summary>Chunk size used only for the one-time startup H1 backfill.</summary>
        public TimeSpan H1BackfillChunkSize { get; set; } = TimeSpan.FromDays(3);

        /// <summary>
        /// Delay FXCMHistoricalImporter sleeps between chunks during the one-time startup H1
        /// backfill (its own default - kept explicit here so it can be set to zero in tests).
        /// </summary>
        public TimeSpan H1BackfillRequestDelay { get; set; } = TimeSpan.FromSeconds(1);

        /// <summary>
        /// How far back each incremental live H1 refresh looks once a new H1 hour boundary
        /// has passed. Deliberately small - the startup backfill already ensured deep
        /// history, so live refreshes only need to pick up the most recent candle(s).
        /// </summary>
        public TimeSpan H1RefreshLookback { get; set; } = TimeSpan.FromHours(6);

        /// <summary>
        /// Error backoff durations, applied in order after 1st, 2nd, 3rd... consecutive
        /// failures; the last value repeats for any further consecutive failure (the cap).
        /// </summary>
        public TimeSpan[] ErrorBackoffSteps { get; set; } =
        {
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(15),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(60)
        };

        /// <summary>
        /// Consecutive NO_NEW_CANDLE results (a successful request that simply found nothing
        /// newer than the last processed candle) before the runner starts backing off from
        /// strict one-minute polling. Not an error - just a sign the market may be closed or
        /// broker maintenance is in progress.
        /// </summary>
        public int NoDataBackoffThreshold { get; set; } = 3;

        /// <summary>
        /// No-data backoff durations, applied once NoDataBackoffThreshold is exceeded; the
        /// last value repeats for any further consecutive no-data result (the cap, ~5 min).
        /// </summary>
        public TimeSpan[] NoDataBackoffSteps { get; set; } =
        {
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(2),
            TimeSpan.FromMinutes(5)
        };
    }
}
