using Candleworks.PriceHistoryMgr;
using fxcore2;
using System;
using System.Collections.Generic;
using TradingBot.FXCM.Models;

/* 
 * connect to FXCM historical candle API
 * request EUR/USD M1 candles
 * convert FXCM data → Candle model
*/

namespace TradingBot.FXCM
{
    public class FXCMChartData
    {
        private readonly O2GSession session;
        
        public FXCMChartData(O2GSession session)
        {
            this.session = session;
        }

        public List<Candle> GetHistoricalCandles(
            string instrument,
            string timeframe,
            int count
        )
        {
            var candles = new List<Candle>();

            if (session == null)
                throw new Exception("FXCM Session is null");

            O2GRequestFactory factory = session.getRequestFactory();
            
            foreach (var tf in factory.Timeframes)
            {
                Console.WriteLine(tf.ID);
            }

            O2GTimeframe fxcmTimeframe = factory.Timeframes[timeframe];

            O2GRequest request = factory.createMarketDataSnapshotRequestInstrument(
                instrument, fxcmTimeframe, count );

            if (request == null)
            {
                Console.WriteLine("Request creation failed");
                return candles;
            }

            Console.WriteLine(
               "Request ID: " + request.RequestID
           );

            Console.WriteLine(
                "Children count: " + request.ChildrenCount
            );

            DateTime timeTo = DateTime.UtcNow;

            DateTime timeFrom =
                timeTo.AddMinutes(-count);

            factory.fillMarketDataSnapshotRequestTime(
                request,
                timeFrom,
                timeTo,
                true
            );

            Console.WriteLine(
                "Sending candle request..."
            );

            var listener =
                new HistoricalDataResponseListener(session);
        
            session.subscribeResponse( listener );

            session.sendRequest( request );

            if (listener.Wait())
            {
                candles = listener.Candles;
            }

            session.unsubscribeResponse(listener);

            return candles;
        }
    }
}