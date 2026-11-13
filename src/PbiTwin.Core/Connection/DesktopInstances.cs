using System.Globalization;
using System.Text;
using Microsoft.AnalysisServices.Tabular;

namespace PbiTwin.Core.Connection;

/// <summary>A running Power BI Desktop model on this machine.</summary>
public sealed record DesktopInstance(int Port, string WorkspaceFolder)
{
    public string ConnectionString => LocalhostGuard.BuildConnectionString(Port);
}

public sealed record DesktopModel(int Port, string DatabaseName, string DatabaseId, IReadOnlyList<string> Tables);

/// <summary>Finds Power BI Desktop instances via the <c>msmdsrv.port.txt</c> files of their local Analysis Services.</summary>
public static class DesktopInstances
{
    public static IEnumerable<string> WorkspaceRoots()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Combine(local, "Microsoft", "Power BI Desktop", "AnalysisServicesWorkspaces");
        yield return Path.Combine(profile, "Microsoft", "Power BI Desktop Store App", "AnalysisServicesWorkspaces");
    }

    public static List<DesktopInstance> Find(IEnumerable<string>? roots = null)
    {
        var result = new List<DesktopInstance>();
        foreach (var root in roots ?? WorkspaceRoots())
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var workspace in Directory.EnumerateDirectories(root))
            {
                var portFile = Path.Combine(workspace, "Data", "msmdsrv.port.txt");
                if (File.Exists(portFile) && TryReadPort(File.ReadAllBytes(portFile), out var port))
                {
                    result.Add(new DesktopInstance(port, workspace));
                }
            }
        }

        return result;
    }

    /// <summary>The port file is UTF-16 on most versions; accept UTF-8/ASCII too.</summary>
    public static bool TryReadPort(byte[] bytes, out int port)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        var text = bytes.Length >= 2 && bytes[1] == 0 ? Encoding.Unicode.GetString(bytes) : Encoding.UTF8.GetString(bytes);
        text = new string(text.Where(char.IsAsciiDigit).ToArray());
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is > 0 and <= 65535;
    }

    /// <summary>Connects through TOM (localhost only) and lists the model's tables.</summary>
    public static DesktopModel Describe(DesktopInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);

        var cs = instance.ConnectionString;
        LocalhostGuard.EnsureLocal(cs);
        using var server = new Server();
        server.Connect(cs);
        try
        {
            var db = server.Databases[0];
            return new DesktopModel(instance.Port, db.Name, db.ID, db.Model.Tables.Select(t => t.Name).ToList());
        }
        finally
        {
            server.Disconnect();
        }
    }
}
