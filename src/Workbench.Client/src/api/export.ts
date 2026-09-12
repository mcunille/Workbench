import type { components } from './generated';
import { ApiError, mutationHeaders } from './auth';

export type ExportScope = 'active' | 'all';
export type ExportFormat = 'csv' | 'zip';
export type ExportFile = { blob: Blob; filename: string };
export async function prepareExport(scope: ExportScope, signal: AbortSignal, format: ExportFormat = 'csv'): Promise<ExportFile | null> {
  const body: components['schemas']['ExportItemsRequest'] = { scope };
  const headers = await mutationHeaders();
  signal.throwIfAborted();
  // Use fetch for the binary attachment; JSON requests remain generated-contract typed.
  const response = await fetch(new URL(format === 'zip' ? '/api/items/export-package' : '/api/items/export', window.location.origin), {
    method: 'POST', credentials: 'same-origin', cache: 'no-store', signal,
    headers: { ...headers, 'Content-Type': 'application/json', Accept: format === 'zip' ? 'application/zip' : 'text/csv' },
    body: JSON.stringify(body),
  });
  signal.throwIfAborted();
  if (response.status === 204) return null;
  if (response.status !== 200) throw new ApiError(response.status);
  const length = Number(response.headers.get('Content-Length'));
  const disposition = response.headers.get('Content-Disposition') ?? '';
  const filename = /filename="?([^";]+)"?/i.exec(disposition)?.[1];
  const mediaType = format === 'zip' ? /^application\/zip(?:;|$)/i : /^text\/csv(?:;|$)/i;
  if (!mediaType.test(response.headers.get('Content-Type') ?? '')
    || !Number.isInteger(length) || length <= 0 || length > (format === 'zip' ? 128 : 32) * 1024 * 1024
    || !disposition.toLowerCase().startsWith('attachment;')
    || !filename || !new RegExp(`^workbench-${format === 'zip' ? 'package' : 'records'}-v2-${scope}-[0-9TZ.\\-]+\\.${format}$`).test(filename)) {
    throw new Error('The export response could not be verified.');
  }
  const blob = await response.blob();
  signal.throwIfAborted();
  if (blob.size !== length) throw new Error('The export response was incomplete.');
  return { blob, filename };
}
