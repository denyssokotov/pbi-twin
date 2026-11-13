using PbiTwin.Core.Connection;

namespace PbiTwin.Core.Tests.Connection;

public class LocalhostGuardTests
{
    [Theory]
    [InlineData("Data Source=localhost:52123")]
    [InlineData("Data Source=127.0.0.1:52123")]
    [InlineData("data source=LOCALHOST:1")]
    [InlineData("Data Source=localhost:65535;")]
    [InlineData(" Data Source = localhost:52123 ; Initial Catalog=abc-123-def ")]
    [InlineData("Data Source=localhost:52123;Catalog=abc")]
    public void Accepts_local_connection_strings(string cs)
    {
        Assert.True(LocalhostGuard.IsLocal(cs, out var reason), reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Data Source=myserver:52123")]
    [InlineData("Data Source=powerbi://api.powerbi.com/v1.0/myorg/Workspace")]
    [InlineData("Data Source=asazure://westeurope.asazure.windows.net/srv")]
    [InlineData("Data Source=192.168.1.10:52123")]
    [InlineData("Data Source=127.0.0.2:52123")]
    [InlineData("Data Source=localhost.evil.com:52123")]
    [InlineData("Data Source=localhost")]
    [InlineData("Data Source=localhost:")]
    [InlineData("Data Source=localhost:0")]
    [InlineData("Data Source=localhost:65536")]
    [InlineData("Data Source=localhost:99999")]
    [InlineData("Data Source=localhost:12a")]
    [InlineData("Data Source=[::1]:52123")]
    [InlineData("Initial Catalog=abc")]
    [InlineData("Data Source=localhost:52123;Location=remote:1")]
    [InlineData("Data Source=localhost:52123;Data Source=remote:1")]
    [InlineData("Data Source=remote:1;data source=localhost:52123")]
    [InlineData("Data Source=localhost:52123;User ID=x;Password=y")]
    [InlineData("Server=localhost:52123")]
    [InlineData("DataSource=localhost:52123")]
    [InlineData("Data Source=\"localhost:52123\"")]
    [InlineData("Data Source='remote;x'")]
    [InlineData("Data Source=localhost:52123;garbage")]
    public void Rejects_anything_else(string? cs)
    {
        Assert.False(LocalhostGuard.IsLocal(cs, out var reason));
        Assert.False(string.IsNullOrEmpty(reason));
    }

    [Fact]
    public void EnsureLocal_throws_for_remote()
    {
        Assert.Throws<InvalidOperationException>(() => LocalhostGuard.EnsureLocal("Data Source=remote:1"));
    }

    [Fact]
    public void BuildConnectionString_produces_local_string()
    {
        var cs = LocalhostGuard.BuildConnectionString(52123, "db-guid");
        Assert.Equal("Data Source=localhost:52123;Initial Catalog=db-guid", cs);
        Assert.True(LocalhostGuard.IsLocal(cs, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void BuildConnectionString_rejects_bad_ports(int port)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LocalhostGuard.BuildConnectionString(port));
    }

    [Fact]
    public void BuildConnectionString_rejects_injection_through_database_name()
    {
        Assert.Throws<InvalidOperationException>(() => LocalhostGuard.BuildConnectionString(1, "x;Data Source=remote:1"));
    }
}
