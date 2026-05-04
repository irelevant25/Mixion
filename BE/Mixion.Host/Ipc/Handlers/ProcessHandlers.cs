using System.Collections.Immutable;
using Mixion.Host.Audio;
using Mixion.Host.State;

namespace Mixion.Host.Ipc.Handlers;

/// <summary>
/// Per-process loopback management RPCs (BE-105):
/// <list type="bullet">
///   <item><c>listAudioProcesses</c> — read-only enumeration of currently audio-producing processes. The FE uses this to show an "Add app" sub-picker without forcing a full engine rebuild just to inspect the candidate list.</item>
///   <item><c>removeProcessLoopback</c> — drop a single process-loopback channel from <see cref="MixerState"/> and rebuild the engine without re-discovering it. Useful when the user wants a specific app out of the mixer (or wants to free its WASAPI handle) without touching everything else.</item>
/// </list>
///
/// "Add" intentionally has no dedicated RPC: the existing
/// <c>refreshDevices</c> already opens loopbacks for every audio-producing
/// process, including newly launched ones. A separate
/// <c>addProcessLoopback(name)</c> would do the same engine-rebuild work and
/// add no new value over the broader refresh — we keep the API surface lean.
/// </summary>
public static class ProcessHandlers
{
    public sealed record RemoveParams(string ChannelId);

    public static void Register(JsonRpcDispatcher dispatcher, EngineHost engineHost, EngineFactory engineFactory)
    {
        dispatcher.Register("listAudioProcesses", (_, _, _) =>
        {
            var enumerator = new AudioProcessEnumerator();
            var processes = enumerator.Enumerate();
            // Surface the channel id we WOULD assign so the FE can dedupe
            // against MixerState.Inputs (a process already wired up shows
            // its existing channel id).
            var dto = processes.Select(p => new
            {
                channelId       = $"process:{p.ProcessName}",
                processId       = p.ProcessId,
                processName     = p.ProcessName,
                executablePath  = p.ExecutablePath,
            }).ToArray();
            return Task.FromResult<object?>(new { processes = dto });
        });

        dispatcher.Register("removeProcessLoopback", async (paramsEl, _, _) =>
        {
            var p = JsonRpcDispatcher.RequireParams<RemoveParams>(paramsEl);

            var newState = await engineHost.RebuildAsync(
                engineFactory,
                transform: prev => prev is null ? null : RemoveChannel(prev, p.ChannelId),
                autoDiscoverNewProcesses: false);

            return newState.ToDto();
        });
    }

    /// <summary>
    /// Build a new <see cref="MixerState"/> with the input channel matching
    /// <paramref name="channelId"/> dropped. Matrix shrinks by removing the
    /// corresponding input row. Returns the original state unchanged when
    /// no such channel exists — idempotent so repeated client calls are safe.
    /// </summary>
    private static MixerState RemoveChannel(MixerState state, string channelId)
    {
        var idx = -1;
        for (var i = 0; i < state.Inputs.Length; i++)
        {
            if (state.Inputs[i].Id == channelId) { idx = i; break; }
        }
        if (idx < 0) return state;

        var newInputs = state.Inputs.RemoveAt(idx);
        var newMatrix = ShrinkMatrixRemoveInputRow(state.Matrix, idx);
        return state with { Inputs = newInputs, Matrix = newMatrix };
    }

    /// <summary>
    /// Routing matrix is positional <c>bool[input, output]</c>. Dropping an
    /// input shifts every higher-indexed row up by one; lower-indexed rows
    /// stay put. Output count is unchanged.
    /// </summary>
    private static RoutingMatrix ShrinkMatrixRemoveInputRow(RoutingMatrix matrix, int removedInput)
    {
        if (removedInput < 0 || removedInput >= matrix.Inputs) return matrix;
        var result = new RoutingMatrix(matrix.Inputs - 1, matrix.Outputs);
        for (var i = 0; i < matrix.Inputs; i++)
        {
            if (i == removedInput) continue;
            var newI = i < removedInput ? i : i - 1;
            for (var o = 0; o < matrix.Outputs; o++)
            {
                if (matrix[i, o]) result = result.With(newI, o, true);
            }
        }
        return result;
    }
}
