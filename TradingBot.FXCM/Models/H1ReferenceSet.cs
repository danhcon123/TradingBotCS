using System;
using System.Collections.Generic;

namespace TradingBot.FXCM.Models
{
    /// <summary>
    /// One qualifying H1 reference candle (00:00 UTC-4 open) reduced to the
    /// three price levels the strategy compares against a Dragonfly's lower wick.
    /// </summary>
    public class H1ReferenceSet
    {
        public DateTime SourceCandleTimeUtc { get; set; }

        public DateTime StrategyDate { get; set; }

        public double High { get; set; }

        public double Midpoint { get; set; }

        public double Low { get; set; }

        /// <summary>
        /// Expands this set into its three individual levels, ordered
        /// High, Midpoint, Low to match the strategy's primary-intersection priority.
        /// </summary>
        public IReadOnlyList<PriceLevel> ToPriceLevels()
        {
            return new List<PriceLevel>
            {
                new PriceLevel
                {
                    Price = High,
                    LevelType = PriceLevelType.High,
                    SourceCandleTimeUtc = SourceCandleTimeUtc,
                    StrategyDate = StrategyDate
                },
                new PriceLevel
                {
                    Price = Midpoint,
                    LevelType = PriceLevelType.Midpoint,
                    SourceCandleTimeUtc = SourceCandleTimeUtc,
                    StrategyDate = StrategyDate
                },
                new PriceLevel
                {
                    Price = Low,
                    LevelType = PriceLevelType.Low,
                    SourceCandleTimeUtc = SourceCandleTimeUtc,
                    StrategyDate = StrategyDate
                }
            };
        }

        public override string ToString()
        {
            return
                $"H1Ref {StrategyDate:yyyy-MM-dd} " +
                $"(source {SourceCandleTimeUtc:yyyy-MM-dd HH:mm} UTC) " +
                $"H:{High} M:{Midpoint} L:{Low}";
        }
    }
}
