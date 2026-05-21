using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls(builder.Configuration["urls"] ?? "http://localhost:5080");
builder.Services.AddSignalR();

var app = builder.Build();
var sharedBrowserRoot = Path.GetFullPath(Path.Combine(
	app.Environment.ContentRootPath,
	"..",
	"..",
	"shared",
	"browser-client"));

app.MapGet("/", () => "SignalR transport sample server is running. Hub: /hub");
app.UseStaticFiles(new StaticFileOptions
{
	FileProvider = new PhysicalFileProvider(sharedBrowserRoot)
});
app.MapHub<ChatHub>("/hub");

app.Run();