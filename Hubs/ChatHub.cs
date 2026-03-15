using Microsoft.AspNetCore.SignalR;
using MessengerServer.Services;

namespace MessengerServer.Hubs
{
    public class ChatHub : Hub
    {
        // userId -> connectionId mapping
        public static readonly Dictionary<int, string> UserConnections = new();

        public async Task Register(int userId, string token)
        {
            if (!TokenService.Validate(userId, token)) return;

            lock (UserConnections)
                UserConnections[userId] = Context.ConnectionId;

            await Groups.AddToGroupAsync(Context.ConnectionId, $"user_{userId}");
        }

        public override Task OnDisconnectedAsync(Exception? exception)
        {
            lock (UserConnections)
            {
                var entry = UserConnections.FirstOrDefault(x => x.Value == Context.ConnectionId);
                if (entry.Key != 0)
                    UserConnections.Remove(entry.Key);
            }
            return base.OnDisconnectedAsync(exception);
        }
    }
}
