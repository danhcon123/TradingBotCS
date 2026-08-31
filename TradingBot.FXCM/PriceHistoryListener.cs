using System;
using System.Collections.Generic;
using System.Threading;
using Candleworks.PriceHistoryMgr;
using fxcore2;
using TradingBot.FXCM.Models;

namespace TradingBot.FXCM
{
    public class PriceHistoryListener :
        IPriceHistoryCommunicatorListener
    {
        private static readonly TimeSpan DefaultTimeout =
            TimeSpan.FromSeconds(30);

        private readonly IPriceHistoryCommunicator communicator;

        /*
         * Replaces:
         *
         *     bool completed
         *
         * and the Thread.Sleep polling loop.
         *
         * The FXCM callback signals this event as soon as the request
         * completes, fails, or is cancelled.
         */
        private readonly ManualResetEventSlim completedEvent =
            new ManualResetEventSlim(false);

        private Exception callbackException;

        public List<Candle> Candles
        {
            get;
            private set;
        }

        /// <summary>
        /// FXCM error text when onRequestFailed() was called.
        /// Null when FXCM did not report a request failure.
        /// </summary>
        public string ErrorMessage
        {
            get;
            private set;
        }

        /// <summary>
        /// True when FXCM cancelled the request.
        /// </summary>
        public bool WasCancelled
        {
            get;
            private set;
        }

        public PriceHistoryListener(
            IPriceHistoryCommunicator communicator)
        {
            if (communicator == null)
            {
                throw new ArgumentNullException(
                    nameof(communicator));
            }

            this.communicator =
                communicator;

            Candles =
                new List<Candle>();
        }

        public void onRequestCompleted(
            IPriceHistoryCommunicatorRequest request,
            IPriceHistoryCommunicatorResponse response)
        {
            try
            {
                Console.WriteLine(
                    "Price history completed");

                O2GMarketDataSnapshotResponseReader reader =
                    communicator.createResponseReader(
                        response);

                if (reader == null)
                {
                    throw new InvalidOperationException(
                        "FXCM returned a null price-history response reader.");
                }

                Console.WriteLine(
                    "Candle count: " +
                    reader.Count);

                for (int i = 0;
                     i < reader.Count;
                     i++)
                {
                    Candle candle =
                        new Candle
                        {
                            Time =
                                reader.getDate(i),

                            Open =
                                reader.getBidOpen(i),

                            High =
                                reader.getBidHigh(i),

                            Low =
                                reader.getBidLow(i),

                            Close =
                                reader.getBidClose(i),

                            Volume =
                                reader.getVolume(i)
                        };

                    Candles.Add(candle);
                }
            }
            catch (Exception ex)
            {
                /*
                 * Very important:
                 *
                 * In the old implementation an exception above meant
                 * "completed = true" was never reached. Wait() would then
                 * loop forever.
                 *
                 * Store the exception and signal completion in finally.
                 */
                callbackException =
                    ex;
            }
            finally
            {
                completedEvent.Set();
            }
        }

        public void onRequestFailed(
            IPriceHistoryCommunicatorRequest request,
            PriceHistoryError error)
        {
            try
            {
                ErrorMessage =
                    error != null
                        ? error.ToString()
                        : "Unknown FXCM price-history error.";

                Console.WriteLine(
                    "Price history failed: " +
                    ErrorMessage);
            }
            finally
            {
                completedEvent.Set();
            }
        }

        public void onRequestCancelled(
            IPriceHistoryCommunicatorRequest request)
        {
            try
            {
                WasCancelled = true;

                Console.WriteLine(
                    "Price history cancelled");
            }
            finally
            {
                completedEvent.Set();
            }
        }

        /// <summary>
        /// Backwards-compatible overload for your existing
        /// FXCMPriceHistory.GetCandles() implementation.
        ///
        /// Unlike the old implementation, this cannot wait forever.
        /// </summary>
        public bool Wait()
        {
            return Wait(DefaultTimeout);
        }

        /// <summary>
        /// Waits until FXCM signals completion, failure, or cancellation.
        ///
        /// Throws on timeout, FXCM failure, cancellation, or an exception
        /// while parsing the response.
        /// </summary>
        public bool Wait(
            TimeSpan timeout)
        {
            if (timeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(timeout),
                    timeout,
                    "Timeout must be greater than zero.");
            }

            bool signalled =
                completedEvent.Wait(timeout);

            if (!signalled)
            {
                throw new TimeoutException(
                    $"FXCM price-history request did not complete within {timeout}.");
            }

            /*
             * Something went wrong while converting the FXCM response
             * into Candle objects.
             */
            if (callbackException != null)
            {
                throw new InvalidOperationException(
                    "Failed to read the FXCM price-history response.",
                    callbackException);
            }

            /*
             * FXCM explicitly reported a request failure.
             */
            if (!string.IsNullOrWhiteSpace(
                    ErrorMessage))
            {
                throw new InvalidOperationException(
                    "FXCM price-history request failed: " +
                    ErrorMessage);
            }

            /*
             * FXCM explicitly cancelled the request.
             */
            if (WasCancelled)
            {
                throw new OperationCanceledException(
                    "FXCM price-history request was cancelled.");
            }

            return true;
        }
    }
}