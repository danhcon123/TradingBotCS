using System;
using System.Collections.Generic;
using System.Linq;
using TradingBot.FXCM.Models;

namespace TradingBot.FXCM.Strategy
{
    /// <summary>
    /// Compares a confirmed Dragonfly's LOWER WICK - never the full candle or its body -
    /// against a set of H1 reference price levels.
    ///
    /// This must only run after <see cref="DragonflyDojiDetector"/> has already confirmed
    /// the candle is a Dragonfly under the currently enabled settings. If it is not, the
    /// H1 levels are never scanned - <see cref="FindIntersections"/> returns immediately.
    /// </summary>
    public class LevelIntersectionDetector
    {
        /// <summary>
        /// Same role as the epsilon in <see cref="DragonflyDojiDetector"/>: absorbs
        /// floating-point rounding at the inclusive wick boundaries without relaxing them.
        /// </summary>
        private const double Epsilon = 1e-9;

        /// <summary>
        /// Finds every H1 reference level whose price falls within the Dragonfly's lower
        /// wick: [current.Low, Math.Min(current.Open, current.Close)], inclusive at both
        /// ends. Returns an empty list immediately when <paramref name="dragonflyAnalysis"/>
        /// is not a confirmed Dragonfly - H1 levels are never scanned in that case.
        ///
        /// Results are ordered by the strategy's primary-intersection priority: newest
        /// reference set first, then within each set High, Midpoint, Low - so
        /// <see cref="GetPrimaryIntersection"/> can simply take the first entry. All
        /// intersections are preserved, not just the primary one.
        /// </summary>
        public IReadOnlyList<LevelIntersection> FindIntersections(
            DragonflyAnalysis dragonflyAnalysis,
            Candle currentCandle,
            IReadOnlyList<H1ReferenceSet> referenceSets)
        {
            if (dragonflyAnalysis == null)
            {
                throw new ArgumentNullException(nameof(dragonflyAnalysis));
            }

            if (currentCandle == null)
            {
                throw new ArgumentNullException(nameof(currentCandle));
            }

            if (!dragonflyAnalysis.IsDragonfly)
            {
                return new List<LevelIntersection>();
            }

            if (referenceSets == null || referenceSets.Count == 0)
            {
                return new List<LevelIntersection>();
            }

            double lowerWickBottom = currentCandle.Low;
            double lowerWickTop = Math.Min(currentCandle.Open, currentCandle.Close);

            var intersections = new List<LevelIntersection>();

            IEnumerable<H1ReferenceSet> orderedSets = referenceSets
                .Where(s => s != null)
                .OrderByDescending(s => s.SourceCandleTimeUtc);

            foreach (H1ReferenceSet referenceSet in orderedSets)
            {
                foreach (PriceLevel level in referenceSet.ToPriceLevels())
                {
                    if (IsWithinWick(level.Price, lowerWickBottom, lowerWickTop))
                    {
                        intersections.Add(new LevelIntersection
                        {
                            Level = level,
                            LowerWickBottom = lowerWickBottom,
                            LowerWickTop = lowerWickTop
                        });
                    }
                }
            }

            return intersections;
        }

        /// <summary>
        /// The highest-priority intersection (newest H1 set first, then High/Mid/Low), or
        /// null when there are none. Assumes <paramref name="intersections"/> came from
        /// <see cref="FindIntersections"/>, which already returns them in priority order.
        /// Internal on purpose: only <c>DragonflyStrategy</c> (Phase 6) should be picking a
        /// primary intersection out of a list it just got from this same class - it must
        /// not become a general-purpose "first element" helper that a caller could feed an
        /// arbitrary, unordered list into and silently get the wrong answer.
        /// </summary>
        internal LevelIntersection GetPrimaryIntersection(IReadOnlyList<LevelIntersection> intersections)
        {
            return intersections != null && intersections.Count > 0
                ? intersections[0]
                : null;
        }

        private static bool IsWithinWick(double price, double bottom, double top)
        {
            return price - bottom >= -Epsilon && price - top <= Epsilon;
        }
    }
}
