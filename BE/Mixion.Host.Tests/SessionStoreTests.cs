using Mixion.Host.Web;
using Xunit;

namespace Mixion.Host.Tests;

public class SessionStoreTests
{
    [Fact]
    public void Issue_ProducesDistinct256BitTokens()
    {
        var store = new SessionStore();

        var a = store.Issue();
        var b = store.Issue();

        Assert.NotEqual(a, b);
        Assert.Equal(64, a.Length);
    }

    [Fact]
    public void TryRedeem_AcceptsATokenOnce()
    {
        var store = new SessionStore();
        var token = store.Issue();

        Assert.True(store.TryRedeem(token));
        Assert.False(store.TryRedeem(token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000")]
    public void TryRedeem_RejectsTokensThatWereNeverIssued(string? token)
    {
        var store = new SessionStore();
        store.Issue();

        Assert.False(store.TryRedeem(token));
    }

    [Fact]
    public void TryRedeem_RejectsAnExpiredToken()
    {
        var clock = new ManualClock();
        var store = new SessionStore(clock);
        var token = store.Issue();

        clock.Advance(SessionStore.Lifetime);

        Assert.False(store.TryRedeem(token));
    }

    [Fact]
    public void TryRedeem_AcceptsATokenJustBeforeItExpires()
    {
        var clock = new ManualClock();
        var store = new SessionStore(clock);
        var token = store.Issue();

        clock.Advance(SessionStore.Lifetime - TimeSpan.FromSeconds(1));

        Assert.True(store.TryRedeem(token));
    }

    [Fact]
    public void Issue_PrunesExpiredTokens()
    {
        var clock = new ManualClock();
        var store = new SessionStore(clock);
        for (var i = 0; i < 100; i++) store.Issue();

        clock.Advance(SessionStore.Lifetime);
        store.Issue();

        Assert.Equal(1, store.Count);
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
