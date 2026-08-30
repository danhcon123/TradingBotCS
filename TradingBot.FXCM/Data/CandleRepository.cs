using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using TradingBot.FXCM.Models;
using TradingBot.FXCM.Strategy;

namespace TradingBot.FXCM.Data
{
    /// <summary>
    /// SQLite-backed store for OHLC candles, keyed by (Instrument, Timeframe, TimestampUtc).
    /// A unique index on that triple makes every write idempotent: saving the same candle
    /// twice - e.g. because a downloader requested two overlapping date ranges - is a no-op
    /// rather than a duplicate row. This is what makes a resumable historical importer safe
    /// to re-run any number of times.
    /// </summary>
    public class CandleRepository
    {
        private readonly string _connectionString;

        public CandleRepository(string databasePath)
        {
            if (string.IsNullOrWhiteSpace(databasePath))
            {
                throw new ArgumentException("Database path must not be null or empty.", nameof(databasePath));
            }

            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath
            }.ToString();

            EnsureSchema();
        }

        private void EnsureSchema()
        {
            using (SqliteConnection connection = OpenConnection())
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = @"
                    CREATE TABLE IF NOT EXISTS Candles (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        Instrument TEXT NOT NULL,
                        Timeframe TEXT NOT NULL,
                        TimestampUtc INTEGER NOT NULL,
                        Open REAL NOT NULL,
                        High REAL NOT NULL,
                        Low REAL NOT NULL,
                        Close REAL NOT NULL,
                        Volume INTEGER NOT NULL
                    );

                    CREATE UNIQUE INDEX IF NOT EXISTS UX_Candles_Instrument_Timeframe_TimestampUtc
                        ON Candles (Instrument, Timeframe, TimestampUtc);
                ";
                command.ExecuteNonQuery();
            }
        }

        private SqliteConnection OpenConnection()
        {
            var connection = new SqliteConnection(_connectionString);
            connection.Open();
            return connection;
        }

        /// <summary>
        /// Inserts every candle not already present for (instrument, timeframe, its own
        /// TimestampUtc); rows that already exist are left untouched. Safe to call
        /// repeatedly with overlapping or already-imported ranges - all inserts happen in
        /// one transaction. Returns the number of rows actually inserted (excludes skipped
        /// duplicates).
        /// </summary>
        public int SaveCandles(string instrument, string timeframe, IEnumerable<Candle> candles)
        {
            RequireNonEmpty(instrument, nameof(instrument));
            RequireNonEmpty(timeframe, nameof(timeframe));

            if (candles == null)
            {
                return 0;
            }

            int inserted = 0;

            using (SqliteConnection connection = OpenConnection())
            using (SqliteTransaction transaction = connection.BeginTransaction())
            {
                using (SqliteCommand command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"
                        INSERT OR IGNORE INTO Candles
                            (Instrument, Timeframe, TimestampUtc, Open, High, Low, Close, Volume)
                        VALUES
                            (@instrument, @timeframe, @timestampUtc, @open, @high, @low, @close, @volume);
                    ";

                    SqliteParameter instrumentParam = command.Parameters.Add("@instrument", SqliteType.Text);
                    SqliteParameter timeframeParam = command.Parameters.Add("@timeframe", SqliteType.Text);
                    SqliteParameter timestampParam = command.Parameters.Add("@timestampUtc", SqliteType.Integer);
                    SqliteParameter openParam = command.Parameters.Add("@open", SqliteType.Real);
                    SqliteParameter highParam = command.Parameters.Add("@high", SqliteType.Real);
                    SqliteParameter lowParam = command.Parameters.Add("@low", SqliteType.Real);
                    SqliteParameter closeParam = command.Parameters.Add("@close", SqliteType.Real);
                    SqliteParameter volumeParam = command.Parameters.Add("@volume", SqliteType.Integer);

                    instrumentParam.Value = instrument;
                    timeframeParam.Value = timeframe;

                    foreach (Candle candle in candles)
                    {
                        if (candle == null)
                        {
                            continue;
                        }

                        timestampParam.Value = ToUnixSeconds(candle.Time);
                        openParam.Value = candle.Open;
                        highParam.Value = candle.High;
                        lowParam.Value = candle.Low;
                        closeParam.Value = candle.Close;
                        volumeParam.Value = candle.Volume;

                        inserted += command.ExecuteNonQuery();
                    }
                }

                transaction.Commit();
            }

            return inserted;
        }

        /// <summary>
        /// Returns every stored candle for (instrument, timeframe) with
        /// fromUtc &lt;= TimestampUtc &lt; toUtc, ordered ascending by time.
        /// </summary>
        public IReadOnlyList<Candle> GetCandles(string instrument, string timeframe, DateTime fromUtc, DateTime toUtc)
        {
            RequireNonEmpty(instrument, nameof(instrument));
            RequireNonEmpty(timeframe, nameof(timeframe));

            var results = new List<Candle>();

            using (SqliteConnection connection = OpenConnection())
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = @"
                    SELECT TimestampUtc, Open, High, Low, Close, Volume
                    FROM Candles
                    WHERE Instrument = @instrument
                      AND Timeframe = @timeframe
                      AND TimestampUtc >= @fromUtc
                      AND TimestampUtc < @toUtc
                    ORDER BY TimestampUtc ASC;
                ";

                command.Parameters.AddWithValue("@instrument", instrument);
                command.Parameters.AddWithValue("@timeframe", timeframe);
                command.Parameters.AddWithValue("@fromUtc", ToUnixSeconds(fromUtc));
                command.Parameters.AddWithValue("@toUtc", ToUnixSeconds(toUtc));

                using (SqliteDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        results.Add(new Candle
                        {
                            Time = FromUnixSeconds(reader.GetInt64(0)),
                            Open = reader.GetDouble(1),
                            High = reader.GetDouble(2),
                            Low = reader.GetDouble(3),
                            Close = reader.GetDouble(4),
                            Volume = reader.GetInt64(5)
                        });
                    }
                }
            }

            return results;
        }

        /// <summary>
        /// The most recent stored candle's UTC timestamp for (instrument, timeframe), or
        /// null if none exist. Used by a resumable importer to know where to continue from.
        /// </summary>
        public DateTime? GetLatestTimestampUtc(string instrument, string timeframe)
        {
            return ExecuteTimestampScalar(instrument, timeframe, "MAX");
        }

        /// <summary>
        /// The oldest stored candle's UTC timestamp for (instrument, timeframe), or null if
        /// none exist.
        /// </summary>
        public DateTime? GetEarliestTimestampUtc(string instrument, string timeframe)
        {
            return ExecuteTimestampScalar(instrument, timeframe, "MIN");
        }

        /// <summary>Total number of stored candles for (instrument, timeframe).</summary>
        public long GetCandleCount(string instrument, string timeframe)
        {
            RequireNonEmpty(instrument, nameof(instrument));
            RequireNonEmpty(timeframe, nameof(timeframe));

            using (SqliteConnection connection = OpenConnection())
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = @"
                    SELECT COUNT(*)
                    FROM Candles
                    WHERE Instrument = @instrument AND Timeframe = @timeframe;
                ";
                command.Parameters.AddWithValue("@instrument", instrument);
                command.Parameters.AddWithValue("@timeframe", timeframe);

                return Convert.ToInt64(command.ExecuteScalar());
            }
        }

        private DateTime? ExecuteTimestampScalar(string instrument, string timeframe, string aggregateFunction)
        {
            RequireNonEmpty(instrument, nameof(instrument));
            RequireNonEmpty(timeframe, nameof(timeframe));

            using (SqliteConnection connection = OpenConnection())
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText =
                    $@"SELECT {aggregateFunction}(TimestampUtc)
                       FROM Candles
                       WHERE Instrument = @instrument AND Timeframe = @timeframe;";

                command.Parameters.AddWithValue("@instrument", instrument);
                command.Parameters.AddWithValue("@timeframe", timeframe);

                object result = command.ExecuteScalar();
                if (result == null || result is DBNull)
                {
                    return null;
                }

                return FromUnixSeconds(Convert.ToInt64(result));
            }
        }

        private static void RequireNonEmpty(string value, string paramName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException($"{paramName} must not be null or empty.", paramName);
            }
        }

        private static long ToUnixSeconds(DateTime utc)
        {
            StrategyUtcContract.Ensure(utc);
            return new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();
        }

        private static DateTime FromUnixSeconds(long seconds)
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
        }
    }
}
