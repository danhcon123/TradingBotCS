using System;

namespace TradingBot.FXCM.Strategy
{
    /// <summary>
    /// Shared fixed-offset (UTC-4, intentionally NOT DST-aware) timezone conversion for the
    /// strategy layer. Both <see cref="H1LevelCalculator"/> (00:00 UTC-4 qualifying-candle
    /// check) and <see cref="StrategyTimeFilter"/> (12:00-15:59 UTC-4 blocked-hours check)
    /// depend on this, rather than one depending on the other.
    /// </summary>
    internal static class StrategyTime
    {
        public static readonly TimeSpan UtcOffset = TimeSpan.FromHours(-4);

        /// <summary>
        /// Converts a UTC timestamp into the strategy's fixed UTC-4 local time. Enforces the
        /// same UTC contract as <see cref="StrategyUtcContract"/> on its input.
        /// </summary>
        public static DateTime ToStrategyLocal(DateTime utcTime)
        {
            StrategyUtcContract.Ensure(utcTime);
            return DateTime.SpecifyKind(utcTime, DateTimeKind.Unspecified) + UtcOffset;
        }
    }
}
