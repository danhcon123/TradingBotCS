using System;
using System.Collections.Generic;
using System.Linq;
using TradingBot.FXCM.Models;

namespace TradingBot.FXCM.Strategy
{
    /// <summary>
    /// Builds the set of H1 reference levels (High / Midpoint / Low) that a Dragonfly's
    /// lower wick is compared against.
    ///
    /// A qualifying H1 candle is one whose open time is 00:00 in the strategy's FIXED
    /// UTC-4 timezone (no daylight saving adjustment), which corresponds to 04:00 UTC.
    ///
    /// Only candles that have fully completed at or before the supplied evaluation
    /// timestamp are considered, which prevents look-ahead bias when this calculator
    /// is used during backtesting.
    /// </summary>
    public class H1LevelCalculator
    {
        public const int DefaultReferenceCount = 28;

        public int ReferenceCount { get; }

        public H1LevelCalculator(int referenceCount = DefaultReferenceCount)
        {
            if (referenceCount <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(referenceCount),
                    "Reference count must be greater than zero.");
            }

            ReferenceCount = referenceCount;
        }

        /// <summary>
        /// True when the given H1 candle open time (UTC) corresponds to
        /// 00:00 UTC-4, i.e. 04:00 UTC.
        /// </summary>
        public static bool IsQualifyingH1Candle(DateTime candleStartUtc)
        {
            DateTime local = StrategyTime.ToStrategyLocal(candleStartUtc);
            return local.Hour == 0 && local.Minute == 0 && local.Second == 0;
        }

        /// <summary>
        /// True when the candle has fully closed at or before <paramref name="evaluationTimeUtc"/>.
        /// An H1 candle starting at Time and covering one hour completes at Time + 1h.
        /// </summary>
        public static bool IsCompletedAt(Candle candle, DateTime evaluationTimeUtc)
        {
            if (candle == null)
            {
                return false;
            }

            DateTime candleEndUtc = candle.Time.AddHours(1);
            return candleEndUtc <= evaluationTimeUtc;
        }

        /// <summary>
        /// Returns the most recent <see cref="ReferenceCount"/> valid, completed, qualifying
        /// H1 reference sets available as of <paramref name="evaluationTimeUtc"/>, ordered
        /// newest to oldest.
        ///
        /// Candles that do not qualify (wrong start time), have not yet completed, or lie in
        /// the future relative to the evaluation time are ignored. Gaps caused by weekends,
        /// holidays or missing data simply result in fewer candidates - they are never
        /// synthesized or assumed. Duplicate candles sharing the same source timestamp are
        /// collapsed into a single reference set so one H1 candle can never be counted twice.
        /// </summary>
        public IReadOnlyList<H1ReferenceSet> CalculateReferenceSets(
            IEnumerable<Candle> h1Candles,
            DateTime evaluationTimeUtc)
        {
            StrategyUtcContract.Ensure(evaluationTimeUtc);

            if (h1Candles == null)
            {
                return new List<H1ReferenceSet>();
            }

            return h1Candles
                .Where(c => c != null)
                .Select(c => { StrategyUtcContract.Ensure(c.Time); return c; })
                .Where(c => IsQualifyingH1Candle(c.Time))
                .Where(c => IsCompletedAt(c, evaluationTimeUtc))
                .GroupBy(c => c.Time)
                .Select(g => g.First())
                .OrderByDescending(c => c.Time)
                .Take(ReferenceCount)
                .Select(ToReferenceSet)
                .ToList();
        }

        /// <summary>
        /// A final BUY signal requires the full configured number of reference sets
        /// (28 by default). Fewer available sets must not produce a final signal.
        /// </summary>
        public bool HasFullReferenceSet(IReadOnlyList<H1ReferenceSet> referenceSets)
        {
            return referenceSets != null && referenceSets.Count == ReferenceCount;
        }

        private static H1ReferenceSet ToReferenceSet(Candle candle)
        {
            double high = candle.High;
            double low = candle.Low;
            double midpoint = (high + low) / 2.0;

            return new H1ReferenceSet
            {
                SourceCandleTimeUtc = candle.Time,
                StrategyDate = StrategyTime.ToStrategyLocal(candle.Time).Date,
                High = high,
                Midpoint = midpoint,
                Low = low
            };
        }
    }
}
