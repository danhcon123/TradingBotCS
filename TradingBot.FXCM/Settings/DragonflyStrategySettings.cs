using System;

namespace TradingBot.FXCM.Settings
{
    /// <summary>
    /// Configurable thresholds for Dragonfly Doji detection and the surrounding strategy.
    /// Keeping these as plain settable properties (rather than hard-coded constants) lets
    /// the same detector/strategy code be backtested under multiple variants later.
    /// </summary>
    public class DragonflyStrategySettings
    {
        public DragonflyStrategySettings()
        {
        }

        /// <summary>
        /// Copies every threshold from <paramref name="other"/>. Used by
        /// <c>DragonflyStrategy</c> to take an immutable snapshot at construction time, so a
        /// caller mutating the settings object it originally passed in cannot reach into an
        /// already-running strategy and invalidate its (already-validated) configuration -
        /// each strategy instance/backtest run keeps a permanently fixed configuration.
        /// </summary>
        public DragonflyStrategySettings(DragonflyStrategySettings other)
        {
            if (other == null)
            {
                throw new ArgumentNullException(nameof(other));
            }

            H1ReferenceCount = other.H1ReferenceCount;
            MinLowerToBody = other.MinLowerToBody;
            MaxBodyPct = other.MaxBodyPct;
            UpperWickToleranceTicks = other.UpperWickToleranceTicks;
            TickSize = other.TickSize;
            UsePreviousRangeComparison = other.UsePreviousRangeComparison;
            MinRangeVsPrevious = other.MinRangeVsPrevious;
            UseBlockedHours = other.UseBlockedHours;
        }

        /// <summary>
        /// Number of qualifying H1 reference candles required for a full reference set.
        /// </summary>
        public int H1ReferenceCount { get; set; } = 28;

        /// <summary>
        /// Minimum lower-wick-to-body ratio required for a Dragonfly. Inclusive boundary.
        /// </summary>
        public double MinLowerToBody { get; set; } = 5.0;

        /// <summary>
        /// Maximum body size, expressed as a percentage of the candle's total range.
        /// Inclusive boundary.
        /// </summary>
        public double MaxBodyPct { get; set; } = 20.0;

        /// <summary>
        /// Allowed upper wick, expressed in ticks. Combined with <see cref="TickSize"/>
        /// to compute an absolute price tolerance.
        /// </summary>
        public int UpperWickToleranceTicks { get; set; } = 0;

        /// <summary>
        /// Instrument tick size used to convert <see cref="UpperWickToleranceTicks"/> into
        /// an absolute price tolerance.
        /// </summary>
        public double TickSize { get; set; }

        /// <summary>
        /// When true, the current M1 candle's range must be at least
        /// <see cref="MinRangeVsPrevious"/> times the previous completed M1 candle's range.
        /// </summary>
        public bool UsePreviousRangeComparison { get; set; } = true;

        /// <summary>
        /// Minimum ratio of current M1 range to previous M1 range. Inclusive boundary.
        /// Only applied when <see cref="UsePreviousRangeComparison"/> is true.
        /// </summary>
        public double MinRangeVsPrevious { get; set; } = 1.5;

        /// <summary>
        /// When true, signals are blocked during 12:00-15:59 strategy-local (UTC-4) time.
        /// </summary>
        public bool UseBlockedHours { get; set; } = true;

        /// <summary>
        /// Validates that every threshold is a sane, non-nonsensical value. Every strategy
        /// component that consumes these settings should call this once before using them,
        /// so a misconfigured settings object can never silently produce signals.
        /// </summary>
        public void Validate()
        {
            if (H1ReferenceCount <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(H1ReferenceCount), H1ReferenceCount, "H1ReferenceCount must be greater than zero.");
            }

            if (MinLowerToBody < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(MinLowerToBody), MinLowerToBody, "MinLowerToBody must be >= 0.");
            }

            if (MaxBodyPct < 0 || MaxBodyPct > 100)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(MaxBodyPct), MaxBodyPct, "MaxBodyPct must be between 0 and 100 inclusive.");
            }

            if (UpperWickToleranceTicks < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(UpperWickToleranceTicks), UpperWickToleranceTicks, "UpperWickToleranceTicks must be >= 0.");
            }

            if (TickSize <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(TickSize), TickSize, "TickSize must be greater than zero.");
            }

            if (MinRangeVsPrevious < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(MinRangeVsPrevious), MinRangeVsPrevious, "MinRangeVsPrevious must be >= 0.");
            }
        }
    }
}
