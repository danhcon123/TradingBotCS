namespace TradingBot.FXCM.Models
{
    /// <summary>
    /// One H1 reference price level found to intersect a confirmed Dragonfly's lower wick.
    /// </summary>
    public class LevelIntersection
    {
        public PriceLevel Level { get; set; }

        /// <summary>Lower boundary of the wick that was tested (== candle Low).</summary>
        public double LowerWickBottom { get; set; }

        /// <summary>Upper boundary of the wick that was tested (== bottom of the candle body).</summary>
        public double LowerWickTop { get; set; }

        public override string ToString()
        {
            return $"{Level} intersects wick [{LowerWickBottom} - {LowerWickTop}]";
        }
    }
}
