using System.Text.RegularExpressions;

namespace PbiTwin.Core.Tests.Guards;

/// <summary>
/// Source-level check that the shipped code contains no networking APIs.
/// Mirrored by .github/scripts/check-no-network.sh, which runs in CI on every push.
/// </summary>
internal static class NoNetworkGuard
{
    private static readonly (string Name, Regex Pattern)[] Rules =
    [
        ("System.Net namespace", new Regex(@"\bSystem\.Net\b")),
        ("HttpClient", new Regex("HttpClient")),
        ("WebClient", new Regex("WebClient")),
        ("WebRequest", new Regex("WebRequest")),
        ("Socket", new Regex("Socket")),
        ("TcpClient/UdpClient/TcpListener", new Regex("TcpClient|UdpClient|TcpListener")),
        ("Dns", new Regex(@"\bDns\b")),
        ("Process.Start with URL", new Regex(@"Process\.Start\b.*(https?:|://|www\.)", RegexOptions.IgnoreCase)),
        ("Hyperlink NavigateUri", new Regex(@"NavigateUri\s*=")),
    ];

    public static IEnumerable<string> FindViolations(string fileName, string content)
    {
        var lines = content.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            foreach (var (name, pattern) in Rules)
            {
                if (pattern.IsMatch(lines[i]))
                {
                    yield return $"{fileName}:{i + 1}: {name}: {lines[i].Trim()}";
                }
            }
        }
    }
}
