using MessengerServer.Data;
using MessengerServer.Hubs;
using MessengerServer.Models;
using MessengerServer.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;

Database.Init();

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSignalR();
builder.WebHost.UseUrls("http://0.0.0.0:5000");

var app = builder.Build();
app.MapHub<ChatHub>("/chathub");

// ════════════════════════════════════════════
//  AUTH
// ════════════════════════════════════════════

app.MapPost("/auth/register", (RegisterDto dto) =>
{
    if (string.IsNullOrWhiteSpace(dto.Username) || string.IsNullOrWhiteSpace(dto.Password) || dto.Password.Length < 6)
        return Results.Ok(new AuthResponse(false, "Логин или пароль слишком короткий", 0, "", "", ""));

    try
    {
        using var con = new SqliteConnection(Database.ConnStr);
        con.Open();
        var hash = BCrypt.Net.BCrypt.HashPassword(dto.Password);
        var cmd = con.CreateCommand();
        cmd.CommandText = "INSERT INTO Users (Username, DisplayName, PasswordHash, CreatedAt) VALUES ($u,$d,$h,$t)";
        cmd.Parameters.AddWithValue("$u", dto.Username.Trim().ToLower());
        cmd.Parameters.AddWithValue("$d", dto.DisplayName.Trim());
        cmd.Parameters.AddWithValue("$h", hash);
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();

        // Get new user id
        var id = con.CreateCommand();
        id.CommandText = "SELECT last_insert_rowid()";
        int userId = Convert.ToInt32(id.ExecuteScalar());
        var token = TokenService.GenerateToken(userId);
        return Results.Ok(new AuthResponse(true, "Регистрация успешна!", userId, token, dto.DisplayName.Trim(), dto.Username.Trim().ToLower()));
    }
    catch
    {
        return Results.Ok(new AuthResponse(false, "Это имя пользователя уже занято", 0, "", "", ""));
    }
});

app.MapPost("/auth/login", (LoginDto dto) =>
{
    using var con = new SqliteConnection(Database.ConnStr);
    con.Open();
    var cmd = con.CreateCommand();
    cmd.CommandText = "SELECT Id, Username, DisplayName, PasswordHash FROM Users WHERE Username=$u";
    cmd.Parameters.AddWithValue("$u", dto.Username.Trim().ToLower());
    using var r = cmd.ExecuteReader();
    if (!r.Read())
        return Results.Ok(new AuthResponse(false, "Пользователь не найден", 0, "", "", ""));

    int id = r.GetInt32(0);
    string username = r.GetString(1);
    string display = r.GetString(2);
    string hash = r.GetString(3);
    r.Close();

    if (!BCrypt.Net.BCrypt.Verify(dto.Password, hash))
        return Results.Ok(new AuthResponse(false, "Неверный пароль", 0, "", "", ""));

    var upd = con.CreateCommand();
    upd.CommandText = "UPDATE Users SET IsOnline=1 WHERE Id=$id";
    upd.Parameters.AddWithValue("$id", id);
    upd.ExecuteNonQuery();

    var token = TokenService.GenerateToken(id);
    return Results.Ok(new AuthResponse(true, "Вход выполнен!", id, token, display, username));
});

app.MapPost("/auth/logout", (SetOnlineDto dto) =>
{
    if (!TokenService.Validate(dto.UserId, dto.Token))
        return Results.Ok(new ApiResponse(false, "Нет доступа"));

    using var con = new SqliteConnection(Database.ConnStr);
    con.Open();
    var cmd = con.CreateCommand();
    cmd.CommandText = "UPDATE Users SET IsOnline=0 WHERE Id=$id";
    cmd.Parameters.AddWithValue("$id", dto.UserId);
    cmd.ExecuteNonQuery();
    TokenService.Remove(dto.UserId);
    return Results.Ok(new ApiResponse(true, "Выход выполнен"));
});

// ════════════════════════════════════════════
//  USERS / SEARCH
// ════════════════════════════════════════════

app.MapGet("/users/search", (string query, int currentUserId) =>
{
    using var con = new SqliteConnection(Database.ConnStr);
    con.Open();
    var cmd = con.CreateCommand();
    cmd.CommandText = @"SELECT Id, Username, DisplayName, IsOnline FROM Users
                        WHERE (Username LIKE $q OR DisplayName LIKE $q) AND Id != $id LIMIT 20";
    cmd.Parameters.AddWithValue("$q", $"%{query}%");
    cmd.Parameters.AddWithValue("$id", currentUserId);
    using var r = cmd.ExecuteReader();
    var list = new List<object>();
    while (r.Read())
        list.Add(new { Id = r.GetInt32(0), Username = r.GetString(1), DisplayName = r.GetString(2), IsOnline = r.GetInt32(3) == 1 });
    return Results.Ok(list);
});

// ════════════════════════════════════════════
//  MESSAGES
// ════════════════════════════════════════════

app.MapPost("/messages/send", async (SendMessageDto dto, IHubContext<ChatHub> hub) =>
{
    if (!TokenService.Validate(dto.SenderId, dto.Token))
        return Results.Ok(new ApiResponse(false, "Нет доступа"));

    using var con = new SqliteConnection(Database.ConnStr);
    con.Open();

    // Get sender display name
    var nameCmd = con.CreateCommand();
    nameCmd.CommandText = "SELECT DisplayName FROM Users WHERE Id=$id";
    nameCmd.Parameters.AddWithValue("$id", dto.SenderId);
    var senderName = nameCmd.ExecuteScalar()?.ToString() ?? "";

    var now = DateTime.UtcNow;
    var cmd = con.CreateCommand();
    cmd.CommandText = "INSERT INTO Messages (SenderId, ReceiverId, Content, SentAt) VALUES ($s,$r,$c,$t)";
    cmd.Parameters.AddWithValue("$s", dto.SenderId);
    cmd.Parameters.AddWithValue("$r", dto.ReceiverId);
    cmd.Parameters.AddWithValue("$c", dto.Content);
    cmd.Parameters.AddWithValue("$t", now.ToString("o"));
    cmd.ExecuteNonQuery();

    // Push via SignalR to receiver
    var msgData = new
    {
        SenderId = dto.SenderId,
        SenderName = senderName,
        Content = dto.Content,
        SentAt = now
    };
    await hub.Clients.Group($"user_{dto.ReceiverId}").SendAsync("ReceiveMessage", msgData);

    return Results.Ok(new ApiResponse(true, "Отправлено"));
});

app.MapGet("/messages", (int userId, int friendId, string token) =>
{
    if (!TokenService.Validate(userId, token))
        return Results.Ok(new List<object>());

    using var con = new SqliteConnection(Database.ConnStr);
    con.Open();
    var cmd = con.CreateCommand();
    cmd.CommandText = @"
        SELECT m.Id, m.SenderId, m.ReceiverId, m.Content, m.SentAt, m.IsRead, u.DisplayName
        FROM Messages m JOIN Users u ON u.Id = m.SenderId
        WHERE (m.SenderId=$u AND m.ReceiverId=$f) OR (m.SenderId=$f AND m.ReceiverId=$u)
        ORDER BY m.SentAt ASC";
    cmd.Parameters.AddWithValue("$u", userId);
    cmd.Parameters.AddWithValue("$f", friendId);
    using var r = cmd.ExecuteReader();
    var list = new List<object>();
    while (r.Read())
        list.Add(new
        {
            Id = r.GetInt32(0),
            SenderId = r.GetInt32(1),
            ReceiverId = r.GetInt32(2),
            Content = r.GetString(3),
            SentAt = DateTime.Parse(r.GetString(4)),
            IsRead = r.GetInt32(5) == 1,
            SenderName = r.GetString(6)
        });
    r.Close();

    // Mark as read
    var upd = con.CreateCommand();
    upd.CommandText = "UPDATE Messages SET IsRead=1 WHERE SenderId=$f AND ReceiverId=$u AND IsRead=0";
    upd.Parameters.AddWithValue("$f", friendId);
    upd.Parameters.AddWithValue("$u", userId);
    upd.ExecuteNonQuery();

    return Results.Ok(list);
});

app.MapGet("/messages/unread", (int userId, int friendId, string token) =>
{
    if (!TokenService.Validate(userId, token)) return Results.Ok(0);
    using var con = new SqliteConnection(Database.ConnStr);
    con.Open();
    var cmd = con.CreateCommand();
    cmd.CommandText = "SELECT COUNT(*) FROM Messages WHERE SenderId=$f AND ReceiverId=$u AND IsRead=0";
    cmd.Parameters.AddWithValue("$f", friendId);
    cmd.Parameters.AddWithValue("$u", userId);
    return Results.Ok(Convert.ToInt32(cmd.ExecuteScalar()));
});

// ════════════════════════════════════════════
//  FRIENDS
// ════════════════════════════════════════════

app.MapGet("/friends", (int userId, string token) =>
{
    if (!TokenService.Validate(userId, token)) return Results.Ok(new List<object>());
    using var con = new SqliteConnection(Database.ConnStr);
    con.Open();
    var cmd = con.CreateCommand();
    cmd.CommandText = @"
        SELECT DISTINCT u.Id, u.Username, u.DisplayName, u.IsOnline
        FROM Friends f
        JOIN Users u ON u.Id = CASE WHEN f.UserId=$id THEN f.FriendId ELSE f.UserId END
        WHERE f.UserId=$id OR f.FriendId=$id
        ORDER BY u.DisplayName";
    cmd.Parameters.AddWithValue("$id", userId);
    using var r = cmd.ExecuteReader();
    var list = new List<object>();
    while (r.Read())
        list.Add(new { Id = r.GetInt32(0), Username = r.GetString(1), DisplayName = r.GetString(2), IsOnline = r.GetInt32(3) == 1 });
    return Results.Ok(list);
});

app.MapPost("/friends/request", async (SendFriendRequestDto dto, IHubContext<ChatHub> hub) =>
{
    if (!TokenService.Validate(dto.SenderId, dto.Token))
        return Results.Ok(new ApiResponse(false, "Нет доступа"));

    using var con = new SqliteConnection(Database.ConnStr);
    con.Open();

    var check = con.CreateCommand();
    check.CommandText = @"SELECT COUNT(*) FROM FriendRequests
        WHERE ((SenderId=$s AND ReceiverId=$r) OR (SenderId=$r AND ReceiverId=$s)) AND Status='pending'";
    check.Parameters.AddWithValue("$s", dto.SenderId);
    check.Parameters.AddWithValue("$r", dto.ReceiverId);
    if (Convert.ToInt32(check.ExecuteScalar()) > 0)
        return Results.Ok(new ApiResponse(false, "Заявка уже отправлена"));

    var alreadyFriends = con.CreateCommand();
    alreadyFriends.CommandText = "SELECT COUNT(*) FROM Friends WHERE (UserId=$s AND FriendId=$r) OR (UserId=$r AND FriendId=$s)";
    alreadyFriends.Parameters.AddWithValue("$s", dto.SenderId);
    alreadyFriends.Parameters.AddWithValue("$r", dto.ReceiverId);
    if (Convert.ToInt32(alreadyFriends.ExecuteScalar()) > 0)
        return Results.Ok(new ApiResponse(false, "Вы уже друзья"));

    var cmd = con.CreateCommand();
    cmd.CommandText = "INSERT INTO FriendRequests (SenderId, ReceiverId, CreatedAt) VALUES ($s,$r,$t)";
    cmd.Parameters.AddWithValue("$s", dto.SenderId);
    cmd.Parameters.AddWithValue("$r", dto.ReceiverId);
    cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
    cmd.ExecuteNonQuery();

    // Notify receiver via SignalR
    await hub.Clients.Group($"user_{dto.ReceiverId}").SendAsync("FriendRequest", new { FromId = dto.SenderId });

    return Results.Ok(new ApiResponse(true, "Заявка отправлена!"));
});

app.MapGet("/friends/requests", (int userId, string token) =>
{
    if (!TokenService.Validate(userId, token)) return Results.Ok(new List<object>());
    using var con = new SqliteConnection(Database.ConnStr);
    con.Open();
    var cmd = con.CreateCommand();
    cmd.CommandText = @"
        SELECT fr.Id, fr.SenderId, fr.ReceiverId, fr.Status, fr.CreatedAt, u.DisplayName
        FROM FriendRequests fr JOIN Users u ON u.Id = fr.SenderId
        WHERE fr.ReceiverId=$id AND fr.Status='pending'";
    cmd.Parameters.AddWithValue("$id", userId);
    using var r = cmd.ExecuteReader();
    var list = new List<object>();
    while (r.Read())
        list.Add(new
        {
            Id = r.GetInt32(0),
            SenderId = r.GetInt32(1),
            ReceiverId = r.GetInt32(2),
            Status = r.GetString(3),
            CreatedAt = DateTime.Parse(r.GetString(4)),
            SenderName = r.GetString(5)
        });
    return Results.Ok(list);
});

app.MapPost("/friends/respond", (RespondFriendDto dto) =>
{
    if (!TokenService.Validate(dto.UserId, dto.Token))
        return Results.Ok(new ApiResponse(false, "Нет доступа"));

    using var con = new SqliteConnection(Database.ConnStr);
    con.Open();

    // Get request
    var get = con.CreateCommand();
    get.CommandText = "SELECT SenderId, ReceiverId FROM FriendRequests WHERE Id=$id";
    get.Parameters.AddWithValue("$id", dto.RequestId);
    using var r = get.ExecuteReader();
    if (!r.Read()) return Results.Ok(new ApiResponse(false, "Заявка не найдена"));
    int senderId = r.GetInt32(0);
    int receiverId = r.GetInt32(1);
    r.Close();

    var status = dto.Accept ? "accepted" : "declined";
    var upd = con.CreateCommand();
    upd.CommandText = "UPDATE FriendRequests SET Status=$s WHERE Id=$id";
    upd.Parameters.AddWithValue("$s", status);
    upd.Parameters.AddWithValue("$id", dto.RequestId);
    upd.ExecuteNonQuery();

    if (dto.Accept)
    {
        var ins = con.CreateCommand();
        ins.CommandText = "INSERT OR IGNORE INTO Friends (UserId, FriendId, CreatedAt) VALUES ($u,$f,$t)";
        ins.Parameters.AddWithValue("$u", senderId);
        ins.Parameters.AddWithValue("$f", receiverId);
        ins.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
        ins.ExecuteNonQuery();
    }

    return Results.Ok(new ApiResponse(true, dto.Accept ? "Принято" : "Отклонено"));
});

app.MapGet("/friends/areFriends", (int userId, int friendId) =>
{
    using var con = new SqliteConnection(Database.ConnStr);
    con.Open();
    var cmd = con.CreateCommand();
    cmd.CommandText = "SELECT COUNT(*) FROM Friends WHERE (UserId=$u AND FriendId=$f) OR (UserId=$f AND FriendId=$u)";
    cmd.Parameters.AddWithValue("$u", userId);
    cmd.Parameters.AddWithValue("$f", friendId);
    return Results.Ok(Convert.ToInt32(cmd.ExecuteScalar()) > 0);
});

Console.WriteLine("=== Messenger Server запущен на http://0.0.0.0:5000 ===");
Console.WriteLine("Нажмите Ctrl+C для остановки.");
app.Run();
