using System.Collections.Generic;

namespace TradingBot.FXCM.Models
{
    /// <summary>
    /// Full diagnostic result of replaying a chronological M1 candle series through
    /// DragonflyStrategy. Aggregate counts give a quick read on how selective each gate is;
    /// <see cref="DragonflyResults"/> keeps the full per-candle diagnostics for every
    /// detected Dragonfly (not just final BUYs) - Dragonflies are rare enough under the
    /// default thresholds that keeping full results for all of them, including near-misses
    /// that failed a later gate, is cheap and valuable for manual chart review.
    /// </summary>
    public class BacktestResult
    {
        public int M1CandlesEvaluated { get; set; }

        public int DragonfliesDetected { get; set; }

        public int DragonfliesWithFullH1Set { get; set; }

        public int DragonfliesWithIntersection { get; set; }

        public int SignalsBlockedByTime { get; set; }

        public int BuySignals { get; set; }

        /// <summary>Every StrategyResult where IsDragonfly was true, in chronological order.</summary>
        public IReadOnlyList<StrategyResult> DragonflyResults { get; set; }
    }
}
