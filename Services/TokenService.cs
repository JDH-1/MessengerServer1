using System.Collections.Concurrent;

namespace MessengerServer.Services
{
    // Simple in-memory token store (userId -> token)
    public static class TokenService
    {
        private static readonly ConcurrentDictionary<int, string> _tokens = new();

        public static string GenerateToken(int userId)
        {
            var token = Convert.ToBase64String(Guid.NewGuid().ToByteArray()) +
                        Convert.ToBase64String(Guid.NewGuid().ToByteArray());
            _tokens[userId] = token;
            return token;
        }

        public static bool Validate(int userId, string token)
        {
            return _tokens.TryGetValue(userId, out var t) && t == token;
        }

        public static void Remove(int userId) => _tokens.TryRemove(userId, out _);
    }
}
