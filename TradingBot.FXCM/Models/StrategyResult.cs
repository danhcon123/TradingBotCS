using System;
using System.Collections.Generic;

namespace TradingBot.FXCM.Models
{
    /// <summary>
    /// Full diagnostic result of evaluating one completed M1 candle. Fields belonging to a
    /// step that was never reached (e.g. no Dragonfly, so H1/intersection/time-filter steps
    /// never ran) are left at their neutral defaults (0 / false / empty / null) - the
    /// corresponding <c>...Evaluated</c> flag distinguishes "this stage ran and found
    /// nothing" from "this stage never ran". <see cref="DragonflyAnalysis"/> is always
    /// populated since analyzing the candle's geometry is the one step that always runs.
    /// </summary>
    public class StrategyResult
    {
        public SignalType Signal { get; set; }

        /// <summary>True only when Signal == SignalType.Buy. Always derived from Signal - never set independently, so the two can never disagree.</summary>
        public bool FinalSignal => Signal == SignalType.Buy;

        public Candle CurrentCandle { get; set; }

        public Candle PreviousCandle { get; set; }

        /// <summary>The M1 candle's own open timestamp (UTC). Also what the blocked-hours check used - see <see cref="BlockedByTime"/>.</summary>
        public DateTime CandleTimeUtc { get; set; }

        /// <summary>
        /// The instant the strategy actually knows the outcome for this candle:
        /// CandleTimeUtc + 1 minute (when the M1 candle closes). This is what was passed to
        /// the H1 calculator to judge reference-set availability. A backtest simulating
        /// entries must use this timestamp, never <see cref="CandleTimeUtc"/> - entering at
        /// the candle's open time using a signal only known one minute later is look-ahead
        /// bias.
        /// </summary>
        public DateTime EvaluationTimeUtc { get; set; }

        /// <summary>CandleTimeUtc converted to strategy-local (fixed UTC-4) time.</summary>
        public DateTime CandleTimeUtcMinus4 { get; set; }

        public DragonflyAnalysis DragonflyAnalysis { get; set; }

        /// <summary>True once the H1 reference-set lookup actually ran (i.e. a Dragonfly was confirmed).</summary>
        public bool H1Evaluated { get; set; }

        public int ReferenceSetCount { get; set; }

        public bool HasFullH1Set { get; set; }

        /// <summary>True once the lower-wick intersection scan actually ran (i.e. a full 28-set H1 history was available).</summary>
        public bool IntersectionEvaluated { get; set; }

        public bool HasIntersection { get; set; }

        public IReadOnlyList<LevelIntersection> Intersections { get; set; }

        public LevelIntersection PrimaryIntersection { get; set; }

        /// <summary>True once the blocked-hours filter actually ran (i.e. at least one intersection was found).</summary>
        public bool TimeFilterEvaluated { get; set; }

        public bool BlockedByTime { get; set; }
    }
}
