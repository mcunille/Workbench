import { expect, test, type Locator } from './diagnostic-fixture';
import { cameraImage, photoSignIn, savedPhotoItem } from './photo-fixture';
import { mkdtemp } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

async function expectNoticeContained(notice: Locator) {
  // Measure the text and frame together so font loading cannot move one between reads.
  await expect.poll(() => notice.evaluate(element => {
    const text = element.getBoundingClientRect();
    const frame = element.parentElement!.getBoundingClientRect();
    return text.top >= frame.top - 1 && text.bottom <= frame.bottom + 1
      && text.left >= frame.left - 1 && text.right <= frame.right + 1;
  })).toBe(true);
}

test('photo failures remain readable in compact lists and enlarged item details', async ({ page }) => {
  const evidence = await mkdtemp(join(tmpdir(), 'workbench-photo-hardening-'));
  console.log(`Photo hardening screenshots: ${evidence}`);
  // GIVEN a real saved photograph whose storage later becomes unavailable.
  await photoSignIn(page);
  const name = 'Photo failure boundary sample';
  await savedPhotoItem(page, name);
  await page.getByLabel('Choose photograph', { exact: true }).setInputFiles(await cameraImage(page));
  await expect(page.getByAltText('Prepared photograph preview')).toBeVisible();
  await page.getByRole('button', { name: 'Upload photograph', exact: true }).click();
  await expect(page.getByAltText(`Photograph of ${name}`, { exact: true })).toBeVisible();
  const detailUrl = page.url();
  let status = 503;
  let unavailable = true;
  await page.route('**/api/items/*/photo/*/*', route => unavailable
    ? route.fulfill({ status, body: '' })
    : route.continue());

  for (status of [503, 410]) {
    unavailable = true;
    // WHEN browsing failures in List view at a narrow width with enlarged text.
    await page.goto('/inventory');
    await page.getByRole('searchbox').fill(name);
    await page.getByRole('button', { name: 'Search', exact: true }).click();
    await page.getByRole('button', { name: 'List', exact: true }).click();
    await page.setViewportSize({ width: 320, height: 568 });
    await page.addStyleTag({ content: 'html { font-size: 200%; }' });
    const card = page.getByRole('link').filter({ has: page.getByText(name, { exact: true }) });
    const notice = card.getByRole('status');
    await expect(notice).toContainText(status === 410 ? 'could not be recovered' : 'Photograph unavailable');

    // THEN the entire explanation fits inside its container and the item remains accessible.
    await expectNoticeContained(notice);
    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(320);
    await card.scrollIntoViewIfNeeded();
    await card.locator('.item-photo').screenshot({ path: join(evidence, `list-${status}.png`) });
    await card.click();
    await expect(page).toHaveURL(detailUrl);
    await expect(page.getByRole('heading', { name, exact: true })).toBeVisible();

    // AND the detail explanation and applicable retry control are not clipped either.
    const detailPhoto = page.locator('.photo-editor > .item-photo');
    const detailNotice = detailPhoto.getByRole('status');
    await expect(detailNotice).toBeVisible();
    await expectNoticeContained(detailNotice);
    await expect(page.getByRole('button', { name: 'Retry photograph', exact: true })).toHaveCount(status === 410 ? 0 : 1);
    await detailPhoto.scrollIntoViewIfNeeded();
    await detailPhoto.screenshot({ path: join(evidence, `detail-${status}.png`) });
    if (status === 503) {
      // WHEN storage recovers THEN the visible retry restores the saved photograph.
      unavailable = false;
      await page.getByRole('button', { name: 'Retry photograph', exact: true }).click();
      await expect(page.getByAltText(`Photograph of ${name}`, { exact: true })).toBeVisible();
    }
  }
});
