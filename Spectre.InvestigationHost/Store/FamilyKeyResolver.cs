using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Spectre.InvestigationHost.Store;

public static class FamilyKeyResolver
{
    public static (string Key, string Name) Resolve(string basePath)
    {
        var fileName = Path.GetFileNameWithoutExtension(basePath);
        var slug = Regex.Replace(fileName, @"[^a-zA-Z0-9]+", "-").ToLowerInvariant().Trim('-');
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(basePath))))[..8];
        return ($"{slug}-{hash}", fileName);
    }
}
