import { expect, test, type Page } from './diagnostic-fixture';
import { useAuthenticatedSession } from './auth-fixture';

test.setTimeout(120_000);

function comparison(page: Page, heading: string) {
  return page.locator('.po-comparison-content').filter({
    has: page.getByRole('heading', { name: heading, exact: true }),
  });
}

async function expectToolbarScrollTransition(page: Page) {
  const toolbar = page.locator('.po-editor-toolbar');
  await page.evaluate(() => window.scrollTo(0, 0));
  await expect(toolbar).not.toHaveClass(/is-pinned/);
  await expect(toolbar).toHaveCSS('background-color', 'rgba(0, 0, 0, 0)');
  await page.evaluate(() => window.scrollTo(0, 600));
  await expect(toolbar).toHaveClass(/is-pinned/);
  await expect(toolbar).toHaveCSS('background-color', /^rgb\(/);
  await expect(toolbar).not.toHaveCSS('box-shadow', 'none');
  await page.evaluate(() => window.scrollTo(0, 0));
  await expect(toolbar).not.toHaveClass(/is-pinned/);
  await expect(toolbar).toHaveCSS('background-color', 'rgba(0, 0, 0, 0)');
}

test('an ordered purchase preserves unknown agreed costs and its original revision after amendment', async ({ page }) => {
  // GIVEN an owner has saved valid quantities with an unknown stone price and estimated shipping.
  await useAuthenticatedSession(page);
  await page.goto('/purchase-orders/new');
  const title = `PO-04 agreement ${Date.now()}`;
  await page.getByLabel('Title', { exact: true }).fill(title);
  await page.getByLabel('Supplier name', { exact: true }).fill('Original gemstone supplier');
  await page.getByLabel('Currency', { exact: true }).fill('USD');
  await page.getByRole('button', { name: 'Add line', exact: true }).first().click();
  await page.getByLabel('Description 1', { exact: true }).fill('Blue sapphires');
  await page.getByLabel('Quantity 1', { exact: true }).fill('2');
  await page.getByLabel('Unit 1', { exact: true }).selectOption('piece');
  await page.getByRole('button', { name: 'Add charge', exact: true }).click();
  await page.getByLabel('Category 1', { exact: true }).selectOption('shipping');
  await page.getByLabel('Charge amount 1', { exact: true }).fill('500');
  await page.getByLabel('Amount status 1', { exact: true }).selectOption('estimated');
  await page.getByRole('button', { name: 'Save draft', exact: true }).click();
  await expect(page).toHaveURL(/\/purchase-orders\/[a-f0-9-]{36}$/);
  await expect(page.getByRole('heading', { level: 1 })).toHaveText(/PO-/);
  const reference = await page.getByRole('heading', { level: 1 }).innerText();
  const orderPath = new URL(page.url()).pathname;

  // WHEN the owner records the saved purchase as ordered on an explicit calendar date.
  await page.getByRole('button', { name: 'Record as ordered', exact: true }).click();
  const dialog = page.getByRole('dialog', { name: 'Record as ordered', exact: true });
  const summary = dialog.getByRole('region', { name: 'Saved purchase summary' });
  await expect(summary).toContainText('Original gemstone supplier');
  await expect(summary).toContainText('1 line · USD');
  await expect(summary).toContainText('Unknown or incomplete costs: 1 line');
  await expect(summary.getByText('Total purchase estimate', { exact: true })).toBeVisible();
  // AND expanding complete contents at phone width keeps the confirmation controls inside the modal.
  await page.setViewportSize({ width: 390, height: 844 });
  await dialog.getByText('Review saved contents', { exact: true }).click();
  const confirm = dialog.getByRole('button', { name: 'Confirm order', exact: true });
  await expect(confirm).toBeInViewport({ ratio: 1 });
  const dialogBounds = (await dialog.boundingBox())!;
  const confirmBounds = (await confirm.boundingBox())!;
  expect(confirmBounds.y + confirmBounds.height).toBeLessThanOrEqual(dialogBounds.y + dialogBounds.height);
  expect(dialogBounds.y + dialogBounds.height).toBeLessThanOrEqual(844);
  expect(await dialog.evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true);
  await page.getByLabel('Order date', { exact: true }).fill('2026-09-12');
  await page.getByRole('button', { name: 'Confirm order', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Create amendment', exact: true })).toBeVisible();
  await page.reload();
  await page.setViewportSize({ width: 1440, height: 960 });

  // THEN the persisted agreement is ordered and cannot use draft editing or deletion.
  await expect(page.getByRole('heading', { level: 1 })).toHaveText(reference);
  await expect(page.locator('time[datetime="2026-09-12"]')).toHaveText('2026-09-12');
  await expect(page.getByText('Ordered', { exact: true }).first()).toBeVisible();
  await expect(page.getByRole('button', { name: 'Save draft', exact: true })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Delete draft', exact: true })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Clear all amounts', exact: true })).toHaveCount(0);
  await expect(page.getByRole('textbox', { name: 'Quantity 1', exact: true })).toHaveCount(0);
  await expect(page.getByText('Supplier estimate', { exact: true })).toBeVisible();
  await expect(page.getByText('Total purchase estimate', { exact: true })).toBeVisible();

  // AND estimate details remain available without crowding the initial ordered view.
  const estimateBreakdown = page.locator('.po-estimate-breakdown');
  await expect(estimateBreakdown).not.toHaveAttribute('open');
  await estimateBreakdown.getByText('Estimate breakdown', { exact: true }).click();
  await expect(estimateBreakdown.getByText('Supplier charges', { exact: true })).toBeVisible();
  await estimateBreakdown.getByText('Estimate breakdown', { exact: true }).click();

  // WHEN scrolling the ordered record THEN its toolbar transitions just like the editing view.
  await page.getByText('Show complete agreed contents', { exact: true }).click();
  await expectToolbarScrollTransition(page);
  await page.getByText('Show complete agreed contents', { exact: true }).click();
  // AND concise itemization is visible before estimates without opening complete record details.
  const contents = page.getByRole('region', { name: 'Items', exact: true });
  await expect(contents.getByRole('heading', { name: 'Blue sapphires', exact: true })).toBeInViewport();
  await expect(contents).toContainText('2 pieces');
  await expect(contents).toContainText('Unknown');
  await expect(contents).toContainText('USD 5.00');
  await expect(contents).toContainText('Estimated');
  await expect(comparison(page, 'Complete agreed contents')).toBeHidden();
  expect((await contents.boundingBox())!.y).toBeLessThan((await page.locator('.po-financial-summary').boundingBox())!.y);

  // AND the first revision shows the original supplier, quantity and unresolved unit price.
  await page.getByRole('button', { name: 'View history', exact: true }).click();
  await page.getByRole('button', { name: 'View revision 1', exact: true }).click();
  const original = comparison(page, 'Revision 1');
  await expect(page.getByRole('region', { name: 'Revision 1 details', exact: true })).toBeVisible();
  await expect(original).toBeHidden();
  await page.getByText('Show complete revision contents', { exact: true }).click();
  await expect(original).toContainText('Original gemstone supplier');
  await expect(original).toContainText('Blue sapphires');
  await expect(original.locator('div').filter({ has: page.locator('dt').getByText('Quantity', { exact: true }) })).toContainText('2 pieces');
  await expect(original.locator('div').filter({ has: page.locator('dt').getByText('Unit price', { exact: true }) })).toContainText('Unknown');
  await expect(original).toContainText('Estimated');
  const agreedContents = await original.innerText();
  const commitmentHistory = page.locator('.po-history-list li').filter({ has: page.getByRole('button', { name: 'View revision 1', exact: true }) });
  const originalActor = await commitmentHistory.locator('.po-history-actor').innerText();
  const originalRecordedAt = await commitmentHistory.locator('time').getAttribute('datetime');
  expect(originalActor).toMatch(/Recorded by [a-f0-9-]{36}/i);
  expect(originalRecordedAt).toBeTruthy();

  // WHEN the owner amends the supplier, quantity and price with an explanation.
  await page.goto(orderPath);
  await page.getByRole('button', { name: 'Create amendment', exact: true }).click();
  await expectToolbarScrollTransition(page);
  await expect(page.getByLabel('Currency', { exact: true })).toBeDisabled();
  await page.locator('.po-supplier-summary').click();
  await page.getByLabel('Supplier name', { exact: true }).fill('Corrected gemstone supplier');
  await page.locator('.po-line-disclosure > summary').first().click();
  await page.getByLabel('Quantity 1', { exact: true }).fill('3');
  await page.getByLabel('Unit price 1', { exact: true }).fill('2000');
  await page.getByLabel('Amendment reason', { exact: true }).fill('Supplier confirmed three stones and corrected their trading name.');
  await page.getByRole('button', { name: 'Review amendment', exact: true }).click();
  // THEN review is a separate screen with explicit before/after pairs and no editable form.
  await expect(page.getByRole('heading', { name: 'Review amendment', exact: true })).toBeFocused();
  await expect(page.locator('#po-draft-form')).toHaveCount(0);
  await expect(comparison(page, 'Current agreed contents')).toBeHidden();
  const changes = page.getByRole('region', { name: 'Changes', exact: true });
  const supplierChange = changes.locator('.po-change-row').filter({ has: page.getByText('Supplier', { exact: true }) });
  await expect(supplierChange.locator('dd')).toHaveText(['Original gemstone supplier', 'Corrected gemstone supplier']);
  const quantityChange = changes.locator('.po-change-row').filter({ has: page.getByText('Line 1 · Quantity', { exact: true }) });
  await expect(quantityChange.locator('dt')).toHaveText(['Before', 'After']);
  await expect(quantityChange.locator('dd')).toHaveText(['2', '3']);
  await page.getByRole('button', { name: 'Keep editing', exact: true }).click();
  await expect(page.getByLabel('Quantity 1', { exact: true })).toHaveValue('3');
  await expect(page.getByLabel('Amendment reason', { exact: true })).toHaveValue('Supplier confirmed three stones and corrected their trading name.');
  await page.getByRole('button', { name: 'Review amendment', exact: true }).click();
  await page.getByRole('button', { name: 'Record amendment', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Create amendment', exact: true })).toBeVisible();
  await page.reload();

  // THEN the latest purchase estimate reflects the amendment and remains usable on a phone.
  await expect(page.getByRole('heading', { level: 1 })).toHaveText(reference);
  await expect(page.locator('time[datetime="2026-09-12"]')).toHaveText('2026-09-12');
  await expect(page.locator('.po-summary-total dd')).toHaveText('USD 65.00');
  await page.setViewportSize({ width: 390, height: 844 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await page.getByRole('button', { name: 'View history', exact: true }).click();
  await page.getByRole('button', { name: 'View revision 2', exact: true }).click();
  const amended = comparison(page, 'Revision 2');
  await expect(page.getByRole('region', { name: 'Revision 2 details', exact: true })).toBeVisible();
  await expect(amended).toBeHidden();
  await expect(quantityChange.locator('dd')).toHaveText(['2', '3']);
  await page.getByText('Show complete revision contents', { exact: true }).click();
  await expect(amended).toContainText('Corrected gemstone supplier');
  await expect(amended.locator('div').filter({ has: page.locator('dt').getByText('Quantity', { exact: true }) })).toContainText('3 pieces');
  await expect(amended.locator('div').filter({ has: page.locator('dt').getByText('Unit price', { exact: true }) })).toContainText('20.00');
  await expect(page.getByRole('region', { name: 'Order history', exact: true })).toContainText('Supplier confirmed three stones and corrected their trading name.');

  // AND inspecting the earlier revision after reload retains exactly the originally agreed contents.
  await page.getByRole('button', { name: 'View revision 1', exact: true }).click();
  await expect(page.getByRole('region', { name: 'Revision 1 details', exact: true })).toBeVisible();
  // The disclosure may retain its open state while switching the selected revision.
  if (await comparison(page, 'Revision 1').isHidden()) {
    await page.getByText('Show complete revision contents', { exact: true }).click();
  }
  await expect(comparison(page, 'Revision 1')).toHaveText(agreedContents, { useInnerText: true });
  await expect(commitmentHistory.locator('.po-history-actor')).toHaveText(originalActor);
  await expect(commitmentHistory.locator('time')).toHaveAttribute('datetime', originalRecordedAt!);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);

  // WHEN filtering the list by status THEN the ordered badge and filter survive a detail visit.
  await page.goto('/purchase-orders');
  const status = page.getByRole('combobox', { name: 'Order status' });
  await status.selectOption('Ordered');
  await page.getByRole('searchbox', { name: 'Search purchase orders' }).fill(title);
  const listRow = page.locator('.po-draft-list a').filter({ hasText: title });
  await expect(listRow.locator('.po-status-badge')).toHaveText('Ordered');
  await listRow.click();
  await page.getByRole('button', { name: 'Purchase orders', exact: true }).click();
  await expect(status).toHaveValue('Ordered');
  // WHEN clearing status THEN the search remains and keyboard focus returns to the single filter box.
  await page.getByRole('button', { name: 'Clear status filter' }).click();
  await expect(status).toHaveValue('');
  await expect(status).toBeFocused();
  await expect(page.getByRole('searchbox', { name: 'Search purchase orders' })).toHaveValue(title);
  await expect(listRow).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
});
