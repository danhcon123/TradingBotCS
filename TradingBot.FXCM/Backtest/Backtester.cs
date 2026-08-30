using System.Collections.Generic;
using TradingBot.FXCM.Models;
using TradingBot.FXCM.Settings;
using TradingBot.FXCM.Strategy;

namespace TradingBot.FXCM.Backtest
{
    /// <summary>
    /// Replays a chronological list of completed M1 candles through
    /// <see cref="DragonflyStrategy"/>, exactly as a live loop would process them one at a
    /// time - the only difference is the data source (a pre-fetched list here, live candles
    /// in production). No look-ahead is possible: each M1 candle is evaluated using only the
    /// immediately preceding M1 candle in the supplied list and the full H1 candle list,
    /// and <see cref="DragonflyStrategy"/>/<see cref="H1LevelCalculator"/> internally
    /// restrict themselves to H1 candles that had actually completed by that M1 candle's own
    /// evaluation instant.
    /// </summary>
    public static class Backtester
    {
        /// <summary>
        /// Evaluates <c>m1Candles[i]</c> against <c>m1Candles[i-1]</c> as the previous
        /// candle for every <c>i</c> from 1 to the end, against the full
        /// <paramref name="h1Candles"/> list as H1 reference history, using a single fresh
        /// <see cref="DragonflyStrategy"/> built from <paramref name="settings"/> for this
        /// run only (so results from one run can never be influenced by another).
        /// <paramref name="m1Candles"/> must already be sorted chronologically ascending -
        /// exactly what <c>CandleRepository.GetCandles</c> already returns. Null or
        /// fewer-than-two-candle input simply evaluates nothing rather than throwing.
        /// </summary>
        public static BacktestResult Run(
            IReadOnlyList<Candle> m1Candles,
            IReadOnlyList<Candle> h1Candles,
            DragonflyStrategySettings settings)
        {
            var strategy = new DragonflyStrategy(settings);

            var result = new BacktestResult
            {
                DragonflyResults = new List<StrategyResult>()
            };

            if (m1Candles == null)
            {
                return result;
            }

            var dragonflyResults = new List<StrategyResult>();

            for (int i = 1; i < m1Candles.Count; i++)
            {
                Candle current = m1Candles[i];
                if (current == null)
                {
                    continue;
                }

                Candle previous = m1Candles[i - 1];

                StrategyResult strategyResult = strategy.Evaluate(current, previous, h1Candles);
                result.M1CandlesEvaluated++;

                if (!strategyResult.DragonflyAnalysis.IsDragonfly)
                {
                    continue;
                }

                result.DragonfliesDetected++;
                dragonflyResults.Add(strategyResult);

                if (strategyResult.HasFullH1Set)
                {
                    result.DragonfliesWithFullH1Set++;
                }

                if (strategyResult.HasIntersection)
                {
                    result.DragonfliesWithIntersection++;
                }

                if (strategyResult.BlockedByTime)
                {
                    result.SignalsBlockedByTime++;
                }

                if (strategyResult.FinalSignal)
                {
                    result.BuySignals++;
                }
            }

            result.DragonflyResults = dragonflyResults;
            return result;
        }
    }
}
