import { expect, type Page } from '@playwright/test';
export { useAuthenticatedSession as photoSignIn } from './auth-fixture';

// Deliberately synthetic gemstone illustration: no outside photo rights or embedded metadata.
export async function cameraImage(page: Page, hue = 212) {
  const base64 = await page.evaluate((hue) => {
    const canvas = document.createElement('canvas');
    canvas.width = 4096;
    canvas.height = 3072;
    const ctx = canvas.getContext('2d')!;
    const pixels = ctx.createImageData(canvas.width, canvas.height);
    let seed = 41;
    for (let p = 0; p < pixels.data.length; p += 4) {
      seed = (Math.imul(seed, 1664525) + 1013904223) | 0;
      const grain = (seed >>> 24) % 13;
      pixels.data[p] = 231 + grain;
      pixels.data[p + 1] = 225 + grain;
      pixels.data[p + 2] = 214 + grain;
      pixels.data[p + 3] = 255;
    }
    ctx.putImageData(pixels, 0, 0);
    ctx.save();
    ctx.translate(2048, 1480);
    ctx.scale(1.22, 1);
    ctx.shadowColor = '#42351f70';
    ctx.shadowBlur = 100;
    ctx.shadowOffsetY = 100;
    ctx.fillStyle = `hsl(${hue} 72% 30%)`;
    ctx.beginPath();
    ctx.ellipse(0, 0, 920, 1000, 0, 0, Math.PI * 2);
    ctx.fill();
    ctx.shadowColor = 'transparent';
    const outer = Array.from({ length: 12 }, (_, i) => [
      Math.cos((i * Math.PI) / 6) * 920,
      Math.sin((i * Math.PI) / 6) * 1000,
    ]);
    const inner = outer.map(([x, y]) => [x * 0.55, y * 0.55]);
    for (let i = 0; i < 12; i++) {
      ctx.fillStyle = `hsl(${hue + (i % 3) * 4} ${62 + (i % 4) * 7}% ${24 + ((i * 13) % 45)}%)`;
      ctx.beginPath();
      ctx.moveTo(...(outer[i] as [number, number]));
      ctx.lineTo(...(outer[(i + 1) % 12] as [number, number]));
      ctx.lineTo(...(inner[(i + 1) % 12] as [number, number]));
      ctx.lineTo(...(inner[i] as [number, number]));
      ctx.closePath();
      ctx.fill();
    }
    const shine = ctx.createLinearGradient(-550, -500, 650, 600);
    shine.addColorStop(0, `hsl(${hue} 65% 75%)`);
    shine.addColorStop(0.45, `hsl(${hue} 74% 35%)`);
    shine.addColorStop(1, `hsl(${hue} 80% 19%)`);
    ctx.fillStyle = shine;
    ctx.beginPath();
    inner.forEach(([x, y], i) => (i ? ctx.lineTo(x, y) : ctx.moveTo(x, y)));
    ctx.closePath();
    ctx.fill();
    ctx.strokeStyle = '#ffffff80';
    ctx.lineWidth = 7;
    ctx.beginPath();
    ctx.moveTo(-600, -490);
    ctx.lineTo(-250, -830);
    ctx.stroke();
    ctx.restore();
    return canvas.toDataURL('image/png').split(',')[1];
  }, hue);
  return {
    name: 'synthetic-stone-camera.png',
    mimeType: 'image/png',
    buffer: Buffer.from(base64, 'base64'),
  };
}

export async function savedPhotoItem(page: Page, name: string) {
  await page.getByRole('link', { name: 'Add item', exact: true }).click();
  await page.getByLabel('Name', { exact: true }).fill(name);
  await page
    .getByLabel('Storage location (optional)', { exact: true })
    .fill('Studio tray A');
  await page.getByRole('button', { name: 'Save item', exact: true }).click();
  await expect(page.getByRole('heading', { name, exact: true })).toBeVisible();
  return page.url();
}
