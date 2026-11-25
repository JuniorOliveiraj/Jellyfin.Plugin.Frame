using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Jellyfin.Plugin.Frame;

internal sealed record BrowseAuthTokens(string AccessToken, DateTimeOffset ExpiresAtUtc,
    string RefreshToken, DateTimeOffset RefreshTokenExpiresAtUtc);

internal sealed class BrowseAccountSession(HttpClient http, Uri origin, string path, IDataProtectionProvider protection)
{
    private const string SessionPrefix = "session-v1\n";
    internal const string CredentialsFileName = "Jellyfin.Plugin.Frame.browse-token";
    internal static readonly SemaphoreSlim Gate = new(1);

    internal async Task SignIn(string email, string password, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var tokens = await RequestTokens("auth/login", new { email, password }, ct);
            await Save(tokens, CancellationToken.None);
        }
        finally
        {
            Gate.Release();
        }
    }

    internal async Task<string?> GetAccessToken(CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var saved = await BrowseTokenStore.Read(path, origin, protection, ct);
            var session = ReadSession(saved);
            if (session is null)
                return saved;
            if (session.ExpiresAtUtc > DateTimeOffset.UtcNow.AddSeconds(30))
                return session.AccessToken;
            if (session.RefreshTokenExpiresAtUtc <= DateTimeOffset.UtcNow)
                throw new HttpRequestException("Sessão FILMES-JR expirada.", null, HttpStatusCode.Unauthorized);

            // Keep a rotated refresh token even if the user closes the page during the request.
            var renewed = await RequestTokens("auth/refresh", new { refreshToken = session.RefreshToken },
                CancellationToken.None);
            await Save(renewed, CancellationToken.None);
            ct.ThrowIfCancellationRequested();
            return renewed.AccessToken;
        }
        finally
        {
            Gate.Release();
        }
    }

    internal async Task<bool> HasSession(CancellationToken ct)
    {
        var saved = await BrowseTokenStore.Read(path, origin, protection, ct);
        return ReadSession(saved)?.RefreshTokenExpiresAtUtc > DateTimeOffset.UtcNow;
    }

    internal async Task<bool> SignOut(CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            BrowseAuthTokens? session = null;
            try
            {
                session = ReadSession(await BrowseTokenStore.Read(path, origin, protection, ct));
            }
            catch (Exception ex) when (ex is CryptographicException or JsonException or InvalidDataException)
            {
                // A session tied to an older origin can be removed locally without sending its tokens.
            }
            if (File.Exists(path))
                File.Delete(path);
            if (session is null)
                return true;

            try
            {
                using var response = await http.PostAsJsonAsync(new Uri(origin, "auth/logout"),
                    new { refreshToken = session.RefreshToken }, CancellationToken.None);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                return false;
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task<BrowseAuthTokens> RequestTokens<T>(string route, T payload, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(new Uri(origin, route), payload, ct);
        response.EnsureSuccessStatusCode();
        var tokens = await response.Content.ReadFromJsonAsync<BrowseAuthTokens>(cancellationToken: ct);
        if (tokens is null || string.IsNullOrWhiteSpace(tokens.AccessToken) ||
            string.IsNullOrWhiteSpace(tokens.RefreshToken) || tokens.ExpiresAtUtc <= DateTimeOffset.UtcNow ||
            tokens.RefreshTokenExpiresAtUtc <= DateTimeOffset.UtcNow)
            throw new InvalidDataException("A API FILMES-JR não retornou uma sessão válida.");
        return tokens;
    }

    private Task Save(BrowseAuthTokens tokens, CancellationToken ct) =>
        BrowseTokenStore.Write(path, origin, SessionPrefix + JsonSerializer.Serialize(tokens), protection, ct);

    private static BrowseAuthTokens? ReadSession(string? saved) =>
        saved?.StartsWith(SessionPrefix, StringComparison.Ordinal) == true
            ? JsonSerializer.Deserialize<BrowseAuthTokens>(saved[SessionPrefix.Length..])
                ?? throw new InvalidDataException("Sessão FILMES-JR inválida.")
            : null;
}
