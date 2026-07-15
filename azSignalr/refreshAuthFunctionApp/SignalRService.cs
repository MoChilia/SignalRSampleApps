// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license.

using Microsoft.Azure.SignalR.Management;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RefreshAuthFunctionApp;

/// <summary>
/// Creates the <see cref="ServiceHubContext"/> once at host startup and disposes it at shutdown,
/// following the Azure SignalR serverless Management sample
/// (AzureSignalR-samples/samples/Management/NegotiationServer). The Function App is the serverless
/// auth boundary: it uses this context to negotiate connections, refresh their auth via
/// <c>RefreshConnectionAuthenticationAsync</c>, and read claims via <c>GetConnectionClaimsAsync</c>.
/// </summary>
public sealed class SignalRService : IHostedService
{
    /// <summary>The ASRS hub name the sample connects clients to.</summary>
    public const string HubName = "chat";

    private readonly IConfiguration _configuration;
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>The hub context. Available once the host has started.</summary>
    public ServiceHubContext ChatHubContext { get; private set; } = null!;

    public SignalRService(IConfiguration configuration, ILoggerFactory loggerFactory)
    {
        _configuration = configuration;
        _loggerFactory = loggerFactory;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // WithConfiguration binds the "Azure:SignalR" section (ConnectionString, ServiceTransportType,
        // ServiceEndpoints, ...), so no manual connection-string reading or transport parsing is needed.
        // The ServiceManager is only needed to create the context, so it is disposed here; the returned
        // ServiceHubContext keeps its own connection alive for the lifetime of the app.
        using var serviceManager = new ServiceManagerBuilder()
            .WithConfiguration(_configuration)
            .WithLoggerFactory(_loggerFactory)
            .BuildServiceManager();
        ChatHubContext = await serviceManager.CreateHubContextAsync(HubName, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) =>
        ChatHubContext?.DisposeAsync() ?? Task.CompletedTask;
}
