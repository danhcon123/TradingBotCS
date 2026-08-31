using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TradingBot.App.Historical;
using TradingBot.App.Live;
using TradingBot.FXCM;
using TradingBot.FXCM.Data;
using TradingBot.FXCM.Settings;

namespace TradingBot.App
{
    /// <summary>
    /// Application entry point only: parse the mode argument, load FXCM credentials, own the
    /// FXCM connection's lifetime, pick the right database for the chosen mode, and hand off
    /// to <see cref="HistoricalWorkflowRunner"/> and/or <see cref="LiveSignalRunner"/>. No
    /// historical/backtest or live-strategy logic lives here - see those two classes.
    /// </summary>
    class Program
    {
        private const string Instrument = "XAU/USD";

        // ForexConnect timeframe codes: minutes are lowercase ("m1"), hours are uppercase
        // ("H1") - uppercase "M1" means MONTHLY, not 1-minute. Easy to get backwards.
        private const string M1Timeframe = "m1";
        private const string H1Timeframe = "H1";

        /// <summary>The live database - never used for the historical workflow.</summary>
        private static readonly string LiveDatabasePath = ResolveDatabasePath("candles.db");

        /// <summary>The historical/backtest database - never used for live processing.</summary>
        private static readonly string HistoricalDatabasePath = ResolveDatabasePath("candles-3months.db");

        private enum AppMode
        {
            /// <summary>dotnet run - live database, LiveSignalRunner only.</summary>
            Live,

            /// <summary>dotnet run -- --historical - historical database, old workflow only, then exit.</summary>
            Historical,

            /// <summary>dotnet run -- --historical-then-live - historical database first, then a separate live database + LiveSignalRunner.</summary>
            HistoricalThenLive
        }

        static async Task Main(string[] args)
        {
            Console.WriteLine("Trading Bot Starting");

            AppMode mode = ParseMode(args);
            Console.WriteLine($"Mode: {mode}");

            string login = Environment.GetEnvironmentVariable("FXCM_LOGIN");
            string password = Environment.GetEnvironmentVariable("FXCM_PASSWORD");

            if (string.IsNullOrWhiteSpace(login) || string.IsNullOrWhiteSpace(password))
            {
                throw new InvalidOperationException(
                    "FXCM_LOGIN and FXCM_PASSWORD are not configured.");
            }

            var fxcm = new FXCMConnection();

            try
            {
                bool connected = fxcm.Connect(
                    login,
                    password,
                    "http://www.fxcorporate.com/Hosts.jsp",
                    "Real");

                Console.WriteLine("Connected: " + connected);

                if (!connected)
                {
                    Console.WriteLine("FXCM connection failed.");
                    return;
                }

                switch (mode)
                {
                    case AppMode.Historical:
                        RunHistoricalMode(fxcm);
                        break;

                    case AppMode.HistoricalThenLive:
                        RunHistoricalMode(fxcm);
                        await RunLiveMode(fxcm);
                        break;

                    case AppMode.Live:
                    default:
                        await RunLiveMode(fxcm);
                        break;
                }
            }
            finally
            {
                // Runs even if a mode above throws, so a crash never leaves the FXCM session
                // dangling.
                Console.WriteLine("[SHUTDOWN] Disconnecting FXCM...");
                fxcm.Disconnect();
                Console.WriteLine("[SHUTDOWN] FXCM disconnect step returned.");
            }

            Console.WriteLine("Trading Bot stopped.");

            // ForexConnect's session/table-manager machinery starts internal worker threads
            // that the managed API (logout/Dispose, both already called above) exposes no
            // way to join. Even after every step above - cancelling LiveSignalRunner,
            // awaiting it, logging out, disposing the session - completes cleanly, those
            // threads can keep the CLR process alive indefinitely instead of returning to
            // the shell. There is no exposed API to force them to stop, so an explicit exit
            // is the only reliable way to guarantee the process actually terminates.
            Environment.Exit(0);
        }

        private static AppMode ParseMode(string[] args)
        {
            if (args != null)
            {
                foreach (string arg in args)
                {
                    if (string.Equals(arg, "--historical-then-live", StringComparison.OrdinalIgnoreCase))
                    {
                        return AppMode.HistoricalThenLive;
                    }

                    if (string.Equals(arg, "--historical", StringComparison.OrdinalIgnoreCase))
                    {
                        return AppMode.Historical;
                    }
                }
            }

            return AppMode.Live;
        }

        private static void RunHistoricalMode(FXCMConnection fxcm)
        {
            CandleRepository repository = OpenRepository(HistoricalDatabasePath, "historical");

            HistoricalWorkflowRunner.Run(fxcm, repository);
        }

        /// <summary>
        /// Starts LiveSignalRunner against the live database and blocks until Ctrl+C
        /// requests cancellation.
        /// </summary>
        private static async Task RunLiveMode(FXCMConnection fxcm)
        {
            CandleRepository repository = OpenRepository(LiveDatabasePath, "live");

            Console.WriteLine();
            Console.WriteLine("################################################");
            Console.WriteLine("Starting live signal runner (Ctrl+C to stop)");
            Console.WriteLine("################################################");

            FXCMPriceHistory history = fxcm.GetPriceHistory();

            var strategySettings = new DragonflyStrategySettings
            {
                TickSize = 0.01, // XAU/USD
                UsePreviousRangeComparison = true
            };

            var runner = new LiveSignalRunner(
                history.GetCandles,
                repository,
                strategySettings,
                Instrument,
                M1Timeframe,
                H1Timeframe);

            using (var cts = new CancellationTokenSource())
            {
                ConsoleCancelEventHandler cancelHandler = (sender, eventArgs) =>
                {
                    // Prevent the default behavior (immediate process termination) so the
                    // runner gets a chance to stop its loop before FXCM is disconnected.
                    eventArgs.Cancel = true;
                    Console.WriteLine();
                    Console.WriteLine("Ctrl+C received - stopping live signal runner...");
                    cts.Cancel();

                    // Safety net: normal shutdown is cancel -> await RunAsync -> logout ->
                    // dispose -> Environment.Exit(0), all in Main. If that pipeline ever
                    // stalls for any reason (e.g. an FXCM SDK worker thread that the managed
                    // API gives no way to join), this guarantees the process still
                    // terminates and the shell prompt returns, instead of hanging forever.
                    Task.Run(async () =>
                    {
                        await Task.Delay(TimeSpan.FromSeconds(15));
                        Console.WriteLine("[SHUTDOWN] Clean shutdown did not finish within 15s - forcing exit.");
                        Environment.Exit(1);
                    });
                };

                Console.CancelKeyPress += cancelHandler;

                try
                {
                    Console.WriteLine("[SHUTDOWN] Awaiting LiveSignalRunner.RunAsync completion...");
                    await runner.RunAsync(cts.Token);
                    Console.WriteLine("[SHUTDOWN] LiveSignalRunner.RunAsync returned.");
                }
                finally
                {
                    Console.CancelKeyPress -= cancelHandler;
                }
            }
        }

        private static CandleRepository OpenRepository(string databasePath, string label)
        {
            string dbDirectory = Path.GetDirectoryName(databasePath);

            if (!string.IsNullOrEmpty(dbDirectory))
            {
                Directory.CreateDirectory(dbDirectory);
            }

            Console.WriteLine($"Database ({label}): {databasePath}");

            return new CandleRepository(databasePath);
        }

        /// <summary>
        /// Places a database file at &lt;solution root&gt;/data/&lt;fileName&gt; rather than
        /// inside the build output directory. Living under bin/ would mean a `dotnet clean`
        /// or a plain deletion of bin/obj - both completely normal things to do - would
        /// silently wipe out downloaded history along with build artifacts. The solution
        /// root is found by walking up from the running exe's directory looking for the
        /// .sln file, so this works regardless of configuration/platform/TFM subfolder depth.
        /// </summary>
        private static string ResolveDatabasePath(string fileName)
        {
            string root = FindSolutionRootDirectory() ?? AppDomain.CurrentDomain.BaseDirectory;
            return Path.Combine(root, "data", fileName);
        }

        private static string FindSolutionRootDirectory()
        {
            var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);

            while (directory != null)
            {
                if (directory.GetFiles("*.sln").Length > 0)
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            return null;
        }
    }
}
