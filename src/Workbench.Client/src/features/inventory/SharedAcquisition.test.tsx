import { fireEvent, render, screen } from '@testing-library/react';
import { vi } from 'vitest';
import * as acquisitions from '../../api/acquisitions';
import { AcquisitionPanel } from './AcquisitionPanel';

vi.mock('../../api/acquisitions', async (original) => ({
  ...await original<typeof import('../../api/acquisitions')>(),
  getAcquisition: vi.fn(),
}));
const item = { id: 'stone', name: 'Blue sapphire', notes: null, location: null, photo: null,
  version: 'item-v1', createdAtUtc: '2026-01-01T00:00:00Z', archivedAtUtc: null };
const acquisition = { id: 'fair', method: 'Purchase', source: 'Autumn fair', year: 2025,
  month: null, day: null, notes: null, version: 'fair-v1' };
function setup(archived = false, viewHref?: string) {
  return render(<AcquisitionPanel item={{ ...item, archivedAtUtc: archived ? '2026-01-02T00:00:00Z' : null }}
    disabled={false} viewHref={viewHref} onEditingChange={vi.fn()} onDirtyChange={vi.fn()} onAuthLost={vi.fn()} onCurrent={vi.fn()} />);
}
it('offers an explicit saved acquisition picker for an unlinked piece', async () => {
  // GIVEN an active unlinked piece.
  vi.mocked(acquisitions.getAcquisition).mockResolvedValue({ acquisition: null, itemVersion: item.version });
  setup();
  // WHEN its context loads THEN connection and creation are distinct choices.
  expect(await screen.findByRole('button', { name: 'Connect to an acquisition' })).toBeEnabled();
  expect(screen.getByRole('button', { name: 'Add acquisition' })).toBeEnabled();
});
it('discloses the scope of shared context edits before saving', async () => {
  // GIVEN a linked piece.
  vi.mocked(acquisitions.getAcquisition).mockResolvedValue({ acquisition, itemVersion: item.version });
  setup();
  // WHEN editing THEN the collector sees that archived pieces share the correction too.
  fireEvent.click(await screen.findByRole('button', { name: 'Edit acquisition' }));
  expect(screen.getByText(/Changes apply to every associated piece, including archived pieces/)).toBeVisible();
});
it('offers archived acquisition navigation without relationship writes', async () => {
  // GIVEN an archived piece with retained acquisition context.
  vi.mocked(acquisitions.getAcquisition).mockResolvedValue({ acquisition, itemVersion: item.version });
  setup(true, '/acquisitions/fair/from/active-sibling');
  // WHEN context loads THEN navigation remains available and changes are absent.
  expect(await screen.findByRole('link', { name: 'View acquisition' })).toHaveAttribute('href', '/acquisitions/fair/from/stone');
  expect(screen.queryByRole('button', { name: /Change acquisition|Remove connection|Edit acquisition/ })).not.toBeInTheDocument();
});
