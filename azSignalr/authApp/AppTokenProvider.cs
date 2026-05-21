using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

public sealed class AppTokenProvider
{
    private const string Issuer = "azure-signalr-auth-sample";
    private const string Audience = "azure-signalr-auth-sample-client";
    private static readonly byte[] SigningKey = Encoding.UTF8.GetBytes("azure-signalr-auth-sample-demo-signing-key-change-me");

    public string CreateToken(string userId, string role)
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

    public bool TryValidateToken(string token, out DemoJwtUser user)
    {
        user = default;
        var parts = token.Split('.');

        if (parts.Length != 3)
        {
            return false;
        }

        var unsignedToken = $"{parts[0]}.{parts[1]}";
        var expectedSignature = Sign(unsignedToken);
        var actualSignature = Base64UrlDecode(parts[2]);

        if (!CryptographicOperations.FixedTimeEquals(expectedSignature, actualSignature))
        {
            return false;
        }

        using var header = JsonDocument.Parse(Base64UrlDecode(parts[0]));
        using var payload = JsonDocument.Parse(Base64UrlDecode(parts[1]));

        if (ReadString(header.RootElement, "alg") != "HS256")
        {
            return false;
        }

        var expiresAt = ReadLong(payload.RootElement, "exp");

        if (expiresAt is null || DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= expiresAt.Value)
        {
            return false;
        }

        if (ReadString(payload.RootElement, "iss") != Issuer || ReadString(payload.RootElement, "aud") != Audience)
        {
            return false;
        }

        var userId = ReadString(payload.RootElement, "sub");
        var role = ReadString(payload.RootElement, "role");

        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(role))
        {
            return false;
        }

        user = new DemoJwtUser(userId, role);
        return true;
    }

    private static byte[] Sign(string value)
    {
        using var hmac = new HMACSHA256(SigningKey);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(value));
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static long? ReadLong(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.TryGetInt64(out var value)
            ? value
            : null;

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        var padding = base64.Length % 4;

        if (padding > 0)
        {
            base64 = base64.PadRight(base64.Length + 4 - padding, '=');
        }

        return Convert.FromBase64String(base64);
    }
}

public readonly record struct DemoJwtUser(string UserId, string Role);