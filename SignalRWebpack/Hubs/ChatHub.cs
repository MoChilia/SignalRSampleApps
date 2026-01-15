using Microsoft.AspNetCore.SignalR;
using System.Dynamic;

namespace SignalRWebpack.Hubs;

// Create a hub by declaring a class that inherits from Hub. Add public methods to the class to make them callable from clients
// Hubs are transient.
// Don't store state in a property of the hub class. Each hub method call is executed on a new hub instance.
// Don't instantiate a hub directly via dependency injection. To send messages to a client from elsewhere in your application use an IHubContext.
public class ChatHub : Hub
{
    // Broadcasts to all connected clients via Clients.All
    // Triggers the client-side event "messageReceived" with the username and message parameters
    // Use await when calling asynchronous methods that depend on the hub staying alive. 
    public async Task NewMessage(long username, string message) =>
        await Clients.All.SendAsync("messageReceived", username, message);

    // Send message to a specific group only
    // await connection.invoke("SendMessageToGroup", "developers", username, "Hello group!");
    public async Task SendMessageToGroup(string groupName, long username, string message) =>
        await Clients.Group(groupName).SendAsync("messageReceived", username, message);

    // Send a private message to a specific user
    // await connection.invoke("SendPrivateMessage", "targetUserId", "Hello privately!");
    public Task SendPrivateMessage(string user, string message)
    {
        return Clients.User(user).SendAsync("ReceiveMessage", message);
    }

    // Join a chat room/group
    // await connection.invoke("AddToGroup", "developers", username);
    public async Task AddToGroup(string groupName, long username)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, groupName);

        await Clients.Group(groupName).SendAsync("Send", $"User {username} has joined the group {groupName}.");
    }

    // Leave a chat room/group
    // await connection.invoke("RemoveFromGroup", "developers", username);
    public async Task RemoveFromGroup(string groupName, long username)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, groupName);

        await Clients.Group(groupName).SendAsync("Send", $"User {username} has left the group {groupName}.");
    }
}