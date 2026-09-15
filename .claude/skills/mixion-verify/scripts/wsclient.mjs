// Smoke-test client: connects like the SPA and logs what the host pushes.
// Usage: node wsclient.mjs <port>
const port = process.argv[2] ?? '54917';
const base = `http://127.0.0.1:${port}`;
const t0 = Date.now();
const log = (...args) => console.log(`[${((Date.now() - t0) / 1000).toFixed(1)}s]`, ...args);
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

for (let i = 0; i < 240; i++) {
  try {
    const r = await fetch(`${base}/api/health`);
    if (r.ok) break;
  } catch { /* host not up yet */ }
  await sleep(250);
}

const session = await (await fetch(`${base}/api/session`)).json();
log(`session: ${session.mixerStateInit.inputs.length} inputs, ${session.mixerStateInit.outputs.length} outputs`);

const apps = (state) =>
  state.inputs
    .filter((c) => c.id.startsWith('process:'))
    .map((c) => `${c.id}=${c.available ? 'up' : 'down'}`)
    .join(' ') || '(no apps)';

const ws = new WebSocket(`ws://127.0.0.1:${port}/ws?token=${session.token}`);
ws.binaryType = 'arraybuffer';

let meterFrames = 0;
let meterSides = 0;
let nextId = 1;
const pending = new Map();
const call = (method, params) =>
  new Promise((resolve) => {
    const id = nextId++;
    pending.set(id, resolve);
    ws.send(JSON.stringify({ jsonrpc: '2.0', id, method, params }));
  });

ws.addEventListener('message', (ev) => {
  if (typeof ev.data !== 'string') {
    const view = new DataView(ev.data);
    if (view.getUint8(0) === 0x4d) {
      meterFrames++;
      meterSides = view.getUint32(8, true);
    }
    return;
  }
  const msg = JSON.parse(ev.data);
  if (typeof msg.id === 'number') {
    pending.get(msg.id)?.(msg);
    pending.delete(msg.id);
    return;
  }
  if (msg.method === 'stateChanged') log(`stateChanged: ${msg.params.inputs.length} inputs; ${apps(msg.params)}`);
  else log(`notification: ${msg.method}`);
});

ws.addEventListener('close', (ev) => {
  log(`socket closed: code=${ev.code} reason=${ev.reason} meterFrames=${meterFrames} sides=${meterSides}`);
  process.exit(0);
});

await new Promise((resolve) => ws.addEventListener('open', resolve, { once: true }));
log('connected');
const state = await call('getState');
log(`getState: ${state.result.inputs.length} inputs; ${apps(state.result)}`);
ws.send(JSON.stringify({ jsonrpc: '2.0', method: 'subscribe' }));
setInterval(() => log(`meters: frames=${meterFrames} sides=${meterSides}`), 5000);
