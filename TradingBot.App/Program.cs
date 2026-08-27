using System;
using TradingBot.FXCM;

namespace TradingBot.App
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("Trading Bot Starting");

            FXCMConnection fxcm = new FXCMConnection();

            bool connected = fxcm.Connect(
                "{Account number}",
                "{Password}",
                "http://www.fxcorporate.com/Hosts.jsp",
                "Real"
            );

            if (connected)
            {
                FXCMMarketData marketData =
                        fxcm.GetMarketData();
                    

                marketData.Subscribe(
                    "EUR/USD"
                );
            }

            Console.WriteLine(
                "Connected: " + connected
            );

            Console.WriteLine("Bot running. Press ENTER to stop.");

            Console.ReadLine();
        }
    }
}
