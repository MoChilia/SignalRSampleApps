using Azure.Messaging.WebPubSub.Clients;
using Azure.Messaging.WebPubSub;

var connectionString = Environment.GetEnvironmentVariable("WEBPUBSUB_CONNECTION_STRING");
var hub = Environment.GetEnvironmentVariable("WEBPUBSUB_HUB");
var cancelTimeoutSeconds = int.TryParse(
    Environment.GetEnvironmentVariable("WEBPUBSUB_INVOKE_CANCEL_TIMEOUT_SECONDS"), out var parsedTimeout)
    ? parsedTimeout
    : 3;

if (string.IsNullOrWhiteSpace(connectionString) || string.IsNullOrWhiteSpace(hub))
{
    Console.WriteLine("Set WEBPUBSUB_CONNECTION_STRING and WEBPUBSUB_HUB in launchSettings.json or your shell.");
    return;
}

var serviceClient = new WebPubSubServiceClient(connectionString, hub);
var clientAccessUri = await serviceClient.GetClientAccessUriAsync(
    userId: "user1",
    roles: new[] { "webpubsub.sendToGroup", "webpubsub.joinLeaveGroup" });
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
Console.WriteLine("Client started.\n");

int passed = 0, failed = 0;

try
{
    // ── Test 1: Invoke processOrder with JSON data — expect success ──
    Console.WriteLine("[Test 1] invokeEvent (processOrder) — expecting success");
    await InvokeProcessOrderAsync();

    // ── Test 2: Invoke echo with text data — expect echo back ──
    Console.WriteLine("\n[Test 2] invokeEvent (echo) — expecting text echo back");
    await InvokeEchoTextAsync();

    // ── Test 3: Invoke processOrderError — expect InvocationFailedException ──
    Console.WriteLine("\n[Test 3] invokeEvent (processOrderError) — expecting server error");
    await InvokeProcessOrderErrorAsync();

    // ── Test 4: Invoke slowEvent with short timeout — expect cancellation ──
    Console.WriteLine($"\n[Test 4] invokeEvent (slowEvent) — expecting timeout & cancel (timeout={cancelTimeoutSeconds}s)");
    await InvokeSlowEventCancelAsync(cancelTimeoutSeconds);

    // ── Test 5: Concurrent invocations (3 × echo) ──
    Console.WriteLine("\n[Test 5] concurrent invokeEvent (3 × echo)");
    await InvokeConcurrentEchoAsync(concurrency: 3);
}
finally
{
    await client.StopAsync();
    Console.WriteLine($"\nClient stopped.  passed={passed}  failed={failed}");
}

// ─── Test helpers ────────────────────────────────────────────────────────

async Task InvokeProcessOrderAsync()
{
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    var payload = BinaryData.FromObjectAsJson(new { orderId = 1, createdAt = DateTimeOffset.UtcNow });

    try
    {
        var result = await client.InvokeEventAsync(
            eventName: "processOrder",
            content: payload,
            dataType: WebPubSubDataType.Json,
            cancellationToken: cts.Token);

        Console.WriteLine($"  PASS — InvocationId={result.InvocationId}, DataType={result.DataType}, Data={result.Data}");
        passed++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  FAIL — {ex.GetType().Name}: {ex.Message}");
        failed++;
    }
}

async Task InvokeEchoTextAsync()
{
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    var payload = BinaryData.FromString("hello");

    try
    {
        var result = await client.InvokeEventAsync(
            eventName: "echo",
            content: payload,
            dataType: WebPubSubDataType.Text,
            cancellationToken: cts.Token);

        Console.WriteLine($"  PASS — InvocationId={result.InvocationId}, DataType={result.DataType}, Data={result.Data}");
        passed++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  FAIL — {ex.GetType().Name}: {ex.Message}");
        failed++;
    }
}

async Task InvokeProcessOrderErrorAsync()
{
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    var payload = BinaryData.FromObjectAsJson(new { orderId = 42, testCase = "invokeResponseError" });

    try
    {
        var result = await client.InvokeEventAsync(
            eventName: "processOrderError",
            content: payload,
            dataType: WebPubSubDataType.Json,
            cancellationToken: cts.Token);

        Console.WriteLine($"  FAIL — expected InvocationFailedException but got success: InvocationId={result.InvocationId}, Data={result.Data}");
        failed++;
    }
    catch (InvocationFailedException ex)
    {
        Console.WriteLine($"  PASS — Correctly received InvocationFailedException:");
        Console.WriteLine($"    InvocationId : {ex.InvocationId}");
        Console.WriteLine($"    Code         : {ex.Code}");
        Console.WriteLine($"    Message      : {ex.Message}");
        passed++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  FAIL — unexpected exception {ex.GetType().Name}: {ex.Message}");
        failed++;
    }
}

async Task InvokeSlowEventCancelAsync(int timeoutSeconds)
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
            eventName: "slowEvent",
            content: payload,
            dataType: WebPubSubDataType.Json,
            cancellationToken: cts.Token);

        Console.WriteLine($"  FAIL — expected OperationCanceledException but got success: InvocationId={result.InvocationId}, Data={result.Data}");
        failed++;
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("  PASS — Expected cancellation received.");
        passed++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  FAIL — unexpected exception {ex.GetType().Name}: {ex.Message}");
        failed++;
    }
}

async Task InvokeConcurrentEchoAsync(int concurrency)
{
    var tasks = Enumerable.Range(0, concurrency).Select(async i =>
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var payload = BinaryData.FromString($"concurrent-{i}");

        var result = await client.InvokeEventAsync(
            eventName: "echo",
            content: payload,
            dataType: WebPubSubDataType.Text,
            cancellationToken: cts.Token);

        return (Index: i, result.InvocationId, result.DataType, result.Data);
    }).ToList();

    try
    {
        var results = await Task.WhenAll(tasks);
        foreach (var r in results.OrderBy(r => r.Index))
        {
            Console.WriteLine($"  [{r.Index}] InvocationId={r.InvocationId}  DataType={r.DataType}  Data={r.Data}");
        }
        Console.WriteLine($"  PASS — all {concurrency} concurrent invocations succeeded.");
        passed++;
    }
    catch (InvocationFailedException ex)
    {
        Console.WriteLine($"  FAIL — concurrent invoke failed: InvocationId={ex.InvocationId}, Code={ex.Code}, Message={ex.Message}");
        failed++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  FAIL — {ex.GetType().Name}: {ex.Message}");
        failed++;
    }
}
