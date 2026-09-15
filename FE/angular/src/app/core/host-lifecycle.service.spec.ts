import { TestBed } from '@angular/core/testing';

import { CLOSE_TAB_ON_HOST_EXIT, HostLifecycleService } from './host-lifecycle.service';
import { IpcService } from './ipc.service';

describe('HostLifecycleService', () => {
  let closeSpy: jasmine.Spy;

  beforeEach(() => {
    closeSpy = spyOn(window, 'close');
  });

  function setup(closeTabOnHostExit: boolean) {
    TestBed.configureTestingModule({
      providers: [{ provide: CLOSE_TAB_ON_HOST_EXIT, useValue: closeTabOnHostExit }],
    });
    return { service: TestBed.inject(HostLifecycleService), ipc: TestBed.inject(IpcService) };
  }

  it('closes the tab when the host exits', () => {
    const { service, ipc } = setup(true);

    ipc.hostExited.set(true);
    TestBed.flushEffects();

    expect(service.retired()).toBe('host-exited');
    expect(closeSpy).toHaveBeenCalled();
  });

  it('keeps the tab reconnecting when closing on exit is off (ng serve)', () => {
    const { service, ipc } = setup(false);

    ipc.hostExited.set(true);
    TestBed.flushEffects();

    expect(service.retired()).toBeNull();
    expect(closeSpy).not.toHaveBeenCalled();
  });

  it('retires when a newer tab claims the browser', async () => {
    const { service } = setup(true);
    service.claim();

    const newer = new BroadcastChannel('mixion-ui');
    newer.postMessage({ type: 'claim', tabId: 'newer-tab' });
    await waitFor(() => service.retired() !== null);
    newer.close();

    expect(service.retired()).toBe('replaced');
    expect(closeSpy).toHaveBeenCalled();
  });
});

async function waitFor(condition: () => boolean, timeoutMs = 2000): Promise<void> {
  const started = performance.now();
  while (!condition()) {
    if (performance.now() - started > timeoutMs) throw new Error('Condition was never met.');
    await new Promise((resolve) => setTimeout(resolve, 10));
  }
}
