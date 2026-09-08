import { expect, test } from '@playwright/test';
import { photoSignIn } from './photo-fixture';

test('enlarged editor labels remain readable without overlapping fields', async ({ page }, testInfo) => {
  // GIVEN the real item editor with 200% text in a narrow viewport.
  await photoSignIn(page);
  await page.goto('/inventory/new');
  await page.setViewportSize({ width: 320, height: 900 });
  await page.addStyleTag({ content: 'html { font-size: 200%; }' });
  await page.emulateMedia({ reducedMotion: 'reduce' });
  for (const theme of ['light', 'dark']) {
    await page.getByRole('combobox', { name: 'Appearance' }).selectOption(theme);
    for (const name of ['Name', 'Notes (optional)', 'Storage location (optional)']) {
      const input = page.getByLabel(name, { exact: true });
      const label = page.getByText(name, { exact: true });
      // WHEN empty, focused, populated, and blurred THEN the full label stays above its input.
      for (const state of ['empty', 'focused', 'populated', 'blurred']) {
        if (state === 'focused') await label.click();
        if (state === 'populated') await input.fill('A saved detail');
        if (state === 'blurred') await page.getByRole('heading', { name: 'Add item' }).click();
        const fieldBox = (await input.boundingBox())!;
        const labelBox = (await label.boundingBox())!;
        expect(labelBox.y + labelBox.height, `${name}: ${state}`).toBeLessThanOrEqual(fieldBox.y);
        expect(labelBox.x).toBeGreaterThanOrEqual(0);
        expect(labelBox.x + labelBox.width).toBeLessThanOrEqual(320);
        if (state === 'focused') await expect(input).toBeFocused();
      }
      await input.fill('');
    }
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await page.screenshot({ path: testInfo.outputPath(`enlarged-editor-${theme}.png`), fullPage: true });
  }
});

test('label colors follow appearance changes without an intermediate color transition', async ({ page }) => {
  // GIVEN a populated label with normal motion enabled.
  await page.goto('/');
  await page.emulateMedia({ reducedMotion: 'no-preference' });
  await page.getByLabel('Email', { exact: true }).fill('collector@example.test');
  await page.getByLabel('Password', { exact: true }).focus();
  // WHEN appearance changes THEN label text immediately uses the new theme color.
  for (const theme of ['dark', 'light']) {
    await page.getByRole('combobox', { name: 'Appearance' }).selectOption(theme);
    const colors = await page.getByText('Email', { exact: true }).evaluate(el => {
      const animations = el.getAnimations().filter(animation =>
        animation instanceof CSSTransition && animation.transitionProperty === 'color');
      // Sampling the middle makes a color-transition regression deterministic.
      for (const animation of animations) { animation.pause(); animation.currentTime = 70; }
      const expected = document.createElement('span');
      expected.style.color = 'var(--muted)';
      el.parentElement!.append(expected);
      const result = { actual: getComputedStyle(el).color, expected: getComputedStyle(expected).color };
      expected.remove();
      return result;
    });
    expect(colors.actual).toBe(colors.expected);
  }
});

for (const appearance of ['light', 'dark']) {
  test(`floating labels preserve names and use a single focus border in ${appearance}`, async ({ page }) => {
    // GIVEN an empty sign-in field in the selected appearance.
    await page.goto('/');
    await page.getByRole('combobox', { name: 'Appearance' }).selectOption(appearance);
    const input = page.getByRole('textbox', { name: 'Email', exact: true });
    const label = page.getByText('Email', { exact: true });
    const labelIsRaised = async () => {
      const fieldBox = await input.boundingBox();
      const labelBox = await label.boundingBox();
      return !!fieldBox && !!labelBox && labelBox.y < fieldBox.y &&
        labelBox.y + labelBox.height > fieldBox.y;
    };
    const fieldBox = await input.boundingBox();
    const labelBox = await label.boundingBox();
    expect(labelBox!.y).toBeGreaterThan(fieldBox!.y);
    const idleBorder = await input.evaluate(el => getComputedStyle(el).borderTopColor);

    // WHEN the label is clicked THEN focus moves to its field and the label crosses the top edge.
    await label.click();
    await expect(input).toBeFocused();
    await expect.poll(labelIsRaised).toBe(true);
    expect(await input.evaluate(el => getComputedStyle(el).borderTopColor)).not.toBe(idleBorder);
    expect(await input.evaluate(el => getComputedStyle(el).borderTopWidth)).toBe('2px');
    expect(await input.boundingBox()).toEqual(fieldBox);
    expect(await input.evaluate(el => getComputedStyle(el).outlineStyle)).toBe('none');
    expect(await input.evaluate(el => getComputedStyle(el).boxShadow)).toBe('none');

    // WHEN a value is entered and focus leaves THEN the label stays raised.
    await input.fill('collector@example.test');
    await page.getByLabel('Password', { exact: true }).focus();
    await expect.poll(labelIsRaised).toBe(true);
    await expect(input).toHaveValue('collector@example.test');

    // WHEN cleared and blurred THEN the label returns inside without losing its accessible name.
    await input.fill('');
    await page.getByLabel('Password', { exact: true }).focus();
    await expect.poll(async () => (await label.boundingBox())!.y > (await input.boundingBox())!.y).toBe(true);

    // WHEN the browser restores a value without an input event THEN the label still clears the value.
    await input.evaluate(el => { (el as HTMLInputElement).value = 'restored@example.test'; });
    await expect.poll(labelIsRaised).toBe(true);

    // AND reduced motion disables label animation while forced colors keeps a visible focus boundary.
    await page.emulateMedia({ reducedMotion: 'reduce' });
    expect(await label.evaluate(el => getComputedStyle(el).transitionDuration)).toBe('0s');
    await page.emulateMedia({ forcedColors: 'active' });
    await input.focus();
    expect(await input.evaluate(el => getComputedStyle(el).borderTopWidth)).toBe('2px');
    expect(await input.evaluate(el => getComputedStyle(el).borderTopStyle)).toBe('solid');
  });
}
