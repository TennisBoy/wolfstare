import type { BlockListDto, RuleDto, StatusDto } from "./types";

// The token is anti-CSRF, not anti-user (spec §2.1): the service injects it into the page it
// serves, and the loopback + Origin checks stop a cross-origin page from using the API. In dev
// the Vite env var stands in.
function token(): string {
  const meta = document
    .querySelector('meta[name="wolfstare-token"]')
    ?.getAttribute("content");
  if (meta) return meta;
  return (import.meta.env.VITE_WOLFSTARE_TOKEN as string | undefined) ?? "";
}

export class ApiError extends Error {
  constructor(
    readonly status: number,
    message: string,
    readonly remainingSeconds?: number,
  ) {
    super(message);
  }
}

async function request<T>(method: string, path: string, body?: unknown): Promise<T> {
  const response = await fetch(`/api${path}`, {
    method,
    headers: {
      Authorization: `Bearer ${token()}`,
      ...(body === undefined ? {} : { "Content-Type": "application/json" }),
    },
    body: body === undefined ? undefined : JSON.stringify(body),
  });

  if (!response.ok) {
    const payload = await response.json().catch(() => ({}));
    throw new ApiError(
      response.status,
      payload.message ?? `Request failed (${response.status}).`,
      payload.remainingSeconds,
    );
  }

  // 200/201/204 with or without a body.
  const text = await response.text();
  return (text ? JSON.parse(text) : undefined) as T;
}

export interface StartOptions {
  durationMinutes: number | null;
  lock: {
    kind: "none" | "password" | "timed" | "randomtext";
    password?: string;
    textLength?: number;
  };
}

export const api = {
  status: () => request<StatusDto>("GET", "/status"),
  blockLists: () => request<BlockListDto[]>("GET", "/blocklists"),
  createList: (name: string, rules: RuleDto[], allowlist: RuleDto[]) =>
    request<BlockListDto>("POST", "/blocklists", { name, rules, allowlist }),
  updateList: (id: string, name: string, rules: RuleDto[], allowlist: RuleDto[]) =>
    request<BlockListDto>("PUT", `/blocklists/${id}`, { name, rules, allowlist }),
  deleteList: (id: string) => request<void>("DELETE", `/blocklists/${id}`),
  start: (id: string, options: StartOptions) =>
    request<void>("POST", `/blocklists/${id}/start`, options),
  stop: (id: string, password?: string) =>
    request<void>("POST", `/blocklists/${id}/stop`, { password: password ?? null }),
  unlock: (blockListId: string, password: string) =>
    request<void>("POST", "/unlock", { blockListId, password }),
};
