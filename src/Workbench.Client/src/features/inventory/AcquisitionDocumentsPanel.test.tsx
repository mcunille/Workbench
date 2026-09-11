import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { vi } from 'vitest';
import { ApiError } from '../../api/auth';
import * as api from '../../api/acquisitionDocuments';
import { AcquisitionDocumentsPanel } from './AcquisitionDocumentsPanel';

vi.mock('../../api/acquisitionDocuments', async original => ({ ...await original<typeof import('../../api/acquisitionDocuments')>(), getDocuments: vi.fn(), uploadDocument: vi.fn(), changeDocument: vi.fn(), getDocumentOperation: vi.fn(), downloadDocument: vi.fn() }));
const document = { id: 'doc', label: 'Receipt', mediaType: 'application/pdf', extension: '.pdf', length: 123, createdAtUtc: '2026-09-11T00:00:00Z', version: 'd1', unavailable: false };
const listing = { documents: [document], itemVersion: 'i1', acquisitionVersion: 'a1' };
const done = { requestId: 'request', state: 'Completed', documentId: 'doc', itemVersion: 'i2', acquisitionVersion: 'a2' };
function setup(archived = false) {
  const props = { itemId: 'item', acquisitionId: 'acq', itemVersion: 'i1', acquisitionVersion: 'a1', archived, disabled: false, onDirtyChange: vi.fn(), onEditingChange: vi.fn(), onAuthLost: vi.fn(), onCurrent: vi.fn() };
  return { ...render(<AcquisitionDocumentsPanel {...props} />), props };
}
beforeEach(() => { vi.resetAllMocks(); vi.mocked(api.getDocuments).mockResolvedValue(listing); });
it('lists truthful metadata and permits archived downloads without mutation controls', async () => {
  // GIVEN an archived item with acquisition paperwork.
  setup(true);
  // WHEN its documents load THEN the exact bytes and download remain available.
  await screen.findByText('Receipt');
  expect(screen.getByText(/123 bytes/)).toBeVisible();
  expect(screen.queryByRole('button', { name: 'Add document' })).not.toBeInTheDocument();
  fireEvent.click(screen.getByRole('button', { name: 'Download Receipt' }));
  await waitFor(() => expect(api.downloadDocument).toHaveBeenCalledWith('item', 'acq', document));
});
it('preserves the exact upload and resolves status before retrying an uncertain result', async () => {
  // GIVEN an upload whose response was lost.
  vi.mocked(api.uploadDocument).mockRejectedValueOnce(new TypeError('network')).mockResolvedValueOnce(done);
  vi.mocked(api.getDocumentOperation).mockResolvedValue({ ...done, state: 'Pending' });
  const { props } = setup();
  fireEvent.click(await screen.findByRole('button', { name: 'Add document' }));
  const file = new File(['paperwork'], 'receipt.pdf', { type: 'application/pdf' });
  fireEvent.change(screen.getByLabelText('Document label'), { target: { value: ' Shop receipt ' } });
  fireEvent.change(screen.getByLabelText('Choose document'), { target: { files: [file] } });
  // WHEN sending and retrying THEN the original command and file are retained.
  fireEvent.submit(screen.getByRole('button', { name: 'Upload document' }).closest('form')!);
  fireEvent.click(await screen.findByRole('button', { name: 'Check and retry' }));
  await waitFor(() => expect(api.uploadDocument).toHaveBeenCalledTimes(2));
  expect(api.uploadDocument).toHaveBeenNthCalledWith(2, ...vi.mocked(api.uploadDocument).mock.calls[0]);
  expect(api.getDocumentOperation).toHaveBeenCalled();
  await waitFor(() => expect(props.onCurrent).toHaveBeenCalledWith('i2', 'a2'));
});
it('keeps load failure distinct from empty and clears private drafts on authentication loss', async () => {
  // GIVEN an unavailable document list.
  vi.mocked(api.getDocuments).mockRejectedValueOnce(new ApiError(503));
  const { props } = setup();
  expect(await screen.findByRole('button', { name: 'Retry loading documents' })).toBeVisible();
  expect(screen.queryByText('No documents yet.')).not.toBeInTheDocument();
  // WHEN authentication expires during upload THEN clear the private editor.
  fireEvent.click(screen.getByRole('button', { name: 'Retry loading documents' }));
  fireEvent.click(await screen.findByRole('button', { name: 'Add document' }));
  fireEvent.change(screen.getByLabelText('Document label'), { target: { value: 'Private' } });
  fireEvent.change(screen.getByLabelText('Choose document'), { target: { files: [new File(['x'], 'x.pdf')] } });
  vi.mocked(api.uploadDocument).mockRejectedValue(new ApiError(401));
  fireEvent.submit(screen.getByRole('button', { name: 'Upload document' }).closest('form')!);
  await waitFor(() => expect(props.onAuthLost).toHaveBeenCalled());
  expect(screen.queryByLabelText('Document label')).not.toBeInTheDocument();
});

it('checks completed operations without resending and warns before discarding a draft', async () => {
  // GIVEN an uncertain rename that already committed on the server.
  vi.mocked(api.changeDocument).mockRejectedValue(new TypeError('lost'));
  vi.mocked(api.getDocumentOperation).mockResolvedValue(done);
  const { props } = setup();
  fireEvent.click(await screen.findByRole('button', { name: 'Rename Receipt' }));
  fireEvent.change(screen.getByLabelText('Document label'), { target: { value: 'Correct label' } });
  // WHEN resolving THEN no duplicate mutation is sent and versions are refreshed.
  fireEvent.click(screen.getByRole('button', { name: 'Save label' }));
  fireEvent.click(await screen.findByRole('button', { name: 'Check and retry' }));
  await waitFor(() => expect(props.onCurrent).toHaveBeenCalledWith('i2', 'a2'));
  expect(api.changeDocument).toHaveBeenCalledTimes(1);
  fireEvent.click(screen.getByRole('button', { name: 'Add document' }));
  fireEvent.change(screen.getByLabelText('Document label'), { target: { value: 'Keep me' } });
  vi.spyOn(window, 'confirm').mockReturnValue(false);
  fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
  expect(screen.getByLabelText('Document label')).toHaveValue('Keep me');
});
it('requires explicit shared removal and freezes a conflict without adopting new versions', async () => {
  // GIVEN a stale document removal.
  vi.mocked(api.changeDocument).mockRejectedValue(new ApiError(409));
  setup();
  fireEvent.click(await screen.findByRole('button', { name: 'Remove Receipt' }));
  expect(api.changeDocument).not.toHaveBeenCalled();
  expect(screen.getByText(/from all linked pieces/)).toBeVisible();
  // WHEN confirmed THEN a conflict has no implicit retry.
  fireEvent.click(screen.getByRole('button', { name: 'Confirm removal' }));
  expect(await screen.findByRole('button', { name: 'Discard request and reload documents' })).toBeVisible();
  expect(api.changeDocument).toHaveBeenCalledWith('item', 'acq', 'doc', expect.objectContaining({ expectedDocumentVersion: 'd1', label: null }), true);
  expect(screen.queryByRole('button', { name: 'Check and retry' })).not.toBeInTheDocument();
});
it('retries downloads independently and distinguishes known unavailable files', async () => {
  // GIVEN a download that failed transiently.
  vi.mocked(api.downloadDocument).mockRejectedValueOnce(new ApiError(503)).mockResolvedValueOnce();
  setup(true);
  fireEvent.click(await screen.findByRole('button', { name: 'Download Receipt' }));
  // WHEN retrying THEN only the read operation repeats.
  fireEvent.click(await screen.findByRole('button', { name: 'Retry download' }));
  await waitFor(() => expect(api.downloadDocument).toHaveBeenCalledTimes(2));
  expect(api.uploadDocument).not.toHaveBeenCalled();
});
it('ignores a late mutation response after leaving the private context', async () => {
  // GIVEN a document rename still awaiting a response.
  let finish!: (value: typeof done) => void;
  vi.mocked(api.changeDocument).mockReturnValue(new Promise(resolve => { finish = resolve; }));
  const { unmount, props } = setup();
  fireEvent.click(await screen.findByRole('button', { name: 'Rename Receipt' }));
  fireEvent.click(screen.getByRole('button', { name: 'Save label' }));
  // WHEN the context unmounts before completion THEN its late response cannot change the next context.
  unmount(); props.onCurrent.mockClear();
  finish(done);
  await new Promise(resolve => setTimeout(resolve, 0));
  expect(props.onCurrent).not.toHaveBeenCalled();
});
it('keeps a rejected file draft editable with the server validation message', async () => {
  // GIVEN server content validation rejects an unsafe file.
  const { ItemValidationError } = await import('../../api/items');
  vi.mocked(api.uploadDocument).mockRejectedValue(new ItemValidationError({ document: ['This PDF contains unsupported actions.'] }));
  setup();
  fireEvent.click(await screen.findByRole('button', { name: 'Add document' }));
  fireEvent.change(screen.getByLabelText('Document label'), { target: { value: 'Receipt' } });
  fireEvent.change(screen.getByLabelText('Choose document'), { target: { files: [new File(['x'], 'x.pdf')] } });
  // WHEN rejected THEN a replacement file can be selected without retrying an uncertain command.
  fireEvent.submit(screen.getByRole('button', { name: 'Upload document' }).closest('form')!);
  await screen.findByText('This PDF contains unsupported actions.');
  expect(screen.getByLabelText('Choose document')).toBeEnabled();
  expect(screen.getByLabelText('Document label')).toHaveValue('Receipt');
  expect(screen.queryByRole('button', { name: 'Check and retry' })).not.toBeInTheDocument();
});
it('retains the frozen draft until current facts can be reloaded after completion', async () => {
  // GIVEN completion is confirmed but reloading the editable context fails.
  vi.mocked(api.changeDocument).mockResolvedValue(done);
  vi.mocked(api.getDocumentOperation).mockResolvedValue(done);
  const { props } = setup();
  props.onCurrent.mockRejectedValueOnce(new Error('reload unavailable')).mockResolvedValueOnce(undefined);
  fireEvent.click(await screen.findByRole('button', { name: 'Rename Receipt' }));
  fireEvent.click(screen.getByRole('button', { name: 'Save label' }));
  // WHEN retrying the current-state read THEN keep the draft guarded until the read succeeds.
  await screen.findByRole('button', { name: 'Check and retry' });
  expect(screen.getByLabelText('Document label')).toBeDisabled();
  expect(screen.getByLabelText('Document label')).toHaveValue('Receipt');
  fireEvent.click(screen.getByRole('button', { name: 'Check and retry' }));
  await waitFor(() => expect(screen.queryByLabelText('Document label')).not.toBeInTheDocument());
  expect(api.changeDocument).toHaveBeenCalledTimes(1);
});

it('shows pending-capacity guidance and retains a conflict draft if reload fails', async () => {
  // GIVEN visible documents do not include a pending reservation that filled capacity.
  vi.mocked(api.uploadDocument).mockRejectedValue(new api.DocumentConflictError('This acquisition already has 20 current or pending documents. Remove a document before uploading another.'));
  setup();
  fireEvent.click(await screen.findByRole('button', { name: 'Add document' }));
  fireEvent.change(screen.getByLabelText('Document label'), { target: { value: 'Keep receipt' } });
  fireEvent.change(screen.getByLabelText('Choose document'), { target: { files: [new File(['x'], 'x.pdf')] } });
  // WHEN rejected THEN capacity guidance is explicit, and failed reload does not discard private input.
  fireEvent.submit(screen.getByRole('button', { name: 'Upload document' }).closest('form')!);
  await screen.findByText(/20 current or pending documents/);
  vi.mocked(api.getDocuments).mockRejectedValueOnce(new ApiError(503));
  fireEvent.click(screen.getByRole('button', { name: 'Discard request and reload documents' }));
  await screen.findByText(/Your draft is still here/);
  expect(screen.getByLabelText('Document label')).toHaveValue('Keep receipt');
  expect(screen.getByLabelText('Document label')).toBeDisabled();
});
