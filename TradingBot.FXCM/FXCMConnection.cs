using System;
using fxcore2;

namespace TradingBot.FXCM
{
    public class FXCMConnection
    {
        private O2GSession session;
        private SessionStatusListener listener;

        public bool IsConnected { get; private set; }

        public O2GSession Session {  get { return session; } }

        public bool Connect(
            string login,
            string password,
            string url,
            string connection) 
        {
            session = O2GTransport.createSession();

            session.useTableManager(O2GTableManagerMode.Yes, null);

            listener = new SessionStatusListener(
                session,
                "",
                ""
            );

            session.subscribeSessionStatus(listener);

            Console.WriteLine(
                "Connecting to FXCM..."
            );


            session.login(
                login,
                password,
                url,
                connection
            );


            return listener.WaitEvents()
                   && listener.Connected;
        }

        public FXCMMarketData GetMarketData()
        {
            return new FXCMMarketData(session);
        }
    }
}
