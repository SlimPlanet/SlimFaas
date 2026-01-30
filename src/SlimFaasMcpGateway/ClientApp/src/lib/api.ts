const API_BASE_URL = import.meta.env.VITE_API_BASE_URL ?? "";

export type HttpMethod = "GET" | "POST" | "PUT" | "PATCH" | "DELETE";

export async function apiJson<T>(
  path: string,
  method: HttpMethod = "GET",
  body?: unknown,
  auditAuthor?: string
): Promise<T> {
  const res = await fetch(`${API_BASE_URL}${path}`, {
    method,
    headers: {
      "Content-Type": "application/json",
      ...(auditAuthor ? { "X-Audit-Author": auditAuthor } : {}),
    },
    body: body === undefined ? undefined : JSON.stringify(body),
  });

  const text = await res.text();
  if (!res.ok) {
    try {
      const parsed = JSON.parse(text);
      throw new Error(parsed?.error ?? text);
    } catch {
      throw new Error(text || `${res.status} ${res.statusText}`);
    }
  }

  return text ? (JSON.parse(text) as T) : (undefined as unknown as T);
}

export async function apiText(
  path: string,
  method: HttpMethod = "GET"
): Promise<string> {
  const res = await fetch(`${API_BASE_URL}${path}`, { method });
  const text = await res.text();
  if (!res.ok) throw new Error(text || `${res.status} ${res.statusText}`);
  return text;
}
