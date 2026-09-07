const sourceLimit = 20 * 1024 * 1024;
const uploadLimit = 4 * 1024 * 1024;
export function photoDimensions(width: number, height: number, edge = 2048) {
  const scale = Math.min(1, edge / Math.max(width, height));
  return {
    width: Math.max(1, Math.round(width * scale)),
    height: Math.max(1, Math.round(height * scale)),
  };
}
function inspect(bytes: Uint8Array, type: string) {
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  const text = (offset: number, length: number) =>
    String.fromCharCode(...bytes.subarray(offset, offset + length));
  let width = 0,
    height = 0;
  if (type === 'image/png' && text(1, 3) === 'PNG') {
    width = view.getUint32(16);
    height = view.getUint32(20);
    for (let p = 8; p + 12 <= bytes.length;) {
      if (text(p + 4, 4) === 'acTL')
        throw new Error(
          'Choose a single-frame photograph. Animated images are not supported.',
        );
      const size = view.getUint32(p);
      p += 12 + size;
    }
  } else if (
    type === 'image/webp' &&
    text(0, 4) === 'RIFF' &&
    text(8, 4) === 'WEBP'
  ) {
    const kind = text(12, 4);
    const uint24 = (p: number) =>
      bytes[p] + bytes[p + 1] * 256 + bytes[p + 2] * 65536;
    if (kind === 'VP8X') {
      if (bytes[20] & 2)
        throw new Error(
          'Choose a single-frame photograph. Animated images are not supported.',
        );
      width = uint24(24) + 1;
      height = uint24(27) + 1;
    } else if (kind === 'VP8L') {
      const bits = view.getUint32(21, true);
      width = (bits & 0x3fff) + 1;
      height = ((bits >>> 14) & 0x3fff) + 1;
    } else if (kind === 'VP8 ') {
      width = view.getUint16(26, true) & 0x3fff;
      height = view.getUint16(28, true) & 0x3fff;
    }
  } else if (type === 'image/jpeg' && bytes[0] === 255 && bytes[1] === 216) {
    for (let p = 2; p + 4 <= bytes.length;) {
      if (bytes[p++] !== 255) break;
      while (bytes[p] === 255) p++;
      const marker = bytes[p++];
      if (marker === 218 || marker === 217) break;
      const length = view.getUint16(p);
      if (length < 2) break;
      if (marker === 226 && text(p + 2, 3) === 'MPF')
        throw new Error(
          'Choose a single-frame photograph. Multi-picture images are not supported.',
        );
      if ([192, 193, 194].includes(marker)) {
        height = view.getUint16(p + 3);
        width = view.getUint16(p + 5);
      }
      p += length;
    }
  }
  if (!width || !height)
    throw new Error(
      'This photograph could not be decoded. Choose a valid JPEG, PNG, or WebP.',
    );
  if (width > 16384 || height > 16384 || width * height > 40_000_000)
    throw new Error(
      'Choose a photograph up to 40 megapixels and 16,384 pixels per side.',
    );
}
function encode(canvas: HTMLCanvasElement, type: string): Promise<Blob> {
  return new Promise((resolve, reject) =>
    canvas.toBlob(
      (blob) =>
        blob
          ? resolve(blob)
          : reject(
              new Error('Your browser could not prepare this photograph.'),
            ),
      type,
      0.86,
    ),
  );
}
export async function preparePhoto(file: File): Promise<Blob> {
  if (file.size > sourceLimit)
    throw new Error('Choose a photograph up to 20 MiB.');
  if (!['image/jpeg', 'image/png', 'image/webp'].includes(file.type))
    throw new Error('Choose a JPEG, PNG, or WebP photograph.');
  const bytes = await new Promise<ArrayBuffer>((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(reader.result as ArrayBuffer);
    reader.onerror = () => reject(new Error('Could not read this photograph.'));
    reader.readAsArrayBuffer(file);
  });
  try {
    inspect(new Uint8Array(bytes), file.type);
  } catch (error) {
    if (error instanceof RangeError)
      throw new Error(
        'This photograph is incomplete or invalid. Choose another image.',
      );
    throw error;
  }
  if (!globalThis.createImageBitmap)
    throw new Error(
      'This browser cannot prepare photographs. Please use a current browser.',
    );
  let bitmap: ImageBitmap;
  try {
    bitmap = await createImageBitmap(file, {
      imageOrientation: 'from-image',
      colorSpaceConversion: 'default',
    });
  } catch {
    throw new Error(
      'This photograph could not be decoded. Choose another image.',
    );
  }
  try {
    const canvas = document.createElement('canvas');
    const context = canvas.getContext('2d', { colorSpace: 'srgb' });
    if (!context) throw new Error('This browser cannot prepare photographs.');
    for (let edge = 2048; edge >= 256; edge = Math.floor(edge * 0.75)) {
      Object.assign(canvas, photoDimensions(bitmap.width, bitmap.height, edge));
      context.drawImage(bitmap, 0, 0, canvas.width, canvas.height);
      let blob = await encode(canvas, 'image/webp');
      if (blob.type !== 'image/webp') {
        const pixels = context.getImageData(
          0,
          0,
          canvas.width,
          canvas.height,
        ).data;
        let alpha = false;
        for (let p = 3; p < pixels.length; p += 4) {
          if (pixels[p] !== 255) {
            alpha = true;
            break;
          }
        }
        blob = await encode(canvas, alpha ? 'image/png' : 'image/jpeg');
      }
      if (blob.size <= uploadLimit) return blob;
    }
    throw new Error(
      'Could not fit this photograph within the 4 MiB upload limit. Choose another image.',
    );
  } finally {
    bitmap.close();
  }
}
