import { ApiError } from '../../api/auth';
import { prepareExport, type ExportScope, type ExportFormat } from '../../api/export';

type ExportState = {
  status: 'idle' | 'preparing' | 'ready' | 'empty' | 'failed';
  scope?: ExportScope;
  format: ExportFormat;
  url?: string;
  filename?: string;
  expiresAt?: number;
  message?: string;
};

// One owner per signed-in application. No browser storage or durable file cache.
export class ExportMemory {
  private state: ExportState = { status: 'idle', format: 'csv' };
  private listeners = new Set<() => void>();
  private controller?: AbortController;
  private expiryTimer?: ReturnType<typeof setTimeout>;

  getSnapshot = () => this.state;
  subscribe = (listener: () => void) => {
    this.listeners.add(listener);
    return () => { this.listeners.delete(listener); };
  };
  private publish(state: ExportState) {
    this.state = state;
    this.listeners.forEach(listener => listener());
  }
  private release() {
    this.controller?.abort();
    this.controller = undefined;
    clearTimeout(this.expiryTimer);
    if (this.state.url) URL.revokeObjectURL(this.state.url);
  }
  select(scope: ExportScope) {
    if (scope === this.state.scope) return;
    this.release();
    this.publish({ status: 'idle', scope, format: this.state.format });
  }
  selectFormat(format: ExportFormat) {
    if (format === this.state.format) return;
    this.release();
    this.publish({ status: 'idle', scope: this.state.scope, format });
  }
  cancel() {
    this.release();
    this.publish({ status: 'idle', scope: this.state.scope, format: this.state.format, message: 'Preparation cancelled. No records were exported.' });
  }
  dispose() {
    this.release();
    this.publish({ status: 'idle', format: 'csv' });
  }
  private expire() {
    this.release();
    this.publish({ status: 'idle', scope: this.state.scope, format: this.state.format, message: 'The prepared file expired. Prepare a new export; records, acquisition facts, photographs and documents may have changed.' });
  }
  startDownload(): boolean {
    if (this.state.status !== 'ready' || !this.state.expiresAt) return false;
    if (Date.now() >= this.state.expiresAt) {
      this.expire();
      return false;
    }
    this.publish({ ...this.state, message: `Download started. Your browser handles saving the ${this.state.format.toUpperCase()}.` });
    return true;
  }
  async prepare(onAuthLost: () => void) {
    const scope = this.state.scope;
    const format = this.state.format;
    if (!scope || this.state.status === 'preparing') return;
    this.release();
    const controller = new AbortController();
    this.controller = controller;
    this.publish({ status: 'preparing', scope, format, message: 'Preparing export… Download will be available after the complete file arrives.' });
    try {
      const file = await prepareExport(scope, controller.signal, format);
      if (this.controller !== controller || controller.signal.aborted) return;
      this.controller = undefined;
      if (!file) {
        this.publish({ status: 'empty', scope, format, message: 'There are no records in the selected scope. No records were exported.' });
        return;
      }
      const url = URL.createObjectURL(file.blob);
      this.publish({ status: 'ready', scope, format, url, filename: file.filename, expiresAt: Date.now() + 600_000, message: `Your ${format.toUpperCase()} is ready. Download it within ten minutes.` });
      this.expiryTimer = setTimeout(() => this.expire(), 600_000);
    } catch (error) {
      if (this.controller !== controller || controller.signal.aborted) return;
      this.controller = undefined;
      const status = error instanceof ApiError ? error.status : 0;
      const message = status === 422
        ? format === 'zip'
          ? `The package exceeds a limit: 10,000 records, 10,000 documents, 32 MiB CSV, 16 MiB manifest, or 128 MiB total contents or ZIP.${scope === 'all' ? ' Try Active records.' : ''} Choose Records (CSV) for records and acquisition facts; CSV excludes photographs and acquisition documents.`
          : `The export exceeds the limit of 10,000 records or 32 MiB.${scope === 'all' ? ' Try Active records.' : ''}`
        : status === 429
          ? 'Export preparation is busy. Wait briefly, then retry.'
          : status === 401 || status === 403
            ? 'Your session expired or access changed. Sign in again to prepare an export.'
            : `We could not prepare the complete export. Retry to prepare a new snapshot; records may have changed.${format === 'zip' ? ' Photographs or acquisition documents may also have changed. If this keeps happening, ask your operator to investigate photograph or document storage. You can choose Records (CSV) for records and acquisition facts; CSV excludes photographs and acquisition documents.' : ''}`;
      this.publish({ status: 'failed', scope, format, message });
      if (status === 401 || status === 403) onAuthLost();
    }
  }
}
