import { expect, test } from './diagnostic-fixture';
import { useInterceptedSession } from './intercepted-auth-fixture';
import { setAppearance } from './user-menu-fixture';

test('saved discounted purchase retains keyboard order and geometry across layout boundaries', async ({ page }) => {
  // GIVEN a saved discounted purchase with long payee text and a large monetary value.
  const id = '00000000-0000-4000-8000-000000000001';
  const draft = {
    title: 'Charge layout', supplierName: 'Sample supplier', supplierId: null,
    supplierContactName: null, supplierEmail: null, supplierPhone: null, supplierWebsite: null,
    supplierPostalAddress: null, supplierOrderReference: null, platform: null,
    currency: 'USD', notes: null, sourceLinks: [],
    entries: [{ id: '00000000-0000-4000-8000-000000000004', description: 'Stones',
      notes: null, sourceLink: null, indicativePrice: null, quantity: '10', unitOfMeasure: 'piece',
      priceMode: 'perUnit', price: '20.00', legacyPricing: null, supplierSku: null, itemType: null,
      discount: { mode: 'percentage', value: '10' } }],
    orderDiscount: { mode: 'fixed', value: '10.00' },
    charges: [
      { id: '00000000-0000-4000-8000-000000000002', category: 'shipping', label: 'Shipping / freight',
        amount: '15.00', payeeKind: 'supplier', payeeName: null, amountStatus: 'confirmed', reference: null, notes: null },
      { id: '00000000-0000-4000-8000-000000000003', category: 'paymentFee', label: 'Payment / bank / currency-conversion fee',
        amount: '999999999.99', payeeKind: 'thirdParty',
        payeeName: 'International payment processing and currency conversion department',
        amountStatus: 'confirmed', reference: null, notes: null },
    ],
  };
  const calculation = { lines: [{ id: '00000000-0000-4000-8000-000000000004',
    gross: '200.00', discountBase: '200.00', discountAmount: '20.00', net: '180.00' }],
    incompleteLineCount: 0, merchandiseEstimate: '200.00', lineDiscountTotal: '20.00',
    merchandiseNet: '180.00', orderDiscountBase: '180.00', orderDiscountAmount: '10.00',
    discountedMerchandise: '170.00', supplierCharges: '15.00', thirdPartyCharges: '999999999.99',
    supplierEstimate: '185.00', purchaseEstimate: '1000000184.99', incompleteChargeCount: 0 };
  await page.route('**/api/beta/items', route => route.fulfill({ json: { items: [], nextCursor: null } }));
  await page.route('**/api/beta/purchase-order-drafts/calculate', route => {
    expect(route.request().method()).toBe('POST');
    return route.fulfill({ json: calculation });
  });
  await page.route(`**/api/beta/purchase-orders/${id}`, route => {
    expect(route.request().method()).toBe('GET');
    return route.fulfill({ json: { id, draft, calculation, version: 'AAAAAAAAAAA=', poReference: 'PO-000001',
      supplierIsArchived: false, createdAtUtc: '2026-09-01T12:00:00Z', updatedAtUtc: '2026-09-01T12:00:00Z' } });
  });
  await useInterceptedSession(page);
  await page.goto(`/purchase-orders/${id}`);
  await expect(page.locator('.po-summary-total dd')).toHaveText('USD 1000000184.99');
  await expect(page.getByLabel('Edit line 1: Stones', { exact: true })).toContainText('USD 180.00');
  await expect(page.getByRole('button', { name: 'Remove order discount', exact: true })).toBeVisible();
  const toggle = page.getByLabel('Edit charge 2: Payment / bank / currency-conversion fee', { exact: true });
  // WHEN inspecting collapsed and expanded charges in both appearances at each layout boundary.
  for (const dark of [false, true]) {
    await setAppearance(page, dark);
    for (const width of [320, 820, 1440]) {
      await page.setViewportSize({ width, height: 960 });
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
      await toggle.click();
      await expect(page.getByLabel('Payee name 2', { exact: true })).toBeVisible();
      // THEN controls follow the visual reading order and remain inside the viewport.
      if (width === 820) {
        const box = async (label: string) => (await page.getByLabel(label, { exact: true }).boundingBox())!;
        expect(Math.abs((await box('Charge amount 2')).y - (await box('Amount status 2')).y)).toBeLessThan(2);
        expect(Math.abs((await box('Payee 2')).y - (await box('Payee name 2')).y)).toBeLessThan(2);
        expect((await box('Payee 2')).y).toBeGreaterThan((await box('Charge amount 2')).y);
        await page.getByLabel('Amount status 2', { exact: true }).press('Tab');
        await expect(page.getByLabel('Payee 2', { exact: true })).toBeFocused();
      }
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
      if (width === 320) {
        await page.evaluate(() => { document.documentElement.style.fontSize = '200%'; });
        expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
        await page.evaluate(() => { document.documentElement.style.fontSize = ''; });
      }
      await toggle.click();
    }
  }
});
