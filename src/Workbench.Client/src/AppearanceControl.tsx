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
}: {
  preference: Appearance;
  setPreference(value: Appearance): void;
}) {
  const systemDark = useSyncExternalStore(
    subscribeToSystemTheme,
    readSystemDark,
  );
  const dark = preference === 'system' ? systemDark : preference === 'dark';
  useEffect(() => {
    applyAppearance(preference, systemDark);
  }, [preference, systemDark]);
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
        setPreference(value);
        try {
          localStorage.setItem('workbench.appearance', value);
        } catch {
          /* Preference remains usable for this page. */
        }
      }}
    >
      <Icon name="sun" />
      <Icon name="moon" />
    </button>
  );
}
