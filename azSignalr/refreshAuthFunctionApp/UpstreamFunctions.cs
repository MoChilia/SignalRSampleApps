// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license.

using System;
using System.Linq;

using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace RefreshAuthFunctionApp;

/// <summary>
/// Optional <b>upstream</b> handler for client-to-server messages. In serverless mode there is no hub
/// server, so when a client calls <c>connection.SendAsync("SendToAll", msg)</c> the Azure SignalR
/// service forwards that "messages" event to this Function App's upstream webhook
/// (<c>{funcAppBase}/runtime/webhooks/signalr</c>). The <see cref="SignalRTriggerAttribute"/> below
/// binds that webhook; the <see cref="SignalROutputAttribute"/> return value broadcasts back to clients.
///
/// This requires the Azure SignalR resource's <b>Upstream settings</b> to point at this app's webhook,
/// and (for local runs) a public tunnel so the cloud service can reach your machine. See README.md.
/// Unlike negotiate/refresh/broadcast, this path uses the Functions SignalR binding (the
/// <c>AzureSignalRConnectionString</c> setting), not the Management SDK directly.
/// </summary>
public static class UpstreamFunctions
{
    [Function("SendToAll")]
    [SignalROutput(HubName = SignalRService.HubName)]
    public static SignalRMessageAction OnSendToAll(
        [SignalRTrigger(SignalRService.HubName, "messages", "SendToAll", parameterNames: new[] { "message" })]
        SignalRInvocationContext invocationContext,
        string message,
        FunctionContext context)
    {
        // The upstream payload carries the connection's CURRENT claims (X-ASRS-User-Claims). After a
        // refresh, the runtime lazily rebuilds these from the refreshed claim set on the NEXT client
        // message (spec: "claims ride the next client message") — so the marker below should change to
        // the value minted by the most recent /hub/refresh.
        var claims = invocationContext.Claims;
        var marker = "<none>";
        if (claims != null && claims.TryGetValue("marker", out var markerValue) && !string.IsNullOrEmpty(markerValue))
        {
            marker = markerValue;
        }

        context.GetLogger(nameof(UpstreamFunctions))
            .LogInformation(
                "Upstream SendToAll from {User}/{Connection} marker={Marker}: {Message}. All claims: [{Claims}]",
                invocationContext.UserId,
                invocationContext.ConnectionId,
                marker,
                message,
                claims is null ? "<null>" : string.Join(", ", claims.Select(c => $"{c.Key}={c.Value}")));

        // Broadcast "ReceiveMessage" (user, message) to every client, which refreshAuthClient renders
        // through its connection.On<string,string>("ReceiveMessage", ...) handler. Append the marker so
        // the refreshed-claims propagation is visible on the client too.
        return new SignalRMessageAction("ReceiveMessage")
        {
            Arguments = new object[] { invocationContext.UserId ?? "anon", $"{message} [marker={marker}]" },
        };
    }

    // Client INVOCATION (connection.InvokeAsync<string>("WhoAmIMarker")) -> upstream "messages" event
    // WhoAmIMarker. Unlike SendToAll this returns a VALUE (no [SignalROutput]); the returned string
    // becomes the invocation completion sent back to the calling client. It reports the connection's
    // current server-side "marker" claim, so a client can confirm refreshed claims took effect.
    [Function("WhoAmIMarker")]
    public static string OnWhoAmIMarker(
        [SignalRTrigger(SignalRService.HubName, "messages", "WhoAmIMarker")]
        SignalRInvocationContext invocationContext,
        FunctionContext context)
    {
        var claims = invocationContext.Claims;
        var marker = "<none>";
        if (claims != null && claims.TryGetValue("marker", out var markerValue) && !string.IsNullOrEmpty(markerValue))
        {
            marker = markerValue.ToString();
        }

        var logger = context.GetLogger(nameof(UpstreamFunctions));

        // Raw header dump: the runtime sends the connection's claims in the X-ASRS-User-Claims header,
        // one entry per claim formatted as "Type: Value" (Claim.ToString()). Logging it verbatim shows
        // the runtime-side value so we can compare it against the parsed Claims dictionary above.
        var headers = invocationContext.Headers;
        var rawUserClaims = "<header missing>";
        if (headers != null)
        {
            var match = headers.FirstOrDefault(h => string.Equals(h.Key, "X-ASRS-User-Claims", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(match.Key))
            {
                rawUserClaims = match.Value.ToString();
            }
        }

        logger.LogInformation("Raw X-ASRS-User-Claims header: [{RawUserClaims}]", rawUserClaims);

        var headersDump = headers is null
            ? "<null>"
            : string.Join("; ", headers.Select(h => h.Key + "=" + h.Value.ToString()));
        logger.LogInformation("All upstream headers: [{Headers}]", headersDump);

        logger.LogInformation(
                "Upstream WhoAmIMarker from {User}/{Connection} -> marker={Marker}. All claims: [{Claims}]",
                invocationContext.UserId,
                invocationContext.ConnectionId,
                marker,
                claims is null ? "<null>" : string.Join(", ", claims.Select(c => $"{c.Key}={c.Value}")));

        return marker;
    }
}
