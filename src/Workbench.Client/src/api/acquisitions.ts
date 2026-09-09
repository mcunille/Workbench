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
async function request(
  url: string,
  init?: RequestInit,
): Promise<AcquisitionContext> {
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
