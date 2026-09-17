import { test, expect } from './diagnostic-fixture';
import { useAuthenticatedSession } from './auth-fixture';

test('supplier unit and total line pricing persist with explainable draft estimates', async ({ page }) => {
  // GIVEN a business owner planning stones priced by weight and a fixed quote for settings.
  await useAuthenticatedSession(page);
  await page.goto('/purchase-orders/new');
  await page.getByLabel('Title', { exact: true }).fill(`Itemized purchase ${Date.now()}`);
  await page.getByLabel('Currency', { exact: true }).fill('USD');
  await page.getByRole('button', { name: 'Add line', exact: true }).first().click();
  await page.getByLabel('Description 1', { exact: true }).fill('Ten sapphires');
  await page.getByLabel('Quantity 1', { exact: true }).fill('12.5');
  await page.getByLabel('Unit 1', { exact: true }).selectOption('carat');
  await page.getByLabel('Unit price 1', { exact: true }).fill('2000');
  await page.getByText('Line details', { exact: true }).click();
  await page.getByLabel('Supplier SKU 1', { exact: true }).fill('SAP-10');
  await page.getByLabel('Item type 1', { exact: true }).fill('Gemstone');
  await page.getByRole('button', { name: 'Add line', exact: true }).first().click();
  await page.getByLabel('Description 2', { exact: true }).fill('Settings');
  await page.getByLabel('Quantity 2', { exact: true }).fill('250');
  await page.getByLabel('Unit 2', { exact: true }).selectOption('piece');
  await page.getByRole('radio', { name: 'Total line 2', exact: true }).check();
  await page.getByLabel('Total line price 2', { exact: true }).fill('2000');
  // THEN the subtotal is 250 for stones plus 20 for settings, without inventing a count from weight.
  await expect(page.getByText('Merchandise gross', { exact: true })).toBeVisible();
  await expect(page.locator('.po-summary-merchandise dd')).toHaveText('USD 270.00');
  await expect(page.getByRole('radio', { name: 'Per unit 1', exact: true })).toBeChecked();
  await expect(page.getByLabel('Pricing unit 1', { exact: true })).toHaveCount(0);
  // WHEN saving and reopening THEN supplier quantities, price modes and metadata survive.
  const saved = page.waitForResponse(response => /\/api\/v4\/purchase-order-drafts(?:\/[a-f0-9-]+)?$/.test(response.url()) && response.request().method() === 'POST');
  await page.getByRole('button', { name: 'Save draft', exact: true }).click();
  expect((await saved).ok()).toBeTruthy();
  await expect(page).toHaveURL(/\/purchase-orders\/[a-f0-9-]{36}$/);
  await expect(page.getByLabel('Title', { exact: true })).toBeEnabled();
  await page.reload();
  await page.locator('.po-line-disclosure > summary').first().click();
  await expect(page.getByLabel('Quantity 1', { exact: true })).toHaveValue('12.5');
  await expect(page.getByLabel('Supplier SKU 1', { exact: true })).toHaveValue('SAP-10');
  await expect(page.locator('.po-summary-merchandise dd')).toHaveText('USD 270.00');
  await page.locator('.po-line-disclosure > summary').nth(1).click();
  await expect(page.getByRole('radio', { name: 'Total line 2', exact: true })).toBeChecked();
  await expect(page.getByLabel('Total line price 2', { exact: true })).toHaveValue('20.00');
  // WHEN an incomplete line is added THEN the known subtotal is explicit, never a complete total.
  await page.getByRole('button', { name: 'Add line', exact: true }).first().click();
  await expect(page.getByText('Known line subtotal', { exact: true })).toBeVisible();
  await expect(page.locator('.po-merchandise-estimate')).toContainText('1 line needs quantity or pricing details');
  // AND enlarged text on a narrow screen retains readable controls without horizontal scrolling.
  await page.setViewportSize({ width: 320, height: 900 });
  await page.evaluate(() => { document.documentElement.style.fontSize = '200%'; });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
});

test('quantity and unit controls keep combined labels at ordinary text sizes', async ({ page }) => {
  // GIVEN a line with populated quantities and native unit selectors.
  await useAuthenticatedSession(page);
  await page.goto('/purchase-orders/new');
  await page.getByRole('button', { name: 'Add line', exact: true }).first().click();
  await page.getByLabel('Quantity 1', { exact: true }).fill('10');
  await page.getByLabel('Unit 1', { exact: true }).selectOption('piece');
  await page.getByRole('heading', { name: 'Order lines', exact: true }).click();
  // WHEN desktop and mobile layouts render THEN each label crosses its control's top border.
  for (const width of [1024, 390]) {
    await page.setViewportSize({ width, height: 900 });
    for (const name of ['Quantity 1', 'Unit 1']) {
      const control = page.getByLabel(name, { exact: true });
      const label = page.locator(`label[for="${await control.getAttribute('id')}"]`);
      await expect.poll(async () => {
        const field = (await control.boundingBox())!;
        const text = (await label.boundingBox())!;
        return text.y < field.y && text.y + text.height > field.y;
      }, { message: `${name} has a combined label at ${width}px` }).toBe(true);
    }
  }
});

test('fixed supplier quotes save without quantity and mode changes update the estimate', async ({ page }) => {
  // GIVEN a fixed quote without a known count or weight.
  await useAuthenticatedSession(page);
  await page.goto('/purchase-orders/new');
  await page.getByLabel('Currency', { exact: true }).fill('USD');
  await page.getByRole('button', { name: 'Add line', exact: true }).first().click();
  await page.getByLabel('Description 1', { exact: true }).fill('Fixed parcel quote');
  await page.getByRole('radio', { name: 'Total line 1', exact: true }).check();
  await page.getByLabel('Total line price 1', { exact: true }).fill('25000');
  await expect(page.getByLabel('Quantity 1', { exact: true })).toHaveValue('');
  await expect(page.getByLabel('Unit 1', { exact: true })).toHaveValue('');
  await expect(page.locator('.po-summary-merchandise dd')).toHaveText('USD 250.00');
  // WHEN saved and reopened THEN the quote remains a total without an invented quantity.
  await page.getByRole('button', { name: 'Save draft', exact: true }).click();
  await expect(page).toHaveURL(/\/purchase-orders\/[a-f0-9-]{36}$/);
  await page.reload();
  await page.locator('.po-line-disclosure > summary').first().click();
  await expect(page.getByRole('radio', { name: 'Total line 1', exact: true })).toBeChecked();
  await expect(page.getByLabel('Total line price 1', { exact: true })).toHaveValue('250.00');
  await expect(page.getByLabel('Quantity 1', { exact: true })).toHaveValue('');
  // WHEN explicitly changing to a unit quote THEN its retained amount uses the supplied quantity.
  await page.getByRole('radio', { name: 'Per unit 1', exact: true }).check();
  await expect(page.getByLabel('Unit price 1', { exact: true })).toHaveValue('250.00');
  await page.getByLabel('Quantity 1', { exact: true }).fill('2');
  await page.getByLabel('Unit 1', { exact: true }).selectOption('piece');
  await expect(page.locator('.po-summary-merchandise dd')).toHaveText('USD 500.00');
  await page.getByRole('radio', { name: 'Total line 1', exact: true }).check();
  await expect(page.locator('.po-summary-merchandise dd')).toHaveText('USD 250.00');
});
