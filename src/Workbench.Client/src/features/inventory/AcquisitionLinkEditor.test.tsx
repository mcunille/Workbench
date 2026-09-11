import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { vi } from 'vitest';
import * as api from '../../api/acquisitions';
import { getItem } from '../../api/items';
import { AcquisitionLinkEditor } from './AcquisitionLinkEditor';

vi.mock('../../api/acquisitions', async original => ({ ...await original<typeof import('../../api/acquisitions')>(),
  getAcquisition: vi.fn(), getSharedAcquisition: vi.fn(), saveAcquisitionLink: vi.fn(),
}));
vi.mock('../../api/items', async original => ({ ...await original<typeof import('../../api/items')>(), getItem: vi.fn() }));
const item = { id: 'stone', name: 'Blue sapphire', notes: null, location: null, photo: null,
  version: 'i1', createdAtUtc: '2026-01-01T00:00:00Z', archivedAtUtc: null };
const old = { id: 'old', method: 'Gift', source: 'Family gift', year: 1990, month: null, day: null, notes: null, version: 'a1' };
const target = { ...old, id: 'fair', method: 'Purchase', source: 'Autumn fair', version: 'b1' };
function setup(destination: api.Acquisition | null = target, newlySaved = false) {
  const onClose = vi.fn(); const onDirtyChange = vi.fn();
  render(<AcquisitionLinkEditor item={item} initial={{ acquisition: old, itemVersion: item.version }} target={destination}
    newlySaved={newlySaved} onAuthLost={vi.fn()} onDirtyChange={onDirtyChange} onClose={onClose} />);
  return { onClose, onDirtyChange };
}
beforeEach(() => {
  vi.mocked(api.getAcquisition).mockReset().mockResolvedValue({ acquisition: old, itemVersion: 'i2' });
  vi.mocked(api.getSharedAcquisition).mockReset().mockResolvedValue({ ...target, version: 'b2' });
  vi.mocked(api.saveAcquisitionLink).mockReset();
  vi.mocked(getItem).mockReset().mockResolvedValue({ ...item, version: 'i2' });
});
it('reviews both acquisitions then sends one atomic replacement with all expected versions', async () => {
  // GIVEN a piece already connected to a different acquisition.
  vi.mocked(api.saveAcquisitionLink).mockResolvedValue({ acquisition: target, itemVersion: 'i2' });
  const { onClose } = setup();
  expect(screen.getByText('Family gift')).toBeVisible(); expect(screen.getByText('Autumn fair')).toBeVisible();
  // WHEN confirming THEN a single conditional request replaces the connection.
  fireEvent.click(screen.getByRole('button', { name: 'Save connection' }));
  await waitFor(() => expect(onClose).toHaveBeenCalled());
  expect(api.saveAcquisitionLink).toHaveBeenCalledExactlyOnceWith('stone', {
    expectedItemVersion: 'i1', expectedAcquisitionId: 'old', expectedAcquisitionVersion: 'a1',
    targetAcquisitionId: 'fair', targetAcquisitionVersion: 'b1',
  });
});
it('names both records and preserves them when removing the final connection', async () => {
  // GIVEN a removal confirmation WHEN confirming THEN only the relationship is removed.
  vi.mocked(api.saveAcquisitionLink).mockResolvedValue({ acquisition: null, itemVersion: 'i2' });
  const { onClose } = setup(null);
  expect(screen.getByText(/Remove the connection between Blue sapphire and Family gift/)).toBeVisible();
  expect(screen.getByText(/Both the piece and acquisition remain saved/)).toBeVisible();
  fireEvent.click(screen.getByRole('button', { name: 'Remove connection' }));
  await waitFor(() => expect(onClose).toHaveBeenCalled());
  expect(api.saveAcquisitionLink).toHaveBeenCalledWith('stone', expect.objectContaining({ targetAcquisitionId: null, targetAcquisitionVersion: null }));
});
it('keeps an uncertain request exact, preserves a saved piece, and requires review before fresh-token saving', async () => {
  // GIVEN a saved new piece whose link response was lost after a possible commit.
  vi.mocked(api.saveAcquisitionLink).mockRejectedValueOnce(new TypeError('Network')).mockRejectedValueOnce(new api.AcquisitionConflictError());
  vi.mocked(api.getAcquisition).mockResolvedValue({ acquisition: target, itemVersion: 'i2' });
  setup(target, true);
  fireEvent.click(screen.getByRole('button', { name: 'Save connection' }));
  await screen.findByText(/Piece saved; acquisition connection not confirmed/);
  // WHEN retrying THEN exact request tokens are retained; equal current state is not called success.
  fireEvent.click(screen.getByRole('button', { name: 'Retry connection save' }));
  await screen.findByRole('button', { name: 'Use saved connection' });
  expect(api.saveAcquisitionLink).toHaveBeenCalledTimes(2);
  expect(vi.mocked(api.saveAcquisitionLink).mock.calls[1]).toEqual(vi.mocked(api.saveAcquisitionLink).mock.calls[0]);
  expect(screen.getByText(/does not establish whether the earlier request saved/)).toBeVisible();
  expect(screen.getByRole('heading', { name: 'Review current connection' })).toHaveFocus();
  expect(screen.queryByRole('button', { name: 'Save connection' })).not.toBeInTheDocument();
  // WHEN explicitly reviewing fresh values THEN saving still requires a separate click.
  fireEvent.click(screen.getByRole('button', { name: 'Review intended connection with current versions' }));
  expect(api.saveAcquisitionLink).toHaveBeenCalledTimes(2);
  vi.mocked(api.saveAcquisitionLink).mockResolvedValue({ acquisition: target, itemVersion: 'i3' });
  fireEvent.click(screen.getByRole('button', { name: 'Save connection' }));
  await waitFor(() => expect(api.saveAcquisitionLink).toHaveBeenCalledTimes(3));
  expect(vi.mocked(api.saveAcquisitionLink).mock.calls[2][1]).toMatchObject({ expectedItemVersion: 'i2', expectedAcquisitionId: 'fair', targetAcquisitionVersion: 'b2' });
});
it('keeps the intended connection after failed reconciliation and blocks writes for a newly archived piece', async () => {
  // GIVEN a stale command and a failed current-state read.
  vi.mocked(api.saveAcquisitionLink).mockRejectedValue(new api.AcquisitionConflictError());
  vi.mocked(api.getAcquisition).mockRejectedValueOnce(new TypeError('Network'));
  setup(); fireEvent.click(screen.getByRole('button', { name: 'Save connection' }));
  await screen.findByText(/could not load the current connection/);
  expect(screen.getByText('Autumn fair')).toBeVisible();
  expect(screen.queryByText('No acquisition connected.')).not.toBeInTheDocument();
  // WHEN the retry reports archive THEN no save with fresh tokens is available.
  vi.mocked(getItem).mockResolvedValue({ ...item, archivedAtUtc: '2026-01-02T00:00:00Z' });
  fireEvent.click(screen.getByRole('button', { name: 'Retry loading current connection' }));
  await screen.findByText(/This piece is archived and read-only/);
  expect(screen.queryByRole('button', { name: 'Review intended connection with current versions' })).not.toBeInTheDocument();
});
