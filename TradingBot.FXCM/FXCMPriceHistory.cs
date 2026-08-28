using System;
using System.Collections.Generic;
using fxcore2;
using Candleworks.PriceHistoryMgr;
using TradingBot.FXCM.Models;

namespace TradingBot.FXCM
{
    public class FXCMPriceHistory
    {
        private readonly IPriceHistoryCommunicator communicator;

        public FXCMPriceHistory(O2GSession session)
        {
            communicator =
                PriceHistoryCommunicatorFactory.createCommunicator(
                    session,
                    @"C:\FXCMQuotesData"
                );
        }

        public List<Candle> GetCandles(
         string instrument,
         string timeframe,
         DateTime from,
         DateTime to)
        {
            var listener =
                new PriceHistoryListener(
                    communicator
                );


            communicator.addListener(listener);


            O2GTimeframe fxcmTimeframe =
                communicator
                    .TimeframeFactory
                    .create(timeframe);


            var request =
                communicator.createRequest(
                    instrument,
                    fxcmTimeframe,
                    from,
                    to,
                    100
                );


            Console.WriteLine(
                "Sending price history request..."
            );


            communicator.sendRequest(request);


            listener.Wait();


            communicator.removeListener(listener);


            return listener.Candles;
        }
    }
}
