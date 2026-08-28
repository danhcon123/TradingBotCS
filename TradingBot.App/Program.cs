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
                "86082266",
                "Hoang1997!",
                "http://www.fxcorporate.com/Hosts.jsp",
                "Real"
            );

            Console.WriteLine(
               "Connected: " + connected
            );

            if (connected)
            {
                var history = fxcm.GetPriceHistory();

                var candles =
                    history.GetCandles(
                        "XAU/USD",
                        "m1",
                        DateTime.UtcNow.AddMinutes(-10),
                        DateTime.UtcNow
                    );


                foreach (var candle in candles)
                {
                    Console.WriteLine(candle);
                }
            }

            Console.WriteLine("Bot running. Press ENTER to stop.");

            Console.ReadLine();
        }
    }
}
