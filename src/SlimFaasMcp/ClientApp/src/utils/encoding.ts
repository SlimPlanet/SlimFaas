export function toBase64Utf8(text: string): string {
  // UTF-8 safe base64 (browser)
  const bytes = new TextEncoder().encode(text);
  let binary = "";
  bytes.forEach((b) => (binary += String.fromCharCode(b)));
  return btoa(binary);
}

export function fromBase64Utf8(b64: string): string {
  const binary = atob(b64);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
  return new TextDecoder().decode(bytes);
}

export function isProbablyBase64(str: string): boolean {
  if (!str) return false;
  return /^[A-Za-z0-9+/]+={0,2}$/.test(str);
}

export function safeJsonParse<T>(txt: string): { ok: true; value: T } | { ok: false; error: string } {
  try {
    return { ok: true, value: JSON.parse(txt) as T };
  } catch (e: any) {
    return { ok: false, error: e?.message ?? String(e) };
  }
}
