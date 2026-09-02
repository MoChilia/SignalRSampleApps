// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using RefreshAuthFunctionApp;

// .NET isolated worker host with the ASP.NET Core HTTP integration so functions can use
// HttpRequest / IActionResult directly.
var host = new HostBuilder()
    .ConfigureFunctionsWebApplication()
    .ConfigureServices(services =>
    {
        // Mints and validates the demo app-plane tokens the client presents to negotiate/refresh.
        services.AddSingleton<AppTokenProvider>();
        // Owns the Management SDK ServiceHubContext (the serverless auth boundary). Registered as a
        // hosted service so the context is created once at host startup and disposed at shutdown.
        services.AddSingleton<SignalRService>();
        services.AddHostedService(sp => sp.GetRequiredService<SignalRService>());
    })
    .Build();

host.Run();
