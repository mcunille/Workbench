import { mkdir, readdir, writeFile, rm } from 'node:fs/promises';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

export const diagnosticsRoot = fileURLToPath(new URL('../../artifacts/browser-diagnostics/', import.meta.url));
export async function resetDiagnostics() {
  await rm(diagnosticsRoot, { recursive: true, force: true });
}

// Never serialize DOM text, attributes, styles, URLs, errors, or browser storage.
// Render only geometry and a closed vocabulary in a fresh, unauthenticated context.
export async function captureLayout(page, root, id) {
  if (!/^failure-[a-f0-9-]+$/.test(id)) throw new Error('Invalid diagnostic identifier');
  await mkdir(root, { recursive: true });
  if ((await readdir(root)).length >= 10) return;
  const layout = await page.evaluate(() => {
    const tags = new Set('html body main header footer nav section article aside div span p h1 h2 h3 h4 h5 h6 button a input select textarea label form fieldset legend ul ol li table thead tbody tr th td img svg dialog details summary'.split(' '));
    const number = value => Math.round(Math.max(-100000, Math.min(100000, value)) * 10) / 10;
    const selector = element => {
      const parts = [];
      for (let current = element; current && parts.length < 20; current = current.parentElement) {
        const tag = tags.has(current.localName) ? current.localName : '*';
        parts.unshift(`${tag}:nth-child(${Array.prototype.indexOf.call(current.parentElement?.children ?? [current], current) + 1})`);
      }
      return parts.join('>');
    };
    const elements = [];
    const candidates = Array.from(document.querySelectorAll('*')).slice(0, 10000);
    for (const element of candidates) {
      if (!tags.has(element.localName)) continue;
      const rect = element.getBoundingClientRect();
      if (!rect.width || !rect.height || getComputedStyle(element).visibility === 'hidden') continue;
      elements.push({ tag: element.localName, selector: selector(element), x: number(rect.x), y: number(rect.y), width: number(rect.width), height: number(rect.height) });
      if (elements.length === 1000) break;
    }
    return { viewport: { width: innerWidth, height: innerHeight }, scroll: { x: number(scrollX), y: number(scrollY) }, elements, truncated: elements.length === 1000 || candidates.length === 10000 };
  });
  const directory = join(root, id);
  const context = await page.context().browser().newContext({ viewport: {
    width: Math.max(1, Math.min(1920, layout.viewport.width)),
    height: Math.max(1, Math.min(1080, layout.viewport.height)),
  } });
  try {
    const preview = await context.newPage();
    await preview.setContent('<html><body style="margin:0;background:white"><canvas></canvas></body></html>');
    await preview.evaluate(layout => {
      const canvas = document.querySelector('canvas');
      canvas.width = innerWidth; canvas.height = innerHeight;
      const drawing = canvas.getContext('2d');
      drawing.fillStyle = 'white'; drawing.fillRect(0, 0, canvas.width, canvas.height);
      drawing.font = '11px monospace';
      layout.elements.forEach((element, index) => {
        const overflow = element.x < 0 || element.x + element.width > layout.viewport.width;
        drawing.strokeStyle = overflow ? '#c02020' : '#65758b';
        drawing.strokeRect(element.x, element.y, element.width, element.height);
        drawing.fillStyle = overflow ? '#c02020' : '#243044';
        drawing.fillText(`${index} ${element.tag}`, element.x + 2, element.y + 12);
      });
    }, layout);
    const png = await preview.screenshot({ type: 'png' });
    const json = JSON.stringify(layout, null, 2);
    if (png.length + Buffer.byteLength(json) > 2 * 1024 * 1024) return;
    await mkdir(directory);
    await writeFile(join(directory, 'layout.json'), json);
    await writeFile(join(directory, 'layout.png'), png);
    return directory;
  } finally { await context.close(); }
}
