var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls(builder.Configuration["urls"] ?? "http://localhost:5080");
builder.Services.AddSignalR();

var app = builder.Build();

app.MapGet("/", () => "SignalR transport sample server is running. Hub: /hub");
app.UseStaticFiles();
app.MapHub<ChatHub>("/hub");

app.Run();