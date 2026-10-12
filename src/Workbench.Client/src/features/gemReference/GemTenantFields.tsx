import { useId } from 'react';
import type { GemReferenceContent, GemReferenceDetailResponse } from '../../api/gemReference';
import { setGemFieldState, type GemEditorDraft } from './gemEditorDraft';
import { fieldLabels, materialKinds, gemValue, singleLineGemText } from './gemPresentation';
import { GemTenantSources, noDate, noSource } from './GemTenantSources';

const fields = ['commonName', 'materialKind', 'group', 'species', 'variety', 'aliases', 'description', 'notableLocality'] as const;
const clearable = new Set(['group', 'species', 'variety', 'description', 'notableLocality']);
export function GemTenantFields({ draft, entry, errors, onChange, disabled }: { draft: GemEditorDraft; entry?: GemReferenceDetailResponse; errors: Record<string, string[]>; onChange: (draft: GemEditorDraft) => void; disabled: boolean }) {
  const fieldId = useId();
  const shared = entry?.origin === 'workbench';
  function update(patch: Partial<GemReferenceContent>) { onChange({ ...draft, content: { ...draft.content, ...patch } }); }
  function choose(field: typeof fields[number], state: 'inherit' | 'replace' | 'clear') {
    const next = setGemFieldState(draft, field, state);
    if (shared && state === 'inherit' && entry.workbenchContent) next.content = { ...next.content, [field]: entry.workbenchContent[field] };
    onChange(next);
  }
  const locality = draft.content.notableLocality;
  return <>{fields.map(field => {
    const label = fieldLabels[field];
    const state = draft.overrides[field]?.state ?? 'inherit';
    const editable = !shared || state === 'replace';
    const errorId = `${fieldId}-${field}-error`;
    const hintId = `${fieldId}-${field}-hint`;
    const invalid = Boolean(errors[field]?.length);
    const accessibility = { 'aria-invalid': invalid || undefined, 'aria-describedby': invalid ? errorId : undefined } as const;
    return <section className="reference-section reference-edit-field" key={field} aria-label={`${label} editor`}>
      <h2>{label}</h2>
      {invalid ? <p id={errorId} className="reference-field-error">{errors[field].join(' ')}</p> : null}
      {shared ? <div className="reference-edit-grid"><div className="reference-shared-value"><h3>Workbench value</h3><p className="reference-prose">{gemValue(entry.workbenchContent?.[field])}</p>{entry.workbenchContent?.sources.filter(source => source.field === field).map(source => <div key={source.id}><p>{source.title} · {source.publisher}</p>{source.citation ? <p className="reference-prose">{source.citation}</p> : null}{source.url ? <p className="reference-prose">{source.url}</p> : null}<p className="reference-attribution">Reviewed {source.reviewedOn}</p></div>)}</div>
        <div><label>{label} choice<select {...accessibility} disabled={disabled} value={state} onChange={event => choose(field, event.target.value as 'inherit' | 'replace' | 'clear')}><option value="inherit">Use Workbench value</option><option value="replace">Use tenant value</option>{clearable.has(field) ? <option value="clear">Clear for this tenant</option> : null}</select></label>
          {state !== 'inherit' ? <button type="button" className="secondary" disabled={disabled} onClick={() => choose(field, 'inherit')}>Reset {label} to Workbench</button> : null}
          <p className="reference-attribution">{state === 'inherit' ? 'Inherits current Workbench value and sources.' : state === 'clear' ? 'Cleared by your tenant. No supporting assertion.' : 'Tenant replacement; Workbench sources do not support this value.'}</p>
        </div></div> : null}
      {editable ? <fieldset className="reference-edit-inputs" disabled={disabled}>
        {field === 'materialKind' ? <label>{label}<select {...accessibility} value={draft.content.materialKind} onChange={event => update({ materialKind: event.target.value })}>{Object.entries(materialKinds).map(([value, title]) => <option value={value} key={value}>{title}</option>)}</select></label>
          : field === 'aliases' ? <label>Aliases (one per line)<textarea {...accessibility} rows={3} value={draft.content.aliases.join('\n')} onChange={event => update({ aliases: event.target.value ? event.target.value.split('\n') : [] })} /></label>
          : field === 'description' ? <><label>{label}<textarea {...accessibility} aria-describedby={[accessibility['aria-describedby'], hintId].filter(Boolean).join(' ')} maxLength={2000} rows={4} value={draft.content.description ?? ''} onChange={event => update({ description: singleLineGemText(event.target.value) || null })} /></label><p id={hintId} className="reference-attribution">Line breaks are converted to spaces.</p></>
          : field === 'notableLocality' ? <>
            {!shared ? <label className="reference-check"><input {...accessibility} type="checkbox" checked={locality !== null} onChange={event => update({ notableLocality: event.target.checked ? { place: '', scope: '', reviewedOn: noDate, sourceAssertionId: noSource } : null, sources: draft.content.sources.filter(source => source.field !== 'notableLocality') })} />Include notable locality</label> : !locality ? <button {...accessibility} type="button" onClick={() => update({ notableLocality: { place: '', scope: '', reviewedOn: noDate, sourceAssertionId: noSource } })}>Add locality assertion</button> : null}
            <p>This reference does not establish the origin of an individual specimen. A locality assertion needs a source supporting its exact scope.</p>
            {locality ? <div className="reference-edit-grid">
              <label>Locality place<input {...accessibility} maxLength={200} value={locality.place} onChange={event => update({ notableLocality: { ...locality, place: event.target.value } })} /></label>
              <label>Locality scope<input {...accessibility} maxLength={200} value={locality.scope} onChange={event => update({ notableLocality: { ...locality, scope: event.target.value } })} /></label>
              <label>Locality supporting source<select {...accessibility} value={locality.sourceAssertionId} onChange={event => { const source = draft.content.sources.find(item => item.id === event.target.value && item.field === field); update({ notableLocality: { ...locality, sourceAssertionId: source?.id ?? noSource, reviewedOn: source?.reviewedOn ?? noDate } }); }}><option value={noSource}>Choose a locality source</option>{draft.content.sources.filter(source => source.field === field).map(source => <option key={source.id} value={source.id}>{source.title || 'Untitled locality source'}</option>)}</select></label>
            </div> : null}
          </> : <label>{label}<input {...accessibility} maxLength={200} value={draft.content[field] ?? ''} onChange={event => update({ [field]: event.target.value || (field === 'commonName' ? '' : null) })} /></label>}
        {field !== 'notableLocality' || locality ? <GemTenantSources draft={draft} field={field} errors={errors.sources ?? errors[field]} onChange={onChange} disabled={disabled} /> : null}
      </fieldset> : null}
    </section>;
  })}</>;
}
