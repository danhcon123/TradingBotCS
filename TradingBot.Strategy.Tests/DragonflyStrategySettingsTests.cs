using System;
using TradingBot.FXCM.Settings;
using Xunit;

namespace TradingBot.Strategy.Tests
{
    public class DragonflyStrategySettingsTests
    {
        private static DragonflyStrategySettings ValidSettings()
        {
            return new DragonflyStrategySettings
            {
                H1ReferenceCount = 28,
                MinLowerToBody = 5.0,
                MaxBodyPct = 20.0,
                UpperWickToleranceTicks = 0,
                TickSize = 0.01,
                UsePreviousRangeComparison = true,
                MinRangeVsPrevious = 1.5,
                UseBlockedHours = true
            };
        }

        [Fact]
        public void Validate_DefaultConfiguredSettings_DoesNotThrow()
        {
            var settings = ValidSettings();

            var ex = Record.Exception(() => settings.Validate());

            Assert.Null(ex);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void Validate_NonPositiveH1ReferenceCount_Throws(int value)
        {
            var settings = ValidSettings();
            settings.H1ReferenceCount = value;

            Assert.Throws<ArgumentOutOfRangeException>(() => settings.Validate());
        }

        [Fact]
        public void Validate_NegativeMinLowerToBody_Throws()
        {
            var settings = ValidSettings();
            settings.MinLowerToBody = -5.0;

            Assert.Throws<ArgumentOutOfRangeException>(() => settings.Validate());
        }

        [Fact]
        public void Validate_ZeroMinLowerToBody_DoesNotThrow()
        {
            var settings = ValidSettings();
            settings.MinLowerToBody = 0.0;

            var ex = Record.Exception(() => settings.Validate());

            Assert.Null(ex);
        }

        [Theory]
        [InlineData(-1.0)]
        [InlineData(500.0)]
        public void Validate_MaxBodyPctOutOfRange_Throws(double value)
        {
            var settings = ValidSettings();
            settings.MaxBodyPct = value;

            Assert.Throws<ArgumentOutOfRangeException>(() => settings.Validate());
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(100.0)]
        public void Validate_MaxBodyPctBoundaries_DoNotThrow(double value)
        {
            var settings = ValidSettings();
            settings.MaxBodyPct = value;

            var ex = Record.Exception(() => settings.Validate());

            Assert.Null(ex);
        }

        [Fact]
        public void Validate_NegativeUpperWickToleranceTicks_Throws()
        {
            var settings = ValidSettings();
            settings.UpperWickToleranceTicks = -1;

            Assert.Throws<ArgumentOutOfRangeException>(() => settings.Validate());
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(-0.01)]
        public void Validate_NonPositiveTickSize_Throws(double value)
        {
            var settings = ValidSettings();
            settings.TickSize = value;

            Assert.Throws<ArgumentOutOfRangeException>(() => settings.Validate());
        }

        [Fact]
        public void Validate_NegativeMinRangeVsPrevious_Throws()
        {
            var settings = ValidSettings();
            settings.MinRangeVsPrevious = -2.0;

            Assert.Throws<ArgumentOutOfRangeException>(() => settings.Validate());
        }

        [Fact]
        public void Validate_ZeroMinRangeVsPrevious_DoesNotThrow()
        {
            var settings = ValidSettings();
            settings.MinRangeVsPrevious = 0.0;

            var ex = Record.Exception(() => settings.Validate());

            Assert.Null(ex);
        }
    }
}
