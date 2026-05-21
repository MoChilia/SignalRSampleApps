using Microsoft.Azure.SignalR;
using System.Security.Claims;

var builder = WebApplication.CreateBuilder(args);

const string connectionStringKey = "Azure:SignalR:ConnectionString";
var connectionString = builder.Configuration[connectionStringKey];

if (string.IsNullOrWhiteSpace(connectionString))
{
	throw new InvalidOperationException(
		$"Missing Azure SignalR connection string. Set '{connectionStringKey}' with user secrets or the 'Azure__SignalR__ConnectionString' environment variable.");
}

builder.WebHost.UseUrls("http://localhost:5090");
builder.Services.AddSignalR().AddAzureSignalR(options =>
{
	options.ConnectionString = connectionString;
    options.ClaimsProvider = context =>
    {
        return new[]
        {
            new Claim("tenant", "contoso"),
            new Claim("role", "operator")
        };
    };
});

var app = builder.Build();

app.MapGet("/", () => "Azure SignalR transport sample server is running. Hub: /hub");
app.UseStaticFiles();
app.MapHub<ChatHub>("/hub");

app.Run();