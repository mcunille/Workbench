import { expect, test, type Page } from '@playwright/test';
import { useAuthenticatedSession as signIn } from './auth-fixture';
import type { Supplier } from '../../src/Workbench.Client/src/api/suppliers';

function supplier(index: number): Supplier {
  return {
    id: `00000000-0000-4000-8000-${String(index).padStart(12, '0')}`,
    supplier: { name: `Gem supplier ${String(index).padStart(2, '0')}`, contactName: `Contact ${index}`, email: `supplier${index}@example.test`, phone: null, website: null, postalAddress: null },
    isArchived: index === 20,
    createdAtUtc: '2026-09-01T12:00:00Z', updatedAtUtc: '2026-09-01T12:00:00Z', version: 'AAAAAAAAB9E=',
  };
}

async function directoryFixture(page: Page, size = 2) {
  const records = Array.from({ length: size }, (_, index) => supplier(index + 1));
  let pending: Promise<void> | undefined;
  let release: (() => void) | undefined;
  await page.route('**/api/suppliers**', async route => {
    const request = route.request();
    const url = new URL(request.url());
    const id = url.pathname.split('/')[3];
    if (id) {
      const record = records.find(item => item.id === id);
      if (!record) return route.fulfill({ status: 404 });
      if (request.method() === 'PUT') {
        const body = request.postDataJSON();
        record.supplier = body.supplier;
        record.version = 'AAAAAAAAB9I=';
        record.updatedAtUtc = '2026-09-15T12:00:00Z';
        return route.fulfill({ json: { requestId: body.requestId, supplierId: id, savedVersion: record.version, replayed: false, completedAtUtc: record.updatedAtUtc } });
      }
      return route.fulfill({ json: record });
    }
    if (pending) await pending;
    const query = (url.searchParams.get('query') ?? '').toLowerCase();
    const includeArchived = url.searchParams.get('includeArchived') === 'true';
    return route.fulfill({ json: { items: records.filter(item => (includeArchived || !item.isArchived) && item.supplier.name.toLowerCase().includes(query)), nextCursor: null } });
  });
  return {
    records,
    hold() { pending = new Promise<void>(resolve => { release = resolve; }); },
    resume() { release?.(); pending = undefined; },
  };
}

for (const width of [320, 1440]) for (const textSize of [100, 200]) {
  test(`supplier rows and search remain usable at ${width}px with ${textSize}% text`, async ({ page }) => {
    // GIVEN an isolated synthetic directory at a supported viewport and text size.
    await signIn(page);
    const fixture = await directoryFixture(page);
    await page.setViewportSize({ width, height: 900 });
    await page.goto('/suppliers');
    await page.evaluate(size => { document.documentElement.style.fontSize = `${size}%`; }, textSize);
    // AND wider fallback metrics reproduce loading feedback wrapping on Linux.
    await page.addStyleTag({ content: '.po-supplier-results-toolbar .po-draft-progress { font-family: monospace; }' });
    const directory = page.getByRole('region', { name: 'Supplier directory' });
    const row = directory.getByRole('link', { name: 'Edit Gem supplier 01', exact: true });
    const search = directory.getByRole('searchbox', { name: 'Search suppliers', exact: true });
    const refresh = directory.getByRole('button', { name: 'Refresh suppliers', exact: true });
    const status = directory.getByRole('status');
    await expect(status).toContainText('2');
    await expect(row).toHaveAttribute('href', `/suppliers/${fixture.records[0].id}`);
    expect(await row.evaluate(element => element.tagName)).toBe('A');

    // WHEN opening from the name, contact summary, or otherwise empty row padding.
    for (const target of ['name', 'contact', 'padding']) {
      if (target === 'name') await row.getByText('Gem supplier 01', { exact: true }).click();
      else if (target === 'contact') await row.getByText(/Contact 1/).click();
      else await row.click({ position: { x: 3, y: 3 } });
      // THEN every portion is the same native navigation to the supplier editor.
      await expect(page).toHaveURL(new RegExp(`/suppliers/${fixture.records[0].id}$`));
      await expect(page.getByLabel('Supplier name', { exact: true })).toHaveValue('Gem supplier 01');
      await page.getByRole('button', { name: 'Back to suppliers', exact: true }).click();
      await expect(row).toBeVisible();
    }

    // GIVEN settled geometry before any search controls or feedback change.
    await expect(status).toContainText('2');
    const panel = directory.locator('.po-supplier-list');
    const controls = [search, refresh, panel];
    const originalScroll = await page.evaluate(() => scrollY);
    const original = await Promise.all(controls.map(control => control.boundingBox()));
    const expectAnchored = async () => {
      for (let index = 0; index < controls.length; index++) {
        const box = (await controls[index].boundingBox())!;
        expect(box.x).toBeCloseTo(original[index]!.x, 0);
        expect(box.y + await page.evaluate(() => scrollY)).toBeCloseTo(original[index]!.y + originalScroll, 0);
        expect(box.width).toBeCloseTo(original[index]!.width, 0);
      }
    };
    fixture.hold();
    try {
      // WHEN a search is delayed THEN neither actions nor existing rows shift.
      await search.fill('Gem supplier 01');
      await expect(status).toContainText('Loading suppliers');
      await expectAnchored();
    } finally { fixture.resume(); }
    // THEN completion retains a live status and announces the matching count.
    await expect(status).not.toContainText('Loading suppliers');
    await expect(status).toContainText('1');
    await expect(status).toHaveAttribute('aria-live', 'polite');
    await expectAnchored();
    // WHEN clearing THEN keyboard focus returns to search and geometry is preserved.
    await directory.getByRole('button', { name: 'Clear search', exact: true }).click();
    await expect(search).toBeFocused();
    await expect(search).toHaveValue('');
    await expect(status).toContainText('2');
    await expectAnchored();
    // WHEN nothing matches THEN the same status region reports the empty result.
    await search.fill('No such supplier');
    await expect(status).toContainText('No matching suppliers');
    await expect(status).toHaveCount(1);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    expect((await refresh.boundingBox())!.height).toBeGreaterThanOrEqual(44);
  });

  test(`supplier return retains filters and position after saving at ${width}px with ${textSize}% text`, async ({ page }) => {
    // GIVEN a filtered directory containing enough synthetic records to require scrolling.
    await signIn(page);
    const fixture = await directoryFixture(page, 20);
    await page.setViewportSize({ width, height: 900 });
    await page.goto('/suppliers');
    await page.evaluate(size => { document.documentElement.style.fontSize = `${size}%`; }, textSize);
    const search = page.getByRole('searchbox', { name: 'Search suppliers', exact: true });
    const archived = page.getByLabel('Include archived suppliers');
    await search.fill('Gem supplier');
    await archived.check();
    await expect(page.getByRole('status')).toContainText('20');
    const row = page.getByRole('link', { name: 'Edit Gem supplier 15', exact: true });
    await row.scrollIntoViewIfNeeded();
    const scroll = await page.evaluate(() => scrollY);
    expect(scroll).toBeGreaterThan(0);
    // WHEN inspecting a supplier and returning without changes.
    await row.click();
    await expect(page.getByLabel('Supplier name', { exact: true })).toHaveValue('Gem supplier 15');
    await page.getByRole('button', { name: 'Back to suppliers', exact: true }).click();
    // THEN the query, archived filter, and reading position are retained.
    await expect(search).toHaveValue('Gem supplier');
    await expect(archived).toBeChecked();
    await expect.poll(() => page.evaluate(() => scrollY)).toBeCloseTo(scroll, 0);
    await expect(page.getByRole('link', { name: 'Edit Gem supplier 20', exact: true })).toHaveCount(1);
    // WHEN saving changed contact details and returning to the directory.
    await row.click();
    await page.getByLabel('Contact name', { exact: true }).fill('Updated contact');
    await page.getByRole('button', { name: 'Save supplier', exact: true }).click();
    await expect(page.getByRole('status')).toContainText('Saved ');
    await expect(page.getByRole('button', { name: 'Save supplier', exact: true })).toBeDisabled();
    await expect(page.getByRole('status')).not.toContainText('Unsaved changes');
    await page.getByRole('button', { name: 'Back to suppliers', exact: true }).click();
    // THEN refreshed contact data appears without resetting the user's place or filters.
    await expect(row).toContainText('Updated contact');
    await expect(search).toHaveValue('Gem supplier');
    await expect(archived).toBeChecked();
    await expect.poll(() => page.evaluate(() => scrollY)).toBeCloseTo(scroll, 0);
    expect(fixture.records[14].supplier.contactName).toBe('Updated contact');
  });
}
