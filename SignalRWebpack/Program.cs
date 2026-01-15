using SignalRWebpack.Hubs;

var builder = WebApplication.CreateBuilder(args);

// To register the services required by SignalR hubs
builder.Services.AddSignalR();

var app = builder.Build();

// Maps requests to default files when a directory is requested
// Default files include: index.html, index.htm, default.html, default.htm
// Example: A request to http://localhost/ would serve wwwroot/index.html if it exists
// Must be called before UseStaticFiles() to work correctly
app.UseDefaultFiles();

// Enables serving of static files (HTML, CSS, JavaScript, images, etc.) from the wwwroot folder
// Files must be in wwwroot or a configured static file directory
// Example: wwwroot/css/style.css becomes accessible at http://localhost/css/style.css
app.UseStaticFiles();


// Configures a SignalR hub endpoint for real-time communication between the server and clients
// Maps the ChatHub class to the route /hub
// Creates a WebSocket endpoint at http://localhost/hub
// Clients connect to this endpoint to send/receive messages through SignalR
app.MapHub<ChatHub>("/hub");

app.Run();
