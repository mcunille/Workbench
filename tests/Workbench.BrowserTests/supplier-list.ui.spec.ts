import { expect, test, type Page } from './diagnostic-fixture';
import { useInterceptedSession } from './intercepted-auth-fixture';

async function signIn(page: Page) {
  await page.route('**/api/beta/items', route => route.fulfill({ json: { items: [], nextCursor: null } }));
  await useInterceptedSession(page);
}
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
  await page.route('**/api/beta/suppliers**', async route => {
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

for (const { width, textSize } of [{ width: 1440, textSize: 100 }, { width: 320, textSize: 200 }]) {
  test(`supplier search and results keep stable geometry at ${width}px with ${textSize}% text`, async ({ page }) => {
    // GIVEN an isolated synthetic directory at a supported viewport and text size.
    await signIn(page);
    const fixture = await directoryFixture(page);
    await page.setViewportSize({ width, height: 900 });
    await page.goto('/suppliers');
    await page.evaluate(size => { document.documentElement.style.fontSize = `${size}%`; }, textSize);
    // AND wider text metrics expose header actions and loading text that need to wrap across platforms.
    await page.addStyleTag({ content: '.po-list { font-family: monospace; } .po-empty-state { letter-spacing: .08em; }' });
    const directory = page.getByRole('region', { name: 'Supplier directory' });
    const search = directory.getByRole('searchbox', { name: 'Search suppliers', exact: true });
    const refresh = directory.getByRole('button', { name: 'Refresh suppliers', exact: true });
    const status = directory.getByRole('status');
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
    const overflow = await page.evaluate(() => ({
      fits: document.documentElement.scrollWidth <= innerWidth,
      elements: [...document.querySelectorAll('body *')].map(element => ({ tag: element.tagName, class: element.className, right: element.getBoundingClientRect().right, width: element.getBoundingClientRect().width }))
        .filter(element => element.right > innerWidth + .5).slice(0, 12),
    }));
    expect(overflow.fits, JSON.stringify(overflow.elements)).toBe(true);
    expect((await refresh.boundingBox())!.height).toBeGreaterThanOrEqual(44);
  });
}

test('supplier row padding opens the editor and returning retains filters and position after saving', async ({ page }) => {
  // GIVEN a filtered directory containing enough synthetic records to require scrolling.
  await signIn(page);
  const fixture = await directoryFixture(page, 20);
  await page.setViewportSize({ width: 320, height: 900 });
  await page.goto('/suppliers');
  const search = page.getByRole('searchbox', { name: 'Search suppliers', exact: true });
  const archived = page.getByLabel('Include archived suppliers');
  await search.fill('Gem supplier');
  await archived.check();
  await expect(page.getByRole('status')).toContainText('20');
  const row = page.getByRole('link', { name: 'Edit Gem supplier 15', exact: true });
  await row.scrollIntoViewIfNeeded();
  const scroll = await page.evaluate(() => scrollY);
  expect(scroll).toBeGreaterThan(0);
  // WHEN opening a supplier through otherwise empty row padding and returning without changes.
  await expect(row).toHaveAttribute('href', `/suppliers/${fixture.records[14].id}`);
  await row.click({ position: { x: 3, y: 3 } });
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
