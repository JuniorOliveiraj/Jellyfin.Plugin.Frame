using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Xunit;

namespace Jellyfin.Plugin.Frame.Tests;

public sealed class BrowseAccountSessionTests
{
    [Fact]
    public async Task SignsInRefreshesAndRevokesWithoutPersistingThePassword()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "token");
        var origin = new Uri("https://frame.example/api/");
        var protection = new EphemeralDataProtectionProvider();
        var routes = new List<string>();
        using var http = new HttpClient(new FakeApi(async request =>
        {
            var route = request.RequestUri!.AbsolutePath;
            routes.Add(route);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Null(request.Headers.Authorization);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            if (route == "/api/auth/login")
            {
                Assert.Equal("teste@example.org", body.RootElement.GetProperty("email").GetString());
                Assert.Equal("example-password", body.RootElement.GetProperty("password").GetString());
                return Tokens("first-access", "first-refresh", TimeSpan.FromSeconds(15));
            }
            if (route == "/api/auth/refresh")
            {
                Assert.Equal("first-refresh", body.RootElement.GetProperty("refreshToken").GetString());
                return Tokens("new-access", "new-refresh", TimeSpan.FromMinutes(30));
            }
            Assert.Equal("/api/auth/logout", route);
            Assert.Equal("new-refresh", body.RootElement.GetProperty("refreshToken").GetString());
            return new(HttpStatusCode.NoContent);
        }));

        try
        {
            var session = new BrowseAccountSession(http, origin, path, protection);
            await session.SignIn("teste@example.org", "example-password", default);
            Assert.True(await session.HasSession(default));
            var stored = await BrowseTokenStore.Read(path, origin, protection, default);
            Assert.DoesNotContain("example-password", stored);
            Assert.DoesNotContain("first-refresh", await File.ReadAllTextAsync(path));

            Assert.Equal("new-access", await session.GetAccessToken(default));
            Assert.Equal("new-access", await session.GetAccessToken(default));
            Assert.True(await session.SignOut(default));
            Assert.Null(await session.GetAccessToken(default));
            Assert.Equal(["/api/auth/login", "/api/auth/refresh", "/api/auth/logout"], routes);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InvalidLoginDoesNotReplaceTheManualToken()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "token");
        var origin = new Uri("https://frame.example/");
        var protection = new EphemeralDataProtectionProvider();
        using var http = new HttpClient(new FakeApi(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized))));

        try
        {
            await BrowseTokenStore.Write(path, origin, "manual-token", protection, default);
            var session = new BrowseAccountSession(http, origin, path, protection);
            var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
                session.SignIn("teste@example.org", "incorrect", default));
            Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
            Assert.False(await session.HasSession(default));
            Assert.Equal("manual-token", await session.GetAccessToken(default));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static HttpResponseMessage Tokens(string access, string refresh, TimeSpan lifetime) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                accessToken = access,
                expiresAtUtc = DateTimeOffset.UtcNow.Add(lifetime),
                refreshToken = refresh,
                refreshTokenExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(1)
            }), Encoding.UTF8, "application/json")
        };

    private sealed class FakeApi(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            respond(request);
    }
}
