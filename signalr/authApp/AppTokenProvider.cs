using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

public sealed class AppTokenProvider
{
    public const string Issuer = "signalr-auth-sample";
    public const string Audience = "signalr-auth-sample-client";
    public const string SigningKey = "signalr-auth-sample-demo-signing-key-change-me";
    private static readonly byte[] SigningKeyBytes = Encoding.UTF8.GetBytes(SigningKey);

    public string CreateToken(string userId, string tenantId, string role)
    {
        var now = DateTimeOffset.UtcNow;
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
            ["tenant_id"] = tenantId,
            ["role"] = role,
            ["iat"] = now.ToUnixTimeSeconds(),
            ["exp"] = now.AddHours(1).ToUnixTimeSeconds()
        };

        var encodedHeader = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(header));
        var encodedPayload = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(payload));
        var unsignedToken = $"{encodedHeader}.{encodedPayload}";
        var signature = Base64UrlEncode(Sign(unsignedToken));

        return $"{unsignedToken}.{signature}";
    }

    private static byte[] Sign(string value)
    {
        using var hmac = new HMACSHA256(SigningKeyBytes);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(value));
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}