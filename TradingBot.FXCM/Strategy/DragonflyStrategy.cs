using System;
using System.Collections.Generic;
using TradingBot.FXCM.Models;
using TradingBot.FXCM.Settings;

namespace TradingBot.FXCM.Strategy
{
    /// <summary>
    /// Coordinates <see cref="H1LevelCalculator"/>, <see cref="DragonflyDojiDetector"/>,
    /// <see cref="LevelIntersectionDetector"/> and <see cref="StrategyTimeFilter"/> into the
    /// final per-candle BUY/no-signal decision. Owns full settings validation - each
    /// individual component only validates what it actually consumes; this constructor is
    /// where the complete configuration is checked, exactly once. The settings are also
    /// snapshotted (copy-constructed) here, so a caller mutating the original settings
    /// object after construction can never reach into an already-running strategy instance.
    ///
    /// Precondition: <see cref="Evaluate"/> must only ever be given already-completed M1
    /// candles. This class has no wall-clock "now" to verify that itself - the caller (a
    /// live loop or the backtester) is responsible for only presenting closed candles.
    /// </summary>
    public class DragonflyStrategy
    {
        private static readonly IReadOnlyList<LevelIntersection> NoIntersections = new List<LevelIntersection>();

        private readonly H1LevelCalculator _h1Calculator;
        private readonly DragonflyDojiDetector _dragonflyDetector;
        private readonly LevelIntersectionDetector _intersectionDetector;
        private readonly StrategyTimeFilter _timeFilter;

        public DragonflyStrategy(DragonflyStrategySettings settings)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            settings.Validate();

            // Snapshot: nothing downstream ever touches the caller's original object again.
            var snapshot = new DragonflyStrategySettings(settings);

            _h1Calculator = new H1LevelCalculator(snapshot.H1ReferenceCount);
            _dragonflyDetector = new DragonflyDojiDetector(snapshot);
            _intersectionDetector = new LevelIntersectionDetector();
            _timeFilter = new StrategyTimeFilter(snapshot);
        }

        /// <summary>
        /// Evaluates one completed M1 candle following the required sequence: Dragonfly
        /// geometry first; only when confirmed do H1 reference levels get retrieved; only
        /// with a full 28-set do lower-wick intersections get scanned; only with at least
        /// one intersection is the blocked-hours filter consulted. Each step short-circuits
        /// the ones after it, so no unnecessary work is done for a candle that was already
        /// going to produce no signal. The corresponding <c>...Evaluated</c> flag on the
        /// result tells you whether a stage actually ran or was skipped.
        /// </summary>
        /// <param name="currentM1">The completed M1 candle being evaluated. Required.</param>
        /// <param name="previousM1">
        /// The immediately preceding completed M1 candle actually present in the market
        /// data, or null if none exists (e.g. start of history). This is NOT necessarily
        /// the candle at <c>currentM1.Time.AddMinutes(-1)</c> - weekends/gaps/missing data
        /// mean the caller (backtester or live feed) must supply whatever candle it actually
        /// has immediately before this one, never a synthesized one. Only required for the
        /// verdict when <see cref="DragonflyStrategySettings.UsePreviousRangeComparison"/>
        /// is enabled - see <see cref="DragonflyDojiDetector"/>.
        /// </param>
        /// <param name="h1Candles">
        /// Raw H1 candle history. May be null, empty, or incomplete - never scanned unless
        /// <paramref name="currentM1"/> is already a confirmed Dragonfly.
        /// </param>
        public StrategyResult Evaluate(Candle currentM1, Candle previousM1, IReadOnlyList<Candle> h1Candles)
        {
            if (currentM1 == null)
            {
                throw new ArgumentNullException(nameof(currentM1));
            }

            DragonflyAnalysis dragonflyAnalysis = _dragonflyDetector.Analyze(currentM1, previousM1);

            // EvaluationTimeUtc is the instant the strategy actually knows this outcome
            // (one minute after the candle's own open time, i.e. when it closes). A
            // backtest simulating entries must use EvaluationTimeUtc, never CandleTimeUtc -
            // otherwise it would be entering on a signal it could not have known yet.
            DateTime evaluationTimeUtc = currentM1.Time.AddMinutes(1);

            var result = new StrategyResult
            {
                CurrentCandle = currentM1,
                PreviousCandle = previousM1,
                CandleTimeUtc = currentM1.Time,
                EvaluationTimeUtc = evaluationTimeUtc,
                CandleTimeUtcMinus4 = StrategyTime.ToStrategyLocal(currentM1.Time),
                DragonflyAnalysis = dragonflyAnalysis,
                H1Evaluated = false,
                ReferenceSetCount = 0,
                HasFullH1Set = false,
                IntersectionEvaluated = false,
                HasIntersection = false,
                Intersections = NoIntersections,
                PrimaryIntersection = null,
                TimeFilterEvaluated = false,
                BlockedByTime = false,
                Signal = SignalType.None
            };

            if (!dragonflyAnalysis.IsDragonfly)
            {
                return result;
            }

            // H1 reference availability is judged as of evaluationTimeUtc - NOT
            // currentM1.Time. An H1 candle that opened at the same moment as this M1 candle
            // can only be "completed" once this M1 candle itself has closed.
            result.H1Evaluated = true;
            IReadOnlyList<H1ReferenceSet> referenceSets =
                _h1Calculator.CalculateReferenceSets(h1Candles, evaluationTimeUtc);

            result.ReferenceSetCount = referenceSets.Count;
            result.HasFullH1Set = _h1Calculator.HasFullReferenceSet(referenceSets);

            if (!result.HasFullH1Set)
            {
                return result;
            }

            result.IntersectionEvaluated = true;
            IReadOnlyList<LevelIntersection> intersections =
                _intersectionDetector.FindIntersections(dragonflyAnalysis, currentM1, referenceSets);

            result.Intersections = intersections;
            result.HasIntersection = intersections.Count > 0;
            result.PrimaryIntersection = _intersectionDetector.GetPrimaryIntersection(intersections);

            if (!result.HasIntersection)
            {
                return result;
            }

            // Unlike H1 availability above, the blocked-hours rule uses the candle's own
            // OPEN timestamp (currentM1.Time / CandleTimeUtc), matching the original Pine
            // logic's bar timestamp. Using evaluationTimeUtc here instead would shift the
            // 12:00/16:00 UTC-4 boundary by a minute and is a deliberate NOT to do.
            result.TimeFilterEvaluated = true;
            result.BlockedByTime = _timeFilter.IsBlocked(currentM1.Time);

            if (result.BlockedByTime)
            {
                return result;
            }

            result.Signal = SignalType.Buy;
            return result;
        }
    }
}
