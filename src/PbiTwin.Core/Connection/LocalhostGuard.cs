using System.Globalization;
using System.Text.RegularExpressions;

namespace PbiTwin.Core.Connection;

/// <summary>
/// Makes sure PbiTwin only ever connects to a Power BI Desktop instance running on this machine.
/// </summary>
/// <remarks>
/// The parser is intentionally strict: only a small set of keys is allowed, values cannot be quoted,
/// and duplicate keys are rejected. Anything unusual is refused rather than interpreted.
/// </remarks>
public static partial class LocalhostGuard
{
    private const string DataSourceKey = "data source";

    private static readonly HashSet<string> AllowedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        DataSourceKey,
        "initial catalog",
        "catalog",
    };

    [GeneratedRegex(@"^(?:localhost|127\.0\.0\.1):(?<port>[0-9]{1,5})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LocalDataSource();

    /// <summary>Builds the only kind of connection string PbiTwin uses.</summary>
    public static string BuildConnectionString(int port, string? database = null)
    {
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), port, "Port must be between 1 and 65535.");
        }

        var cs = string.Create(CultureInfo.InvariantCulture, $"Data Source=localhost:{port}");
        if (!string.IsNullOrEmpty(database))
        {
            cs += ";Initial Catalog=" + database;
        }

        EnsureLocal(cs);
        return cs;
    }

    /// <summary>Throws if <paramref name="connectionString"/> could reach anything other than localhost.</summary>
    public static void EnsureLocal(string connectionString)
    {
        if (!IsLocal(connectionString, out var reason))
        {
            throw new InvalidOperationException("Refusing to connect: " + reason);
        }
    }

    /// <summary>Returns true only for "Data Source=localhost:&lt;port&gt;" or "127.0.0.1:&lt;port&gt;".</summary>
    public static bool IsLocal(string? connectionString, out string reason)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            reason = "connection string is empty.";
            return false;
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in connectionString.Split(';'))
        {
            if (part.Trim().Length == 0)
            {
                continue;
            }

            var eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                reason = "connection string is malformed.";
                return false;
            }

            var key = part[..eq].Trim();
            var value = part[(eq + 1)..].Trim();

            if (!AllowedKeys.Contains(key))
            {
                reason = $"key '{key}' is not allowed.";
                return false;
            }

            if (value.IndexOfAny(['"', '\'', '{', '}']) >= 0)
            {
                reason = $"quoted value for '{key}' is not allowed.";
                return false;
            }

            if (!values.TryAdd(key, value))
            {
                reason = $"key '{key}' appears more than once.";
                return false;
            }
        }

        if (!values.TryGetValue(DataSourceKey, out var dataSource))
        {
            reason = "Data Source is missing.";
            return false;
        }

        var match = LocalDataSource().Match(dataSource);
        if (!match.Success)
        {
            reason = "Data Source must be localhost:<port> or 127.0.0.1:<port>.";
            return false;
        }

        var port = int.Parse(match.Groups["port"].Value, NumberStyles.None, CultureInfo.InvariantCulture);
        if (port is < 1 or > 65535)
        {
            reason = "port must be between 1 and 65535.";
            return false;
        }

        reason = string.Empty;
        return true;
    }
}
