import { ApiError, mutationHeaders } from './auth';
import { ItemValidationError } from './items';
import type { components } from './generated';
export type AcquisitionDocument = components['schemas']['AcquisitionDocumentResponse'];
export type DocumentList = components['schemas']['AcquisitionDocumentsResponse'];
export type DocumentOperation = components['schemas']['AcquisitionDocumentOperationResponse'];
export type DocumentChange = components['schemas']['ChangeAcquisitionDocumentRequest'];
export type DocumentUpload = Pick<DocumentChange, 'requestId' | 'expectedItemVersion' | 'expectedAcquisitionVersion' | 'label'> & { file: File };
export class DocumentConflictError extends ApiError {
  readonly reason: string;
  constructor(reason: unknown) {
    super(409);
    this.reason = typeof reason === 'string' ? reason.slice(0, 1000) : 'The item, acquisition or document changed. Reload before trying again.';
  }
}
function base(itemId: string, acquisitionId: string) { return `/api/items/${encodeURIComponent(itemId)}/acquisition/${encodeURIComponent(acquisitionId)}/documents`; }
async function request(path: string, init?: RequestInit) {
  const response = await fetch(new URL(path, window.location.origin), { credentials: 'same-origin', cache: 'no-store', ...init });
  if (response.status === 409) {
    const problem = await response.json().catch(() => null);
    throw new DocumentConflictError(problem?.title);
  }
  if ([400, 413, 415, 422].includes(response.status)) {
    const problem = await response.json().catch(() => null);
    throw new ItemValidationError(problem?.errors ?? { document: [response.status === 413 ? 'Choose a document no larger than 10 MiB.' : problem?.detail ?? problem?.title ?? 'Check the label and choose a valid PDF, JPEG, PNG or WebP file.'] });
  }
  if (!response.ok) throw new ApiError(response.status);
  return response;
}
export async function getDocuments(itemId: string, acquisitionId: string): Promise<DocumentList> { return (await request(base(itemId, acquisitionId))).json(); }
export async function uploadDocument(itemId: string, acquisitionId: string, command: DocumentUpload): Promise<DocumentOperation> {
  const body = new FormData();
  body.set('requestId', command.requestId);
  body.set('expectedItemVersion', command.expectedItemVersion ?? '');
  body.set('expectedAcquisitionVersion', command.expectedAcquisitionVersion ?? '');
  body.set('label', command.label ?? '');
  body.set('file', command.file);
  return (await request(base(itemId, acquisitionId), { method: 'POST', headers: await mutationHeaders(), body })).json();
}
export async function changeDocument(itemId: string, acquisitionId: string, documentId: string, command: DocumentChange, remove: boolean): Promise<DocumentOperation> {
  return (await request(`${base(itemId, acquisitionId)}/${encodeURIComponent(documentId)}`, { method: remove ? 'DELETE' : 'PUT', headers: { ...await mutationHeaders(), 'Content-Type': 'application/json' }, body: JSON.stringify(command) })).json();
}
export async function getDocumentOperation(itemId: string, acquisitionId: string, requestId: string): Promise<DocumentOperation> { return (await request(`${base(itemId, acquisitionId)}/operations/${encodeURIComponent(requestId)}`)).json(); }
export async function downloadDocument(itemId: string, acquisitionId: string, document: AcquisitionDocument): Promise<void> {
  const response = await request(`${base(itemId, acquisitionId)}/${encodeURIComponent(document.id)}/download`);
  const url = URL.createObjectURL(await response.blob());
  const anchor = window.document.createElement('a');
  anchor.href = url;
  const serverFilename = response.headers.get('Content-Disposition')?.match(/(?:^|;)\s*filename="?(document-[a-f0-9]{32}\.(?:pdf|jpg|jpeg|png|webp))"?(?:;|$)/i)?.[1];
  anchor.download = serverFilename ?? `document-${document.id.replace(/[^a-zA-Z0-9]/g, '')}.${document.extension.replace(/[^a-zA-Z0-9]/g, '')}`;
  anchor.click();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}
