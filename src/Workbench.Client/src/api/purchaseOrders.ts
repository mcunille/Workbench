import createClient from 'openapi-fetch';
import type { components, paths } from './generated';
import { ApiError, mutationHeaders } from './auth';
export type DraftContent = components['schemas']['DraftContentV2'];
export type DraftEntry = components['schemas']['DraftEntry'];
export type DraftOrder = components['schemas']['DraftOrderResponseV2'];
export type DraftPage = components['schemas']['DraftOrderPageResponseV2'];
export type SaveReceipt = components['schemas']['SaveDraftOrderResponse'];
export type CreateDraftRequest = components['schemas']['CreateDraftOrderRequestV2'];
export type UpdateDraftRequest = components['schemas']['UpdateDraftOrderRequestV2'];
export class DraftError extends ApiError {
  constructor(status: number, public readonly code?: string, public readonly errors: Record<string, string[]> = {}) { super(status); }
}
const api = createClient<paths>({ baseUrl: window.location.origin, cache: 'no-store', credentials: 'same-origin' });
function requireDraft<T>({ response, data, error }: { response: Response; data?: T; error?: unknown }): T {
  if (!response.ok || data === undefined) {
    const problem = error && typeof error === 'object' ? error as { code?: unknown; errors?: unknown } : {};
    const errors: Record<string, string[]> = {};
    if (problem.errors && typeof problem.errors === 'object') {
      for (const [key, value] of Object.entries(problem.errors)) if (Array.isArray(value) && value.every(item => typeof item === 'string')) errors[key] = value;
    }
    throw new DraftError(response.status, typeof problem.code === 'string' ? problem.code : undefined, errors);
  }
  return data;
}
export async function getDrafts(cursor?: string, query?: string): Promise<DraftPage> {
  return requireDraft(await api.GET('/api/v2/purchase-order-drafts', { params: { query: { cursor, query } } }));
}
export async function getDraft(id: string): Promise<DraftOrder> {
  return requireDraft(await api.GET('/api/v2/purchase-order-drafts/{id}', { params: { path: { id } } }));
}
export async function createDraft(body: CreateDraftRequest): Promise<SaveReceipt> {
  return requireDraft(await api.POST('/api/v2/purchase-order-drafts', { body, headers: await mutationHeaders() }));
}
export async function updateDraft(id: string, body: UpdateDraftRequest): Promise<SaveReceipt> {
  return requireDraft(await api.PUT('/api/v2/purchase-order-drafts/{id}', { params: { path: { id } }, body, headers: await mutationHeaders() }));
}
export type DeleteDraftRequest = components['schemas']['DeleteDraftOrderRequest'];
export async function deleteDraft(id: string, body: DeleteDraftRequest): Promise<SaveReceipt> {
  return requireDraft(await api.DELETE('/api/v2/purchase-order-drafts/{id}', { params: { path: { id } }, body, headers: await mutationHeaders() }));
}
