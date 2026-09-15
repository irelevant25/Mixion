using System.Net;
using System.Net.Sockets;
using Mixion.Host.Web;
using Xunit;

namespace Mixion.Host.Tests;

public class PortPreferenceTests : IDisposable
{
    private readonly string _dir  = Path.Combine(Path.GetTempPath(), "mixion-test-" + Guid.NewGuid().ToString("N"));
    private string File => Path.Combine(_dir, "host-port.txt");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void SaveThenRead_RoundTrips()
    {
        PortPreference.Save(File, 54812);

        Assert.Equal(54812, PortPreference.Read(File));
    }

    [Theory]
    [InlineData("not a port")]
    [InlineData("80")]
    [InlineData("70000")]
    [InlineData("-5")]
    public void Read_IgnoresInvalidContent(string content)
    {
        Directory.CreateDirectory(_dir);
        System.IO.File.WriteAllText(File, content);

        Assert.Null(PortPreference.Read(File));
    }

    [Fact]
    public void Read_WithoutAFile_ReturnsNull()
    {
        Assert.Null(PortPreference.Read(File));
    }

    [Fact]
    public void ResolveListenPort_PrefersExplicitThenRememberedWhenFree()
    {
        Assert.Equal(5000, PortPreference.ResolveListenPort(5000, 6000, _ => false));
        Assert.Equal(6000, PortPreference.ResolveListenPort(null, 6000, _ => true));
        Assert.Equal(0,    PortPreference.ResolveListenPort(null, 6000, _ => false));
        Assert.Equal(0,    PortPreference.ResolveListenPort(null, null, _ => true));
    }

    [Fact]
    public void IsAvailable_IsFalseWhileSomethingListensOnThePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Assert.False(PortPreference.IsAvailable(port));
        }
        finally
        {
            listener.Stop();
        }
    }
}
