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


            IsConnected = listener.WaitEvents()
                          && listener.Connected;

            return IsConnected;
        }

        public FXCMMarketData GetMarketData()
        {
            return new FXCMMarketData(session);
        }

        public FXCMChartData GetChartData()
        {
            return new FXCMChartData(session);
        }

        public FXCMPriceHistory GetPriceHistory()
        {
            return new FXCMPriceHistory(session);
        }

        /// <summary>
        /// Logs out of FXCM and releases the session cleanly. Safe to call even if Connect()
        /// was never called or already failed (a no-op logout is skipped in that case) - and
        /// safe to call more than once. Swallows and logs any exception rather than throwing,
        /// since this normally runs during shutdown where a cleanup failure should not
        /// prevent the process from exiting.
        /// </summary>
        public void Disconnect()
        {
            if (session == null)
            {
                return;
            }

            Console.WriteLine("[FXCM] Disconnecting...");

            try
            {
                if (IsConnected)
                {
                    listener?.Reset();
                    session.logout();
                    Console.WriteLine("[FXCM] logout() returned - waiting for disconnect confirmation...");

                    // A short, bounded wait - this is a shutdown path, not a login, so it
                    // must not be able to stall process exit for the full 30s login timeout.
                    bool confirmed = listener != null && listener.WaitEvents(5000);
                    Console.WriteLine(confirmed
                        ? "[FXCM] Disconnect confirmed by FXCM."
                        : "[FXCM] Warning: no disconnect confirmation from FXCM within 5s - continuing cleanup anyway.");

                    if (listener != null)
                    {
                        session.unsubscribeSessionStatus(listener);
                    }
                }

                Console.WriteLine("[FXCM] Disposing session...");
                session.Dispose();
                Console.WriteLine("[FXCM] Session disposed.");
            }
            catch (Exception ex)
            {
                Console.WriteLine("[FXCM] Error while disconnecting: " + ex.Message);
            }
            finally
            {
                IsConnected = false;
                session = null;
                listener = null;
                Console.WriteLine("[FXCM] Disconnected.");
            }
        }
    }
}
