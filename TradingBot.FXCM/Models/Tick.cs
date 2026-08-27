namespace TradingBot.FXCM.Models
{
    public class Tick
    {
        public string Instrument {  get; set; }

        public double Bid {  get; set; }

        public double Ask { get; set; }

        public System.DateTime Time { get; set; }
    }
}
