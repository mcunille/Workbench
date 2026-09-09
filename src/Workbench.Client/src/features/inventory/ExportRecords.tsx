import { useSyncExternalStore, type MouseEvent } from 'react';
import type { ExportMemory } from './exportMemory';
export function ExportRecords({ memory, follow, onAuthLost }: { memory: ExportMemory; follow(event: MouseEvent<HTMLAnchorElement>): void; onAuthLost(): void }) {
  const state = useSyncExternalStore(memory.subscribe, memory.getSnapshot);
  const preparing = state.status === 'preparing';
  return <section className="export-records">
    <div className="page-heading">
      <div><h1>Export records</h1><p className="lede">Take a copy of your collection’s current text records.</p></div>
      <a className="secondary button" href="/inventory" onClick={follow}>Back to collection</a>
    </div>
    <div className="panel export-panel">
      <form onSubmit={event => { event.preventDefault(); void memory.prepare(onAuthLost); }}>
        <fieldset className="export-scope" disabled={preparing} aria-describedby="export-scope-help">
          <legend>Choose records to export</legend>
          <label><input type="radio" name="export-scope" value="active" checked={state.scope === 'active'} onChange={() => memory.select('active')} />Active records</label>
          <label><input type="radio" name="export-scope" value="all" checked={state.scope === 'all'} onChange={() => memory.select('all')} />Active and archived records</label>
        </fieldset>
        <p id="export-scope-help">Export includes every record in your chosen scope. Search, loaded pages, and the screen you came from do not limit it.</p>
        <dl className="export-facts">
          <div><dt>Format</dt><dd>CSV version 1 · UTF-8</dd></div>
          <div><dt>Limits</dt><dd>10,000 records · 32 MiB</dd></div>
          <div><dt>File availability</dt><dd>Ten minutes, in this tab. Sign-out or reload clears it.</dd></div>
        </dl>
        <p>This is not a backup and cannot restore Workbench. Photos, history, and session information are excluded.</p>
        <details className="export-guidance" open>
          <summary>Opening the CSV in a spreadsheet</summary>
          <p>Import columns as Text to preserve exact IDs, timestamps, and user text. Names, notes, and locations have one added apostrophe to protect against spreadsheet formulas; it may remain visible.</p>
          <p>To recover the original text after parsing the CSV, remove exactly one leading apostrophe from each present name, notes, and location value. Empty optional fields mean absent. Removing the prefix or re-saving may remove formula protection.</p>
        </details>
        <div className="button-row">
          <button className="primary" type="submit" disabled={!state.scope || preparing}>{state.status === 'failed' ? 'Retry' : state.status === 'ready' || state.status === 'empty' ? 'Prepare new export' : 'Prepare export'}</button>
          {preparing ? <button className="secondary" type="button" onClick={() => memory.cancel()}>Cancel</button> : null}
          {state.status === 'ready' && state.url ? <a className="primary button" href={state.url} download={state.filename} onClick={event => { if (!memory.startDownload()) event.preventDefault(); }}>Download CSV</a> : null}
        </div>
        {state.status === 'ready' || state.status === 'empty' || state.status === 'failed' ? <p className="muted">Preparing again creates a new snapshot. Records may have changed.</p> : null}
        <p role={state.status === 'failed' ? 'alert' : 'status'} aria-live={state.status === 'failed' ? 'assertive' : 'polite'}>{state.message ?? 'Select a scope to prepare your export.'}</p>
      </form>
    </div>
  </section>;
}
