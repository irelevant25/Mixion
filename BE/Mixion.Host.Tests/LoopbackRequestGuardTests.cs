using Microsoft.AspNetCore.Http;
using Mixion.Host.Web;
using Xunit;

namespace Mixion.Host.Tests;

public class LoopbackRequestGuardTests
{
    [Theory]
    [InlineData("127.0.0.1:54812")]
    [InlineData("localhost:4200")]
    [InlineData("LOCALHOST")]
    [InlineData("[::1]:54812")]
    public void IsLoopbackHost_AcceptsThisMachine(string host)
        => Assert.True(LoopbackRequestGuard.IsLoopbackHost(new HostString(host)));

    [Theory]
    [InlineData("")]
    [InlineData("attacker.example:54812")]   // DNS rebinding: the page's own name, now resolving to 127.0.0.1
    [InlineData("localhost.attacker.example")]
    [InlineData("127.0.0.1.nip.io")]
    [InlineData("192.168.1.10:54812")]
    public void IsLoopbackHost_RefusesOtherNames(string host)
        => Assert.False(LoopbackRequestGuard.IsLoopbackHost(new HostString(host)));

    [Theory]
    [InlineData(null)]                        // not a cross-origin browser request (local tools, same-origin GET)
    [InlineData("")]
    [InlineData("http://127.0.0.1:54812")]    // the packaged UI
    [InlineData("http://localhost:4200")]     // ng serve
    [InlineData("http://[::1]:54812")]
    public void IsAllowedOrigin_AcceptsNoOriginAndLoopbackOrigins(string? origin)
        => Assert.True(LoopbackRequestGuard.IsAllowedOrigin(origin));

    [Theory]
    [InlineData("https://attacker.example")]
    [InlineData("http://localhost.attacker.example")]
    [InlineData("null")]                      // sandboxed iframe, file:// page
    [InlineData("chrome-extension://abcdefghijklmnop")]
    [InlineData("file://")]
    public void IsAllowedOrigin_RefusesForeignOrigins(string origin)
        => Assert.False(LoopbackRequestGuard.IsAllowedOrigin(origin));
}
