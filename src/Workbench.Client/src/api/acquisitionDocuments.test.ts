// @vitest-environment node
import { http, HttpResponse } from 'msw';
import { vi } from 'vitest';
import { server } from '../test/server';
vi.stubGlobal('window', { location: { origin: 'http://localhost:3000' } });
const { changeDocument, downloadDocument, getDocumentOperation, uploadDocument } = await import('./acquisitionDocuments');
const { ApiError } = await import('./auth');
const { ItemValidationError } = await import('./items');
const url = '*/api/items/item/acquisition/acq/documents';
const command = { requestId: 'request', expectedItemVersion: 'i1', expectedAcquisitionVersion: 'a1', expectedDocumentVersion: 'd1', label: 'Receipt' };
beforeEach(() => { server.use(http.get('*/api/auth/antiforgery', () => HttpResponse.json({ requestToken: 'csrf' }))); });
it('sends multipart bytes with CSRF and immutable command evidence', async () => {
  // GIVEN a document upload with deliberately misleading MIME information.
  let captured: FormData | undefined;
  let headers: Headers | undefined;
  server.use(http.post(url, async ({ request }) => { captured = await request.formData(); headers = request.headers; return HttpResponse.json({ state: 'Pending' }); }));
  // WHEN uploaded THEN the original bytes and all command fields reach server validation.
  await uploadDocument('item', 'acq', { ...command, file: new File(['original'], 'receipt.pdf', { type: 'text/plain' }) });
  expect(captured?.get('requestId')).toBe('request');
  expect(captured?.get('expectedItemVersion')).toBe('i1');
  expect(captured?.get('expectedAcquisitionVersion')).toBe('a1');
  expect(captured?.get('label')).toBe('Receipt');
  expect(await (captured?.get('file') as File).text()).toBe('original');
  expect(headers?.get('X-CSRF-TOKEN')).toBe('csrf');
  expect(headers?.get('Content-Type')).toContain('multipart/form-data; boundary=');
});
it('uses checked JSON removal and a read-only status lookup', async () => {
  // GIVEN an explicit removal command.
  let captured: unknown;
  server.use(http.delete(`${url}/doc`, async ({ request }) => { captured = await request.json(); return HttpResponse.json({ state: 'Completed' }); }), http.get(`${url}/operations/request`, () => HttpResponse.json({ state: 'Completed' })));
  // WHEN removal and status lookup run THEN versions and identity survive serialization.
  await changeDocument('item', 'acq', 'doc', { ...command, label: null }, true);
  expect(captured).toEqual({ ...command, label: null });
  expect(await getDocumentOperation('item', 'acq', 'request')).toEqual({ state: 'Completed' });
});
it.each([400, 413, 415, 422, 409, 503, 401])('exposes HTTP %i without treating it as a saved document', async status => {
  // GIVEN a rejected upload WHEN sending THEN the status remains actionable.
  server.use(http.post(url, () => HttpResponse.json({}, { status })));
  await expect(uploadDocument('item', 'acq', { ...command, file: new File(['x'], 'x.pdf') })).rejects.toBeInstanceOf([400, 413, 415, 422].includes(status) ? ItemValidationError : ApiError);
});
it('does not initiate a browser download when authenticated delivery fails', async () => {
  // GIVEN unavailable storage WHEN downloading THEN no success blob is constructed.
  const create = vi.fn(); URL.createObjectURL = create;
  server.use(http.get(`${url}/doc/download`, () => new HttpResponse(null, { status: 503 })));
  await expect(downloadDocument('item', 'acq', { id: 'doc', label: 'Private label', mediaType: 'application/pdf', extension: '.pdf', length: 1, createdAtUtc: '', version: 'd1', unavailable: false })).rejects.toBeInstanceOf(ApiError);
  expect(create).not.toHaveBeenCalled();
});


it('preserves the server format rejection title for actionable correction', async () => {
  // GIVEN a PDF rejected by content validation WHEN uploading THEN the server reason reaches the editor.
  server.use(http.post(url, () => HttpResponse.json({ title: 'Encrypted PDF documents are not supported.' }, { status: 422 })));
  await expect(uploadDocument('item', 'acq', { ...command, file: new File(['x'], 'x.pdf') })).rejects.toMatchObject({ errors: { document: ['Encrypted PDF documents are not supported.'] } });
});
it('uses the safe server attachment filename after downloading validated bytes', async () => {
  // GIVEN a successful private download with a server generated attachment filename.
  const anchor = { href: '', download: '', click: vi.fn() };
  vi.stubGlobal('window', { location: { origin: 'http://localhost:3000' }, document: { createElement: () => anchor } });
  URL.createObjectURL = vi.fn(() => 'blob:private-document');
  URL.revokeObjectURL = vi.fn();
  server.use(http.get(`${url}/doc/download`, () => new HttpResponse('original', { headers: { 'Content-Disposition': 'attachment; filename=document-1234567890abcdef1234567890abcdef.pdf' } })));
  // WHEN delivered THEN the private label cannot become the download filename.
  await downloadDocument('item', 'acq', { id: 'doc', label: 'Private label', mediaType: 'application/pdf', extension: 'pdf', length: 8, createdAtUtc: '', version: 'd1', unavailable: false });
  expect(anchor.download).toBe('document-1234567890abcdef1234567890abcdef.pdf');
  expect(anchor.click).toHaveBeenCalledOnce();
});
it('preserves a bounded conflict reason including pending-document capacity', async () => {
  // GIVEN a reservation consumes the last slot WHEN rejected THEN the server capacity guidance reaches the UI.
  server.use(http.post(url, () => HttpResponse.json({ title: 'This acquisition already has 20 current or pending documents. Remove a document before uploading another.' }, { status: 409 })));
  await expect(uploadDocument('item', 'acq', { ...command, file: new File(['x'], 'x.pdf') })).rejects.toMatchObject({ status: 409, reason: 'This acquisition already has 20 current or pending documents. Remove a document before uploading another.' });
});
