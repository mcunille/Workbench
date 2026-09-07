import { expect, it, vi } from 'vitest';
import { photoDimensions, preparePhoto } from './preparePhoto';
it('fits the longest edge without upscaling or changing aspect ratio', () => {
  // GIVEN camera and small images WHEN fitted THEN aspect ratio is preserved.
  expect(photoDimensions(6000, 4000)).toEqual({ width: 2048, height: 1365 });
  expect(photoDimensions(300, 600)).toEqual({ width: 300, height: 600 });
});
it('rejects large sources before decoding', async () => {
  // GIVEN a source above the byte budget.
  const decode = vi.fn();
  vi.stubGlobal('createImageBitmap', decode);
  const file = new File([new Uint8Array(20 * 1024 * 1024 + 1)], 'large.jpg', {
    type: 'image/jpeg',
  });
  // WHEN preparing THEN no decode or original upload occurs.
  await expect(preparePhoto(file)).rejects.toThrow(/20 MiB/);
  expect(decode).not.toHaveBeenCalled();
  vi.unstubAllGlobals();
});
it('rejects unsupported and malformed images', async () => {
  // GIVEN unsupported or falsely labeled content WHEN preparing THEN reject it.
  await expect(
    preparePhoto(new File(['x'], 'x.svg', { type: 'image/svg+xml' })),
  ).rejects.toThrow(/JPEG, PNG, or WebP/);
  await expect(
    preparePhoto(new File(['x'], 'x.jpg', { type: 'image/jpeg' })),
  ).rejects.toThrow();
});
function png(width: number, height: number, animated = false) {
  const bytes = new Uint8Array(animated ? 45 : 33);
  bytes.set([137, 80, 78, 71, 13, 10, 26, 10]);
  const view = new DataView(bytes.buffer);
  view.setUint32(8, 13);
  bytes.set([73, 72, 68, 82], 12);
  view.setUint32(16, width);
  view.setUint32(20, height);
  if (animated) bytes.set([97, 99, 84, 76], 37);
  return new File([bytes], 'photo.png', { type: 'image/png' });
}
it('rejects oversized dimensions and animation before allocating decoded pixels', async () => {
  // GIVEN header-declared dimensions or multiple frames.
  const decode = vi.fn();
  vi.stubGlobal('createImageBitmap', decode);
  // WHEN preparing THEN reject before decode.
  await expect(preparePhoto(png(10000, 10000))).rejects.toThrow(
    /40 megapixels/,
  );
  await expect(preparePhoto(png(20000, 1))).rejects.toThrow(/16,384/);
  await expect(preparePhoto(png(20, 20, true))).rejects.toThrow(/single-frame/);
  expect(decode).not.toHaveBeenCalled();
  vi.unstubAllGlobals();
});
it.each([false, true])(
  'falls back when WebP encoding is unavailable, preserving alpha=%s',
  async (alpha) => {
    // GIVEN an oriented bitmap and a browser that silently encodes PNG for WebP.
    const close = vi.fn();
    const decode = vi
      .fn()
      .mockResolvedValue({ width: 4000, height: 6000, close });
    vi.stubGlobal('createImageBitmap', decode);
    const drawImage = vi.fn();
    const context = vi
      .spyOn(HTMLCanvasElement.prototype, 'getContext')
      .mockReturnValue({
        drawImage,
        getImageData: () => ({
          data: new Uint8ClampedArray([1, 2, 3, alpha ? 0 : 255]),
        }),
      } as unknown as CanvasRenderingContext2D);
    const types: string[] = [];
    const encode = vi
      .spyOn(HTMLCanvasElement.prototype, 'toBlob')
      .mockImplementation(function (callback, type) {
        types.push(type!);
        callback(
          new Blob(['prepared'], {
            type: type === 'image/webp' ? 'image/png' : type,
          }),
        );
      });
    // WHEN preparing THEN upload only freshly encoded oriented pixels with correct fallback.
    const result = await preparePhoto(png(6000, 4000));
    expect(decode).toHaveBeenCalledWith(expect.any(File), {
      imageOrientation: 'from-image',
      colorSpaceConversion: 'default',
    });
    expect(drawImage).toHaveBeenCalledWith(expect.anything(), 0, 0, 1365, 2048);
    expect(types).toEqual(['image/webp', alpha ? 'image/png' : 'image/jpeg']);
    expect(result.type).toBe(alpha ? 'image/png' : 'image/jpeg');
    expect(close).toHaveBeenCalledOnce();
    encode.mockRestore();
    context.mockRestore();
    vi.unstubAllGlobals();
  },
);
it('rejects JPEG multi-picture sources before decoding', async () => {
  // GIVEN a JPEG carrying the MPF multi-picture marker.
  const file = new File(
    [
      new Uint8Array([
        255, 216, 255, 226, 0, 6, 77, 80, 70, 0, 255, 192, 0, 8, 8, 0, 10, 0,
        10, 1,
      ]),
    ],
    'multi.jpg',
    { type: 'image/jpeg' },
  );
  // WHEN preparing THEN require a single-frame source.
  await expect(preparePhoto(file)).rejects.toThrow(/single-frame/);
});
