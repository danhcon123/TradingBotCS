namespace TradingBot.FXCM.Models
{
    /// <summary>
    /// Full diagnostic result of analyzing one M1 candle's geometry for a Dragonfly Doji.
    /// Every intermediate value and per-rule pass/fail flag is preserved so results can be
    /// inspected during debugging and backtesting, not just the final boolean verdict.
    /// </summary>
    public class DragonflyAnalysis
    {
        /// <summary>
        /// False when the candle's OHLC values are internally inconsistent (High &lt; Open/Close,
        /// Low &gt; Open/Close, High &lt; Low) or contain NaN/Infinity. Malformed historical data
        /// must never be able to qualify as a Dragonfly.
        /// </summary>
        public bool IsValidOhlc { get; set; }

        /// <summary>False when High == Low (zero-range candle) - always disqualifying.</summary>
        public bool HasRange { get; set; }

        public double Body { get; set; }

        public double Range { get; set; }

        public double UpperWick { get; set; }

        public double LowerWick { get; set; }

        /// <summary>
        /// LowerWick / Body. When Body is zero this is PositiveInfinity if LowerWick > 0,
        /// otherwise 0 - it is diagnostic only and never used directly for the pass/fail rule.
        /// </summary>
        public double LowerWickToBodyRatio { get; set; }

        /// <summary>Body expressed as a percentage of Range (0 when Range is zero).</summary>
        public double BodyPercentage { get; set; }

        public bool LowerWickRulePassed { get; set; }

        public bool BodyRulePassed { get; set; }

        public bool UpperWickRulePassed { get; set; }

        /// <summary>
        /// Previous completed M1 candle's range, when a previous candle was supplied.
        /// Populated for diagnostics even when <see cref="PreviousRangeRuleEnabled"/> is false.
        /// </summary>
        public double? PreviousRange { get; set; }

        /// <summary>
        /// Range / PreviousRange, whenever a valid, positive previous range is available -
        /// regardless of whether the rule is enabled. Saves recomputing this during
        /// backtest reporting/inspection.
        /// </summary>
        public double? CurrentToPreviousRangeRatio { get; set; }

        public bool PreviousRangeRuleEnabled { get; set; }

        /// <summary>
        /// Null when the rule was not evaluated (disabled). True/false when it was.
        /// </summary>
        public bool? PreviousRangeRulePassed { get; set; }

        public bool IsDragonfly { get; set; }
    }
}
