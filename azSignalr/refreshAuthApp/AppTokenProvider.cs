using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

public sealed class AppTokenProvider
{
    public const string Issuer = "azure-signalr-refresh-auth-sample";
    public const string Audience = "azure-signalr-refresh-auth-sample-client";
    public const string SigningKey = "azure-signalr-refresh-auth-sample-demo-signing-key-change-me";

    // Short lifetime so the refresh scenario is easy to observe end-to-end.
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromSeconds(60);

    private static readonly byte[] SigningKeyBytes = Encoding.UTF8.GetBytes(SigningKey);

    /// <summary>
    /// Creates a JWT with a short <c>exp</c> claim. The refresh-auth Phase 1 feature is designed
    /// to extend this <c>exp</c> on the running connection without forcing a reconnect.
    /// </summary>
    public TokenResult CreateToken(string userId, string role, TimeSpan? lifetime = null)
    {
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.Add(lifetime ?? DefaultLifetime);

        var header = new Dictionary<string, object>
        {
            ["alg"] = "HS256",
            ["typ"] = "JWT"
        };
        var payload = new Dictionary<string, object>
        {
            ["iss"] = Issuer,
            ["aud"] = Audience,
            ["sub"] = userId,
            ["name"] = userId,
            ["role"] = role,
            ["iat"] = now.ToUnixTimeSeconds(),
            ["exp"] = expiresAt.ToUnixTimeSeconds()
        };

        var encodedHeader = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(header));
        var encodedPayload = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(payload));
        var unsignedToken = $"{encodedHeader}.{encodedPayload}";
        var signature = Base64UrlEncode(Sign(unsignedToken));

        return new TokenResult($"{unsignedToken}.{signature}", expiresAt);
    }

    private static byte[] Sign(string value)
    {
        using var hmac = new HMACSHA256(SigningKeyBytes);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(value));
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public readonly record struct TokenResult(string AccessToken, DateTimeOffset ExpiresAt);
