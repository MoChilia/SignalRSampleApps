using Microsoft.Azure.SignalR;
using System.Security.Claims;
using Microsoft.Extensions.FileProviders;

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
});

var app = builder.Build();
var sharedBrowserRoot = Path.GetFullPath(Path.Combine(
    app.Environment.ContentRootPath,
    "..",
    "..",
    "shared",
    "browser-client"));

app.MapGet("/", () => "Azure SignalR transport sample server is running. Hub: /hub");
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(sharedBrowserRoot)
});
app.MapHub<ChatHub>("/hub");

app.Run();