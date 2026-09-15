import { TestBed } from '@angular/core/testing';

import { ChannelDto, MixerStateStore } from './mixer-state.store';

function channel(id: string, patch: Partial<ChannelDto> = {}): ChannelDto {
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
    ...patch,
  };
}

describe('MixerStateStore.applyTopology', () => {
  let store: MixerStateStore;

  beforeEach(() => {
    TestBed.configureTestingModule({});
    store = TestBed.inject(MixerStateStore);
  });

  it('takes availability and names from the host but keeps local settings and routes', () => {
    store.hydrate({
      inputs: [channel('mic', { gainDb: -6, muted: true }), channel('process:chrome', { available: false })],
      outputs: [channel('out')],
      matrix: [[true], [false]],
    });

    store.applyTopology({
      inputs: [channel('mic'), channel('process:chrome', { name: 'chrome (app)' })],
      outputs: [channel('out')],
      matrix: [[false], [false]],
    });

    const [mic, chrome] = store.inputs();
    expect(mic.gainDb).toBe(-6);
    expect(mic.muted).toBeTrue();
    expect(chrome.available).toBeTrue();
    expect(chrome.name).toBe('chrome (app)');
    expect(store.matrix()).toEqual([[true], [false]]);
  });

  it('appends channels the host attached, unrouted', () => {
    store.hydrate({ inputs: [channel('mic')], outputs: [channel('out')], matrix: [[true]] });

    store.applyTopology({
      inputs: [channel('mic'), channel('process:spotify')],
      outputs: [channel('out'), channel('headset')],
      matrix: [[false, false], [false, false]],
    });

    expect(store.inputs().map((c) => c.id)).toEqual(['mic', 'process:spotify']);
    expect(store.outputs().map((c) => c.id)).toEqual(['out', 'headset']);
    expect(store.matrix()).toEqual([[true, false], [false, false]]);
  });

  it('hydrates from the push when nothing is loaded yet', () => {
    store.applyTopology({ inputs: [channel('mic')], outputs: [channel('out')], matrix: [[false]] });

    expect(store.inputs().length).toBe(1);
    expect(store.outputs().length).toBe(1);
  });
});
