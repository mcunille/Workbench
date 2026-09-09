import { vi } from 'vitest';
import { ExportMemory } from './exportMemory';
import { prepareExport } from '../../api/export';
import { ApiError } from '../../api/auth';

vi.mock('../../api/export', () => ({ prepareExport: vi.fn() }));
const file = () => ({ blob: new Blob(['complete']), filename: 'records.csv' });
beforeEach(() => {
  vi.mocked(prepareExport).mockReset();
  vi.stubGlobal('URL', Object.assign(URL, {
    createObjectURL: vi.fn(() => 'blob:export'), revokeObjectURL: vi.fn(),
  }));
});
afterEach(() => vi.useRealTimers());

it('requires explicit scope and exposes only a completely prepared result', async () => {
  // GIVEN an unselected export and an unfinished request.
  const memory = new ExportMemory();
  const authLost = vi.fn();
  let finish!: (value: ReturnType<typeof file>) => void;
  vi.mocked(prepareExport).mockReturnValue(new Promise(resolve => { finish = resolve; }));
  // WHEN attempting preparation before selection THEN no request starts.
  await memory.prepare(authLost);
  expect(prepareExport).not.toHaveBeenCalled();
  memory.select('all');
  const pending = memory.prepare(authLost);
  await memory.prepare(authLost);
  expect(prepareExport).toHaveBeenCalledTimes(1);
  expect(memory.getSnapshot().status).toBe('preparing');
  expect(memory.getSnapshot().url).toBeUndefined();
  // WHEN the entire response arrives THEN download becomes available.
  finish(file());
  await pending;
  expect(memory.getSnapshot()).toMatchObject({ status: 'ready', url: 'blob:export', scope: 'all' });
  memory.dispose();
});

it.each(['cancel', 'dispose', 'scope'] as const)('rejects late response after %s', async action => {
  // GIVEN a preparing export whose transport completes even after abort.
  const memory = new ExportMemory();
  let finish!: (value: ReturnType<typeof file>) => void;
  vi.mocked(prepareExport).mockReturnValue(new Promise(resolve => { finish = resolve; }));
  memory.select('active');
  const pending = memory.prepare(vi.fn());
  const signal = vi.mocked(prepareExport).mock.calls[0][1];
  // WHEN cancelled, signed out, or changing identity/scope before completion.
  if (action === 'scope') memory.select('all');
  else memory[action]();
  finish(file());
  await pending;
  // THEN the request is aborted and its late file is never offered.
  expect(signal.aborted).toBe(true);
  expect(memory.getSnapshot().url).toBeUndefined();
  expect(URL.createObjectURL).not.toHaveBeenCalled();
});

it('expires files after ten minutes and disposes object URLs on scope changes', async () => {
  // GIVEN a fully prepared file.
  vi.useFakeTimers();
  vi.mocked(prepareExport).mockResolvedValue(file());
  const memory = new ExportMemory();
  memory.select('active');
  await memory.prepare(vi.fn());
  // WHEN ten minutes pass THEN the private file is released, retaining only scope.
  await vi.advanceTimersByTimeAsync(600_000);
  expect(memory.getSnapshot()).toMatchObject({ status: 'idle', scope: 'active', message: expect.stringMatching(/expired/) });
  expect(memory.getSnapshot().url).toBeUndefined();
  expect(URL.revokeObjectURL).toHaveBeenCalledWith('blob:export');
  await memory.prepare(vi.fn());
  memory.select('all');
  expect(URL.revokeObjectURL).toHaveBeenCalledTimes(2);
  memory.dispose();
});

it.each([204, 422, 429, 503, 401, 403])('reports preparation outcome %s without a downloadable file', async status => {
  // GIVEN an empty collection, preparation failure, or lost session.
  if (status === 204) vi.mocked(prepareExport).mockResolvedValue(null);
  else vi.mocked(prepareExport).mockRejectedValue(new ApiError(status));
  const memory = new ExportMemory();
  const authLost = vi.fn();
  memory.select('all');
  // WHEN preparation finishes THEN the result is explicit and never downloadable.
  await memory.prepare(authLost);
  expect(memory.getSnapshot().url).toBeUndefined();
  expect(memory.getSnapshot().status).toBe(status === 204 ? 'empty' : 'failed');
  expect(memory.getSnapshot().message).toBeTruthy();
  expect(authLost).toHaveBeenCalledTimes(status === 401 || status === 403 ? 1 : 0);
  memory.dispose();
});

it('reports download started without claiming a save, and checks expiry even when the timer is delayed', async () => {
  // GIVEN a ready file and an observer of the visible export state.
  vi.useFakeTimers();
  vi.mocked(prepareExport).mockResolvedValue(file());
  const memory = new ExportMemory();
  const listener = vi.fn();
  const unsubscribe = memory.subscribe(listener);
  expect(memory.startDownload()).toBe(false);
  memory.select('active');
  await memory.prepare(vi.fn());
  // WHEN a download starts THEN the status describes only what the browser requested.
  expect(memory.startDownload()).toBe(true);
  expect(memory.getSnapshot().message).toBe('Download started. Your browser handles saving the CSV.');
  expect(listener).toHaveBeenCalled();
  // WHEN a background tab has delayed its expiry timer THEN activation still refuses expired bytes.
  vi.setSystemTime(Date.now() + 600_000);
  expect(memory.startDownload()).toBe(false);
  expect(memory.getSnapshot().url).toBeUndefined();
  expect(URL.revokeObjectURL).toHaveBeenCalledWith('blob:export');
  unsubscribe();
  listener.mockClear();
  memory.dispose();
  expect(listener).not.toHaveBeenCalled();
});

it('does not suggest search or archived scope reduction for an active-only limit failure', async () => {
  // GIVEN an active-only export too large for the server limits.
  vi.mocked(prepareExport).mockRejectedValue(new ApiError(422));
  const memory = new ExportMemory();
  memory.select('active');
  // WHEN rejected THEN the explanation identifies both supported limits without an ineffective workaround.
  await memory.prepare(vi.fn());
  expect(memory.getSnapshot().message).toBe('The export exceeds the limit of 10,000 records or 32 MiB.');
  memory.dispose();
});
