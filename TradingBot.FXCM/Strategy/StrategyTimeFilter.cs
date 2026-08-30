using System;
using TradingBot.FXCM.Settings;

namespace TradingBot.FXCM.Strategy
{
    /// <summary>
    /// Optionally blocks signals during 12:00-15:59 strategy-local (fixed UTC-4) time.
    /// When <see cref="DragonflyStrategySettings.UseBlockedHours"/> is false, this filter
    /// never rejects a signal.
    /// </summary>
    public class StrategyTimeFilter
    {
        private readonly DragonflyStrategySettings _settings;

        /// <summary>
        /// Unlike <see cref="DragonflyDojiDetector"/>, this constructor deliberately does
        /// NOT call <see cref="DragonflyStrategySettings.Validate"/>: the only setting this
        /// filter reads is <see cref="DragonflyStrategySettings.UseBlockedHours"/>, a plain
        /// bool with no invalid values, so there is nothing here to defensively validate.
        /// Full cross-cutting validation of the shared settings object belongs to
        /// <c>DragonflyStrategy</c>, which owns the complete configuration.
        /// </summary>
        public StrategyTimeFilter(DragonflyStrategySettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        /// <summary>
        /// True when <paramref name="candleTimeUtc"/> falls inside the blocked window
        /// (strategy-local hour in [12, 16)) and the filter is enabled. Always false when
        /// <see cref="DragonflyStrategySettings.UseBlockedHours"/> is disabled.
        /// </summary>
        public bool IsBlocked(DateTime candleTimeUtc)
        {
            StrategyUtcContract.Ensure(candleTimeUtc);

            if (!_settings.UseBlockedHours)
            {
                return false;
            }

            DateTime local = StrategyTime.ToStrategyLocal(candleTimeUtc);
            return local.Hour >= 12 && local.Hour < 16;
        }
    }
}
