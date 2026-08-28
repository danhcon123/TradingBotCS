using fxcore2;
using System;
using System.Collections.Generic;
using TradingBot.FXCM.Models;


namespace TradingBot.FXCM
{
    public class HistoricalDataResponseListener : IO2GResponseListener
    {
        public List<Candle> Candles { get; set; }

        private bool completed = false;
        private string error = "";
        private readonly O2GSession session;


        public HistoricalDataResponseListener(
            O2GSession session)
        {
            this.session = session;
            Candles = new List<Candle>();
        }

        /*public void onRequestCompleted(
            string requestId,
            O2GResponse response )
        {
            Console.WriteLine(
                "Historical data request completed"
            );

            O2GResponseReaderFactory readerFactory =
                session.getResponseReaderFactory();

            O2GMarketDataSnapshotResponseReader reader =
                readerFactory.createMarketDataSnapshotReader(response);

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
                Console.WriteLine(candle);
            }
             
            completed = true;
        }*/
        public void onRequestCompleted(
    string requestId,
    O2GResponse response)
        {
            Console.WriteLine(
                "Historical data request completed"
            );


            Console.WriteLine(
                "Response null: " + (response == null)
            );

            Console.WriteLine(
                "Session null: " + (session == null)
            );


            var readerFactory =
                session.getResponseReaderFactory();


            Console.WriteLine(
                "ReaderFactory null: " + (readerFactory == null)
            );


            if (readerFactory == null)
            {
                completed = true;
                return;
            }


            var reader =
                readerFactory.createMarketDataSnapshotReader(response);

            Console.WriteLine(
                "Response type: " + response.Type
            );

            Console.WriteLine(
                "Reader null: " + (reader == null)
            );

            completed = true;
        }

        public void onRequestFailed(
            string requestId,
            string error)
        {
            Console.WriteLine(
                "Request failed: " + error);

            this.error = error;
            completed = true;
        }

        public void onTablesUpdates(
            O2GResponse response)
        {
            // Not required for historical candle requests
        }


        public bool Wait()
        {
            while (!completed)
            {
                System.Threading.Thread.Sleep(100);
            }

            return string.IsNullOrEmpty(error);
        }
    }
}
