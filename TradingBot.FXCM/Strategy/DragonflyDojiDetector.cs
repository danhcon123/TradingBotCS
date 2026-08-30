using System;
using TradingBot.FXCM.Models;
using TradingBot.FXCM.Settings;

namespace TradingBot.FXCM.Strategy
{
    /// <summary>
    /// Analyzes a single completed M1 candle's geometry for a Dragonfly Doji, per the
    /// configurable thresholds in <see cref="DragonflyStrategySettings"/>.
    ///
    /// A Dragonfly is NOT simply Open == Close. It is defined by four independent rules,
    /// all of which are inclusive at their boundary:
    ///   A. Range must be greater than zero.
    ///   B. LowerWick must be at least MinLowerToBody times Body.
    ///   C. Body must be at most MaxBodyPct percent of Range.
    ///   D. UpperWick must be within UpperWickToleranceTicks * TickSize.
    /// Optionally (Mode A), the current candle's range must also be at least
    /// MinRangeVsPrevious times the previous completed M1 candle's range. This rule can be
    /// fully disabled (Mode B) via UsePreviousRangeComparison, in which case the previous
    /// candle never affects the verdict, even if it is missing.
    /// </summary>
    public class DragonflyDojiDetector
    {
        /// <summary>
        /// Small tolerance used only to make inclusive-boundary comparisons (lowerWick vs.
        /// MinLowerToBody*body, body vs. MaxBodyPct*range, upperWick vs. tick tolerance,
        /// range vs. MinRangeVsPrevious*previousRange) safe against floating-point rounding.
        /// XAU/USD prices run in the thousands with tick sizes around hundredths, so 1e-9 is
        /// many orders of magnitude below anything that could be a genuine market value -
        /// it only absorbs arithmetic noise, never a real wick/body/range difference. Do not
        /// enlarge this: a bigger epsilon would start accepting genuinely nonzero wicks.
        /// </summary>
        private const double Epsilon = 1e-9;

        private readonly DragonflyStrategySettings _settings;

        public DragonflyDojiDetector(DragonflyStrategySettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _settings.Validate();
        }

        /// <summary>
        /// Analyzes <paramref name="current"/> (a completed M1 candle). <paramref name="previous"/>
        /// is the immediately preceding completed M1 candle and may be null; it is only
        /// required for the verdict when <see cref="DragonflyStrategySettings.UsePreviousRangeComparison"/>
        /// is enabled.
        /// </summary>
        public DragonflyAnalysis Analyze(Candle current, Candle previous)
        {
            if (current == null)
            {
                throw new ArgumentNullException(nameof(current));
            }

            bool isValidOhlc = IsValidOhlc(current);

            double body = Math.Abs(current.Close - current.Open);
            double range = current.High - current.Low;
            double upperWick = current.High - Math.Max(current.Open, current.Close);
            double lowerWick = Math.Min(current.Open, current.Close) - current.Low;

            bool hasRange = isValidOhlc && range > 0;

            double lowerWickToBodyRatio = body > 0
                ? lowerWick / body
                : (lowerWick > 0 ? double.PositiveInfinity : 0.0);

            double bodyPercentage = hasRange ? (body / range) * 100.0 : 0.0;

            bool lowerWickRulePassed = hasRange
                && GreaterOrEqual(lowerWick, _settings.MinLowerToBody * body);

            bool bodyRulePassed = hasRange
                && LessOrEqual(body, range * _settings.MaxBodyPct / 100.0);

            double upperTolerance = _settings.UpperWickToleranceTicks * _settings.TickSize;
            bool upperWickRulePassed = hasRange && LessOrEqual(upperWick, upperTolerance);

            bool previousValidOhlc = previous != null && IsValidOhlc(previous);
            double? previousRange = previousValidOhlc
                ? previous.High - previous.Low
                : (double?)null;

            double? currentToPreviousRangeRatio = (isValidOhlc && previousRange.HasValue && previousRange.Value > 0)
                ? range / previousRange.Value
                : (double?)null;

            bool previousRangeRuleEnabled = _settings.UsePreviousRangeComparison;
            bool? previousRangeRulePassed = null;

            if (previousRangeRuleEnabled)
            {
                if (previousRange == null || previousRange.Value <= 0)
                {
                    previousRangeRulePassed = false;
                }
                else
                {
                    previousRangeRulePassed =
                        GreaterOrEqual(range, _settings.MinRangeVsPrevious * previousRange.Value);
                }
            }

            bool isDragonfly = isValidOhlc
                && hasRange
                && lowerWickRulePassed
                && bodyRulePassed
                && upperWickRulePassed
                && (!previousRangeRuleEnabled || previousRangeRulePassed == true);

            return new DragonflyAnalysis
            {
                IsValidOhlc = isValidOhlc,
                HasRange = hasRange,
                Body = body,
                Range = range,
                UpperWick = upperWick,
                LowerWick = lowerWick,
                LowerWickToBodyRatio = lowerWickToBodyRatio,
                BodyPercentage = bodyPercentage,
                LowerWickRulePassed = lowerWickRulePassed,
                BodyRulePassed = bodyRulePassed,
                UpperWickRulePassed = upperWickRulePassed,
                PreviousRange = previousRange,
                CurrentToPreviousRangeRatio = currentToPreviousRangeRatio,
                PreviousRangeRuleEnabled = previousRangeRuleEnabled,
                PreviousRangeRulePassed = previousRangeRulePassed,
                IsDragonfly = isDragonfly
            };
        }

        /// <summary>
        /// True when the candle's OHLC values are internally consistent (High is the true
        /// maximum, Low is the true minimum, no NaN/Infinity). Malformed historical data must
        /// never be able to qualify as a Dragonfly.
        /// </summary>
        private static bool IsValidOhlc(Candle candle)
        {
            if (candle == null)
            {
                return false;
            }

            double open = candle.Open;
            double high = candle.High;
            double low = candle.Low;
            double close = candle.Close;

            if (!IsFinite(open) || !IsFinite(high) || !IsFinite(low) || !IsFinite(close))
            {
                return false;
            }

            if (high < open || high < close || low > open || low > close || high < low)
            {
                return false;
            }

            return true;
        }

        private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        private static bool GreaterOrEqual(double a, double b) => a - b >= -Epsilon;

        private static bool LessOrEqual(double a, double b) => a - b <= Epsilon;
    }
}
