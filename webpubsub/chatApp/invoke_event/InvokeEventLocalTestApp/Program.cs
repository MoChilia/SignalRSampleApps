using Azure.Messaging.WebPubSub.Clients;
using Azure.Messaging.WebPubSub;

var connectionString = Environment.GetEnvironmentVariable("WEBPUBSUB_CONNECTION_STRING");
var hub = Environment.GetEnvironmentVariable("WEBPUBSUB_HUB");
var successEventName = Environment.GetEnvironmentVariable("WEBPUBSUB_INVOKE_SUCCESS_EVENT") ?? "processOrder";
var errorEventName = Environment.GetEnvironmentVariable("WEBPUBSUB_INVOKE_ERROR_EVENT") ?? "processOrderError";
var cancelEventName = Environment.GetEnvironmentVariable("WEBPUBSUB_INVOKE_CANCEL_EVENT") ?? "slowEvent";
var cancelTimeoutSeconds = int.TryParse(Environment.GetEnvironmentVariable("WEBPUBSUB_INVOKE_CANCEL_TIMEOUT_SECONDS"), out var parsedTimeout)
    ? parsedTimeout
    : 3;

if (string.IsNullOrWhiteSpace(connectionString) || string.IsNullOrWhiteSpace(hub))
{
    Console.WriteLine("Set WEBPUBSUB_CONNECTION_STRING and WEBPUBSUB_HUB in launchSettings.json or your shell.");
    return;
}

var serviceClient = new WebPubSubServiceClient(connectionString, hub);
var clientAccessUri = await serviceClient.GetClientAccessUriAsync();
var client = new WebPubSubClient(clientAccessUri);

client.Connected += args =>
{
    Console.WriteLine($"Connected. ConnectionId={args.ConnectionId}, UserId={args.UserId}");
    return Task.CompletedTask;
};

client.Disconnected += args =>
{
    Console.WriteLine($"Disconnected. Message={args.DisconnectedMessage}");
    return Task.CompletedTask;
};

await client.StartAsync();
Console.WriteLine("Client started.");

try
{
    Console.WriteLine($"[Test 1] Success event: {successEventName}");
    await InvokeAndExpectSuccessAsync(successEventName);

    Console.WriteLine($"[Test 2] Error event: {errorEventName}");
    await InvokeAndExpectErrorAsync(errorEventName);

    Console.WriteLine($"[Test 3] Cancel event: {cancelEventName}, timeout={cancelTimeoutSeconds}s");
    await InvokeAndExpectCancelAsync(cancelEventName, cancelTimeoutSeconds);
}
finally
{
    await client.StopAsync();
    Console.WriteLine("Client stopped.");
}

async Task InvokeAndExpectSuccessAsync(string eventName)
{
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var payload = BinaryData.FromObjectAsJson(new { orderId = 1, createdAt = DateTimeOffset.UtcNow });

    var result = await client.InvokeEventAsync(
        eventName: eventName,
        content: payload,
        dataType: WebPubSubDataType.Json,
        cancellationToken: cts.Token);

    Console.WriteLine($"Success: InvocationId={result.InvocationId}, Data={result.Data}");
}

async Task InvokeAndExpectErrorAsync(string eventName)
{
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var payload = BinaryData.FromObjectAsJson(new { orderId = 2, testCase = "invokeResponseError" });

    try
    {
        var result = await client.InvokeEventAsync(
            eventName: eventName,
            content: payload,
            dataType: WebPubSubDataType.Json,
            cancellationToken: cts.Token);

        Console.WriteLine($"Unexpected success for error test. InvocationId={result.InvocationId}, Data={result.Data}");
    }
    catch (InvocationFailedException ex)
    {
        Console.WriteLine($"Expected invokeResponseError received. InvocationId={ex.InvocationId}, Code={ex.Code}, Message={ex.Message}");
    }
}

async Task InvokeAndExpectCancelAsync(string eventName, int timeoutSeconds)
{
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
    var payload = BinaryData.FromObjectAsJson(new
    {
        orderId = 3,
        testCase = "cancel",
        delay = timeoutSeconds + 5
    });

    try
    {
        var result = await client.InvokeEventAsync(
            eventName: eventName,
            content: payload,
            dataType: WebPubSubDataType.Json,
            cancellationToken: cts.Token);

        Console.WriteLine($"Unexpected success for cancel test. InvocationId={result.InvocationId}, Data={result.Data}");
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("Expected cancellation received.");
    }
}
