import { randomUUID } from 'node:crypto';
import { expect, test, type Page } from './diagnostic-fixture';
import { useAuthenticatedSession } from './auth-fixture';
import { captureEvidence } from './evidence-fixture';
import { createThroughUi, publishThroughUi, signInAdmin } from './service-admin-fixture';

test('published admin draft flows through tenant browse, addition, customization and reset with tenant isolation', async ({ page, browser }) => {
  // GIVEN separate service-admin and tenant browser authorities and a new sourced draft.
  const adminContext = await browser.newContext();
  const otherContext = await browser.newContext();
  try {
    const admin = await adminContext.newPage();
    const other = await otherContext.newPage();
    await signInAdmin(admin);
    await useAuthenticatedSession(page);
    await useAuthenticatedSession(other, 'secondary', 'auth');
    const name = `Synthetic release acceptance ${randomUUID().slice(0, 8)}`;
    const draft = await createThroughUi(admin, name);
    await search(page, name);
    await expect(page.getByText('No gems match these filters.')).toBeVisible();

    // WHEN the admin reviews and publishes through the UI THEN both tenants can browse the sourced entry.
    await publishThroughUi(admin, [name]);
    await search(page, name);
    await page.getByRole('link', { name, exact: true }).click();
    await expect(page.getByRole('region', { name: 'Sources', exact: true })).toContainText('Synthetic browser citation');
    await expect(page.getByText('Mineral species is not required for this material kind.')).toBeVisible();
    await other.goto(`/gem-reference/workbench/${draft.entryId}`);
    await expect(other.getByRole('heading', { name, exact: true })).toBeVisible();

    // WHEN one tenant replaces a sourced field THEN only that tenant sees its unsourced assertion.
    const privateName = `${name} private`;
    await page.getByRole('link', { name: 'Customize for this tenant' }).click();
    await page.getByRole('combobox', { name: 'Common name choice', exact: true }).selectOption('replace');
    await page.getByLabel('Common name', { exact: true }).fill(privateName);
    await page.getByRole('button', { name: 'Save changes', exact: true }).click();
    await expect(page.getByRole('heading', { name: privateName, exact: true })).toBeVisible();
    await page.reload();
    await expect(page.getByRole('heading', { name: privateName, exact: true })).toBeVisible();
    const field = page.locator('dt').filter({ hasText: /^Common name$/ }).locator('..');
    await expect(field).toContainText('Tenant-authored · no sources supplied');
    await other.reload();
    await expect(other.getByRole('heading', { name, exact: true })).toBeVisible();
    await expect(other.getByText(privateName, { exact: true })).toHaveCount(0);
    await captureEvidence(page, 'gem-reference/acceptance-customized.png', { fullPage: true });

    // WHEN explicitly resetting that field THEN its shared value and source attribution return together.
    await page.getByRole('link', { name: 'Customize for this tenant' }).click();
    await expect(page.getByRole('region', { name: 'Common name editor' })).toContainText(name);
    await page.getByRole('button', { name: 'Reset Common name to Workbench' }).click();
    await page.getByRole('button', { name: 'Save changes', exact: true }).click();
    await expect(page.getByRole('heading', { name, exact: true })).toBeVisible();
    await expect(field).toContainText('Workbench reference');
    await field.getByRole('link', { name: 'Supporting sources', exact: true }).click();
    await expect(page.getByRole('listitem', { name: 'Common name · Synthetic browser citation', exact: true })).toBeFocused();

    // WHEN adding a personal entry through the UI THEN it survives reload and remains inaccessible to the other tenant.
    const additionName = `${name} personal addition`;
    await page.goto('/gem-reference');
    await page.getByRole('link', { name: 'Add entry', exact: true }).click();
    await page.getByLabel('Common name', { exact: true }).fill(additionName);
    await page.getByRole('combobox', { name: 'Material kind', exact: true }).selectOption('organic');
    await page.getByRole('button', { name: 'Save changes', exact: true }).click();
    await expect(page.getByRole('heading', { name: additionName, exact: true })).toBeVisible();
    const privatePath = new URL(page.url()).pathname;
    await page.reload();
    await expect(page.getByRole('heading', { name: additionName, exact: true })).toBeVisible();
    await search(other, additionName);
    await expect(other.getByText('No gems match these filters.')).toBeVisible();
    await other.goto(privatePath);
    await expect(other.getByRole('heading', { name: 'Gem reference not found.', exact: true })).toBeVisible();
    expect((await other.request.get(`/api/beta/gem-reference/${privatePath.split('/').at(-1)}?origin=tenant`)).status()).toBe(404);
  } finally {
    await Promise.allSettled([adminContext.close(), otherContext.close()]);
  }
});

async function search(page: Page, query: string) {
  await page.goto('/gem-reference');
  await page.getByLabel('Search gems', { exact: true }).fill(query);
  await page.getByRole('button', { name: 'Search', exact: true }).click();
}
