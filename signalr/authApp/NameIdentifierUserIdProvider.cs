using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.AspNetCore.SignalR;

public sealed class NameIdentifierUserIdProvider : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection) =>
    connection.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value
    ?? connection.User?.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
}