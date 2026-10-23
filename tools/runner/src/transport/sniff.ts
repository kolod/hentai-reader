// Image detection by magic bytes, used to keep images out of fixtures (Constitution X).

export function isImage(bytes: Uint8Array): boolean {
  const b = bytes;
  const ascii = (from: number, to: number) => String.fromCharCode(...b.subarray(from, to));
  if (b.length >= 3 && b[0] === 0xff && b[1] === 0xd8 && b[2] === 0xff) return true; // JPEG
  if (b.length >= 4 && b[0] === 0x89 && ascii(1, 4) === "PNG") return true;
  if (b.length >= 4 && ascii(0, 4) === "GIF8") return true;
  if (b.length >= 12 && ascii(0, 4) === "RIFF" && ascii(8, 12) === "WEBP") return true;
  if (b.length >= 12 && ascii(4, 8) === "ftyp" && /^(avif|avis|heic|heix|mif1)$/.test(ascii(8, 12))) return true;
  return false;
}
