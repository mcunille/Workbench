import { apiFetch } from './contract';
import { ApiError, mutationHeaders } from './auth';
import { ItemValidationError } from './items';
import type { components } from './generated';

export type PurchaseDocument = components['schemas']['PurchaseOrderDocumentResponse'];
export type PurchaseDocumentList = components['schemas']['PurchaseOrderDocumentsResponse'];
export type PurchaseDocumentOperation = components['schemas']['PurchaseOrderDocumentOperationResponse'];
export type PurchaseDocumentChange = components['schemas']['ChangePurchaseOrderDocumentRequest'];
export type PurchaseDocumentUpload = Pick<PurchaseDocumentChange, 'requestId' | 'expectedOrderVersion' | 'label'> & { file: File };

export class PurchaseDocumentConflict extends ApiError {
  readonly reason: string;
  constructor(reason: unknown) {
    super(409);
    this.reason = typeof reason === 'string' ? reason.slice(0, 1000) : 'The purchase or file changed. Review the current files before retrying.';
  }
}
function base(id: string) { return `/api/beta/purchase-orders/${encodeURIComponent(id)}/documents`; }
async function request(path: string, init?: RequestInit) {
  const response = await apiFetch(new URL(path, window.location.origin), { credentials: 'same-origin', cache: 'no-store', ...init });
  if (response.status === 409) {
    const problem = await response.json().catch(() => null);
    throw new PurchaseDocumentConflict(problem?.title);
  }
  if ([400, 413, 415, 422].includes(response.status)) {
    const problem = await response.json().catch(() => null);
    throw new ItemValidationError(problem?.errors ?? { document: [response.status === 413 ? 'Choose a file no larger than 10 MiB.' : problem?.detail ?? problem?.title ?? 'Check the label and choose a valid PDF, JPEG, PNG or WebP file.'] });
  }
  if (!response.ok) throw new ApiError(response.status);
  return response;
}
export async function getPurchaseDocuments(id: string): Promise<PurchaseDocumentList> { return (await request(base(id))).json(); }
export async function uploadPurchaseDocument(id: string, command: PurchaseDocumentUpload): Promise<PurchaseDocumentOperation> {
  const body = new FormData();
  body.set('requestId', command.requestId);
  body.set('expectedOrderVersion', command.expectedOrderVersion ?? '');
  body.set('label', command.label ?? '');
  body.set('file', command.file);
  return (await request(base(id), { method: 'POST', headers: await mutationHeaders(), body })).json();
}
export async function changePurchaseDocument(id: string, documentId: string, command: PurchaseDocumentChange, remove: boolean): Promise<PurchaseDocumentOperation> {
  return (await request(`${base(id)}/${encodeURIComponent(documentId)}`, { method: remove ? 'DELETE' : 'PUT', headers: { ...await mutationHeaders(), 'Content-Type': 'application/json' }, body: JSON.stringify(command) })).json();
}
export async function getPurchaseDocumentOperation(id: string, requestId: string): Promise<PurchaseDocumentOperation> { return (await request(`${base(id)}/operations/${encodeURIComponent(requestId)}`)).json(); }
export async function downloadPurchaseDocument(id: string, document: PurchaseDocument): Promise<void> {
  const response = await request(`${base(id)}/${encodeURIComponent(document.id)}/download`);
  const url = URL.createObjectURL(await response.blob());
  const anchor = window.document.createElement('a');
  anchor.href = url;
  const serverFilename = response.headers.get('Content-Disposition')?.match(/(?:^|;)\s*filename="?(document-[a-f0-9]{32}\.(?:pdf|jpg|jpeg|png|webp))"?(?:;|$)/i)?.[1];
  anchor.download = serverFilename ?? `document-${document.id.replace(/[^a-zA-Z0-9]/g, '')}.${document.extension.replace(/[^a-zA-Z0-9]/g, '')}`;
  anchor.click();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}
