using System;

namespace TradingBot.FXCM.Models
{
    public class PriceLevel
    {
        public double Price { get; set; }

        public PriceLevelType LevelType { get; set; }

        public DateTime SourceCandleTimeUtc { get; set; }

        public DateTime StrategyDate { get; set; }

        public override string ToString()
        {
            return
                $"{LevelType} " +
                $"{Price} " +
                $"(source {SourceCandleTimeUtc:yyyy-MM-dd HH:mm} UTC, " +
                $"strategy date {StrategyDate:yyyy-MM-dd})";
        }
    }
}
