namespace MessengerServer.Models
{
    public class User
    {
        public int Id { get; set; }
        public string Username { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string PasswordHash { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public bool IsOnline { get; set; }
    }

    public class Message
    {
        public int Id { get; set; }
        public int SenderId { get; set; }
        public int ReceiverId { get; set; }
        public string Content { get; set; } = "";
        public DateTime SentAt { get; set; }
        public bool IsRead { get; set; }
        public string SenderName { get; set; } = "";
    }

    public class FriendRequest
    {
        public int Id { get; set; }
        public int SenderId { get; set; }
        public int ReceiverId { get; set; }
        public string Status { get; set; } = "pending";
        public DateTime CreatedAt { get; set; }
        public string SenderName { get; set; } = "";
    }

    // ── DTO (data transfer objects) ──────────────────────────────
    public record RegisterDto(string Username, string DisplayName, string Password);
    public record LoginDto(string Username, string Password);
    public record SendMessageDto(int SenderId, string Token, int ReceiverId, string Content);
    public record SendFriendRequestDto(int SenderId, string Token, int ReceiverId);
    public record RespondFriendDto(int UserId, string Token, int RequestId, bool Accept);
    public record SetOnlineDto(int UserId, string Token, bool Online);

    public record AuthResponse(bool Ok, string Message, int UserId, string Token, string DisplayName, string Username);
    public record ApiResponse(bool Ok, string Message);
}
