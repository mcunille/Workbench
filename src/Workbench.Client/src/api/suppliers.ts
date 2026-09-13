import createClient from 'openapi-fetch';
import type { components, paths } from './generated';
import { ApiError, mutationHeaders } from './auth';
export type SupplierContent = components['schemas']['SupplierContent'];
export type Supplier = components['schemas']['SupplierResponse'];
export type SupplierPage = components['schemas']['SupplierPageResponse'];
export type SupplierReceipt = components['schemas']['SaveSupplierResponse'];
export type CreateSupplierRequest = components['schemas']['CreateSupplierRequest'];
export type UpdateSupplierRequest = components['schemas']['UpdateSupplierRequest'];
export type ArchiveSupplierRequest = components['schemas']['ArchiveSupplierRequest'];
export class SupplierError extends ApiError {
  constructor(status: number, public readonly code?: string, public readonly errors: Record<string, string[]> = {}) { super(status); }
}
const api = createClient<paths>({ baseUrl: window.location.origin, cache: 'no-store', credentials: 'same-origin' });
function requireSupplier<T>({ response, data, error }: { response: Response; data?: T; error?: unknown }): T {
  if (!response.ok || data === undefined) {
    const problem = error && typeof error === 'object' ? error as { code?: unknown; errors?: unknown } : {};
    const errors: Record<string, string[]> = {};
    if (problem.errors && typeof problem.errors === 'object') {
      for (const [key, value] of Object.entries(problem.errors)) if (Array.isArray(value) && value.every(item => typeof item === 'string')) errors[key] = value;
    }
    throw new SupplierError(response.status, typeof problem.code === 'string' ? problem.code : undefined, errors);
  }
  return data;
}
export async function getSuppliers(cursor?: string, query?: string, includeArchived = false): Promise<SupplierPage> {
  return requireSupplier(await api.GET('/api/suppliers', { params: { query: { cursor, query, includeArchived } } }));
}
export async function getSupplier(id: string): Promise<Supplier> {
  return requireSupplier(await api.GET('/api/suppliers/{id}', { params: { path: { id } } }));
}
export async function createSupplier(body: CreateSupplierRequest): Promise<SupplierReceipt> {
  return requireSupplier(await api.POST('/api/suppliers', { body, headers: await mutationHeaders() }));
}
export async function updateSupplier(id: string, body: UpdateSupplierRequest): Promise<SupplierReceipt> {
  return requireSupplier(await api.PUT('/api/suppliers/{id}', { params: { path: { id } }, body, headers: await mutationHeaders() }));
}
export async function archiveSupplier(id: string, body: ArchiveSupplierRequest): Promise<SupplierReceipt> {
  return requireSupplier(await api.POST('/api/suppliers/{id}/archive', { params: { path: { id } }, body, headers: await mutationHeaders() }));
}
