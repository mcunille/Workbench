import { ApiError } from '../../api/auth';
import { prepareExport, type ExportScope } from '../../api/export';

type ExportState = {
  status: 'idle' | 'preparing' | 'ready' | 'empty' | 'failed';
  scope?: ExportScope;
  url?: string;
  filename?: string;
  expiresAt?: number;
  message?: string;
};

// One owner per signed-in application. No browser storage or durable file cache.
export class ExportMemory {
  private state: ExportState = { status: 'idle' };
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
    this.publish({ status: 'idle', scope });
  }
  cancel() {
    this.release();
    this.publish({ status: 'idle', scope: this.state.scope, message: 'Preparation cancelled. No records were exported.' });
  }
  dispose() {
    this.release();
    this.publish({ status: 'idle' });
  }
  private expire() {
    this.release();
    this.publish({ status: 'idle', scope: this.state.scope, message: 'The prepared file expired. Prepare a new export; records may have changed.' });
  }
  startDownload(): boolean {
    if (this.state.status !== 'ready' || !this.state.expiresAt) return false;
    if (Date.now() >= this.state.expiresAt) {
      this.expire();
      return false;
    }
    this.publish({ ...this.state, message: 'Download started. Your browser handles saving the CSV.' });
    return true;
  }
  async prepare(onAuthLost: () => void) {
    const scope = this.state.scope;
    if (!scope || this.state.status === 'preparing') return;
    this.release();
    const controller = new AbortController();
    this.controller = controller;
    this.publish({ status: 'preparing', scope, message: 'Preparing export… Download will be available after the complete file arrives.' });
    try {
      const file = await prepareExport(scope, controller.signal);
      if (this.controller !== controller || controller.signal.aborted) return;
      this.controller = undefined;
      if (!file) {
        this.publish({ status: 'empty', scope, message: 'There are no records in the selected scope. No records were exported.' });
        return;
      }
      const url = URL.createObjectURL(file.blob);
      this.publish({ status: 'ready', scope, url, filename: file.filename, expiresAt: Date.now() + 600_000, message: 'Your CSV is ready. Download it within ten minutes.' });
      this.expiryTimer = setTimeout(() => this.expire(), 600_000);
    } catch (error) {
      if (this.controller !== controller || controller.signal.aborted) return;
      this.controller = undefined;
      const status = error instanceof ApiError ? error.status : 0;
      const message = status === 422
        ? `The export exceeds the limit of 10,000 records or 32 MiB.${scope === 'all' ? ' Try Active records.' : ''}`
        : status === 429
          ? 'Export preparation is busy. Wait briefly, then retry.'
          : status === 401 || status === 403
            ? 'Your session expired or access changed. Sign in again to prepare an export.'
            : 'We could not prepare the complete export. Retry to prepare a new snapshot; records may have changed.';
      this.publish({ status: 'failed', scope, message });
      if (status === 401 || status === 403) onAuthLost();
    }
  }
}
