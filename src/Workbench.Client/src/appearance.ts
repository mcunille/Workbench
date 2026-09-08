export type Appearance = 'system' | 'light' | 'dark';
export function readAppearance(storage?: Pick<Storage, 'getItem'>): Appearance {
  try {
    const value = (storage ?? window.localStorage).getItem(
      'workbench.appearance',
    );
    return value === 'light' || value === 'dark' ? value : 'system';
  } catch {
    return 'system';
  }
}
export function applyAppearance(preference: Appearance, dark: boolean) {
  const theme =
    preference === 'system' ? (dark ? 'dark' : 'light') : preference;
  document.documentElement.dataset.theme = theme;
  document.documentElement.style.colorScheme = theme;
  document
    .querySelector('meta[name="theme-color"]')
    ?.setAttribute('content', theme === 'dark' ? '#08090c' : '#ffffff');
}
