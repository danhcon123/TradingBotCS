using System;
using System.Collections.Generic;
using Candleworks.PriceHistoryMgr;
using fxcore2;
using TradingBot.FXCM.Models;

namespace TradingBot.FXCM
{
    public class PriceHistoryListener : IPriceHistoryCommunicatorListener
    {
        public List<Candle> Candles {  get; private set; }

        private readonly IPriceHistoryCommunicator communicator;

        private bool completed  = false;

        public PriceHistoryListener (
            IPriceHistoryCommunicator communicator)
        {
            this.communicator = communicator;
            Candles = new List<Candle> ();
        }

        public void onRequestCompleted(
            IPriceHistoryCommunicatorRequest request,
            IPriceHistoryCommunicatorResponse response)
        {
            Console.WriteLine(
                "Price history completed");

            O2GMarketDataSnapshotResponseReader reader =
                communicator.createResponseReader (response);

            Console.WriteLine(
                "Candle count: " + reader.Count
            );

            for (int i = 0; i < reader.Count; i++) 
            {
                Candle candle = new Candle
                {
                    Time = reader.getDate(i),

                    Open = reader.getBidOpen(i),

                    High = reader.getBidHigh(i),

                    Low = reader.getBidLow(i),

                    Close = reader.getBidClose(i),

                    Volume = reader.getVolume(i),
                };

                Candles.Add(candle);
            }

            completed = true;
        }

        public void onRequestFailed(
            IPriceHistoryCommunicatorRequest request,
            PriceHistoryError error)
        {
            Console.WriteLine(
                "Price history failed: " + error
            );

            completed = true;
        }

        public void onRequestCancelled(
            IPriceHistoryCommunicatorRequest request)
        {
            Console.WriteLine(
                "Price history cancelled"
            );

            completed = true;
        }

        public bool Wait()
        {
            while (!completed)
            {
                System.Threading.Thread.Sleep( 1000 );
            }

            return true;
        }
    }
}
