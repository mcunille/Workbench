import { ApiError, mutationHeaders } from './auth';
import { ItemValidationError } from './items';

import type { components } from './generated';

export type Acquisition = components['schemas']['AcquisitionResponse'];
export type AcquisitionContext =
  components['schemas']['ItemAcquisitionResponse'];
export type AcquisitionCommand =
  | components['schemas']['CreateAcquisitionRequest']
  | components['schemas']['UpdateAcquisitionRequest'];
export class AcquisitionConflictError extends ApiError {
  constructor() {
    super(409);
  }
}
async function request<T = AcquisitionContext>(
  url: string,
  init?: RequestInit,
): Promise<T> {
  const response = await fetch(new URL(url, window.location.origin), {
    credentials: 'same-origin',
    cache: 'no-store',
    ...init,
  });
  if (response.status === 409) throw new AcquisitionConflictError();
  if (response.status === 400) {
    const problem = await response.json().catch(() => null);
    throw new ItemValidationError(
      problem?.errors ?? {
        acquisition: [
          'The request could not be accepted. Check the entered facts and try again.',
        ],
      },
    );
  }
  if (!response.ok) throw new ApiError(response.status);
  return response.json();
}
export type AcquisitionPage = components['schemas']['AcquisitionPageResponse'];
export type AcquisitionItems = components['schemas']['AcquisitionItemsResponse'];
export type LinkAcquisitionCommand = components['schemas']['LinkAcquisitionRequest'];
export function findAcquisitions(search = '', cursor?: string) {
  const query = new URLSearchParams({ search });
  if (cursor) query.set('cursor', cursor);
  return request<AcquisitionPage>(`/api/acquisitions?${query}`);
}
export function getSharedAcquisition(id: string) {
  return request<Acquisition>(`/api/acquisitions/${encodeURIComponent(id)}`);
}
export function getAcquisitionItems(id: string, includeArchived = false, cursor?: string) {
  const query = new URLSearchParams({ includeArchived: String(includeArchived) });
  if (cursor) query.set('cursor', cursor);
  return request<AcquisitionItems>(`/api/acquisitions/${encodeURIComponent(id)}/items?${query}`);
}
export async function saveAcquisitionLink(itemId: string, command: LinkAcquisitionCommand) {
  return request(`/api/items/${encodeURIComponent(itemId)}/acquisition-link`, {
    method: 'PUT', headers: { ...(await mutationHeaders()), 'Content-Type': 'application/json' },
    body: JSON.stringify(command),
  });
}
export function getAcquisition(itemId: string) {
  return request(`/api/items/${encodeURIComponent(itemId)}/acquisition`);
}
export async function saveAcquisition(
  itemId: string,
  acquisitionId: string | undefined,
  command: AcquisitionCommand,
) {
  return request(
    `/api/items/${encodeURIComponent(itemId)}/acquisition${acquisitionId ? '/' + encodeURIComponent(acquisitionId) : ''}`,
    {
      method: acquisitionId ? 'PUT' : 'POST',
      headers: {
        ...(await mutationHeaders()),
        'Content-Type': 'application/json',
      },
      body: JSON.stringify(command),
    },
  );
}
