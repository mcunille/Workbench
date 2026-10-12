import type { GemReferenceSourceContent } from '../../api/gemReference';
import type { GemEditorDraft } from './gemEditorDraft';
import { fieldLabels } from './gemPresentation';

export const noSource = '00000000-0000-0000-0000-000000000000';
export const noDate = '0001-01-01';
export function GemTenantSources({ draft, field, onChange, disabled }: { draft: GemEditorDraft; field: string; onChange: (draft: GemEditorDraft) => void; disabled: boolean }) {
  const sources = draft.content.sources.filter(source => source.field === field);
  function update(all: GemReferenceSourceContent[]) {
    const locality = draft.content.notableLocality;
    const supporting = all.find(source => source.field === 'notableLocality' && source.id === locality?.sourceAssertionId);
    onChange({ ...draft, content: { ...draft.content, sources: all, notableLocality: locality && field === 'notableLocality' ? { ...locality, sourceAssertionId: supporting?.id ?? noSource, reviewedOn: supporting?.reviewedOn ?? noDate } : locality } });
  }
  function patch(id: string, value: Partial<GemReferenceSourceContent>) { update(draft.content.sources.map(source => source.id === id ? { ...source, ...value } : source)); }
  return <div className="reference-tenant-sources">
    <p className="reference-attribution">{sources.length ? 'Tenant-authored · tenant sources' : 'Tenant-authored · no sources supplied'}</p>
    {sources.map((source, index) => {
      const prefix = `${fieldLabels[field]} source ${index + 1}`;
      return <fieldset key={source.id} disabled={disabled} className="reference-source-editor"><legend>{prefix}</legend><div className="reference-edit-grid">
        <label>{prefix} title<input maxLength={200} value={source.title} onChange={event => patch(source.id, { title: event.target.value })} /></label>
        <label>{prefix} publisher<input maxLength={200} value={source.publisher} onChange={event => patch(source.id, { publisher: event.target.value })} /></label>
        <label>{prefix} URL<input inputMode="url" maxLength={2000} value={source.url ?? ''} onChange={event => patch(source.id, { url: event.target.value || null })} /></label>
        <label>{prefix} publication citation<textarea maxLength={2000} rows={2} value={source.citation ?? ''} onChange={event => patch(source.id, { citation: event.target.value || null })} /></label>
        <label>{prefix} accessed date<input type="date" value={source.accessedOn ?? ''} onChange={event => patch(source.id, { accessedOn: event.target.value || null })} /></label>
        <label>{prefix} review date<input type="date" value={source.reviewedOn === noDate ? '' : source.reviewedOn} onChange={event => patch(source.id, { reviewedOn: event.target.value || noDate })} /></label>
      </div><button type="button" className="secondary" onClick={() => update(draft.content.sources.filter(item => item.id !== source.id))}>Remove {prefix}</button></fieldset>;
    })}
    <button type="button" className="secondary" disabled={disabled || draft.content.sources.length >= 64} onClick={() => update([...draft.content.sources, { id: crypto.randomUUID(), field, title: '', publisher: '', url: null, citation: null, accessedOn: null, reviewedOn: noDate }])}>Add {fieldLabels[field]} source</button>
  </div>;
}
