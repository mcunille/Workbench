import type { GemReferenceContent, GemReferenceSourceContent } from '../../api/gemReferenceAdmin';

const noDate = '0001-01-01';
const noSource = '00000000-0000-0000-0000-000000000000';
const fields = [['materialKind', 'Material kind'], ['commonName', 'Common name'], ['aliases', 'Aliases'], ['group', 'Group'], ['species', 'Species'], ['variety', 'Variety'], ['description', 'Description'], ['notableLocality', 'Notable locality']];

export function GemSourceFields({ content, onChange }: { content: GemReferenceContent; onChange: (content: GemReferenceContent) => void }) {
  function updateSource(id: string, patch: Partial<GemReferenceSourceContent>) {
    const sources = content.sources.map((source) => source.id === id ? { ...source, ...patch } : source);
    const selected = sources.find((source) => source.id === content.notableLocality?.sourceAssertionId && source.field === 'notableLocality');
    onChange({ ...content, sources, notableLocality: content.notableLocality ? { ...content.notableLocality, sourceAssertionId: selected?.id ?? noSource, reviewedOn: selected?.reviewedOn ?? noDate } : null });
  }
  function removeSource(id: string) {
    onChange({ ...content, sources: content.sources.filter((source) => source.id !== id), notableLocality: content.notableLocality?.sourceAssertionId === id ? { ...content.notableLocality, sourceAssertionId: noSource, reviewedOn: noDate } : content.notableLocality });
  }
  const locality = content.notableLocality;
  return <>
    <section className="gem-editor-section" aria-labelledby="sources-heading">
      <h2 id="sources-heading">Sources</h2>
      <p>Support each populated field with a source. Sources stay as citations; external content is never embedded.</p>
      {content.sources.length === 0 ? <p>No sources yet. Add a claim-level citation before publication.</p> : null}
      {content.sources.map((source, index) => {
        const prefix = `Source ${index + 1}`;
        return <fieldset className="gem-source-fields" key={source.id}>
          <legend>{prefix}</legend>
          <div className="gem-form-grid">
            <label>{prefix} field<select value={source.field} onChange={(event) => updateSource(source.id, { field: event.target.value })}>{fields.map(([value, label]) => <option key={value} value={value}>{label}</option>)}</select></label>
            <label>{prefix} title<input maxLength={200} value={source.title} onChange={(event) => updateSource(source.id, { title: event.target.value })} /></label>
            <label>{prefix} publisher<input maxLength={200} value={source.publisher} onChange={(event) => updateSource(source.id, { publisher: event.target.value })} /></label>
            <label>{prefix} URL<input type="text" inputMode="url" maxLength={2000} value={source.url ?? ''} onChange={(event) => updateSource(source.id, { url: event.target.value || null })} /></label>
            <label className="gem-wide-field">{prefix} publication citation<textarea rows={2} maxLength={2000} value={source.citation ?? ''} onChange={(event) => updateSource(source.id, { citation: event.target.value || null })} /></label>
            <label>{prefix} accessed date<input type="date" value={source.accessedOn ?? ''} onChange={(event) => updateSource(source.id, { accessedOn: event.target.value || null })} /></label>
            <label>{prefix} review date<input type="date" value={source.reviewedOn === noDate ? '' : source.reviewedOn} onChange={(event) => updateSource(source.id, { reviewedOn: event.target.value || noDate })} /></label>
          </div>
          <button type="button" className="secondary" onClick={() => removeSource(source.id)}>Remove source {index + 1}</button>
        </fieldset>;
      })}
      <button type="button" className="secondary" disabled={content.sources.length >= 64} onClick={() => onChange({ ...content, sources: [...content.sources, { id: crypto.randomUUID(), field: 'commonName', title: '', publisher: '', url: null, citation: null, accessedOn: null, reviewedOn: noDate }] })}>Add source</button>
      <p className="gem-state">{content.sources.length} of 64 sources</p>
    </section>
    <section id="gem-locality" className="gem-editor-section" aria-labelledby="locality-heading">
      <h2 id="locality-heading">Notable locality</h2>
      <label className="gem-check"><input type="checkbox" checked={locality !== null} onChange={(event) => onChange({ ...content, notableLocality: event.target.checked ? { place: '', scope: '', reviewedOn: noDate, sourceAssertionId: noSource } : null })} />Include notable locality</label>
      {locality ? <div className="gem-form-grid">
        <label>Locality place<input maxLength={200} value={locality.place} onChange={(event) => onChange({ ...content, notableLocality: { ...locality, place: event.target.value } })} /></label>
        <label>Locality scope<input maxLength={200} value={locality.scope} onChange={(event) => onChange({ ...content, notableLocality: { ...locality, scope: event.target.value } })} /></label>
        <label>Locality supporting source<select value={locality.sourceAssertionId} onChange={(event) => {
          const source = content.sources.find((item) => item.id === event.target.value && item.field === 'notableLocality');
          onChange({ ...content, notableLocality: { ...locality, sourceAssertionId: source?.id ?? noSource, reviewedOn: source?.reviewedOn ?? noDate } });
        }}><option value={noSource}>Choose a locality source</option>{content.sources.filter((source) => source.field === 'notableLocality').map((source) => <option key={source.id} value={source.id}>{source.title || 'Untitled locality source'}</option>)}</select></label>
        <p>Locality review date: {locality.reviewedOn === noDate ? 'Choose a reviewed source' : locality.reviewedOn}</p>
      </div> : null}
    </section>
  </>;
}
