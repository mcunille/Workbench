import { http, HttpResponse } from 'msw';
import { server } from '../test/server';
import { getSuppliers, getSupplier, createSupplier, updateSupplier, archiveSupplier } from './suppliers';
const supplier = { name: 'Studio', contactName: null, email: 'hello@example.test', phone: null, website: null, postalAddress: 'First line\nSecond line' };
it('sends private query-bound reads and exact protected supplier commands', async () => {
  // GIVEN opaque pagination, a literal search query and complete supplier data.
  const reads: Request[] = []; const writes: { path: string; csrf: string | null; body: unknown }[] = [];
  const receipt = { requestId: 'r', replayed: true, supplierId: 'one', savedVersion: 'v1', completedAtUtc: '2026-09-12T00:00:00Z' };
  const record = { id: 'one', supplier, isArchived: false, version: 'v1', createdAtUtc: receipt.completedAtUtc, updatedAtUtc: receipt.completedAtUtc };
  const handle = async ({ request }: { request: Request }) => { writes.push({ path: new URL(request.url).pathname, csrf: request.headers.get('X-CSRF-TOKEN'), body: await request.json() }); return HttpResponse.json(receipt); };
  server.use(http.get('*/api/auth/antiforgery', () => HttpResponse.json({ requestToken: 'supplier-csrf' })),
    http.get('*/api/suppliers', ({ request }) => { reads.push(request); return HttpResponse.json({ items: [record], nextCursor: null }); }),
    http.get('*/api/suppliers/one', ({ request }) => { reads.push(request); return HttpResponse.json(record); }),
    http.post('*/api/suppliers', handle), http.put('*/api/suppliers/one', handle), http.post('*/api/suppliers/one/archive', handle));
  // WHEN listing, reading and issuing each independent command.
  await getSuppliers('a+b/=', '%_[', true); expect(await getSupplier('one')).toEqual(record);
  const create = { requestId: 'r', supplier }; const update = { ...create, expectedVersion: 'v1' }; const archive = { requestId: 'archive', expectedVersion: 'v1', isArchived: true };
  expect(await createSupplier(create)).toEqual(receipt); await updateSupplier('one', update); await archiveSupplier('one', archive);
  // THEN query values remain literal and requests carry exact receipts, versions, nulls and antiforgery protection.
  expect(new URL(reads[0].url).searchParams.get('query')).toBe('%_['); expect(new URL(reads[0].url).searchParams.get('cursor')).toBe('a+b/='); expect(new URL(reads[0].url).searchParams.get('includeArchived')).toBe('true');
  expect(reads.every(request => request.cache === 'no-store' && request.credentials === 'same-origin')).toBe(true);
  expect(writes.map(write => write.body)).toEqual([create, update, archive]); expect(writes.every(write => write.csrf === 'supplier-csrf')).toBe(true);
});
it('retains stable supplier field errors and concurrency codes', async () => {
  // GIVEN the authoritative API rejects contact input and a stale archive command.
  server.use(http.get('*/api/auth/antiforgery', () => HttpResponse.json({ requestToken: 'csrf' })), http.post('*/api/suppliers', () => HttpResponse.json({ code: 'supplier_validation_failed', errors: { 'supplier.email': ['Invalid email.'], unexpected: 4 } }, { status: 400 })), http.post('*/api/suppliers/one/archive', () => HttpResponse.json({ code: 'supplier_version_conflict' }, { status: 409 })));
  // WHEN commands fail THEN the feature receives useful safe field errors and exact conflict status.
  await expect(createSupplier({ requestId: 'r', supplier })).rejects.toMatchObject({ status: 400, code: 'supplier_validation_failed', errors: { 'supplier.email': ['Invalid email.'] } });
  await expect(archiveSupplier('one', { requestId: 'a', expectedVersion: 'v1', isArchived: true })).rejects.toMatchObject({ status: 409, code: 'supplier_version_conflict' });
});
