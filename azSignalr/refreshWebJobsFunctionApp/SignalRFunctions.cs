// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license.

using System.Threading.Tasks;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Extensions.SignalRService;
using Microsoft.Extensions.Logging;

namespace RefreshWebJobsFunctionApp;

/// <summary>
/// In-process WebJobs Functions that exercise the Microsoft.Azure.WebJobs.Extensions.SignalRService
/// bindings, including the new serverless auth-refresh binding:
///   POST /api/negotiate  -> [SignalRConnectionInfo] returns { url, accessToken, tokenLifetimeSeconds }
///   POST /api/refresh     -> [SignalRRefresh] refreshes a live connection's auth and returns { accessToken, tokenLifetimeSeconds }
///   POST /api/broadcast   -> [SignalR] output binding pushes "ReceiveMessage" to all clients
/// The hub name is fixed for the sample; the client negotiates here, then connects to Azure SignalR.
/// </summary>
public static class SignalRFunctions
{
    private const string HubName = "chat";

    /// <summary>
    /// Standard serverless negotiate. The bound <see cref="SignalRConnectionInfo"/> now carries
    /// <c>tokenLifetimeSeconds</c>, so a refresh-aware client schedules its refresh before the token expires.
    /// </summary>
    [FunctionName("negotiate")]
    public static SignalRConnectionInfo Negotiate(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "negotiate")] HttpRequest req,
        [SignalRConnectionInfo(HubName = HubName, UserId = "{headers.x-user-id}")] SignalRConnectionInfo connectionInfo)
    {
        return connectionInfo;
    }

    /// <summary>
    /// Serverless auth refresh. Mirrors the ASP.NET Core <c>{hubUrl}/refresh</c> contract: the client
    /// POSTs its connection token as <c>?id=</c>, and the <see cref="SignalRRefreshAttribute"/> binding
    /// drives the Management SDK's refresh flow and returns the refreshed token + next lifetime. No
    /// hand-rolled <c>:refreshAuth</c> REST call required.
    /// </summary>
    [FunctionName("refresh")]
    public static SignalRConnectionInfo Refresh(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "refresh")] HttpRequest req,
        [SignalRRefresh(HubName = HubName, ConnectionToken = "{Query.id}", UserId = "{headers.x-user-id}")] SignalRConnectionInfo refreshed,
        ILogger log)
    {
        if (refreshed?.AccessToken is null)
        {
            log.LogWarning("Refresh did not produce a token (missing/invalid connection token or rejected policy).");
        }

        return refreshed;
    }

    /// <summary>
    /// Server-to-client broadcast via the output binding. Pushes "ReceiveMessage" (user, message) to
    /// every connected client, which the sample client renders through its ReceiveMessage handler.
    /// </summary>
    [FunctionName("broadcast")]
    public static async Task<IActionResult> Broadcast(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "broadcast")] BroadcastRequest body,
        [SignalR(HubName = HubName)] IAsyncCollector<SignalRMessage> messages)
    {
        if (string.IsNullOrWhiteSpace(body?.Message))
        {
            return new BadRequestObjectResult(new { error = "message is required." });
        }

        await messages.AddAsync(new SignalRMessage
        {
            Target = "ReceiveMessage",
            Arguments = new object[] { string.IsNullOrWhiteSpace(body.User) ? "server" : body.User, body.Message },
        });

        return new OkObjectResult(new { broadcast = true });
    }

    public sealed class BroadcastRequest
    {
        public string? User { get; set; }

        public string? Message { get; set; }
    }
}
