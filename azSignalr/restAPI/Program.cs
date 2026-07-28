using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using Azure.Core;
using Azure.Identity;

const string DefaultHub = "chat";
const string DefaultConnectionToken = "unknown-connection-token";
const string SignalRScope = "https://signalr.azure.com/.default";

var arguments = ParseArguments(args);
if (arguments.ContainsKey("help"))
{
    PrintUsage();
    return 0;
}

if (!arguments.TryGetValue("endpoint", out var endpoint) || string.IsNullOrWhiteSpace(endpoint))
{
    Console.Error.WriteLine("Missing required --endpoint argument.");
    PrintUsage();
    return 2;
}

var hub = arguments.GetValueOrDefault("hub", DefaultHub);
var connectionToken = arguments.GetValueOrDefault("connection-token", DefaultConnectionToken);
var expectedProfile = arguments.GetValueOrDefault("expected-profile", "none").ToLowerInvariant();
var expectedStatuses = expectedProfile switch
{
    "read" => (Claims: HttpStatusCode.NotFound, Refresh: HttpStatusCode.Forbidden),
    "write" => (Claims: HttpStatusCode.Forbidden, Refresh: HttpStatusCode.NotFound),
    "read-write" => (Claims: HttpStatusCode.NotFound, Refresh: HttpStatusCode.NotFound),
    "none" => (Claims: HttpStatusCode.Forbidden, Refresh: HttpStatusCode.Forbidden),
    _ => throw new ArgumentException("--expected-profile must be read, write, read-write, or none."),
};

endpoint = endpoint.TrimEnd('/');
var claimsUri = new Uri($"{endpoint}/api/hubs/{Uri.EscapeDataString(hub)}/connections/claims?api-version=2026-07-01");
var refreshUri = new Uri($"{endpoint}/api/hubs/{Uri.EscapeDataString(hub)}/connections/:refreshAuth?api-version=2026-07-01");

var credentialOptions = new DefaultAzureCredentialOptions
{
    ExcludeInteractiveBrowserCredential = true,
};
var credential = new DefaultAzureCredential(credentialOptions);
var accessToken = await credential.GetTokenAsync(
    new TokenRequestContext([SignalRScope]),
    CancellationToken.None);
PrintCredentialIdentity(accessToken.Token);

using var client = new HttpClient();
var claimsResult = await SendAsync(
    client,
    claimsUri,
    accessToken.Token,
    JsonSerializer.Serialize(new { connectionToken }));
var refreshResult = await SendAsync(
    client,
    refreshUri,
    accessToken.Token,
    JsonSerializer.Serialize(new
    {
        connectionToken,
        expireTime = DateTimeOffset.MaxValue,
    }));

PrintResult("claims", claimsResult, expectedStatuses.Claims);
PrintResult("refresh", refreshResult, expectedStatuses.Refresh);

if (claimsResult.StatusCode == HttpStatusCode.MethodNotAllowed
    && refreshResult.StatusCode == HttpStatusCode.MethodNotAllowed)
{
    Console.WriteLine("INCONCLUSIVE: the target service does not expose the 2026-07-01 claims/refresh routes.");
    return 3;
}

var passed = claimsResult.StatusCode == expectedStatuses.Claims
    && refreshResult.StatusCode == expectedStatuses.Refresh;
Console.WriteLine(passed
    ? $"PASS: DefaultAzureCredential has the expected '{expectedProfile}' authorization profile."
    : $"FAIL: responses do not match the expected '{expectedProfile}' authorization profile.");
return passed ? 0 : 1;

static async Task<ResponseResult> SendAsync(HttpClient client, Uri uri, string token, string body)
{
    using var request = new HttpRequestMessage(HttpMethod.Post, uri)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    using var response = await client.SendAsync(request);
    return new ResponseResult(response.StatusCode, await response.Content.ReadAsStringAsync());
}

static void PrintResult(string operation, ResponseResult result, HttpStatusCode expected)
{
    Console.WriteLine($"{operation,-8} actual={(int)result.StatusCode} {result.StatusCode}; expected={(int)expected} {expected}");
    if (!string.IsNullOrWhiteSpace(result.Body))
    {
        Console.WriteLine($"  body: {result.Body}");
    }
}

static void PrintCredentialIdentity(string token)
{
    try
    {
        var segment = token.Split('.')[1].Replace('-', '+').Replace('_', '/');
        segment = segment.PadRight(segment.Length + ((4 - segment.Length % 4) % 4), '=');
        using var document = JsonDocument.Parse(Convert.FromBase64String(segment));
        var root = document.RootElement;
        var objectId = root.TryGetProperty("oid", out var oid) ? oid.GetString() : null;
        var tenantId = root.TryGetProperty("tid", out var tid) ? tid.GetString() : null;
        var applicationId = root.TryGetProperty("appid", out var appid) ? appid.GetString() : null;
        var username = root.TryGetProperty("preferred_username", out var preferredUsername)
            ? preferredUsername.GetString()
            : null;
        Console.WriteLine(
            $"Credential identity: oid={objectId ?? "<none>"}, tid={tenantId ?? "<none>"}, " +
            $"appid={applicationId ?? "<none>"}, user={username ?? "<none>"}");
    }
    catch (Exception ex) when (ex is IndexOutOfRangeException or FormatException or JsonException)
    {
        Console.WriteLine("Credential identity: token metadata unavailable.");
    }
}

static Dictionary<string, string> ParseArguments(string[] arguments)
{
    var supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "endpoint", "hub", "connection-token", "expected-profile",
    };
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var index = 0; index < arguments.Length; index++)
    {
        var argument = arguments[index];
        if (argument.Equals("--help", StringComparison.OrdinalIgnoreCase))
        {
            result["help"] = string.Empty;
            continue;
        }
        if (!argument.StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Unexpected positional argument '{argument}'.");
        }

        var option = argument[2..];
        var separator = option.IndexOf('=');
        var name = separator >= 0 ? option[..separator] : option;
        if (!supported.Contains(name))
        {
            throw new ArgumentException($"Unknown argument '--{name}'.");
        }
        if (result.ContainsKey(name))
        {
            throw new ArgumentException($"Argument '--{name}' was specified more than once.");
        }

        string value;
        if (separator >= 0)
        {
            value = option[(separator + 1)..];
        }
        else if (++index < arguments.Length && !arguments[index].StartsWith("--", StringComparison.Ordinal))
        {
            value = arguments[index];
        }
        else
        {
            throw new ArgumentException($"Argument '--{name}' requires a value.");
        }
        result[name] = value;
    }
    return result;
}

static void PrintUsage() => Console.WriteLine(
    "Usage: dotnet run --project RestApiAuthz.csproj -- --endpoint <https://name.service.signalr.net> [options]\n" +
    "  --hub <name>                       Hub name (default: chat)\n" +
    "  --connection-token <token>         Token to query (default: intentionally unknown)\n" +
    "  --expected-profile <profile>       read | write | read-write | none (default: none)\n" +
    "  --help                             Show this help\n\n" +
    "Authentication uses DefaultAzureCredential with scope https://signalr.azure.com/.default.");

internal sealed record ResponseResult(HttpStatusCode StatusCode, string Body);
