using Microsoft.AspNetCore.DataProtection;
using System.Security.Cryptography;
using Xunit;

namespace Jellyfin.Plugin.Frame.Tests;

public sealed class BrowseTokenStoreTests
{
    [Fact]
    public async Task EncryptsTokenAndBindsItToTheConfiguredApi()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "token");
        var protection = new EphemeralDataProtectionProvider();
        var api = new Uri("https://frame.example/api/");
        const string token = "example-token";
        try
        {
            Assert.Null(await BrowseTokenStore.Read(path, api, protection, default));
            await BrowseTokenStore.Write(path, api, token, protection, default);
            Assert.DoesNotContain(token, await File.ReadAllTextAsync(path));
            Assert.Equal(token, await BrowseTokenStore.Read(path, api, protection, default));
            await Assert.ThrowsAsync<CryptographicException>(() =>
                BrowseTokenStore.Read(path, new Uri("https://other.example/api/"), protection, default));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
