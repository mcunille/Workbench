import type { GemReferenceDetailResponse } from '../../api/gemReference';
import type { GemEditorDraft } from './gemEditorDraft';
import { fieldLabels, gemValue } from './gemPresentation';

export function GemConflictComparison({ current, draft, onUseCurrent, onKeepDraft }: { current: GemReferenceDetailResponse; draft: GemEditorDraft; onUseCurrent: () => void; onKeepDraft: () => void }) {
  return <section className="reference-review" aria-label="Review current entry"><h2>Review current entry</h2><p>Your draft is preserved. Compare the current saved values and sources before choosing how to continue. Neither choice saves your draft.</p>
    {Object.entries(fieldLabels).map(([field, label]) => <div className="reference-conflict-row" key={field}><h3>{label}</h3><div className="reference-edit-grid">
      <div><strong>Your draft</strong><p className="reference-prose">{draft.overrides[field]?.state === 'clear' ? 'Cleared by your tenant' : gemValue(draft.content[field as keyof typeof draft.content])}</p>{draft.content.sources.filter(source => source.field === field).map(source => <p key={source.id} className="reference-prose">{source.title} · {source.citation ?? source.url} · Reviewed {source.reviewedOn}</p>)}</div>
      <div><strong>Current saved value</strong><p className="reference-prose">{gemValue(current[field as keyof GemReferenceDetailResponse])}</p>{current.effectiveFields?.[field]?.sources.map(source => <p key={source.id} className="reference-prose">{source.attribution === 'tenant' ? 'Tenant source' : 'Workbench source'} · {source.title} · {source.citation ?? source.url} · Reviewed {source.reviewedOn}</p>)}</div>
    </div></div>)}
    <div className="button-row"><button type="button" onClick={onKeepDraft}>Keep my draft with current versions</button><button type="button" className="secondary" onClick={onUseCurrent}>Use current entry</button></div>
  </section>;
}
