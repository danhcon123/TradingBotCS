using System;
using fxcore2;
using TradingBot.FXCM.Models;

namespace TradingBot.FXCM
{
    public class FXCMMarketData
    {
        private O2GSession session;

        public FXCMMarketData(O2GSession session)
        {
           this.session = session;
        }

        public void Subscribe(string instrument)
        {
            Console.WriteLine(
                $"Subscribing to {instrument}"
            );

            O2GTableManager tableManager = session.getTableManager();

            if (tableManager == null)
            {
                Console.WriteLine(
                    "TableManager is null"
                    );
                return;
            }

            O2GTableManagerStatus status = tableManager.getStatus();

            while(status == O2GTableManagerStatus.TablesLoading)
            {
                System.Threading.Thread.Sleep(100);

                status = tableManager.getStatus();
            }

            if (status == O2GTableManagerStatus.TablesLoadFailed)
            {
                Console.WriteLine(
                    "Tables failed to load"
                );
                return;
            }
        
            O2GOffersTable offers = (O2GOffersTable)tableManager.getTable(O2GTableType.Offers);
            
            for (int i = 0; i < offers.Count; i++)
            {
                O2GOfferRow offer = 
                    offers.getRow(i);

                if (offer.Instrument == instrument)
                {
                    Console.WriteLine(
                        $"{offer.Instrument} " +
                        $"Bid={offer.Bid} " +
                        $"Ask={offer.Ask}"
                    );
                }
            }
        }
    }
}
