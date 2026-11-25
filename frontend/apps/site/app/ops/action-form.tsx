"use client";

import { createContext, useContext, useState, type FormEvent, type ReactNode } from "react";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { Copy, Pencil, Plus } from "lucide-react";
import { Button, type ButtonProps } from "@konnyaku/ui/components/animate-ui/components/buttons/button";
import {
  Sheet,
  SheetContent,
  SheetDescription,
  SheetHeader,
  SheetTitle,
  SheetTrigger,
} from "@konnyaku/ui/components/animate-ui/components/radix/sheet";
import { FormMessage } from "@konnyaku/ui/kk/blocks";
import { cn } from "@konnyaku/ui/lib/utils";

/**
 * Turns form fields into the JSON body the API expects:
 * - dotted names nest (`name.vi` -> { name: { vi } }), empty fields are left out;
 * - `type="number"` or `data-kind="number"` fields become numbers;
 * - a lone checkbox is a boolean, checkboxes named `x[]` collect the checked values into an array;
 * - a `data-kind="json"` field (hidden, written by a client widget) is parsed as JSON.
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
    if (element.dataset.kind === "json") {
      set(name, JSON.parse(element.value) as unknown, append);
      continue;
    }
    set(name, numeric ? Number(element.value) : element.value, append);
  }
  return body;
}

export type Method = "POST" | "PUT" | "DELETE";

export function useAdminAction() {
  const t = useTranslations("admin");
  const router = useRouter();
  const [error, setError] = useState<string | null>(null);
  const [pending, setPending] = useState(false);

  async function send(path: string, method: Method, body?: unknown): Promise<Record<string, unknown> | null> {
    setError(null);
    setPending(true);
    try {
      const response = await fetch(path, {
        method,
        headers: { "Content-Type": "application/json" },
        body: method === "DELETE" ? undefined : JSON.stringify(body ?? {}),
      });
      const data = (await response.json().catch(() => ({}))) as Record<string, unknown>;
      if (response.ok) {
        router.refresh();
        return data;
      }
      const code = typeof data.code === "string" ? data.code : "unknown";
      const key = `errors.${code.replaceAll(".", "_")}`;
      // Error params (a count, an amount) fill the message; numbers and text only.
      const params = Object.fromEntries(
        Object.entries((data.params ?? {}) as Record<string, unknown>).filter(([, v]) => typeof v === "number" || typeof v === "string"),
      ) as Record<string, string | number>;
      setError(t.has(key) ? t(key, params) : t("errors.unknown", { code }));
      return null;
    } finally {
      setPending(false);
    }
  }

  return { t, error, pending, send };
}

/** Lets a form inside a FormSheet close it after a successful save. */
const SheetCloser = createContext<(() => void) | null>(null);

/**
 * Side drawer holding one create/edit form: keeps the list in view and the page uncluttered.
 * `icon` is the trigger's icon: a plus (create), a pencil ("edit") or none.
 */
export function FormSheet({ triggerLabel, title, description, children, variant = "default", size = "default", icon = true }: {
  triggerLabel: string;
  title: string;
  description?: string;
  children: ReactNode;
  variant?: ButtonProps["variant"];
  size?: ButtonProps["size"];
  icon?: boolean | "edit";
}) {
  const [open, setOpen] = useState(false);
  const t = useTranslations("shell");
  return (
    <Sheet open={open} onOpenChange={setOpen}>
      <SheetTrigger asChild>
        <Button variant={variant} size={size}>
          {icon === "edit" ? <Pencil /> : icon && <Plus />}
          {triggerLabel}
        </Button>
      </SheetTrigger>
      <SheetContent className="w-full overflow-y-auto sm:max-w-md sm:w-[28rem]" closeLabel={t("close")}>
        <SheetHeader className="border-b">
          <SheetTitle>{title}</SheetTitle>
          {description && <SheetDescription>{description}</SheetDescription>}
        </SheetHeader>
        <div className="px-4 pb-6">
          <SheetCloser.Provider value={() => setOpen(false)}>{children}</SheetCloser.Provider>
        </div>
      </SheetContent>
    </Sheet>
  );
}

/**
 * A form that posts its fields as JSON to an ops relay and refreshes the page on success.
 * `api` is the API path below /api (relayed through /ops/api/relay) unless `endpoint` names another ops route.
 * `showResult` lists response fields to display once (a temporary password, for example); the drawer then stays open.
 * `layout="inline"` lays the fields out in a row (small forms inside a table cell).
 */
export function ActionForm({
  api,
  endpoint,
  method = "POST",
  submitLabel,
  showResult,
  layout = "stack",
  children,
}: {
  api?: string;
  endpoint?: string;
  method?: Method;
  submitLabel: string;
  showResult?: string[];
  layout?: "stack" | "inline";
  children: ReactNode;
}) {
  const { t, error, pending, send } = useAdminAction();
  const close = useContext(SheetCloser);
  const [result, setResult] = useState<Record<string, unknown> | null>(null);

  async function onSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = event.currentTarget;
    setResult(null);
    const data = await send(endpoint ?? `/ops/api/relay/${api}`, method, serialize(form));
    if (!data) return;
    form.reset();
    if (showResult?.some((key) => data[key] !== undefined)) setResult(data);
    else close?.();
  }

  const inline = layout === "inline";
  return (
    <form method="post" onSubmit={onSubmit} className={cn(inline ? "flex flex-wrap items-end gap-2" : "grid gap-4 pt-4")}>
      {children}
      <Button type="submit" disabled={pending} size={inline ? "sm" : "default"} variant={inline ? "outline" : "default"} className={inline ? "" : "mt-2"}>
        {submitLabel}
      </Button>
      {error && <div className={inline ? "basis-full" : ""}><FormMessage>{error}</FormMessage></div>}
      {result && (
        <output className="grid gap-2 rounded-lg border border-success/30 bg-success/10 p-3 text-sm">
          {showResult?.map((key) => (
            <span key={key} className="flex flex-wrap items-center justify-between gap-2">
              <span className="text-muted-foreground">{t(`result.${key}`)}</span>
              <span className="flex items-center gap-2">
                <code className="rounded bg-background px-2 py-0.5 font-mono font-semibold">{String(result[key] ?? "")}</code>
                <button
                  type="button"
                  aria-label={t("copy")}
                  className="rounded p-1 text-muted-foreground hover:bg-background hover:text-foreground"
                  onClick={() => navigator.clipboard?.writeText(String(result[key] ?? ""))}
                >
                  <Copy className="size-3.5" />
                </button>
              </span>
            </span>
          ))}
        </output>
      )}
    </form>
  );
}

/**
 * One-click write without fields (a status change, deactivating a record). `confirm` asks first — for actions that
 * stop something (deactivate, cancel, delete).
 */
export function ActionButton({ api, endpoint, body, label, variant = "outline", method = "POST", confirm }: {
  api?: string;
  endpoint?: string;
  body?: unknown;
  label: string;
  variant?: ButtonProps["variant"];
  method?: Method;
  confirm?: string;
}) {
  const { error, pending, send } = useAdminAction();
  function onClick() {
    if (confirm && !window.confirm(confirm)) return;
    void send(endpoint ?? `/ops/api/relay/${api}`, method, body);
  }
  return (
    <span className="inline-flex flex-col gap-1">
      <Button type="button" size="sm" variant={variant} disabled={pending} onClick={onClick}>
        {label}
      </Button>
      {error && <span role="alert" className="text-xs text-destructive">{error}</span>}
    </span>
  );
}
