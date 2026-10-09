import { useEffect, useLayoutEffect, useRef, useState, type ReactNode } from 'react';
import { getGem, GemReferenceApiError, type GemReferenceDetailResponse } from '../../api/gemReference';
import { fieldLabels, gemPath, layerLabel, materialKinds, type GemIdentity, type GemNavigation } from './gemPresentation';
import './gem-reference.css';

export function GemDetail(props: GemIdentity & GemNavigation) {
  return <DetailContent key={`${props.origin}:${props.id}`} {...props} />;
}
function DetailContent({ id, origin, follow, onAuthLost }: GemIdentity & GemNavigation) {
  const [entry, setEntry] = useState<GemReferenceDetailResponse>();
  const [failure, setFailure] = useState<number>();
  const [attempt, setAttempt] = useState(0);
  const heading = useRef<HTMLHeadingElement>(null);
  useEffect(() => {
    let current = true;
    const controller = new AbortController();
    void getGem(id, origin, controller.signal).then(result => { if (current) setEntry(result); }).catch((error: unknown) => {
      if (!current) return;
      const status = error instanceof GemReferenceApiError ? error.status : 500;
      if (status === 401 || status === 403) onAuthLost();
      else setFailure(status);
    });
    return () => { current = false; controller.abort(); };
  }, [id, origin, attempt, onAuthLost]);
  useLayoutEffect(() => { if (entry) heading.current?.focus(); }, [entry]);
  return <article className="reference-page">
    <a className="reference-back" href="/gem-reference" onClick={follow}>Back to gem reference</a>
    {failure === 404 ? <><h1>Gem reference not found.</h1><p>This entry is unavailable to your tenant.</p></> : failure ? <div className="form-message error"><p role="alert">We could not load this gem entry. Try again.</p><button type="button" onClick={() => { setFailure(undefined); setAttempt(value => value + 1); }}>Retry</button></div> : !entry ? <p role="status">Loading gem entry…</p> : null}
    {entry ? <>
      <header className="page-heading"><div><h1 ref={heading} tabIndex={-1}>{entry.commonName}</h1><p className="reference-layer">{layerLabel(entry.layer)}</p></div></header>
      {entry.retirement.isRetired ? <section className="reference-section" aria-label="Retired entry"><h2>Retired entry</h2><p>{entry.retirement.explanation}</p>{entry.retirement.redirectEntryId ? <a href={gemPath(entry.retirement.redirectEntryId, 'workbench')} onClick={follow}>View replacement entry</a> : null}</section> : null}
      {entry.isArchived ? <p role="status">Archived tenant entry</p> : null}
      {entry.needsReview ? <section className="reference-review" aria-label="Needs review"><h2>Needs review</h2><p>Classification is unavailable until this entry is reconciled. Its identity and retained reference information remain visible.</p><ul>{Object.entries(entry.reviewReasons ?? {}).flatMap(([field, reasons]) => reasons.map((reason, index) => <li key={`${field}:${index}`}><strong>{fieldLabels[field] ?? field}: </strong>{reason}</li>))}</ul></section> : null}
      <section className="reference-section" aria-labelledby="reference-classification"><h2 id="reference-classification">Classification</h2>{entry.needsReview ? <p>No validated classification is available.</p> : <dl className="reference-fields">
        <Field entry={entry} name="materialKind">{materialKinds[entry.materialKind as keyof typeof materialKinds] ?? entry.materialKind}</Field>
        <Field entry={entry} name="group">{entry.group}</Field>
        <Field entry={entry} name="species" absent={entry.materialKind !== 'mineral' ? 'Mineral species is not required for this material kind.' : undefined}>{entry.species}</Field>
        <Field entry={entry} name="variety">{entry.variety}</Field>
      </dl>}</section>
      <section className="reference-section" aria-label="Names and description"><h2>Names and description</h2><dl className="reference-fields">
        <Field entry={entry} name="commonName">{entry.commonName}</Field>
        <Field entry={entry} name="aliases">{entry.aliases.length ? <ul>{entry.aliases.map(alias => <li key={alias}>{alias}</li>)}</ul> : null}</Field>
        <Field entry={entry} name="description">{entry.description ? <p className="reference-prose">{entry.description}</p> : null}</Field>
      </dl></section>
      <section className="reference-section" aria-label="Reference locality"><h2>Reference locality</h2><p>This reference does not establish the origin of an individual specimen.</p><dl className="reference-fields">
        <Field entry={entry} name="notableLocality">{entry.notableLocality ? <><p>{entry.notableLocality.place}</p><p>{entry.notableLocality.scope}</p><p>Reviewed <time dateTime={entry.notableLocality.reviewedOn}>{entry.notableLocality.reviewedOn}</time></p></> : null}</Field>
      </dl></section>
      <Sources entry={entry} />
    </> : null}
  </article>;
}

function sourceAnchor(field: string, id: string) { return `reference-source-${encodeURIComponent(field)}-${encodeURIComponent(id)}`; }
function Field({ entry, name, children, absent }: { entry: GemReferenceDetailResponse; name: string; children: ReactNode; absent?: string }) {
  const field = entry.effectiveFields?.[name];
  const tenant = field?.attribution === 'tenant';
  const empty = children === null || children === undefined || children === '';
  const cleared = field?.state === 'clear';
  return <div><dt aria-label={fieldLabels[name]}>{fieldLabels[name]}</dt><dd>
    {cleared ? 'Cleared by your tenant.' : empty ? absent ?? (tenant ? 'No tenant assertion recorded.' : 'No Workbench assertion recorded; this optional field is unasserted.') : children}
    <p className="reference-attribution">{tenant ? field?.sources.length ? 'Tenant-authored · tenant sources' : 'Tenant-authored · no sources supplied' : 'Workbench reference'}</p>
    {field?.sources.length ? <a href={`#${sourceAnchor(name, field.sources[0].id)}`}>Supporting sources</a> : null}
  </dd></div>;
}
function safeSourceUrl(url: string | null) {
  if (!url) return undefined;
  try {
    const parsed = new URL(url);
    return (parsed.protocol === 'https:' || parsed.protocol === 'http:') && !parsed.username && !parsed.password ? parsed.href : undefined;
  } catch { return undefined; }
}
function Sources({ entry }: { entry: GemReferenceDetailResponse }) {
  const sources = Object.entries(entry.effectiveFields ?? {}).flatMap(([field, value]) => value.sources.map(source => ({ field, source })));
  return <section className="reference-section" aria-label="Sources"><h2>Sources</h2>{sources.length ? <ul className="reference-sources">{sources.map(({ field, source }) => {
    const href = safeSourceUrl(source.url);
    return <li key={`${field}:${source.id}`} id={sourceAnchor(field, source.id)} tabIndex={-1}>
      <h3>{href ? <a href={href} target="_blank" rel="noopener noreferrer">{source.title}</a> : source.title}</h3>
      <p className="reference-attribution">{fieldLabels[field] ?? field} · {source.attribution === 'tenant' ? 'Tenant source' : 'Workbench source'} · {source.publisher}</p>
      {source.citation ? <p className="reference-prose">{source.citation}</p> : null}
      <p>Reviewed <time dateTime={source.reviewedOn}>{source.reviewedOn}</time>{source.accessedOn ? <> · Accessed <time dateTime={source.accessedOn}>{source.accessedOn}</time></> : null}</p>
    </li>;
  })}</ul> : <p>No sources supplied for this effective entry.</p>}</section>;
}
