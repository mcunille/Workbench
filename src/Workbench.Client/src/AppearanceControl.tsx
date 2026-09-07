import { useEffect } from 'react';
import { applyAppearance, type Appearance } from './appearance';
export function AppearanceControl({
  preference,
  setPreference,
}: {
  preference: Appearance;
  setPreference(value: Appearance): void;
}) {
  useEffect(() => {
    const media = window.matchMedia?.('(prefers-color-scheme: dark)');
    const update = () => applyAppearance(preference, media?.matches ?? false);
    update();
    media?.addEventListener('change', update);
    return () => media?.removeEventListener('change', update);
  }, [preference]);
  return (
    <label className="appearance">
      <span className="appearance-label">Appearance</span>
      <select
        value={preference}
        onChange={(e) => {
          const value = e.target.value as Appearance;
          setPreference(value);
          try {
            localStorage.setItem('workbench.appearance', value);
          } catch {
            /* Preference remains usable for this page. */
          }
        }}
      >
        <option value="system">System</option>
        <option value="light">Light</option>
        <option value="dark">Dark</option>
      </select>
    </label>
  );
}
