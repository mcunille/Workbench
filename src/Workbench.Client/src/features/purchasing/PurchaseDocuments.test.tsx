import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { vi } from 'vitest';
import { PurchaseDocuments } from './PurchaseDocuments';
import * as api from '../../api/purchaseOrderDocuments';
import { ApiError } from '../../api/auth';
import { ItemValidationError } from '../../api/items';

vi.mock('../../api/purchaseOrderDocuments', async original => ({ ...await original<typeof import('../../api/purchaseOrderDocuments')>(), getPurchaseDocuments: vi.fn(), uploadPurchaseDocument: vi.fn(), changePurchaseDocument: vi.fn(), getPurchaseDocumentOperation: vi.fn(), downloadPurchaseDocument: vi.fn() }));
const document = { id: 'file-1', label: 'Supplier invoice', mediaType: 'application/pdf', extension: 'pdf', length: 256, createdAtUtc: '2026-09-18T00:00:00Z', version: 'd1', unavailable: false };
const completed = { requestId: 'request', state: 'Completed', documentId: 'file-1', orderVersion: 'v2' };
const pdf = (name = 'supplier.pdf') => new File(['%PDF-example'], name, { type: 'application/pdf' });
const props = () => ({ orderId: 'order', onAuthLost: vi.fn(), onCurrent: vi.fn().mockResolvedValue(undefined), onStateChange: vi.fn() });
beforeEach(() => {
  vi.resetAllMocks();
  vi.mocked(api.getPurchaseDocuments).mockResolvedValue({ documents: [], orderVersion: 'v1' });
  vi.mocked(api.uploadPurchaseDocument).mockResolvedValue(completed);
  vi.mocked(api.changePurchaseDocument).mockResolvedValue(completed);
});
async function select(files: File[]) {
  fireEvent.click(await screen.findByRole('button', { name: 'Add invoice files' }));
  fireEvent.change(screen.getByLabelText('Choose files'), { target: { files } });
}

it('uploads each selected file with its label and the refreshed purchase version', async () => {
  // GIVEN two PDFs and a new PO version after the first upload.
  const callbacks = props(); render(<PurchaseDocuments {...callbacks} />);
  await select([pdf('one.pdf'), pdf('two.pdf')]);
  vi.mocked(api.getPurchaseDocuments).mockResolvedValue({ documents: [document], orderVersion: 'v2' });
  fireEvent.change(screen.getByLabelText('File label 2'), { target: { value: 'Freight invoice' } });
  // WHEN the owner uploads THEN each file is a distinct sequential operation.
  fireEvent.click(screen.getByRole('button', { name: 'Upload files' }));
  await screen.findByRole('button', { name: 'Done' });
  const calls = vi.mocked(api.uploadPurchaseDocument).mock.calls;
  expect(calls).toHaveLength(2);
  expect(calls[0][1]).toMatchObject({ label: 'one.pdf', expectedOrderVersion: 'v1' });
  expect(calls[1][1]).toMatchObject({ label: 'Freight invoice', expectedOrderVersion: 'v2' });
  expect(calls[0][1].requestId).not.toBe(calls[1][1].requestId);
  expect(callbacks.onCurrent).toHaveBeenCalledTimes(2);
  fireEvent.click(screen.getByRole('button', { name: 'Done' }));
  await waitFor(() => expect(callbacks.onStateChange).toHaveBeenLastCalledWith(false, false, false));
});

it('resolves a lost second response without re-uploading either confirmed file', async () => {
  // GIVEN one confirmed upload followed by an uncertain second upload.
  vi.mocked(api.uploadPurchaseDocument).mockResolvedValueOnce(completed).mockRejectedValueOnce(new TypeError('offline'));
  render(<PurchaseDocuments {...props()} />); await select([pdf('one.pdf'), pdf('two.pdf')]);
  fireEvent.click(screen.getByRole('button', { name: 'Upload files' }));
  await screen.findByRole('button', { name: 'Check and retry file' });
  expect(screen.getByLabelText('File label 2')).toBeDisabled();
  // WHEN status confirms the second write THEN recovery performs no new upload.
  vi.mocked(api.getPurchaseDocumentOperation).mockResolvedValue(completed);
  fireEvent.click(screen.getByRole('button', { name: 'Check and retry file' }));
  await screen.findByRole('button', { name: 'Done' });
  expect(api.uploadPurchaseDocument).toHaveBeenCalledTimes(2);
});

it('retries an absent uncertain operation with the exact original payload', async () => {
  // GIVEN no durable operation was found after a lost upload response.
  vi.mocked(api.uploadPurchaseDocument).mockRejectedValueOnce(new TypeError('offline')).mockResolvedValue(completed);
  vi.mocked(api.getPurchaseDocumentOperation).mockRejectedValue(new ApiError(404));
  render(<PurchaseDocuments {...props()} />); await select([pdf()]);
  fireEvent.click(screen.getByRole('button', { name: 'Upload files' }));
  // WHEN checking and retrying THEN request identity, bytes, label and expected version are unchanged.
  fireEvent.click(await screen.findByRole('button', { name: 'Check and retry file' }));
  await screen.findByRole('button', { name: 'Done' });
  expect(vi.mocked(api.uploadPurchaseDocument).mock.calls[1]).toEqual(vi.mocked(api.uploadPurchaseDocument).mock.calls[0]);
});

it('retries only the read after a confirmed upload and failed purchase refresh', async () => {
  // GIVEN an upload succeeds but refreshing the current purchase fails.
  const callbacks = props(); callbacks.onCurrent.mockRejectedValueOnce(new TypeError('offline')).mockResolvedValue(undefined);
  render(<PurchaseDocuments {...callbacks} />); await select([pdf()]);
  fireEvent.click(screen.getByRole('button', { name: 'Upload files' }));
  // WHEN retrying current details THEN the saved file is not submitted again.
  fireEvent.click(await screen.findByRole('button', { name: 'Retry loading saved files' }));
  await screen.findByRole('button', { name: 'Done' });
  expect(api.uploadPurchaseDocument).toHaveBeenCalledTimes(1);
  expect(api.getPurchaseDocumentOperation).not.toHaveBeenCalled();
});

it('keeps remaining files after validation and permits removing the rejected file', async () => {
  // GIVEN an invalid first PDF and a valid second PDF.
  vi.mocked(api.uploadPurchaseDocument).mockRejectedValueOnce(new ItemValidationError({ document: ['Encrypted PDF documents are not supported.'] })).mockResolvedValue(completed);
  render(<PurchaseDocuments {...props()} />); await select([pdf('encrypted.pdf'), pdf('valid.pdf')]);
  fireEvent.click(screen.getByRole('button', { name: 'Upload files' }));
  await waitFor(() => expect(screen.getByLabelText('File label 1')).toBeEnabled());
  expect(screen.getByLabelText('File label 2')).toHaveValue('valid.pdf');
  // WHEN removing the rejected selection THEN the remaining upload stays available.
  fireEvent.click(screen.getByRole('button', { name: 'Remove selected file 1' }));
  fireEvent.click(screen.getByRole('button', { name: 'Upload files' }));
  await screen.findByRole('button', { name: 'Done' });
  expect(vi.mocked(api.uploadPurchaseDocument).mock.calls[1][1].file.name).toBe('valid.pdf');
});

it('requires review and explicit resubmission after a conflict while preserving the selection', async () => {
  // GIVEN the PO was changed by another session.
  vi.mocked(api.uploadPurchaseDocument).mockRejectedValueOnce(new api.PurchaseDocumentConflict('The purchase changed.')).mockResolvedValue(completed);
  render(<PurchaseDocuments {...props()} />); await select([pdf()]);
  fireEvent.click(screen.getByRole('button', { name: 'Upload files' }));
  await screen.findByRole('button', { name: 'Review current files' });
  vi.mocked(api.getPurchaseDocuments).mockResolvedValue({ documents: [], orderVersion: 'v3' });
  // WHEN reviewing current files THEN nothing is silently resubmitted.
  fireEvent.click(screen.getByRole('button', { name: 'Review current files' }));
  await waitFor(() => expect(screen.getByRole('button', { name: 'Upload files' })).toBeEnabled());
  expect(api.uploadPurchaseDocument).toHaveBeenCalledTimes(1);
  expect(screen.getByLabelText('File label 1')).toHaveValue('supplier.pdf');
  fireEvent.click(screen.getByRole('button', { name: 'Upload files' }));
  await screen.findByRole('button', { name: 'Done' });
  const calls = vi.mocked(api.uploadPurchaseDocument).mock.calls;
  expect(calls[1][1].expectedOrderVersion).toBe('v3');
  expect(calls[1][1].requestId).not.toBe(calls[0][1].requestId);
});

it('renames and confirms removal using document and purchase versions', async () => {
  // GIVEN an attached file.
  vi.mocked(api.getPurchaseDocuments).mockResolvedValue({ documents: [document], orderVersion: 'v1' });
  render(<PurchaseDocuments {...props()} />);
  fireEvent.click(await screen.findByRole('button', { name: 'Rename Supplier invoice' }));
  fireEvent.change(screen.getByLabelText('File label'), { target: { value: 'Corrected label' } });
  fireEvent.click(screen.getByRole('button', { name: 'Save label' }));
  await screen.findByRole('button', { name: 'Add invoice files' });
  expect(api.changePurchaseDocument).toHaveBeenCalledWith('order', 'file-1', expect.objectContaining({ expectedOrderVersion: 'v1', expectedDocumentVersion: 'd1', label: 'Corrected label' }), false);
  // WHEN removal is opened THEN nothing is removed until confirmation.
  fireEvent.click(screen.getByRole('button', { name: 'Remove Supplier invoice' }));
  expect(api.changePurchaseDocument).toHaveBeenCalledTimes(1);
  fireEvent.click(screen.getByRole('button', { name: 'Confirm removal' }));
  await screen.findByRole('button', { name: 'Add invoice files' });
  expect(api.changePurchaseDocument).toHaveBeenLastCalledWith('order', 'file-1', expect.objectContaining({ expectedDocumentVersion: 'd1', label: null }), true);
});

it('clears private selection on authentication loss and does not continue the queue', async () => {
  // GIVEN authentication is lost during the first of two uploads.
  const callbacks = props(); vi.mocked(api.uploadPurchaseDocument).mockRejectedValue(new ApiError(401));
  render(<PurchaseDocuments {...callbacks} />); await select([pdf('private.pdf'), pdf('other.pdf')]);
  fireEvent.click(screen.getByRole('button', { name: 'Upload files' }));
  // THEN private draft state is cleared and no second upload starts.
  await waitFor(() => expect(callbacks.onAuthLost).toHaveBeenCalledOnce());
  expect(screen.queryByDisplayValue('private.pdf')).not.toBeInTheDocument();
  expect(api.uploadPurchaseDocument).toHaveBeenCalledTimes(1);
});

it('keeps unavailable-file metadata and offers a read-only retry on download failure', async () => {
  // GIVEN one recovery-unavailable file and one currently available file.
  vi.mocked(api.getPurchaseDocuments).mockResolvedValue({ documents: [document, { ...document, id: 'lost', label: 'Unavailable invoice', unavailable: true }], orderVersion: 'v1' });
  vi.mocked(api.downloadPurchaseDocument).mockRejectedValueOnce(new ApiError(503)).mockResolvedValue(undefined);
  render(<PurchaseDocuments {...props()} />);
  expect(await screen.findByRole('button', { name: 'Download Unavailable invoice' })).toBeDisabled();
  fireEvent.click(screen.getByRole('button', { name: 'Download Supplier invoice' }));
  // WHEN download is retried THEN no mutation is made.
  fireEvent.click(await screen.findByRole('button', { name: 'Retry download' }));
  await waitFor(() => expect(api.downloadPurchaseDocument).toHaveBeenCalledTimes(2));
  expect(api.uploadPurchaseDocument).not.toHaveBeenCalled();
});

it('ignores a late upload result after unmount', async () => {
  // GIVEN a file upload is still in flight as the panel unmounts.
  let resolve!: (value: typeof completed) => void;
  vi.mocked(api.uploadPurchaseDocument).mockReturnValue(new Promise(done => { resolve = done; }));
  const callbacks = props(); const view = render(<PurchaseDocuments {...callbacks} />);
  await select([pdf()]); fireEvent.click(screen.getByRole('button', { name: 'Upload files' }));
  view.unmount();
  // WHEN its response arrives THEN it cannot refresh another purchase or continue uploads.
  await act(async () => resolve(completed));
  expect(callbacks.onCurrent).not.toHaveBeenCalled();
});

it('cancels removal without another confirmation or a mutation', async () => {
  // GIVEN the explicit removal confirmation is open.
  const confirm = vi.spyOn(window, 'confirm').mockReturnValue(false);
  vi.mocked(api.getPurchaseDocuments).mockResolvedValue({ documents: [document], orderVersion: 'v1' });
  render(<PurchaseDocuments {...props()} />);
  fireEvent.click(await screen.findByRole('button', { name: 'Remove Supplier invoice' }));
  // WHEN cancelling THEN the file remains and there is no second confirmation.
  fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
  expect(confirm).not.toHaveBeenCalled();
  expect(screen.queryByRole('button', { name: 'Confirm removal' })).not.toBeInTheDocument();
  expect(api.changePurchaseDocument).not.toHaveBeenCalled();
  confirm.mockRestore();
});
