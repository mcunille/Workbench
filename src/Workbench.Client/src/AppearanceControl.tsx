import { useEffect, useSyncExternalStore } from 'react';
import { applyAppearance, type Appearance } from './appearance';
import { Icon } from './Icon';

const darkQuery = '(prefers-color-scheme: dark)';
function subscribeToSystemTheme(update: () => void) {
  const media = window.matchMedia?.(darkQuery);
  media?.addEventListener('change', update);
  return () => media?.removeEventListener('change', update);
}
function readSystemDark() {
  return window.matchMedia?.(darkQuery).matches ?? false;
}

export function AppearanceControl({
  preference,
  setPreference,
  variant = 'switch',
}: {
  preference: Appearance;
  setPreference(value: Appearance): void;
  variant?: 'switch' | 'menu';
}) {
  const systemDark = useSyncExternalStore(
    subscribeToSystemTheme,
    readSystemDark,
  );
  const dark = preference === 'system' ? systemDark : preference === 'dark';
  useEffect(() => {
    applyAppearance(preference, systemDark);
  }, [preference, systemDark]);
  function choose(value: Appearance) {
    setPreference(value);
    try {
      localStorage.setItem('workbench.appearance', value);
    } catch {
      /* Preference remains usable for this page. */
    }
  }
  if (variant === 'menu') return (
      <button className="quiet profile-row" type="button"
        aria-label={`Appearance ${preference === 'system' ? 'Auto' : dark ? 'Dark' : 'Light'}`}
        title={`Switch to ${preference === 'system' ? 'dark' : preference === 'dark' ? 'light' : 'automatic'} appearance`}
        onClick={() => choose(preference === 'system' ? 'dark' : preference === 'dark' ? 'light' : 'system')}>
        <Icon name={preference === 'system' ? 'system' : dark ? 'moon' : 'sun'} />
        <span>Appearance</span>
        <span className="profile-row-detail" aria-live="polite">{preference === 'system' ? 'Auto' : dark ? 'Dark' : 'Light'}</span>
      </button>
  );
  return (
    <button
      className="appearance"
      type="button"
      role="switch"
      aria-label="Dark theme"
      aria-checked={dark}
      title={dark ? 'Switch to light theme' : 'Switch to dark theme'}
      onClick={() => {
        const value = dark ? 'light' : 'dark';
        choose(value);
      }}
    >
      <Icon name="sun" />
      <Icon name="moon" />
    </button>
  );
}
