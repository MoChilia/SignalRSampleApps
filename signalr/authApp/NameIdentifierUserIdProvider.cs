using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;

public sealed class NameIdentifierUserIdProvider : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection) =>
        connection.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
}