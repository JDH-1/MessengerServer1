using Microsoft.Data.Sqlite;

namespace MessengerServer.Data
{
    public static class Database
    {
        private static readonly string DbPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "messenger_server.db");

        public static string ConnStr => $"Data Source={DbPath}";

        public static void Init()
        {
            using var con = new SqliteConnection(ConnStr);
            con.Open();
            var cmd = con.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS Users (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Username TEXT UNIQUE NOT NULL COLLATE NOCASE,
                    DisplayName TEXT NOT NULL,
                    PasswordHash TEXT NOT NULL,
                    CreatedAt TEXT NOT NULL,
                    IsOnline INTEGER DEFAULT 0
                );
                CREATE TABLE IF NOT EXISTS Messages (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    SenderId INTEGER NOT NULL,
                    ReceiverId INTEGER NOT NULL,
                    Content TEXT NOT NULL,
                    SentAt TEXT NOT NULL,
                    IsRead INTEGER DEFAULT 0,
                    FOREIGN KEY(SenderId) REFERENCES Users(Id),
                    FOREIGN KEY(ReceiverId) REFERENCES Users(Id)
                );
                CREATE TABLE IF NOT EXISTS Friends (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    UserId INTEGER NOT NULL,
                    FriendId INTEGER NOT NULL,
                    CreatedAt TEXT NOT NULL,
                    UNIQUE(UserId, FriendId)
                );
                CREATE TABLE IF NOT EXISTS FriendRequests (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    SenderId INTEGER NOT NULL,
                    ReceiverId INTEGER NOT NULL,
                    Status TEXT DEFAULT 'pending',
                    CreatedAt TEXT NOT NULL
                );
            ";
            cmd.ExecuteNonQuery();
        }
    }
}
