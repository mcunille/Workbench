import { readAppearance, applyAppearance } from './appearance';
describe('Appearance', () => {
  it('falls back to System for corrupt or inaccessible storage', () => {
    // GIVEN invalid or denied preference storage WHEN reading THEN use System
    expect(readAppearance({ getItem: () => 'invalid' })).toBe('system');
    expect(
      readAppearance({
        getItem: () => {
          throw new Error('denied');
        },
      }),
    ).toBe('system');
  });
  it('resolves explicit preferences independently of the operating system', () => {
    // GIVEN a dark operating system WHEN Light is chosen THEN native and page themes are light
    applyAppearance('light', true);
    expect(document.documentElement.dataset.theme).toBe('light');
    expect(document.documentElement.style.colorScheme).toBe('light');
    // WHEN System is chosen THEN follow the operating system
    applyAppearance('system', true);
    expect(document.documentElement.dataset.theme).toBe('dark');
  });
});
