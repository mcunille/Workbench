import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { vi } from 'vitest';
import * as documents from '../../api/acquisitionDocuments';
import * as acquisitions from '../../api/acquisitions';
import { getItem } from '../../api/items';
import { AcquisitionPanel } from './AcquisitionPanel';
vi.mock('../../api/acquisitionDocuments', async original => ({ ...await original<typeof import('../../api/acquisitionDocuments')>(), getDocuments: vi.fn(), changeDocument: vi.fn() }));
vi.mock('../../api/acquisitions', async original => ({ ...await original<typeof import('../../api/acquisitions')>(), getAcquisition: vi.fn() }));
vi.mock('../../api/items', async original => ({ ...await original<typeof import('../../api/items')>(), getItem: vi.fn() }));
const item = { id: 'stone', name: 'Original name', notes: null, location: null, photo: null, version: 'i1', createdAtUtc: '', archivedAtUtc: null };
const acquisition = { id: 'fair', method: 'Purchase', source: 'Original source', year: null, month: null, day: null, notes: null, version: 'a1' };
it('refreshes full facts after document completion before enabling another editor', async () => {
  // GIVEN remote fact changes that preceded a fresh document-list version snapshot.
  vi.mocked(acquisitions.getAcquisition).mockResolvedValueOnce({ acquisition, itemVersion: 'i1' }).mockResolvedValue({ acquisition: { ...acquisition, source: 'Remote source', version: 'a3' }, itemVersion: 'i3' });
  const currentItem = { ...item, name: 'Remote name', version: 'i3' };
  vi.mocked(getItem).mockResolvedValue(currentItem);
  vi.mocked(documents.getDocuments).mockResolvedValue({ documents: [{ id: 'doc', label: 'Receipt', mediaType: 'application/pdf', extension: 'pdf', length: 1, createdAtUtc: '', version: 'd1', unavailable: false }], itemVersion: 'i2', acquisitionVersion: 'a2' });
  vi.mocked(documents.changeDocument).mockResolvedValue({ requestId: 'r', documentId: 'doc', state: 'Completed', itemVersion: 'i3', acquisitionVersion: 'a3' });
  const onCurrent = vi.fn();
  render(<AcquisitionPanel item={item} disabled={false} onEditingChange={vi.fn()} onDirtyChange={vi.fn()} onAuthLost={vi.fn()} onCurrent={onCurrent} />);
  // WHEN renaming a document THEN the full current item and acquisition facts replace stale editor values.
  fireEvent.click(await screen.findByRole('button', { name: 'Rename Receipt' }));
  fireEvent.click(screen.getByRole('button', { name: 'Save label' }));
  await waitFor(() => expect(onCurrent).toHaveBeenCalledWith('i3', currentItem));
  expect(screen.getByText('Remote source')).toBeVisible();
  await waitFor(() => expect(screen.getByRole('button', { name: 'Edit acquisition' })).toBeEnabled());
  fireEvent.click(screen.getByRole('button', { name: 'Edit acquisition' }));
  expect(screen.getByLabelText('Purchased from (optional)')).toHaveValue('Remote source');
});
