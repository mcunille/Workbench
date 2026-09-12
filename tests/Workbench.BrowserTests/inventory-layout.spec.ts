import { expect, test, type Page } from '@playwright/test';
import { useAuthenticatedSession } from './auth-fixture';
import { cameraImage } from './photo-fixture';

async function savePiece(page: Page, name: string) {
  await useAuthenticatedSession(page);
  await page.getByRole('link', { name: 'Add item', exact: true }).click();
  await page.getByLabel('Name', { exact: true }).fill(name);
  await page.getByLabel('Storage location (optional)', { exact: true }).fill('Tray A, drawer 2');
  await page.getByLabel('Notes (optional)', { exact: true }).fill('Oval violet stone with a small inclusion near the edge.');
  await page.getByRole('button', { name: 'Save item', exact: true }).click();
  await expect(page.getByRole('heading', { name, exact: true })).toBeVisible();
}

test('a phone shows a photo-free piece and its location before scrolling', async ({ page }) => {
  // GIVEN a useful inventory record entered without a photograph.
  const name = 'Layout sample violet stone';
  await savePiece(page, name);
  await page.getByRole('link', { name: 'Back to collection', exact: true }).click();
  await page.getByRole('searchbox').fill(name);
  await page.getByRole('button', { name: 'Search', exact: true }).click();
  const record = page.getByRole('link').filter({ has: page.getByText(name, { exact: true }) });
  await expect(record).toBeVisible();

  // WHEN browsing the default grid on a phone, starting at the top of the page.
  await page.setViewportSize({ width: 390, height: 844 });
  await page.evaluate(() => window.scrollTo(0, 0));
  await expect(page.getByRole('button', { name: 'Grid', exact: true })).toHaveAttribute('aria-pressed', 'true');

  // THEN both identity and storage location are readable above the navigation dock.
  const dock = await page.getByRole('navigation', { name: 'Workspace', exact: true }).boundingBox();
  const location = await record.getByText('Tray A, drawer 2', { exact: true }).boundingBox();
  expect(location!.y + location!.height).toBeLessThan(dock!.y);
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(390);
});

test('saved location and notes lead the item detail reading order', async ({ page }) => {
  // GIVEN a saved piece with information needed to locate and recognize it.
  await page.setViewportSize({ width: 1280, height: 720 });
  await savePiece(page, 'Layout sample detail stone');

  // WHEN opening the saved record without entering a maintenance workflow.
  await page.evaluate(() => window.scrollTo(0, 0));
  const location = page.getByText('Tray A, drawer 2', { exact: true });
  const notes = page.getByText('Oval violet stone with a small inclusion near the edge.', { exact: true });
  const photograph = page.getByRole('heading', { name: 'Photograph', exact: true });

  // THEN location and notes precede photograph maintenance in both reading and visual order.
  expect(await location.evaluate(element => Boolean(element.compareDocumentPosition(document.querySelector('#photo-heading')!) & Node.DOCUMENT_POSITION_FOLLOWING))).toBe(true);
  const notesBounds = await notes.boundingBox();
  const photoBounds = await photograph.boundingBox();
  expect(notesBounds!.y + notesBounds!.height).toBeLessThan(photoBounds!.y);
  expect(notesBounds!.y + notesBounds!.height).toBeLessThan(720);
});

test('grid cards align across different title lengths and photograph availability', async ({ page }) => {
  // GIVEN short and long titles, and a third record with a saved photograph.
  const names = [
    'Uniform grid stone',
    'Uniform grid sterling silver pendant with a long descriptive catalog name',
    'Uniform grid photograph',
  ];
  for (const name of names) await savePiece(page, name);
  await page.getByLabel('Choose photograph', { exact: true }).setInputFiles(await cameraImage(page));
  await expect(page.getByAltText('Prepared photograph preview')).toBeVisible();
  await page.getByRole('button', { name: 'Upload photograph', exact: true }).click();
  await expect(page.getByText('Current saved photograph loaded.', { exact: true })).toBeVisible();
  await page.getByRole('link', { name: 'Back to collection', exact: true }).click();
  await page.getByRole('searchbox').fill('Uniform grid');
  await page.getByRole('button', { name: 'Search', exact: true }).click();
  await expect(page.getByRole('status')).toContainText('3 matching items loaded');

  // WHEN browsing the same grid on desktop and phone.
  for (const width of [1280, 390]) {
    await page.setViewportSize({ width, height: 844 });
    const cards = names.map(name => page.getByRole('link').filter({ has: page.getByText(name, { exact: true }) }));
    const bounds = await Promise.all(cards.map(card => card.boundingBox()));
    const photoBounds = await Promise.all(cards.map(card => card.locator('.item-photo').boundingBox()));

    // THEN every card has the same dimensions and image area, with complete titles.
    const heights = bounds.map(box => box!.height);
    const widths = bounds.map(box => box!.width);
    expect(Math.max(...heights) - Math.min(...heights)).toBeLessThan(1);
    expect(Math.max(...widths) - Math.min(...widths)).toBeLessThan(1);
    const photoHeights = photoBounds.map(box => box!.height);
    expect(Math.max(...photoHeights) - Math.min(...photoHeights)).toBeLessThan(1);
    const imageBounds = await cards[2].getByRole('img').boundingBox();
    expect(imageBounds!.height).toBeLessThanOrEqual(photoBounds[2]!.height);
    expect(imageBounds!.width).toBeLessThanOrEqual(photoBounds[2]!.width);
    for (const [index, card] of cards.entries()) await expect(card.getByText(names[index], { exact: true })).toBeVisible();
  }
});
