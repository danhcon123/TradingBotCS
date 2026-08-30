using System;

namespace TradingBot.FXCM.Strategy
{
    /// <summary>
    /// Central definition of the "every timestamp reaching the strategy layer represents
    /// UTC" contract, shared by every strategy component that consumes a raw timestamp
    /// (candle times, evaluation times, etc). <see cref="DateTimeKind.Utc"/> and
    /// <see cref="DateTimeKind.Unspecified"/> are both trusted as UTC (FXCM candles
    /// typically arrive as Unspecified). <see cref="DateTimeKind.Local"/> is rejected
    /// outright, since it indicates the timestamp was converted somewhere upstream and its
    /// true UTC instant is now ambiguous to the strategy layer.
    /// </summary>
    internal static class StrategyUtcContract
    {
        public static void Ensure(DateTime time)
        {
            if (time.Kind == DateTimeKind.Local)
            {
                throw new ArgumentException(
                    "Timestamp must represent UTC. A DateTimeKind.Local timestamp was " +
                    "supplied - convert it to UTC before it reaches the strategy layer.");
            }
        }
    }
}
