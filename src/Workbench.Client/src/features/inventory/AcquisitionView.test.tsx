import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { vi } from 'vitest';
import * as api from '../../api/acquisitions';
import * as items from '../../api/items';
import { AcquisitionView } from './AcquisitionView';
vi.mock('../../api/acquisitions', async original => ({ ...await original<typeof import('../../api/acquisitions')>(),
  getAcquisition: vi.fn(), getSharedAcquisition: vi.fn(), getAcquisitionItems: vi.fn(), saveAcquisitionLink: vi.fn(),
}));
vi.mock('../../api/items', async original => ({ ...await original<typeof import('../../api/items')>(),
  getItem: vi.fn(), createItem: vi.fn(), getItems: vi.fn(),
}));
const item = { id: 'stone', name: 'Blue sapphire', notes: null, location: null, photo: null,
  version: 'i1', createdAtUtc: '2026-01-01T00:00:00Z', archivedAtUtc: null };
const acquisition = { id: 'fair', method: 'Purchase', source: 'Autumn fair', year: 2025, month: null, day: null, notes: null, version: 'a1' };
function setup(collectionOrigin?: 'active' | 'archived') {
  return render(<AcquisitionView id="fair" originId="stone" collectionOrigin={collectionOrigin} follow={vi.fn()} onDirtyChange={vi.fn()} onAuthLost={vi.fn()} onItemSaved={vi.fn()} />);
}
beforeEach(() => {
  vi.mocked(api.getSharedAcquisition).mockReset().mockResolvedValue(acquisition);
  vi.mocked(api.getAcquisition).mockReset().mockResolvedValue({ acquisition, itemVersion: item.version });
  vi.mocked(api.getAcquisitionItems).mockReset().mockResolvedValue({ items: [item], nextCursor: null });
  vi.mocked(items.getItem).mockReset().mockResolvedValue(item);
  vi.mocked(items.createItem).mockReset().mockResolvedValue({ ...item, id: 'new', name: 'New sapphire' });
  vi.mocked(items.getItems).mockReset().mockResolvedValue({ items: [item], nextCursor: null });
  vi.mocked(api.saveAcquisitionLink).mockReset();
});
it('shows archived origin membership initially and keeps its acquisition view read-only', async () => {
  // GIVEN an archived origin WHEN opening its shared acquisition THEN include archived pieces without write actions.
  vi.mocked(items.getItem).mockResolvedValue({ ...item, archivedAtUtc: '2026-01-02T00:00:00Z' });
  vi.mocked(api.getAcquisitionItems).mockResolvedValue({ items: [{ ...item, archivedAtUtc: '2026-01-02T00:00:00Z' }], nextCursor: null });
  setup();
  await screen.findByText(/This acquisition view is read-only/);
  expect(screen.getByRole('checkbox', { name: 'Show archived pieces' })).toBeChecked();
  await waitFor(() => expect(api.getAcquisitionItems).toHaveBeenCalledWith('fair', true));
  expect(await screen.findByRole('link', { name: item.name })).toHaveAttribute('href', '/inventory/stone');
  expect(screen.queryByRole('button', { name: /Connect existing piece|Record a new piece|Edit shared acquisition/ })).not.toBeInTheDocument();
});
it.each([
  ['active', '2026-01-02T00:00:00Z', 'Back to collection', '/inventory'],
  ['archived', null, 'Back to archive', '/inventory/archive'],
] as const)('keeps the %s traversal return independent of the current piece archive state', async (collectionOrigin, archivedAtUtc, label, href) => {
  // GIVEN a shared acquisition opened after visiting a sibling or restoring a piece in a retained collection traversal.
  vi.mocked(items.getItem).mockResolvedValue({ ...item, archivedAtUtc });
  setup(collectionOrigin);
  // WHEN the current piece loads THEN its archive state controls read-only behavior while the collection origin controls the return destination.
  await screen.findByText('Autumn fair');
  expect(screen.getByRole('link', { name: label })).toHaveAttribute('href', href);
  expect(screen.getByRole('checkbox', { name: 'Show archived pieces' })).toHaveProperty('checked', Boolean(archivedAtUtc));
  if (archivedAtUtc) expect(screen.queryByRole('button', { name: 'Connect existing piece' })).not.toBeInTheDocument();
  else expect(screen.getByRole('button', { name: 'Connect existing piece' })).toBeVisible();
});
it('paginates active pieces and explicitly includes archives', async () => {
  // GIVEN multiple membership pages.
  vi.mocked(api.getAcquisitionItems).mockResolvedValueOnce({ items: [item], nextCursor: 'page2' })
    .mockResolvedValueOnce({ items: [{ ...item, id: 'sibling', name: 'Green sapphire' }], nextCursor: null })
    .mockResolvedValue({ items: [item], nextCursor: null });
  setup();
  // WHEN loading more THEN retain prior results, without including archived items implicitly.
  fireEvent.click(await screen.findByRole('button', { name: 'Load more associated pieces' }));
  expect(await screen.findByRole('link', { name: 'Green sapphire' })).toBeVisible();
  expect(screen.getByRole('link', { name: item.name })).toBeVisible();
  expect(api.getAcquisitionItems).toHaveBeenCalledWith('fair', false, 'page2');
  fireEvent.click(screen.getByRole('checkbox', { name: 'Show archived pieces' }));
  await waitFor(() => expect(api.getAcquisitionItems).toHaveBeenCalledWith('fair', true));
});
it('records a piece once and retries only its separate uncertain connection', async () => {
  // GIVEN ordinary duplicate-safe manual entry from an acquisition.
  vi.mocked(api.saveAcquisitionLink).mockRejectedValueOnce(new TypeError('Network')).mockResolvedValue({ acquisition, itemVersion: 'i2' });
  vi.mocked(items.getItem).mockImplementation(async id => ({ ...item, id, name: id === 'new' ? 'New sapphire' : item.name }));
  setup(); fireEvent.click(await screen.findByRole('button', { name: 'Record a new piece' }));
  fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'New sapphire' } });
  fireEvent.click(screen.getByRole('button', { name: 'Save item' }));
  // WHEN the connection response fails THEN the saved item remains and creation cannot be retried.
  fireEvent.click(await screen.findByRole('button', { name: 'Save connection' }));
  await screen.findByText(/Piece saved; acquisition connection not confirmed/);
  expect(screen.getByText('(new)')).toBeVisible();
  expect(screen.queryByRole('button', { name: 'Save item' })).not.toBeInTheDocument();
  fireEvent.click(screen.getByRole('button', { name: 'Retry connection save' }));
  await screen.findByRole('button', { name: 'Record a new piece' });
  expect(items.createItem).toHaveBeenCalledTimes(1); expect(api.saveAcquisitionLink).toHaveBeenCalledTimes(2);
  expect(vi.mocked(api.saveAcquisitionLink).mock.calls[0][0]).toBe('new');
});
it('shows current acquisition during existing piece selection and then reviews a replacement', async () => {
  // GIVEN an existing piece from a different event.
  vi.mocked(api.getAcquisition).mockResolvedValue({ acquisition: { ...acquisition, id: 'old', source: 'Earlier fair' }, itemVersion: 'i1' });
  setup(); fireEvent.click(await screen.findByRole('button', { name: 'Connect existing piece' }));
  expect(await screen.findByText(/Current acquisition: Earlier fair/)).toBeVisible();
  // WHEN selecting THEN both old context and intended event are shown before one save.
  fireEvent.click(screen.getByRole('button', { name: 'Select Blue sapphire' }));
  await screen.findByRole('button', { name: 'Save connection' });
  expect(screen.getByText('Earlier fair')).toBeVisible();
  expect(api.saveAcquisitionLink).not.toHaveBeenCalled();
});
