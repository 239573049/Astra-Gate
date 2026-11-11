// A drawn bill → PNG, without a DOM-to-image library: the bill is self-contained (its looks are bill.css alone, its only
// image is the logo), so a copy of its markup plus that one stylesheet inside an SVG <foreignObject> renders the same
// picture, which a canvas then rasterises. Pseudo-elements (the speech bubble's tail) come along because the stylesheet does.

import billCss from './bill.css?raw';

const xmlText = (s: string) => s.replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;');

/** Reads a same-origin image into a data URL: an SVG image may not load anything from outside itself. */
async function dataUrl(src: string): Promise<string> {
  const blob = await (await fetch(src)).blob();
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(reader.result as string);
    reader.onerror = () => reject(reader.error ?? new Error('read failed'));
    reader.readAsDataURL(blob);
  });
}

/** Renders `node` (a ReceiptBill / MangaBill root) at `scale`× into a PNG. */
export async function billToPng(node: HTMLElement, scale = 2): Promise<Blob> {
  const w = Math.ceil(node.offsetWidth);
  const h = Math.ceil(node.offsetHeight);
  const copy = node.cloneNode(true) as HTMLElement;
  await Promise.all(
    [...copy.querySelectorAll('img')].map(async (img) => {
      const src = img.getAttribute('src');
      if (src && !src.startsWith('data:')) img.setAttribute('src', await dataUrl(new URL(src, location.href).href));
    }),
  );
  const markup = new XMLSerializer().serializeToString(copy);
  const svg =
    `<svg xmlns="http://www.w3.org/2000/svg" width="${w * scale}" height="${h * scale}" viewBox="0 0 ${w} ${h}">` +
    `<foreignObject x="0" y="0" width="${w}" height="${h}">` +
    `<div xmlns="http://www.w3.org/1999/xhtml"><style>${xmlText(billCss)}</style>${markup}</div>` +
    `</foreignObject></svg>`;

  const image = new Image();
  await new Promise<void>((resolve, reject) => {
    image.onload = () => resolve();
    image.onerror = () => reject(new Error('the bill could not be drawn'));
    image.src = 'data:image/svg+xml;charset=utf-8,' + encodeURIComponent(svg);
  });
  const canvas = document.createElement('canvas');
  canvas.width = w * scale;
  canvas.height = h * scale;
  const ctx = canvas.getContext('2d');
  if (!ctx) throw new Error('no 2D canvas');
  ctx.drawImage(image, 0, 0, canvas.width, canvas.height);
  return new Promise((resolve, reject) => canvas.toBlob((b) => (b ? resolve(b) : reject(new Error('the PNG could not be made'))), 'image/png'));
}

/** Puts the bill on the clipboard as an image (the promise form keeps Safari's user-gesture rule). */
export async function copyBill(node: HTMLElement): Promise<void> {
  if (typeof ClipboardItem === 'undefined' || !navigator.clipboard?.write) throw new Error('clipboard images are not supported here');
  await navigator.clipboard.write([new ClipboardItem({ 'image/png': billToPng(node) })]);
}

/** Hands the bill to the browser as a PNG download. */
export async function saveBill(node: HTMLElement, fileName: string): Promise<void> {
  const url = URL.createObjectURL(await billToPng(node));
  try {
    const a = document.createElement('a');
    a.href = url;
    a.download = fileName;
    a.click();
  } finally {
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  }
}
