namespace PbiTwin.Core.Tests.Guards;

public class NoNetworkGuardTests
{
    [Fact]
    public void Shipped_source_contains_no_network_apis()
    {
        var files = RepoPaths.SourceFiles("src", ".cs", ".xaml", ".csproj", ".props", ".targets").ToList();
        Assert.NotEmpty(files);

        var violations = files
            .SelectMany(f => NoNetworkGuard.FindViolations(Path.GetRelativePath(RepoPaths.Root, f), File.ReadAllText(f)))
            .ToList();

        Assert.True(violations.Count == 0, "Network APIs found in src/:\n" + string.Join('\n', violations));
    }

    [Fact]
    public void Implicit_System_Net_Http_using_is_removed()
    {
        var targets = File.ReadAllText(Path.Combine(RepoPaths.Root, "Directory.Build.targets"));
        Assert.Contains("<Using Remove=\"System.Net.Http\" />", targets, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("using System.Net;")]
    [InlineData("using System.Net.Http;")]
    [InlineData("var c = new HttpClient();")]
    [InlineData("var c = new WebClient();")]
    [InlineData("var r = HttpWebRequest.Create(u);")]
    [InlineData("var s = new Socket(a, b, c);")]
    [InlineData("var ws = new ClientWebSocket();")]
    [InlineData("var t = new TcpClient();")]
    [InlineData("var ip = Dns.GetHostAddresses(h);")]
    [InlineData("Process.Start(\"https://example.com\");")]
    [InlineData("Process.Start(new ProcessStartInfo(\"www.example.com\") { UseShellExecute = true });")]
    [InlineData("<Hyperlink NavigateUri=\"x\">")]
    public void Detects_forbidden_code(string line)
    {
        Assert.NotEmpty(NoNetworkGuard.FindViolations("x.cs", line));
    }

    [Theory]
    [InlineData("Process.Start(\"explorer.exe\", folder);")]
    [InlineData("var cs = LocalhostGuard.BuildConnectionString(port);")]
    [InlineData("xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"")]
    [InlineData("using System.Numerics;")]
    public void Allows_harmless_code(string line)
    {
        Assert.Empty(NoNetworkGuard.FindViolations("x.cs", line));
    }
}
