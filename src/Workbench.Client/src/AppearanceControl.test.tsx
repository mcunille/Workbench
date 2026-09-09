import { act, fireEvent, render, screen } from '@testing-library/react';
import { useState } from 'react';
import { AppearanceControl } from './AppearanceControl';
import { readAppearance } from './appearance';

function Harness({ variant = 'switch' }: { variant?: 'switch' | 'menu' }) {
  const [preference, setPreference] = useState(readAppearance);
  return (
    <AppearanceControl
      preference={preference}
      setPreference={setPreference}
      variant={variant}
    />
  );
}

it('cycles the profile appearance through dark, light, and auto without a submenu', () => {
  // GIVEN auto appearance and a light system theme.
  localStorage.clear();
  const changeSystem = systemTheme(false);
  render(<Harness variant="menu" />);
  // WHEN repeatedly activating the same row THEN each choice applies and persists.
  fireEvent.click(screen.getByRole('button', { name: 'Appearance Auto' }));
  expect(document.documentElement.dataset.theme).toBe('dark');
  expect(localStorage.getItem('workbench.appearance')).toBe('dark');
  fireEvent.click(screen.getByRole('button', { name: 'Appearance Dark' }));
  expect(document.documentElement.dataset.theme).toBe('light');
  expect(localStorage.getItem('workbench.appearance')).toBe('light');
  fireEvent.click(screen.getByRole('button', { name: 'Appearance Light' }));
  expect(screen.getByRole('button', { name: 'Appearance Auto' })).not.toHaveAttribute('aria-expanded');
  expect(localStorage.getItem('workbench.appearance')).toBe('system');
  expect(screen.queryByRole('group', { name: 'Appearance choices' })).not.toBeInTheDocument();
  // AND returning to auto resumes following system changes.
  changeSystem(true);
  expect(document.documentElement.dataset.theme).toBe('dark');
});

afterEach(() => {
  localStorage.clear();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

function systemTheme(dark: boolean) {
  const listeners = new Set<() => void>();
  const media = {
    matches: dark,
    addEventListener: (_: string, listener: () => void) =>
      listeners.add(listener),
    removeEventListener: (_: string, listener: () => void) =>
      listeners.delete(listener),
  };
  vi.stubGlobal('matchMedia', () => media);
  return (value: boolean) =>
    act(() => {
      media.matches = value;
      listeners.forEach((listener) => listener());
    });
}

it.each([false, true])('follows system appearance until toggled, starting dark=%s', dark => {
  // GIVEN no explicit preference and a system appearance.
  localStorage.clear();
  const changeSystem = systemTheme(dark);
  const view = render(<Harness />);
  const control = screen.getByRole('switch', { name: 'Dark theme' });
  expect(control).toHaveAttribute('aria-checked', String(dark));
  expect(screen.queryByRole('combobox')).not.toBeInTheDocument();
  // WHEN the system changes before the first user choice.
  changeSystem(!dark);
  // THEN the switch follows without persisting an explicit choice.
  expect(control).toHaveAttribute('aria-checked', String(!dark));
  expect(document.documentElement.dataset.theme).toBe(!dark ? 'dark' : 'light');
  expect(localStorage.getItem('workbench.appearance')).toBeNull();
  // WHEN the user toggles and the system subsequently changes.
  fireEvent.click(control);
  changeSystem(dark);
  changeSystem(!dark);
  // THEN the user's opposite choice persists and survives a fresh mount.
  expect(control).toHaveAttribute('aria-checked', String(dark));
  expect(localStorage.getItem('workbench.appearance')).toBe(dark ? 'dark' : 'light');
  view.unmount();
  render(<Harness />);
  expect(screen.getByRole('switch')).toHaveAttribute('aria-checked', String(dark));
  expect(document.documentElement.dataset.theme).toBe(dark ? 'dark' : 'light');
});

it('keeps the explicit choice usable when storage and matchMedia are unavailable', () => {
  // GIVEN a browser without media queries and denied preference storage.
  vi.stubGlobal('matchMedia', undefined);
  vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
    throw new Error('denied');
  });
  vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
    throw new Error('denied');
  });
  render(<Harness />);
  const control = screen.getByRole('switch', { name: 'Dark theme' });
  expect(control).toHaveAttribute('aria-checked', 'false');
  // WHEN the user toggles in both directions.
  fireEvent.click(control);
  // THEN the page and switch reflect the explicit choice despite storage failure.
  expect(control).toHaveAttribute('aria-checked', 'true');
  expect(document.documentElement.dataset.theme).toBe('dark');
  fireEvent.click(control);
  expect(control).toHaveAttribute('aria-checked', 'false');
  expect(document.documentElement.dataset.theme).toBe('light');
});
