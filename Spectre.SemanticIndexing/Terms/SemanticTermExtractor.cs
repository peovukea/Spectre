using System.Net;
using System.Net.Sockets;
using Spectre.SemanticIndexing.Metadata;

namespace Spectre.SemanticIndexing.Terms;

internal static class SemanticTermExtractor
{
    public static IReadOnlyList<string> Outgoing(
        string predicate,
        string targetKind,
        NodeMetadata? targetMetadata)
    {
        var prefix = $"OUT:{Normalize(predicate)}";
        var terms = new List<string> { prefix, $"{prefix}:TO_KIND:{Normalize(targetKind)}" };
        AddOptionalTerms(terms, prefix, targetMetadata);
        return terms;
    }

    public static IReadOnlyList<string> Incoming(
        string predicate,
        string sourceKind,
        NodeMetadata? sourceMetadata)
    {
        var prefix = $"IN:{Normalize(predicate)}";
        var terms = new List<string> { prefix, $"{prefix}:FROM_KIND:{Normalize(sourceKind)}" };
        AddOptionalTerms(terms, prefix, sourceMetadata);
        return terms;
    }

    public static string PathBucket(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "UNKNOWN";
        }

        var normalized = value.Trim().Replace('\\', '/').ToLowerInvariant();
        foreach (var root in new[] { "/etc", "/tmp", "/home", "/usr", "/var", "/bin", "/sbin", "/dev", "/proc" })
        {
            if (normalized.Equals(root, StringComparison.Ordinal) ||
                normalized.StartsWith(root + "/", StringComparison.Ordinal))
            {
                return root.ToUpperInvariant() + "/*";
            }
        }

        return "OTHER";
    }

    public static string PortBucket(string value)
    {
        if (!int.TryParse(value, out var port) || port is < 0 or > 65535)
        {
            return "PORT:UNKNOWN";
        }

        return port switch
        {
            22 or 53 or 80 or 443 => $"PORT:{port}",
            <= 1023 => "PORT:0-1023",
            <= 49151 => "PORT:1024-49151",
            _ => "PORT:49152-65535"
        };
    }

    public static string IpScopeBucket(string value)
    {
        if (!IPAddress.TryParse(value, out var address))
        {
            return "IP:UNKNOWN";
        }

        if (IPAddress.IsLoopback(address))
        {
            return "IP:LOOPBACK";
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            if (bytes[0] == 10 ||
                bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
                bytes[0] == 192 && bytes[1] == 168)
            {
                return "IP:PRIVATE";
            }
        }

        return "IP:PUBLIC";
    }

    private static void AddOptionalTerms(List<string> terms, string prefix, NodeMetadata? metadata)
    {
        if (metadata is null)
        {
            return;
        }

        if (metadata.Resolve("HAS_PATH", "HAS_FILE_PATH") is { } path)
        {
            terms.Add($"{prefix}:PATH_BUCKET:{PathBucket(path)}");
        }

        if (metadata.Resolve("HAS_REMOTE_PORT") is { } port)
        {
            terms.Add($"{prefix}:REMOTE_PORT:{PortBucket(port)}");
        }

        if (metadata.Resolve("HAS_REMOTE_IP", "HAS_REMOTE_ADDRESS") is { } ip)
        {
            terms.Add($"{prefix}:REMOTE_IP_SCOPE:{IpScopeBucket(ip)}");
        }
    }

    private static string Normalize(string value) => value.Trim().ToUpperInvariant();
}
