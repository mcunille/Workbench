import { expect, test } from '@playwright/test';
import { photoSignIn } from './photo-fixture';

for (const width of [320, 1280]) {
  test(`collection and archive search stay single-line at ${width}px`, async ({ page }) => {
    // GIVEN the collection and archive at a narrow or desktop viewport.
    await page.setViewportSize({ width, height: 900 });
    await photoSignIn(page);
    for (const path of ['/inventory', '/inventory/archive']) {
      await page.goto(path);
      const search = page.getByRole('searchbox');
      // WHEN the search is empty or populated and focused.
      for (const value of ['', 'A remembered piece']) {
        await search.fill(value);
        // THEN the input and actions retain a compact, usable single-line height.
        for (const control of [search, page.getByRole('button', { name: 'Search', exact: true }), page.getByRole('button', { name: 'Clear', exact: true })]) {
          const box = (await control.boundingBox())!;
          expect(box.height).toBeGreaterThanOrEqual(44);
          expect(box.height).toBeLessThanOrEqual(60);
        }
        expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
      }
    }
  });
}
