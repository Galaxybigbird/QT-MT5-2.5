using System;
using System.Data;
using System.Data.SQLite;
using System.IO;
using NinjaTrader.Cbi;

namespace NinjaTrader.NinjaScript.AddOns
{
    public static class SQLiteHelper
    {
        private static string _databasePath = string.Empty;
        private static string ConnectionString => $"Data Source={DatabasePath};Version=3;";

        public static string DatabasePath
        {
            get
            {
                if (string.IsNullOrEmpty(_databasePath))
                {
                    // Default to UnifiedTradingBridge database location
                    var baseDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                    _databasePath = Path.Combine(baseDir, @"..\..\..\UnifiedTradingBridge\Database\trading_bridge.db");

                    // Create directory if it doesn't exist
                    var directory = Path.GetDirectoryName(_databasePath);
                    if (!Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }
                }
                return _databasePath;
            }
            set { _databasePath = value; }
        }

        /// <summary>
        /// Helper method to log messages directly to NinjaTrader output
        /// </summary>
        private static void LogToBridge(string level, string category, string message)
        {
            try
            {
                var mgr = MultiStratManager.Instance;
                if (mgr != null)
                {
                    switch ((level ?? "").ToUpperInvariant())
                    {
                        case "DEBUG": mgr.LogDebug(category, message); break;
                        case "WARN": mgr.LogWarn(category, message); break;
                        case "ERROR": mgr.LogError(category, message); break;
                        default: mgr.LogInfo(category, message); break;
                    }
                }
                else
                {
                    NinjaTrader.Code.Output.Process($"[SQLITE_{level}][{category}] {message}", PrintTo.OutputTab1);
                }
            }
            catch (Exception ex)
            {
                // Last resort fallback
                NinjaTrader.Code.Output.Process($"[SQLITE_ERROR] Logging failed: {ex.Message} | Original: [{level}][{category}] {message}", PrintTo.OutputTab1);
            }
        }

        public static bool IsAvailable()
        {
            try
            {
                using (var connection = new SQLiteConnection(ConnectionString))
                {
                    connection.Open();
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        public static bool LogTrade(string baseId, string action, string symbol, string direction, 
            double quantity, double? price = null, double? ntBalance = null, double? ntDailyPnL = null, 
            string ntTradeResult = null, int? ntSessionTrades = null)
        {
            try
            {
                using (var connection = new SQLiteConnection(ConnectionString))
                {
                    connection.Open();

                    // Check if trade already exists
                    var checkSql = "SELECT COUNT(*) FROM Trades WHERE BaseId = @baseId";
                    using (var checkCommand = new SQLiteCommand(checkSql, connection))
                    {
                        checkCommand.Parameters.AddWithValue("@baseId", baseId);
                        var count = Convert.ToInt32(checkCommand.ExecuteScalar());
                        
                        if (count > 0)
                        {
                            // Trade already exists, skip logging
                            return true;
                        }
                    }

                    // Insert new trade
                    var sql = @"
                        INSERT INTO Trades (
                            BaseId, Timestamp, Source, Action, Symbol, Direction, Quantity, Price, Status,
                            NTBalance, NTDailyPnL, NTTradeResult, NTSessionTrades, CreatedAt, UpdatedAt
                        ) VALUES (
                            @baseId, @timestamp, 'NT', @action, @symbol, @direction, @quantity, @price, 'PENDING',
                            @ntBalance, @ntDailyPnL, @ntTradeResult, @ntSessionTrades, @createdAt, @updatedAt
                        )";

                    using (var command = new SQLiteCommand(sql, connection))
                    {
                        var now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff");
                        
                        command.Parameters.AddWithValue("@baseId", baseId);
                        command.Parameters.AddWithValue("@timestamp", now);
                        command.Parameters.AddWithValue("@action", action);
                        command.Parameters.AddWithValue("@symbol", symbol);
                        command.Parameters.AddWithValue("@direction", direction);
                        command.Parameters.AddWithValue("@quantity", quantity);
                        command.Parameters.AddWithValue("@price", price.HasValue ? (object)price.Value : DBNull.Value);
                        command.Parameters.AddWithValue("@ntBalance", ntBalance.HasValue ? (object)ntBalance.Value : DBNull.Value);
                        command.Parameters.AddWithValue("@ntDailyPnL", ntDailyPnL.HasValue ? (object)ntDailyPnL.Value : DBNull.Value);
                        command.Parameters.AddWithValue("@ntTradeResult", ntTradeResult ?? (object)DBNull.Value);
                        command.Parameters.AddWithValue("@ntSessionTrades", ntSessionTrades.HasValue ? (object)ntSessionTrades.Value : DBNull.Value);
                        command.Parameters.AddWithValue("@createdAt", now);
                        command.Parameters.AddWithValue("@updatedAt", now);

                        command.ExecuteNonQuery();
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                LogToBridge("ERROR", "DATABASE", $"SQLite Error logging trade {baseId}: {ex.Message}");
                return false;
            }
        }

        public static bool LogClosureRequest(string baseId, double quantity)
        {
            try
            {
                using (var connection = new SQLiteConnection(ConnectionString))
                {
                    connection.Open();

                    // Update trade status to closed
                    var updateSql = @"
                        UPDATE Trades 
                        SET Status = 'CLOSED', ClosureSource = 'NT_INITIATED', ClosedAt = @closedAt, UpdatedAt = @updatedAt
                        WHERE BaseId = @baseId";

                    using (var updateCommand = new SQLiteCommand(updateSql, connection))
                    {
                        var now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff");
                        updateCommand.Parameters.AddWithValue("@baseId", baseId);
                        updateCommand.Parameters.AddWithValue("@closedAt", now);
                        updateCommand.Parameters.AddWithValue("@updatedAt", now);
                        updateCommand.ExecuteNonQuery();
                    }

                    // Add closure request to queue
                    var insertSql = @"
                        INSERT INTO ClosureQueue (BaseId, Source, TargetSystem, Quantity, Status, CreatedAt)
                        VALUES (@baseId, 'NT', 'MT5', @quantity, 'PENDING', @createdAt)";

                    using (var insertCommand = new SQLiteCommand(insertSql, connection))
                    {
                        var now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff");
                        insertCommand.Parameters.AddWithValue("@baseId", baseId);
                        insertCommand.Parameters.AddWithValue("@quantity", quantity);
                        insertCommand.Parameters.AddWithValue("@createdAt", now);
                        insertCommand.ExecuteNonQuery();
                    }

                    return true;
                }
            }
            catch (Exception ex)
            {
                LogToBridge("ERROR", "DATABASE", $"SQLite Error logging closure {baseId}: {ex.Message}");
                return false;
            }
        }

        public static ClosureNotification[] GetPendingClosureNotifications()
        {
            try
            {
                using (var connection = new SQLiteConnection(ConnectionString))
                {
                    connection.Open();

                    var sql = @"
                        SELECT cq.BaseId, cq.Quantity, t.Symbol, t.Direction
                        FROM ClosureQueue cq
                        LEFT JOIN Trades t ON cq.BaseId = t.BaseId
                        WHERE cq.Source = 'MT5' AND cq.TargetSystem = 'NT' AND cq.Status = 'CONFIRMED'
                        ORDER BY cq.CreatedAt";

                    using (var command = new SQLiteCommand(sql, connection))
                    using (var reader = command.ExecuteReader())
                    {
                        var notifications = new System.Collections.Generic.List<ClosureNotification>();

                        while (reader.Read())
                        {
                            notifications.Add(new ClosureNotification
                            {
                                BaseId = reader["BaseId"].ToString(),
                                Quantity = Convert.ToDouble(reader["Quantity"]),
                                Symbol = reader["Symbol"]?.ToString(),
                                Direction = reader["Direction"]?.ToString()
                            });
                        }

                        return notifications.ToArray();
                    }
                }
            }
            catch (Exception ex)
            {
                LogToBridge("ERROR", "DATABASE", $"SQLite Error getting closure notifications: {ex.Message}");
                return new ClosureNotification[0];
            }
        }

        public static bool MarkClosureNotificationProcessed(string baseId)
        {
            try
            {
                using (var connection = new SQLiteConnection(ConnectionString))
                {
                    connection.Open();

                    var sql = @"
                        UPDATE ClosureQueue 
                        SET Status = 'PROCESSED', ProcessedAt = @processedAt
                        WHERE BaseId = @baseId AND Source = 'MT5' AND TargetSystem = 'NT'";

                    using (var command = new SQLiteCommand(sql, connection))
                    {
                        command.Parameters.AddWithValue("@baseId", baseId);
                        command.Parameters.AddWithValue("@processedAt", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                        
                        var rowsAffected = command.ExecuteNonQuery();
                        return rowsAffected > 0;
                    }
                }
            }
            catch (Exception ex)
            {
                LogToBridge("ERROR", "DATABASE", $"SQLite Error marking closure processed {baseId}: {ex.Message}");
                return false;
            }
        }

        public static bool UpdateHeartbeat(string component = "NT_ADDON", string status = "ONLINE", string version = "1.0.0")
        {
            try
            {
                using (var connection = new SQLiteConnection(ConnectionString))
                {
                    connection.Open();

                    var sql = @"
                        INSERT OR REPLACE INTO SystemStatus (Id, Component, Status, LastHeartbeat, Version, UpdatedAt)
                        VALUES (
                            (SELECT Id FROM SystemStatus WHERE Component = @component),
                            @component, @status, @heartbeat, @version, @updatedAt
                        )";

                    using (var command = new SQLiteCommand(sql, connection))
                    {
                        var now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff");
                        command.Parameters.AddWithValue("@component", component);
                        command.Parameters.AddWithValue("@status", status);
                        command.Parameters.AddWithValue("@heartbeat", now);
                        command.Parameters.AddWithValue("@version", version);
                        command.Parameters.AddWithValue("@updatedAt", now);

                        command.ExecuteNonQuery();
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                LogToBridge("ERROR", "DATABASE", $"SQLite Error updating heartbeat: {ex.Message}");
                return false;
            }
        }

        public static void EnsureTablesExist()
        {
            try
            {
                using (var connection = new SQLiteConnection(ConnectionString))
                {
                    connection.Open();

                    var createTablesSql = @"
                        CREATE TABLE IF NOT EXISTS Trades (
                            Id INTEGER PRIMARY KEY AUTOINCREMENT,
                            BaseId TEXT NOT NULL UNIQUE,
                            Timestamp TEXT NOT NULL,
                            Source TEXT NOT NULL,
                            Action TEXT NOT NULL,
                            Symbol TEXT NOT NULL,
                            Direction TEXT NOT NULL,
                            Quantity REAL NOT NULL,
                            Price REAL,
                            Status TEXT NOT NULL,
                            NTBalance REAL,
                            NTDailyPnL REAL,
                            NTTradeResult TEXT,
                            NTSessionTrades INTEGER,
                            ElasticLevel INTEGER,
                            MT5Ticket INTEGER,
                            MT5Symbol TEXT,
                            ClosureSource TEXT,
                            ClosureReason TEXT,
                            ClosedAt TEXT,
                            NTNotified BOOLEAN DEFAULT 0,
                            MT5Notified BOOLEAN DEFAULT 0,
                            CreatedAt TEXT DEFAULT CURRENT_TIMESTAMP,
                            UpdatedAt TEXT DEFAULT CURRENT_TIMESTAMP
                        );

                        CREATE TABLE IF NOT EXISTS ClosureQueue (
                            Id INTEGER PRIMARY KEY AUTOINCREMENT,
                            BaseId TEXT NOT NULL,
                            Source TEXT NOT NULL,
                            TargetSystem TEXT NOT NULL,
                            Quantity REAL NOT NULL,
                            Status TEXT DEFAULT 'PENDING',
                            RetryCount INTEGER DEFAULT 0,
                            CreatedAt TEXT DEFAULT CURRENT_TIMESTAMP,
                            ProcessedAt TEXT
                        );

                        CREATE TABLE IF NOT EXISTS SystemStatus (
                            Id INTEGER PRIMARY KEY,
                            Component TEXT NOT NULL UNIQUE,
                            Status TEXT NOT NULL,
                            LastHeartbeat TEXT,
                            Version TEXT,
                            ErrorMessage TEXT,
                            UpdatedAt TEXT DEFAULT CURRENT_TIMESTAMP
                        );

                        CREATE INDEX IF NOT EXISTS idx_trades_baseid ON Trades(BaseId);
                        CREATE INDEX IF NOT EXISTS idx_trades_status ON Trades(Status);
                        CREATE INDEX IF NOT EXISTS idx_closure_status ON ClosureQueue(Status);
                    ";

                    using (var command = new SQLiteCommand(createTablesSql, connection))
                    {
                        command.ExecuteNonQuery();
                    }
                }
            }
            catch (Exception ex)
            {
                LogToBridge("ERROR", "DATABASE", $"SQLite Error creating tables: {ex.Message}");
            }
        }
    }

    public class ClosureNotification
    {
        public string BaseId { get; set; }
        public double Quantity { get; set; }
        public string Symbol { get; set; }
        public string Direction { get; set; }
    }
}