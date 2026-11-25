using Microsoft.AspNetCore.DataProtection;
using System.Text;

namespace Jellyfin.Plugin.Frame;

internal static class BrowseTokenStore
{
    internal static async Task<string?> Read(string path, Uri origin, IDataProtectionProvider protection, CancellationToken ct)
    {
        try
        {
            var encrypted = await File.ReadAllTextAsync(path, ct);
            return Protector(origin, protection).Unprotect(encrypted);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    internal static async Task Write(string path, Uri origin, string token, IDataProtectionProvider protection, CancellationToken ct)
    {
        var encrypted = Protector(origin, protection).Protect(token);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, encrypted, Encoding.UTF8, ct);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    private static IDataProtector Protector(Uri origin, IDataProtectionProvider protection) =>
        protection.CreateProtector("Jellyfin.Plugin.Frame.BrowseToken.v1", origin.AbsoluteUri);
}
