import { TestBed } from '@angular/core/testing';

import { IpcError, IpcService } from './ipc.service';
import { ChannelDto, MixerStateStore } from './mixer-state.store';
import { SlotActionsService } from './slot-actions.service';
import { SlotsStore } from './slots.store';

function channel(id: string): ChannelDto {
  return {
    id,
    name: id,
    gainDb: 0,
    muted: false,
    soloed: false,
    pan: 0,
    gate: null,
    compressor: null,
    eq: null,
    available: true,
  };
}

interface RouteParams {
  input: number;
  output: number;
  enabled: boolean;
}

describe('SlotActionsService', () => {
  let service: SlotActionsService;
  let mixer: MixerStateStore;
  let slots: SlotsStore;
  let call: jasmine.Spy;

  beforeEach(() => {
    call = jasmine.createSpy('call').and.resolveTo({ ok: true });
    TestBed.configureTestingModule({ providers: [{ provide: IpcService, useValue: { call } }] });
    service = TestBed.inject(SlotActionsService);
    mixer   = TestBed.inject(MixerStateStore);
    slots   = TestBed.inject(SlotsStore);

    // chrome → headphones, chrome → speakers and mic → headphones are routed.
    mixer.hydrate({
      inputs:  [channel('process:chrome'), channel('mic')],
      outputs: [channel('headphones'), channel('speakers')],
      matrix:  [[true, true], [true, false]],
    });
    slots.replace(['process:chrome', 'mic'], ['headphones', 'speakers']);
  });

  function slotOf(bus: 'input' | 'output', deviceId: string): string {
    const list = bus === 'input' ? slots.inputs() : slots.outputs();
    return list.find((s) => s.deviceId === deviceId)!.id;
  }

  it('switches off every route to an output whose slot is removed', async () => {
    await service.removeSlot('output', slotOf('output', 'headphones'));

    expect(call.calls.allArgs()).toEqual([
      ['setRoute', { input: 0, output: 0, enabled: false }],
      ['setRoute', { input: 1, output: 0, enabled: false }],
    ]);
    expect(mixer.matrix()).toEqual([[false, true], [false, false]]);
    expect(slots.outputs().map((s) => s.deviceId)).toEqual(['speakers']);
  });

  it('switches off the routes of an input whose slot is cleared', async () => {
    await service.assignDevice('input', slotOf('input', 'process:chrome'), null);

    expect(call.calls.allArgs()).toEqual([
      ['setRoute', { input: 0, output: 0, enabled: false }],
      ['setRoute', { input: 0, output: 1, enabled: false }],
    ]);
    expect(mixer.matrix()).toEqual([[false, false], [true, false]]);
    expect(slots.inputs().map((s) => s.deviceId)).toEqual([null, 'mic']);
  });

  it('only touches the routes of the device that leaves the slot', async () => {
    await service.assignDevice('output', slotOf('output', 'headphones'), 'speakers');

    expect(call.calls.allArgs()).toEqual([
      ['setRoute', { input: 0, output: 0, enabled: false }],
      ['setRoute', { input: 1, output: 0, enabled: false }],
    ]);
    expect(mixer.matrix()).toEqual([[false, true], [false, false]]);
  });

  it('leaves routes alone when the slot keeps its device', async () => {
    await service.assignDevice('output', slotOf('output', 'headphones'), 'headphones');

    expect(call).not.toHaveBeenCalled();
    expect(mixer.matrix()).toEqual([[true, true], [true, false]]);
  });

  it('keeps the routes while another slot still shows the device', async () => {
    slots.replace(['process:chrome', 'mic'], ['headphones', 'headphones', 'speakers']);

    await service.removeSlot('output', slots.outputs()[0].id);

    expect(call).not.toHaveBeenCalled();
    expect(mixer.matrix()[0][0]).toBeTrue();
  });

  it('leaves the slot and the route as they were when the host refuses', async () => {
    call.and.rejectWith(new IpcError(-32000, 'Audio engine not running.'));

    await expectAsync(service.removeSlot('output', slotOf('output', 'headphones'))).toBeRejected();

    expect(call).toHaveBeenCalledTimes(1);
    expect(mixer.matrix()[0][0]).toBeTrue();
    expect(slots.outputs().map((s) => s.deviceId)).toEqual(['headphones', 'speakers']);
  });

  it('stops at the refused route and keeps the ones already switched off', async () => {
    call.and.callFake((_method: string, params: RouteParams) =>
      params.output === 1 ? Promise.reject(new IpcError(-32000, 'refused')) : Promise.resolve({ ok: true }),
    );

    await expectAsync(service.assignDevice('input', slotOf('input', 'process:chrome'), null)).toBeRejected();

    expect(call).toHaveBeenCalledTimes(2);
    expect(mixer.matrix()[0]).toEqual([false, true]);
    expect(slots.inputs()[0].deviceId).toBe('process:chrome');
  });

  it('finds each route again by channel id when the state changes on the way', async () => {
    let first = true;
    call.and.callFake(async () => {
      if (first) {
        first = false;
        // Meanwhile the host's state arrives with a new input listed before mic.
        mixer.replace({
          inputs:  [channel('process:chrome'), channel('line-in'), channel('mic')],
          outputs: [channel('headphones'), channel('speakers')],
          matrix:  [[false, true], [false, false], [true, false]],
        });
      }
      return { ok: true };
    });

    await service.removeSlot('output', slotOf('output', 'headphones'));

    expect(call.calls.allArgs()).toEqual([
      ['setRoute', { input: 0, output: 0, enabled: false }],
      ['setRoute', { input: 2, output: 0, enabled: false }],
    ]);
    expect(mixer.matrix()).toEqual([[false, true], [false, false], [false, false]]);
  });
});
