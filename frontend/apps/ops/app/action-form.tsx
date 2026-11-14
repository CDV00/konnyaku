"use client";

import { useState, type FormEvent, type ReactNode } from "react";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";

/**
 * Turns form fields into the JSON body the API expects:
 * - dotted names nest (`name.vi` -> { name: { vi } }), empty fields are left out;
 * - `type="number"` or `data-kind="number"` fields become numbers;
 * - a lone checkbox is a boolean, checkboxes named `x[]` collect the checked values into an array.
 */
function serialize(form: HTMLFormElement): Record<string, unknown> {
  const body: Record<string, unknown> = {};
  const set = (path: string, value: unknown, append: boolean) => {
    const keys = path.split(".");
    let node = body;
    for (const key of keys.slice(0, -1)) node = (node[key] ??= {}) as Record<string, unknown>;
    const last = keys[keys.length - 1]!;
    if (append) {
      const list = (node[last] ??= []) as unknown[];
      if (value !== undefined) list.push(value);
    } else {
      node[last] = value;
    }
  };
  for (const element of Array.from(form.elements)) {
    if (!(element instanceof HTMLInputElement || element instanceof HTMLSelectElement || element instanceof HTMLTextAreaElement)) continue;
    if (!element.name || element.disabled) continue;
    const append = element.name.endsWith("[]");
    const name = append ? element.name.slice(0, -2) : element.name;
    const numeric = element.dataset.kind === "number" || (element instanceof HTMLInputElement && element.type === "number");
    if (element instanceof HTMLInputElement && element.type === "checkbox") {
      if (!append) set(name, element.checked, false);
      else set(name, element.checked ? (numeric ? Number(element.value) : element.value) : undefined, true);
      continue;
    }
    if (element.value === "") continue;
    set(name, numeric ? Number(element.value) : element.value, append);
  }
  return body;
}

export function useAdminAction() {
  const t = useTranslations("admin");
  const router = useRouter();
  const [error, setError] = useState<string | null>(null);
  const [pending, setPending] = useState(false);

  async function send(path: string, method: "POST" | "PUT", body?: unknown): Promise<Record<string, unknown> | null> {
    setError(null);
    setPending(true);
    try {
      const response = await fetch(path, {
        method,
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body ?? {}),
      });
      const data = (await response.json().catch(() => ({}))) as Record<string, unknown>;
      if (response.ok) {
        router.refresh();
        return data;
      }
      const code = typeof data.code === "string" ? data.code : "unknown";
      const key = `errors.${code.replaceAll(".", "_")}`;
      setError(t.has(key) ? t(key) : t("errors.unknown", { code }));
      return null;
    } finally {
      setPending(false);
    }
  }

  return { t, error, pending, send };
}

/**
 * A form that posts its fields as JSON to an ops relay and refreshes the page on success.
 * `api` is the API path below /api (relayed through /ops/api/relay) unless `endpoint` names another ops route.
 * `showResult` lists response fields to display once (a temporary password, for example).
 */
export function ActionForm({
  api,
  endpoint,
  method = "POST",
  submitLabel,
  showResult,
  children,
}: {
  api?: string;
  endpoint?: string;
  method?: "POST" | "PUT";
  submitLabel: string;
  showResult?: string[];
  children: ReactNode;
}) {
  const { t, error, pending, send } = useAdminAction();
  const [result, setResult] = useState<Record<string, unknown> | null>(null);

  async function onSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = event.currentTarget;
    setResult(null);
    const data = await send(endpoint ?? `/ops/api/relay/${api}`, method, serialize(form));
    if (data) {
      form.reset();
      if (showResult?.some((key) => data[key] !== undefined)) setResult(data);
    }
  }

  return (
    <form method="post" onSubmit={onSubmit} className="action-form">
      {children}
      <button type="submit" disabled={pending}>{submitLabel}</button>
      {error && <span role="alert" className="error">{error}</span>}
      {result && (
        <output className="result">
          {showResult?.map((key) => (
            <span key={key}>
              {t(`result.${key}`)}
              <code>{String(result[key] ?? "")}</code>
            </span>
          ))}
        </output>
      )}
    </form>
  );
}

/** One-click write without fields (a status change, for example). */
export function ActionButton({ api, body, label }: { api: string; body?: unknown; label: string }) {
  const { error, pending, send } = useAdminAction();
  return (
    <span>
      <button type="button" disabled={pending} onClick={() => send(`/ops/api/relay/${api}`, "POST", body)}>{label}</button>
      {error && <span role="alert" className="error">{error}</span>}
    </span>
  );
}
