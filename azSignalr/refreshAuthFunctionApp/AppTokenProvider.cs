// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RefreshAuthFunctionApp;

/// <summary>
/// Mints and validates the demo app-plane tokens. These are HS256 JWTs whose issuer/audience/signing
/// key match the Default-mode sample (<c>refreshAuthApp</c>) so the same <c>refreshAuthClient</c> works
/// against either host unchanged. In production the app token would come from your real identity
/// provider; the Function App only validates it and never issues service tokens itself.
/// </summary>
public sealed class AppTokenProvider
{
    // Kept identical to refreshAuthApp/AppTokenProvider.cs so tokens are interchangeable across samples.
    public const string Issuer = "azure-signalr-refresh-auth-sample";
    public const string Audience = "azure-signalr-refresh-auth-sample-client";
    private const string SigningKey = "azure-signalr-refresh-auth-sample-demo-signing-key-change-me";

    // Short-lived so the refresh flow is easy to watch (~60s, refresh ~20s before expiry on the client).
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromSeconds(60);

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly byte[] _key = Encoding.UTF8.GetBytes(SigningKey);

    public TokenResult CreateToken(string userId, string role, TimeSpan? lifetime = null)
    {
        var now = DateTimeOffset.UtcNow;
        var expires = now.Add(lifetime ?? DefaultLifetime);

        var header = new Dictionary<string, object> { ["alg"] = "HS256", ["typ"] = "JWT" };
        var payload = new Dictionary<string, object>
        {
            ["iss"] = Issuer,
            ["aud"] = Audience,
            ["sub"] = userId,
            ["name"] = userId,
            ["role"] = role,
            // A per-token marker makes it obvious in logs that a *new* token advanced the connection.
            ["marker"] = Guid.NewGuid().ToString("N"),
            ["iat"] = now.ToUnixTimeSeconds(),
            ["exp"] = expires.ToUnixTimeSeconds(),
        };

        var encodedHeader = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(header, SerializerOptions));
        var encodedPayload = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(payload, SerializerOptions));
        var signature = Base64UrlEncode(Sign($"{encodedHeader}.{encodedPayload}"));

        return new TokenResult($"{encodedHeader}.{encodedPayload}.{signature}", expires);
    }

    /// <summary>
    /// Verifies the HS256 signature, issuer, audience and expiry, and extracts the app claims. Returns
    /// false for any tampered, mismatched or expired token.
    /// </summary>
    public bool TryValidate(string? token, out AppPrincipal principal)
    {
        principal = default;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var parts = token.Split('.');
        if (parts.Length != 3)
        {
            return false;
        }

        var expectedSignature = Base64UrlEncode(Sign($"{parts[0]}.{parts[1]}"));
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(expectedSignature), Encoding.ASCII.GetBytes(parts[2])))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(Base64UrlDecode(parts[1]));
            var root = document.RootElement;

            if (GetString(root, "iss") != Issuer || GetString(root, "aud") != Audience)
            {
                return false;
            }

            var userId = GetString(root, "sub");
            if (string.IsNullOrEmpty(userId))
            {
                return false;
            }

            var exp = root.TryGetProperty("exp", out var expElement) ? expElement.GetInt64() : 0;
            var expiresAt = DateTimeOffset.FromUnixTimeSeconds(exp);
            if (expiresAt <= DateTimeOffset.UtcNow)
            {
                return false;
            }

            principal = new AppPrincipal(userId!, GetString(root, "role"), GetString(root, "marker"), expiresAt);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private byte[] Sign(string value)
    {
        using var hmac = new HMACSHA256(_key);
        return hmac.ComputeHash(Encoding.ASCII.GetBytes(value));
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        switch (normalized.Length % 4)
        {
            case 2: normalized += "=="; break;
            case 3: normalized += "="; break;
        }

        return Convert.FromBase64String(normalized);
    }
}

/// <summary>A minted token plus its absolute expiry.</summary>
public readonly record struct TokenResult(string AccessToken, DateTimeOffset ExpiresAt);

/// <summary>The validated app-plane identity extracted from an incoming app token.</summary>
public readonly record struct AppPrincipal(string UserId, string? Role, string? Marker, DateTimeOffset ExpiresAt);
