import { apiFetch } from './contract';
import createClient from 'openapi-fetch';
import type { components, paths } from './generated';
import { ApiError, mutationHeaders } from './auth';
export type DraftContent = components['schemas']['DraftContent'];
export type DraftEntry = components['schemas']['DraftEntry'];
export type DraftOrder = components['schemas']['DraftOrderResponse'] & Partial<Pick<components['schemas']['PurchaseOrderResponse'], 'state' | 'orderDate' | 'revision'>>;
export type DraftPage = Omit<components['schemas']['DraftOrderPageResponse'], 'items'> & { items: (components['schemas']['DraftOrderSummary'] & Partial<Pick<components['schemas']['PurchaseOrderSummary'], 'state' | 'orderDate' | 'revision'>>)[] };
export type SaveReceipt = components['schemas']['SaveDraftOrderResponse'];
export type CreateDraftRequest = components['schemas']['CreateDraftOrderRequest'];
export type UpdateDraftRequest = components['schemas']['UpdateDraftOrderRequest'];
export class DraftError extends ApiError {
  constructor(status: number, public readonly code?: string, public readonly errors: Record<string, string[]> = {}) { super(status); }
}
const api = createClient<paths>({ fetch: apiFetch, baseUrl: window.location.origin, cache: 'no-store', credentials: 'same-origin' });
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
export async function getDrafts(cursor?: string, query?: string, state?: string): Promise<DraftPage> {
  return requireDraft(await api.GET('/api/beta/purchase-orders', { params: { query: { cursor, query, state } } }));
}
export async function getDraft(id: string): Promise<DraftOrder> {
  return requireDraft(await api.GET('/api/beta/purchase-orders/{id}', { params: { path: { id } } }));
}
export async function createDraft(body: CreateDraftRequest): Promise<SaveReceipt> {
  return requireDraft(await api.POST('/api/beta/purchase-order-drafts', { body, headers: await mutationHeaders() }));
}
export async function updateDraft(id: string, body: UpdateDraftRequest): Promise<SaveReceipt> {
  return requireDraft(await api.PUT('/api/beta/purchase-order-drafts/{id}', { params: { path: { id } }, body, headers: await mutationHeaders() }));
}
export type DeleteDraftRequest = components['schemas']['DeleteDraftOrderRequest'];
export async function deleteDraft(id: string, body: DeleteDraftRequest): Promise<SaveReceipt> {
  return requireDraft(await api.DELETE('/api/beta/purchase-order-drafts/{id}', { params: { path: { id } }, body, headers: await mutationHeaders() }));
}

export type DraftCalculation = components['schemas']['DraftCalculationResponse'];
export async function calculateDraft(draft: DraftContent, signal?: AbortSignal): Promise<DraftCalculation> {
  return requireDraft(await api.POST('/api/beta/purchase-order-drafts/calculate', { body: { draft }, signal, headers: await mutationHeaders() }));
}

export type CommitOrderRequest = components['schemas']['CommitPurchaseOrderRequest'];
export type AmendOrderRequest = components['schemas']['AmendPurchaseOrderRequest'];
export type OrderReceipt = components['schemas']['SavePurchaseOrderResponse'];
export type OrderRevision = components['schemas']['PurchaseOrderRevisionResponse'];
export type OrderRevisionPage = components['schemas']['PurchaseOrderRevisionPageResponse'];
export async function commitOrder(id: string, body: CommitOrderRequest): Promise<OrderReceipt> {
  return requireDraft(await api.POST('/api/beta/purchase-order-drafts/{id}/commit', { params: { path: { id } }, body, headers: await mutationHeaders() }));
}
export async function amendOrder(id: string, body: AmendOrderRequest): Promise<OrderReceipt> {
  return requireDraft(await api.POST('/api/beta/purchase-orders/{id}/amendments', { params: { path: { id } }, body, headers: await mutationHeaders() }));
}
export async function getOrderRevisions(id: string, cursor?: string): Promise<OrderRevisionPage> {
  return requireDraft(await api.GET('/api/beta/purchase-orders/{id}/revisions', { params: { path: { id }, query: { cursor } } }));
}
export async function getOrderRevision(id: string, revision: number): Promise<OrderRevision> {
  return requireDraft(await api.GET('/api/beta/purchase-orders/{id}/revisions/{revision}', { params: { path: { id, revision } } }));
}
