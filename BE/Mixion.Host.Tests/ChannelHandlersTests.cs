using System.Collections.Immutable;
using Mixion.Host.Ipc;
using Mixion.Host.Ipc.Handlers;
using Mixion.Host.State;
using Xunit;

namespace Mixion.Host.Tests;

/// <summary>
/// Pure-function tests over <see cref="ChannelHandlers.ApplyChannelChange"/>
/// — exercised without spinning up the audio engine. Wire-level dispatch is
/// covered indirectly because <see cref="ChannelHandlers"/> uses the same
/// helper inside its registered RPC delegates.
/// </summary>
public class ChannelHandlersTests
{
    private static MixerState MakeState(int inputs, int outputs)
    {
        var inBus  = Enumerable.Range(0, inputs)
            .Select(i => new Channel($"in{i}",  $"In {i}",  GainDb: 0f, Muted: false, Soloed: false))
            .ToImmutableArray();
        var outBus = Enumerable.Range(0, outputs)
            .Select(o => new Channel($"out{o}", $"Out {o}", GainDb: 0f, Muted: false, Soloed: false))
            .ToImmutableArray();
        return new MixerState(inBus, outBus, new RoutingMatrix(inputs, outputs));
    }

    [Fact]
    public void ApplyChannelChange_PatchesInputChannel_LeavesOthersUntouched()
    {
        var state = MakeState(inputs: 3, outputs: 2);

        var next = ChannelHandlers.ApplyChannelChange(
            state, ChannelHandlers.Bus.Input, 1, ch => ch with { GainDb = -6f });

        Assert.Equal(-6f, next.Inputs[1].GainDb);
        Assert.Equal( 0f, next.Inputs[0].GainDb);
        Assert.Equal( 0f, next.Inputs[2].GainDb);
        Assert.Equal(state.Outputs, next.Outputs); // opposite bus untouched
    }

    [Fact]
    public void ApplyChannelChange_PatchesOutputChannel_LeavesInputsUntouched()
    {
        var state = MakeState(inputs: 2, outputs: 2);

        var next = ChannelHandlers.ApplyChannelChange(
            state, ChannelHandlers.Bus.Output, 0, ch => ch with { Muted = true });

        Assert.True(next.Outputs[0].Muted);
        Assert.False(next.Outputs[1].Muted);
        Assert.Equal(state.Inputs, next.Inputs);
    }

    [Fact]
    public void ApplyChannelChange_WithGainDb_RecomputesLinearGain()
    {
        // Channel.WithGainDb is the only correct way to change gain — plain
        // record `with` would copy the cached GainLinear and leave it stale.
        var state = MakeState(inputs: 1, outputs: 1);

        var next = ChannelHandlers.ApplyChannelChange(
            state, ChannelHandlers.Bus.Input, 0, ch => ch.WithGainDb(-6f));

        // 10^(-6/20) ≈ 0.501
        Assert.InRange(next.Inputs[0].GainLinear, 0.49f, 0.51f);
    }

    [Fact]
    public void Channel_WithGainDb_RecomputesLinearGain()
    {
        var ch = new Channel("a", "A", GainDb: 0f, Muted: false, Soloed: false);
        Assert.Equal(1f, ch.GainLinear, precision: 3);

        var loud   = ch.WithGainDb( 6f);
        var quiet  = ch.WithGainDb(-6f);
        var silent = ch.WithGainDb(-60f);

        Assert.InRange(loud.GainLinear, 1.99f, 2.01f);
        Assert.InRange(quiet.GainLinear, 0.49f, 0.51f);
        Assert.InRange(silent.GainLinear, 0.0009f, 0.0011f);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData( 5)]
    public void ApplyChannelChange_OutOfRangeIndex_ThrowsInvalidParams(int index)
    {
        var state = MakeState(inputs: 3, outputs: 1);

        var ex = Assert.Throws<JsonRpcException>(() =>
            ChannelHandlers.ApplyChannelChange(
                state, ChannelHandlers.Bus.Input, index, ch => ch));

        Assert.Equal(JsonRpcErrorCode.InvalidParams, ex.Code);
    }

    [Fact]
    public void ApplyChannelChange_PreservesMatrix()
    {
        var state = MakeState(inputs: 2, outputs: 2);
        var matrix = state.Matrix.With(0, 0, true).With(1, 1, true);
        state = state with { Matrix = matrix };

        var next = ChannelHandlers.ApplyChannelChange(
            state, ChannelHandlers.Bus.Input, 0, ch => ch with { Soloed = true });

        Assert.Same(matrix, next.Matrix); // RoutingMatrix is a class
        Assert.True(next.Inputs[0].Soloed);
    }
}
