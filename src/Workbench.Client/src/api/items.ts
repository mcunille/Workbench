import createClient from 'openapi-fetch';
import type { components, paths } from './generated';
import { ApiError, mutationHeaders } from './auth';
export type CreateItemRequest =
  components['schemas']['CreateItemRequest'];
export type ItemDetail = components['schemas']['ItemDetailResponse'];
export type ItemPage = components['schemas']['ItemPageResponse'];
export type UpdateItemRequest =
  components['schemas']['UpdateItemDetailsRequest'];
export class ItemConflictError extends ApiError {
  constructor() {
    super(409);
  }
}
export class ItemValidationError extends ApiError {
  constructor(public readonly errors: Record<string, string[]>) {
    super(400);
  }
}
const api = createClient<paths>({ baseUrl: window.location.origin });
export async function updateItem(
  id: string,
  body: UpdateItemRequest,
): Promise<ItemDetail> {
  const { data, response, error } = await api.PUT('/api/items/{id}', {
    params: { path: { id } },
    body,
    headers: await mutationHeaders(),
  });
  if (
    response.status === 400 &&
    error &&
    'errors' in error &&
    error.errors
  )
    throw new ItemValidationError(error.errors);
  if (
    response.status === 409 &&
    error &&
    'code' in error &&
    (error.code === 'item_version_conflict' ||
      error.code === 'item_archived')
  )
    throw new ItemConflictError();
  if (!response.ok || !data) throw new ApiError(response.status);
  return data;
}
export async function archiveItem(
  id: string,
  body: components['schemas']['ArchiveItemRequest'],
): Promise<ItemDetail> {
  const { data, response, error } = await api.POST(
    '/api/items/{id}/archive',
    {
      params: { path: { id } },
      body,
      headers: await mutationHeaders(),
    },
  );
  if (
    response.status === 400 &&
    error &&
    'errors' in error &&
    error.errors
  )
    throw new ItemValidationError(error.errors);
  if (response.status === 409) throw new ItemConflictError();
  if (!response.ok || !data) throw new ApiError(response.status);
  return data;
}
export async function restoreItem(
  id: string,
  body: components['schemas']['RestoreItemRequest'],
): Promise<ItemDetail> {
  const { data, response, error } = await api.POST(
    '/api/items/{id}/restore',
    {
      params: { path: { id } },
      body,
      headers: await mutationHeaders(),
    },
  );
  if (
    response.status === 400 &&
    error &&
    'errors' in error &&
    error.errors
  )
    throw new ItemValidationError(error.errors);
  if (response.status === 409) throw new ItemConflictError();
  if (!response.ok || !data) throw new ApiError(response.status);
  return data;
}
export async function createItem(
  body: CreateItemRequest,
): Promise<ItemDetail> {
  const { data, response, error } = await api.POST('/api/items', {
    body,
    headers: await mutationHeaders(),
  });
  if (
    response.status === 400 &&
    error &&
    'errors' in error &&
    error.errors
  )
    throw new ItemValidationError(error.errors);
  if (!response.ok || !data) throw new ApiError(response.status);
  return data;
}
export async function getItems(
  cursor?: string,
  q?: string,
): Promise<ItemPage> {
  const { data, response } = await api.GET('/api/items', {
    params: { query: { cursor, q } },
  });
  if (!response.ok || !data) throw new ApiError(response.status);
  return data;
}
export async function getArchivedItems(
  cursor?: string,
  q?: string,
): Promise<ItemPage> {
  const { data, response } = await api.GET('/api/items/archived', {
    params: { query: { cursor, q } },
  });
  if (!response.ok || !data) throw new ApiError(response.status);
  return data;
}
export async function getItem(id: string): Promise<ItemDetail> {
  const { data, response } = await api.GET('/api/items/{id}', {
    params: { path: { id } },
  });
  if (!response.ok || !data) throw new ApiError(response.status);
  return data;
}

export type PhotoMutationResponse =
  components['schemas']['ItemPhotoMutationResponse'];
export async function putItemPhoto(
  id: string,
  file: Blob,
  requestId: string,
  expectedVersion: string,
): Promise<PhotoMutationResponse> {
  const { data, response } = await api.PUT('/api/items/{id}/photo', {
    params: { path: { id } },
    // OpenAPI represents binary as string; the serializer below sends the actual Blob.
    body: { file: '', requestId, expectedVersion },
    bodySerializer: () => {
      const form = new FormData();
      const extension =
        file.type === 'image/webp'
          ? 'webp'
          : file.type === 'image/png'
            ? 'png'
            : 'jpg';
      form.append('file', file, `photograph.${extension}`);
      form.append('requestId', requestId);
      form.append('expectedVersion', expectedVersion);
      return form;
    },
    headers: await mutationHeaders(),
  });
  if (!response.ok || !data) throw new ApiError(response.status);
  return data;
}
export async function removeItemPhoto(
  id: string,
  requestId: string,
  expectedVersion: string,
): Promise<PhotoMutationResponse> {
  const { data, response } = await api.DELETE('/api/items/{id}/photo', {
    params: { path: { id } },
    body: { requestId, expectedVersion },
    headers: await mutationHeaders(),
  });
  if (!response.ok || !data) throw new ApiError(response.status);
  return data;
}
export async function getPhoto(
  url: string,
  signal?: AbortSignal,
): Promise<Blob> {
  const response = await fetch(new URL(url, window.location.origin), {
    credentials: 'same-origin',
    signal,
    cache: 'no-store',
  });
  if (!response.ok) throw new ApiError(response.status);
  return response.blob();
}
