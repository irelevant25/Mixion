using System.Collections.Immutable;
using Mixion.Host.Audio;
using Mixion.Host.State;
using Xunit;

namespace Mixion.Host.Tests;

/// <summary>
/// Edge cases for the bus-scoped solo behavior described in BE-043:
///   "if any channel in a bus is soloed, all non-soloed channels in that
///    bus become effectively muted".
/// Mute always wins; solo is bus-scoped (input solo doesn't affect outputs).
/// </summary>
public class SoloLogicTests
{
    private static Channel Ch(string id, bool muted = false, bool soloed = false, float gainDb = 0f)
        => new(id, id, gainDb, muted, soloed);

    private static ImmutableArray<Channel> Bus(params Channel[] channels)
        => channels.ToImmutableArray();

    // ------------------------------------------------------------ no solo

    [Fact]
    public void NoSolo_AllNonMutedChannelsAudible()
    {
        var bus = Bus(Ch("a"), Ch("b"), Ch("c"));
        var mask = SoloLogic.AudibilityMask(bus);

        Assert.False(SoloLogic.HasAnySolo(bus));
        Assert.Equal(new[] { true, true, true }, mask);
    }

    [Fact]
    public void NoSolo_MutedChannelSilenced()
    {
        var bus = Bus(Ch("a"), Ch("b", muted: true), Ch("c"));
        var mask = SoloLogic.AudibilityMask(bus);

        Assert.Equal(new[] { true, false, true }, mask);
    }

    // ----------------------------------------------------------- one solo

    [Fact]
    public void OneSolo_OnlySoloedChannelAudible()
    {
        var bus = Bus(Ch("a"), Ch("b", soloed: true), Ch("c"));
        var mask = SoloLogic.AudibilityMask(bus);

        Assert.True(SoloLogic.HasAnySolo(bus));
        Assert.Equal(new[] { false, true, false }, mask);
    }

    [Fact]
    public void OneSolo_OtherChannelsSilencedRegardlessOfPriorSilenceReason()
    {
        // Even already-muted channels stay silent; the solo doesn't un-mute
        // anything.
        var bus = Bus(Ch("a", muted: true), Ch("b", soloed: true), Ch("c"));
        var mask = SoloLogic.AudibilityMask(bus);

        Assert.Equal(new[] { false, true, false }, mask);
    }

    // -------------------------------------------------------- multi solo

    [Fact]
    public void MultipleSolos_AllSoloedChannelsAudible()
    {
        var bus = Bus(
            Ch("a", soloed: true),
            Ch("b"),
            Ch("c", soloed: true),
            Ch("d"));
        var mask = SoloLogic.AudibilityMask(bus);

        Assert.Equal(new[] { true, false, true, false }, mask);
    }

    // ----------------------------------------------------- solo + mute

    [Fact]
    public void SoloAndMuteOnSameChannel_MuteWins()
    {
        // The user soloed the channel and *also* muted it — silence trumps.
        var bus = Bus(Ch("a", muted: true, soloed: true), Ch("b"));
        var mask = SoloLogic.AudibilityMask(bus);

        Assert.Equal(new[] { false, false }, mask);
    }

    [Fact]
    public void SoloOnAnotherChannelPlusMuteOnSoloed_FullySilent()
    {
        // Two solo flags, one of them muted. Only the un-muted soloed channel
        // gets through; everyone else is silent.
        var bus = Bus(
            Ch("a", soloed: true),
            Ch("b", soloed: true, muted: true),
            Ch("c"));
        var mask = SoloLogic.AudibilityMask(bus);

        Assert.Equal(new[] { true, false, false }, mask);
    }

    // -------------------------------------------------- solo across buses

    [Fact]
    public void SoloOnInputBus_DoesNotAffectOutputBus()
    {
        var inputs  = Bus(Ch("in0", soloed: true), Ch("in1"));
        var outputs = Bus(Ch("out0"), Ch("out1"));

        var inMask  = SoloLogic.AudibilityMask(inputs);
        var outMask = SoloLogic.AudibilityMask(outputs);

        Assert.Equal(new[] { true, false }, inMask);
        Assert.Equal(new[] { true, true  }, outMask);
    }

    [Fact]
    public void SoloOnOutputBus_DoesNotAffectInputBus()
    {
        var inputs  = Bus(Ch("in0"), Ch("in1"));
        var outputs = Bus(Ch("out0"), Ch("out1", soloed: true));

        var inMask  = SoloLogic.AudibilityMask(inputs);
        var outMask = SoloLogic.AudibilityMask(outputs);

        Assert.Equal(new[] { true,  true  }, inMask);
        Assert.Equal(new[] { false, true  }, outMask);
    }

    // ------------------------------------------------------------- empty

    [Fact]
    public void EmptyBus_NoSoloAndEmptyMask()
    {
        var bus = ImmutableArray<Channel>.Empty;
        Assert.False(SoloLogic.HasAnySolo(bus));
        Assert.Empty(SoloLogic.AudibilityMask(bus));
    }

    // -------------------------------------------- IsAudible direct cases

    [Theory]
    [InlineData(false, false, false, true )]  // plain → audible
    [InlineData(true,  false, false, false)]  // muted → silent
    [InlineData(false, true,  false, true )]  // soloed alone → audible
    [InlineData(false, false, true,  false)]  // bus has solo elsewhere → silent
    [InlineData(false, true,  true,  true )]  // bus has solo, this is the soloed → audible
    [InlineData(true,  true,  true,  false)]  // muted & soloed → mute wins
    public void IsAudible_TruthTable(bool muted, bool soloed, bool anyInBusSoloed, bool expected)
    {
        var ch = Ch("c", muted: muted, soloed: soloed);
        Assert.Equal(expected, SoloLogic.IsAudible(ch, anyInBusSoloed));
    }
}
