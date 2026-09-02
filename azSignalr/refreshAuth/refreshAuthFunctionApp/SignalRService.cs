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
    private readonly object _sync = new();
    private Task<ServiceHubContext>? _initialization;

    public SignalRService(IConfiguration configuration, ILoggerFactory loggerFactory)
    {
        _configuration = configuration;
        _loggerFactory = loggerFactory;
    }

    public Task StartAsync(CancellationToken cancellationToken) =>
        GetChatHubContextAsync(cancellationToken);

    public Task<ServiceHubContext> GetChatHubContextAsync(CancellationToken cancellationToken = default)
    {
        Task<ServiceHubContext> initialization;
        lock (_sync)
        {
            initialization = _initialization ??= CreateHubContextAsync();
        }

        return cancellationToken.CanBeCanceled
            ? initialization.WaitAsync(cancellationToken)
            : initialization;
    }

    private async Task<ServiceHubContext> CreateHubContextAsync()
    {
        var connectionStringOverride = Environment.GetEnvironmentVariable("E2E_SERVERLESS_CONNECTION_STRING");
        // WithConfiguration binds the "Azure:SignalR" section (ConnectionString, ServiceTransportType,
        // ServiceEndpoints, ...), so no manual connection-string reading or transport parsing is needed.
        // The ServiceManager is only needed to create the context, so it is disposed here; the returned
        // ServiceHubContext keeps its own connection alive for the lifetime of the app.
        var builder = new ServiceManagerBuilder();
        if (string.IsNullOrWhiteSpace(connectionStringOverride))
        {
            builder.WithConfiguration(_configuration);
        }
        else
        {
            builder.WithOptions(options =>
            {
                options.ConnectionString = connectionStringOverride;
                options.ServiceTransportType = _configuration.GetValue(
                    "Azure:SignalR:ServiceTransportType",
                    ServiceTransportType.Transient);
            });
        }

        using var serviceManager = builder
            .WithLoggerFactory(_loggerFactory)
            .BuildServiceManager();
        return await serviceManager.CreateHubContextAsync(HubName, CancellationToken.None);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_initialization is { IsCompletedSuccessfully: true })
        {
            await _initialization.Result.DisposeAsync();
        }
    }
}
