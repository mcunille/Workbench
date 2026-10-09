import createClient from 'openapi-fetch';
import type { components, paths } from './generated';
import { apiFetch } from './contract';
import { serviceAdminMutationHeaders } from './serviceAdmin';

export type GemReferencePageResponse = components['schemas']['GemReferencePageResponse'];
export type GemReferenceDetailResponse = components['schemas']['GemReferenceDetailResponse'];
export type GemReferenceDraftPage = components['schemas']['GemReferenceDraftPage'];
export type GemReferenceDraftResponse = components['schemas']['GemReferenceDraftResponse'];
export type GemReferenceDraftSaveRequest = components['schemas']['GemReferenceDraftSaveRequest'];
export type GemReferenceDraftSelection = components['schemas']['GemReferenceDraftSelection'];
export type GemReferenceReviewResponse = components['schemas']['GemReferenceReviewResponse'];
export type GemReferencePublishRequest = components['schemas']['GemReferencePublishRequest'];
export type GemReferencePublishOutcome = components['schemas']['GemReferencePublishOutcome'];
export type GemReferenceContent = components['schemas']['GemReferenceContent'];
export type GemReferenceSourceContent = components['schemas']['GemReferenceSourceContent'];

export class GemReferenceAdminApiError extends Error {
  constructor(public readonly status: number, public readonly problem: unknown) {
    super('The shared gem-reference request could not be completed.');
  }
}

const api = createClient<paths>({ fetch: apiFetch, baseUrl: window.location.origin });

function success<T>({ data, error, response }: { data?: T; error?: unknown; response: Response }): T {
  if (!response.ok || data === undefined) throw new GemReferenceAdminApiError(response.status, error);
  return data;
}

export async function browseSharedGems(query: { query?: string; materialKind?: string; group?: string; cursor?: string }, signal?: AbortSignal): Promise<GemReferencePageResponse> {
  return success(await api.GET('/api/beta/service-admin/gem-reference', { params: { query }, signal }));
}
export async function getSharedGem(id: string): Promise<GemReferenceDetailResponse> {
  return success(await api.GET('/api/beta/service-admin/gem-reference/{id}', { params: { path: { id } } }));
}
export async function listGemDrafts(cursor?: string): Promise<GemReferenceDraftPage> {
  return success(await api.GET('/api/beta/service-admin/gem-reference/drafts', { params: { query: { cursor } } }));
}
export async function getGemDraft(id: string): Promise<GemReferenceDraftResponse> {
  return success(await api.GET('/api/beta/service-admin/gem-reference/drafts/{draftId}', { params: { path: { draftId: id } } }));
}
export async function saveGemDraft(id: string, request: GemReferenceDraftSaveRequest): Promise<GemReferenceDraftResponse> {
  return success(await api.PUT('/api/beta/service-admin/gem-reference/drafts/{draftId}', {
    params: { path: { draftId: id } }, body: request, headers: await serviceAdminMutationHeaders(),
  }));
}
export async function reviewGemDrafts(drafts: GemReferenceDraftSelection[]): Promise<GemReferenceReviewResponse> {
  return success(await api.POST('/api/beta/service-admin/gem-reference/review', { body: { drafts }, headers: await serviceAdminMutationHeaders() }));
}
export async function publishGemDrafts(request: GemReferencePublishRequest): Promise<GemReferencePublishOutcome> {
  const result = await api.POST('/api/beta/service-admin/gem-reference/publish', { body: request, headers: await serviceAdminMutationHeaders() });
  if ((result.response.status === 409 || result.response.status === 422) && result.error && 'requestId' in result.error) return result.error;
  return success(result);
}
export async function getGemPublication(requestId: string): Promise<GemReferencePublishOutcome | null> {
  const result = await api.GET('/api/beta/service-admin/gem-reference/publications/{requestId}', { params: { path: { requestId } } });
  if (result.response.status === 404) return null;
  return success(result);
}
