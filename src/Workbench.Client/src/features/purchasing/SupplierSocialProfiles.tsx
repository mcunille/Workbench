import { useLayoutEffect, useRef } from 'react';
import { FloatingField } from '../../FloatingField';

type SocialProfile = { label: string; handle: string };
export function SupplierSocialProfiles({ profiles, disabled, errors, onChange }: {
  profiles: readonly SocialProfile[];
  disabled: boolean;
  errors: Record<string, string[]>;
  onChange(profiles: SocialProfile[]): void;
}) {
  const addButton = useRef<HTMLButtonElement>(null);
  const pendingFocus = useRef<number | 'add' | null>(null);
  useLayoutEffect(() => {
    const target = pendingFocus.current;
    pendingFocus.current = null;
    if (target === 'add') addButton.current?.focus();
    else if (target !== null) document.getElementById(`supplier-socialProfiles[${target}].label`)?.focus();
  }, [profiles]);
  function update(index: number, key: keyof SocialProfile, value: string) {
    onChange(profiles.map((profile, position) => position === index ? { ...profile, [key]: value } : profile));
  }
  function field(profile: SocialProfile, index: number, key: keyof SocialProfile) {
    const id = `supplier-socialProfiles[${index}].${key}`;
    const error = errors[`supplier.socialProfiles[${index}].${key}`]?.join(' ');
    const label = `${key === 'label' ? 'Label' : 'Handle'} ${index + 1}`;
    return <div className="po-field">
      <FloatingField htmlFor={id} label={label}>
        <input id={id} type="text" value={profile[key]} placeholder={key === 'label' ? 'e.g. Discord' : 'e.g. @gemdealer'}
          disabled={disabled} maxLength={key === 'label' ? 100 : 2048} required
          aria-invalid={!!error} aria-describedby={error ? `${id}-error` : undefined}
          onChange={event => update(index, key, event.target.value)} />
      </FloatingField>
      {error ? <p className="form-message error" id={`${id}-error`}>{error}</p> : null}
    </div>;
  }
  return <section className="po-form-section" role="group" aria-labelledby="supplier-socialProfiles-heading" id="supplier-socialProfiles" tabIndex={-1}>
    <div className="po-section-heading"><div><h2 id="supplier-socialProfiles-heading">Social handles (optional)</h2><p>Choose a label and keep the handle here for reference.</p></div></div>
    <div className="po-social-profiles">
      {profiles.map((profile, index) => <div className="po-social-profile" key={index}>
        <div className="po-supplier-contact-fields">{field(profile, index, 'label')}{field(profile, index, 'handle')}</div>
        <button type="button" className="quiet danger" disabled={disabled} aria-label={`Remove social ${index + 1}`} onClick={() => {
          pendingFocus.current = 'add';
          onChange(profiles.filter((_, position) => position !== index));
        }}>Remove</button>
      </div>)}
    </div>
    {errors['supplier.socialProfiles'] ? <p className="form-message error">{errors['supplier.socialProfiles'].join(' ')}</p> : null}
    <button ref={addButton} type="button" className="secondary" disabled={disabled || profiles.length >= 20} onClick={() => {
      pendingFocus.current = profiles.length;
      onChange([...profiles, { label: '', handle: '' }]);
    }}>Add social</button>
    {profiles.length >= 20 ? <p className="po-field-help">Up to 20 social handles per supplier.</p> : null}
  </section>;
}
