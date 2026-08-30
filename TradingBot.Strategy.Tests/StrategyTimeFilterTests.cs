using System;
using TradingBot.FXCM.Settings;
using TradingBot.FXCM.Strategy;
using Xunit;

namespace TradingBot.Strategy.Tests
{
    public class StrategyTimeFilterTests
    {
        private static DragonflyStrategySettings Settings(bool useBlockedHours)
        {
            // Intentionally leaves every other setting at its unconfigured default (e.g.
            // TickSize = 0) - StrategyTimeFilter only reads UseBlockedHours and must not
            // require the rest of the configuration to be valid.
            return new DragonflyStrategySettings
            {
                UseBlockedHours = useBlockedHours
            };
        }

        /// <summary>
        /// Builds the UTC instant corresponding to a given strategy-local (fixed UTC-4)
        /// time on 2026-06-15. Strategy-local = UTC - 4h, so UTC = local + 4h.
        /// </summary>
        private static DateTime UtcForStrategyLocal(int hour, int minute)
        {
            var local = new DateTime(2026, 6, 15, hour, minute, 0, DateTimeKind.Utc);
            return local.AddHours(4);
        }

        // ---------------------------------------------------------------
        // Blocked-window boundaries, expressed in strategy-local (UTC-4) time
        // ---------------------------------------------------------------

        [Theory]
        [InlineData(11, 59, false)] // just before the window -> allowed
        [InlineData(12, 0, true)]   // window opens -> blocked
        [InlineData(13, 0, true)]   // mid-window -> blocked
        [InlineData(15, 59, true)]  // last blocked minute -> blocked
        [InlineData(16, 0, false)]  // window closes -> allowed
        public void IsBlocked_StrategyLocalBoundaries_WhenEnabled(int localHour, int localMinute, bool expectedBlocked)
        {
            var filter = new StrategyTimeFilter(Settings(useBlockedHours: true));
            var candleTimeUtc = UtcForStrategyLocal(localHour, localMinute);

            bool blocked = filter.IsBlocked(candleTimeUtc);

            Assert.Equal(expectedBlocked, blocked);
        }

        // ---------------------------------------------------------------
        // Same boundaries, expressed directly as UTC timestamps (no conversion helper),
        // matching the spec's own example: 16:00 UTC == 12:00 UTC-4 -> blocked.
        // ---------------------------------------------------------------

        [Theory]
        [InlineData(15, 59, false)] // 15:59 UTC == 11:59 UTC-4 -> allowed
        [InlineData(16, 0, true)]   // 16:00 UTC == 12:00 UTC-4 -> blocked
        [InlineData(17, 0, true)]   // 17:00 UTC == 13:00 UTC-4 -> blocked
        [InlineData(19, 59, true)]  // 19:59 UTC == 15:59 UTC-4 -> blocked
        [InlineData(20, 0, false)]  // 20:00 UTC == 16:00 UTC-4 -> allowed
        public void IsBlocked_ExplicitUtcTimestamps_WhenEnabled(int utcHour, int utcMinute, bool expectedBlocked)
        {
            var filter = new StrategyTimeFilter(Settings(useBlockedHours: true));
            var candleTimeUtc = new DateTime(2026, 6, 15, utcHour, utcMinute, 0, DateTimeKind.Utc);

            bool blocked = filter.IsBlocked(candleTimeUtc);

            Assert.Equal(expectedBlocked, blocked);
        }

        // ---------------------------------------------------------------
        // UseBlockedHours = false -> never blocked
        // ---------------------------------------------------------------

        [Theory]
        [InlineData(12, 0)]
        [InlineData(13, 0)]
        [InlineData(15, 59)]
        public void IsBlocked_AlwaysFalse_WhenFilterDisabled(int localHour, int localMinute)
        {
            var filter = new StrategyTimeFilter(Settings(useBlockedHours: false));
            var candleTimeUtc = UtcForStrategyLocal(localHour, localMinute);

            bool blocked = filter.IsBlocked(candleTimeUtc);

            Assert.False(blocked);
        }

        // ---------------------------------------------------------------
        // UTC contract and argument guards
        // ---------------------------------------------------------------

        [Fact]
        public void IsBlocked_LocalKindTimestamp_ThrowsArgumentException()
        {
            var filter = new StrategyTimeFilter(Settings(useBlockedHours: true));
            var localKindTime = DateTime.SpecifyKind(UtcForStrategyLocal(12, 0), DateTimeKind.Local);

            Assert.Throws<ArgumentException>(() => filter.IsBlocked(localKindTime));
        }

        [Fact]
        public void IsBlocked_UnspecifiedKindTimestamp_IsTrustedAsUtc()
        {
            var filter = new StrategyTimeFilter(Settings(useBlockedHours: true));
            var unspecifiedTime = DateTime.SpecifyKind(UtcForStrategyLocal(12, 0), DateTimeKind.Unspecified);

            bool blocked = filter.IsBlocked(unspecifiedTime);

            Assert.True(blocked);
        }

        [Fact]
        public void Constructor_NullSettings_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new StrategyTimeFilter(null));
        }

        [Fact]
        public void Constructor_UnconfiguredSettings_DoesNotThrow()
        {
            // Deliberately proves the decoupling: a settings object that would fail full
            // Validate() (TickSize = 0, etc.) is still fine here, because this filter has
            // no dependency on anything except UseBlockedHours.
            var settings = new DragonflyStrategySettings();

            var ex = Record.Exception(() => new StrategyTimeFilter(settings));

            Assert.Null(ex);
        }
    }
}
