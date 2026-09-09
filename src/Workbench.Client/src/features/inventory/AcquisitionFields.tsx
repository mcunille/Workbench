import { methods, sourceLabels, type Draft } from './acquisitionDraft';
export function AcquisitionValues({ value }: { value: Draft }) {
  const date =
    value.precision === 'Unknown'
      ? 'Not recorded'
      : [
          value.year.padStart(4, '0'),
          ...(value.precision !== 'Year' ? [value.month.padStart(2, '0')] : []),
          ...(value.precision === 'Exact date'
            ? [value.day.padStart(2, '0')]
            : []),
        ].join('-');
  return (
    <dl className="item-details">
      <div className="detail-field">
        <dt>Method</dt>
        <dd>{value.method || 'Not recorded'}</dd>
      </div>
      <div className="detail-field">
        <dt>{sourceLabels[value.method] ?? 'Source'}</dt>
        <dd>{value.source || 'Not recorded'}</dd>
      </div>
      <div className="detail-field">
        <dt>Acquired date</dt>
        <dd>{date}</dd>
      </div>
      <div className="detail-field">
        <dt>Provenance notes</dt>
        <dd className="notes">{value.notes || 'Not recorded'}</dd>
      </div>
    </dl>
  );
}
export function AcquisitionFields({
  draft,
  setDraft,
  disabled,
  errors,
}: {
  draft: Draft;
  setDraft(value: Draft): void;
  disabled: boolean;
  errors: Record<string, string[]>;
}) {
  const error = (name: string) =>
    Object.entries(errors)
      .find(([key]) => key.toLowerCase() === name)?.[1]
      ?.join(' ');
  const attributes = (name: string) => ({
    id: 'acquisition-' + name,
    name,
    disabled,
    'aria-invalid': Boolean(error(name)),
    'aria-describedby': error(name) ? 'acquisition-error-' + name : undefined,
  });
  const message = (name: string) =>
    error(name) ? (
      <p className="form-message error" id={'acquisition-error-' + name}>
        {error(name)}
      </p>
    ) : null;
  return (
    <>
      <div className="edit-field">
        <label htmlFor="acquisition-method">Acquisition method</label>
        <select
          {...attributes('method')}
          value={draft.method}
          onChange={(e) => setDraft({ ...draft, method: e.target.value })}
        >
          <option value="">Choose a method</option>
          {methods.map((method) => (
            <option key={method}>{method}</option>
          ))}
        </select>
        {message('method')}
      </div>
      <div className="edit-field">
        <label htmlFor="acquisition-source">
          {sourceLabels[draft.method] ?? 'Source'} (optional)
        </label>
        <input
          {...attributes('source')}
          value={draft.source}
          maxLength={200}
          onChange={(e) => setDraft({ ...draft, source: e.target.value })}
        />
        {message('source')}
      </div>
      <div className="edit-field">
        <label htmlFor="acquisition-precision">Date precision</label>
        <select
          {...attributes('precision')}
          value={draft.precision}
          onChange={(e) => setDraft({ ...draft, precision: e.target.value })}
        >
          {['Unknown', 'Year', 'Month', 'Exact date'].map((precision) => (
            <option key={precision}>{precision}</option>
          ))}
        </select>
        <p className="hint">
          Record only what you know about the date acquired.
        </p>
      </div>
      {(['year', 'month', 'day'] as const)
        .filter(
          (field) =>
            draft.precision !== 'Unknown' &&
            (field === 'year' ||
              (field === 'month' && draft.precision !== 'Year') ||
              (field === 'day' && draft.precision === 'Exact date')),
        )
        .map((field) => (
          <div className="edit-field" key={field}>
            <label htmlFor={'acquisition-' + field}>
              {field[0].toUpperCase() + field.slice(1)}
            </label>
            <input
              {...attributes(field)}
              type="number"
              inputMode="numeric"
              min={1}
              max={field === 'year' ? 9999 : field === 'month' ? 12 : 31}
              value={draft[field]}
              onChange={(e) => setDraft({ ...draft, [field]: e.target.value })}
            />
            {message(field)}
          </div>
        ))}
      <div className="edit-field">
        <label htmlFor="acquisition-notes">Provenance notes (optional)</label>
        <textarea
          {...attributes('notes')}
          rows={5}
          maxLength={4000}
          value={draft.notes}
          onChange={(e) => setDraft({ ...draft, notes: e.target.value })}
        />
        {message('notes')}
      </div>
    </>
  );
}
