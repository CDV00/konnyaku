"use client";

import { createContext, useContext, useState, type FormEvent, type ReactNode } from "react";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { Copy, Plus } from "lucide-react";
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

type Params = Record<string, string | number | Date>;

/** Fields as the JSON body: empty fields are left out, checkboxes named `x[]` collect their values into an array. */
function serialize(form: HTMLFormElement): Record<string, unknown> {
  const body: Record<string, unknown> = {};
  for (const element of Array.from(form.elements)) {
    if (!(element instanceof HTMLInputElement || element instanceof HTMLSelectElement || element instanceof HTMLTextAreaElement)) continue;
    if (!element.name || element.disabled) continue;
    if (element.name.endsWith("[]")) {
      const list = (body[element.name.slice(0, -2)] ??= []) as string[];
      if (!(element instanceof HTMLInputElement && element.type === "checkbox" && !element.checked)) list.push(element.value);
      continue;
    }
    if (element.value !== "") body[element.name] = element.value;
  }
  return body;
}

/** Posts to the console relay; translated error codes (`platform.errors.*`), page refresh on success. */
export function useConsoleAction() {
  const t = useTranslations("platform");
  const router = useRouter();
  const [error, setError] = useState<string | null>(null);
  const [pending, setPending] = useState(false);

  async function send(api: string, body?: unknown): Promise<Record<string, unknown> | null> {
    setError(null);
    setPending(true);
    try {
      const response = await fetch(`/platform/api/relay/${api}`, {
        method: "POST",
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
      const params = (data.params ?? {}) as Params;
      setError(t.has(key) ? t(key as "errors.unknown", { ...params, code }) : t("errors.unknown", { code }));
      return null;
    } finally {
      setPending(false);
    }
  }

  return { t, error, pending, send };
}

const SheetCloser = createContext<(() => void) | null>(null);

/** Side drawer holding one console form. */
export function ConsoleSheet({ triggerLabel, title, description, children, variant = "default", size = "default", icon = true }: {
  triggerLabel: string;
  title: string;
  description?: string;
  children: ReactNode;
  variant?: ButtonProps["variant"];
  size?: ButtonProps["size"];
  icon?: boolean;
}) {
  const [open, setOpen] = useState(false);
  const t = useTranslations("shell");
  return (
    <Sheet open={open} onOpenChange={setOpen}>
      <SheetTrigger asChild>
        <Button variant={variant} size={size}>
          {icon && <Plus />}
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

/** Values to show once after a successful write (a temporary password): copyable, the drawer stays open. */
function ResultBox({ result, keys }: { result: Record<string, unknown>; keys: string[] }) {
  const t = useTranslations("platform");
  return (
    <output className="grid gap-2 rounded-lg border border-success/30 bg-success/10 p-3 text-sm">
      {keys.map((key) => {
        const value = result[key];
        if (value === null || value === undefined) {
          return key === "temporaryPassword" ? <span key={key} className="text-muted-foreground">{t("result.keptPassword")}</span> : null;
        }
        return (
          <span key={key} className="flex flex-wrap items-center justify-between gap-2">
            <span className="text-muted-foreground">{t(`result.${key}` as "result.email")}</span>
            <span className="flex min-w-0 items-center gap-2">
              <code className="truncate rounded bg-background px-2 py-0.5 font-mono font-semibold">{String(value)}</code>
              <button
                type="button"
                aria-label={t("copy")}
                className="rounded p-1 text-muted-foreground hover:bg-background hover:text-foreground"
                onClick={() => navigator.clipboard?.writeText(String(value))}
              >
                <Copy className="size-3.5" />
              </button>
            </span>
          </span>
        );
      })}
    </output>
  );
}

/**
 * A console form posting its fields to `/platform/api/relay/{api}`. `showResult` keeps the drawer open and shows
 * those response fields once; `doneMessage` shows a confirmation instead of closing (standalone forms); `doneHref` navigates.
 */
export function ConsoleForm({ api, submitLabel, showResult, doneMessage, doneHref, variant = "default", layout = "stack", children }: {
  api: string;
  submitLabel: string;
  showResult?: string[];
  doneMessage?: string;
  /** Page to open after success (full navigation: the console menu changes, e.g. after the first password). */
  doneHref?: string;
  variant?: ButtonProps["variant"];
  layout?: "stack" | "inline";
  children: ReactNode;
}) {
  const { error, pending, send } = useConsoleAction();
  const close = useContext(SheetCloser);
  const [result, setResult] = useState<Record<string, unknown> | null>(null);
  const [done, setDone] = useState(false);

  async function onSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = event.currentTarget;
    setResult(null);
    setDone(false);
    const data = await send(api, serialize(form));
    if (!data) return;
    form.reset();
    if (doneHref) window.location.assign(doneHref);
    else if (showResult) setResult(data);
    else if (doneMessage) setDone(true);
    else close?.();
  }

  const inline = layout === "inline";
  return (
    <form method="post" onSubmit={onSubmit} className={cn(inline ? "flex flex-wrap items-end gap-2" : "grid gap-4 pt-4")}>
      {children}
      <Button type="submit" variant={variant} disabled={pending} size={inline ? "sm" : "default"} className={inline ? "" : "mt-2"}>
        {submitLabel}
      </Button>
      {error && <div className={inline ? "basis-full" : ""}><FormMessage>{error}</FormMessage></div>}
      {done && doneMessage && <FormMessage tone="success">{doneMessage}</FormMessage>}
      {result && showResult && <ResultBox result={result} keys={showResult} />}
    </form>
  );
}

/** One-click console write (resume a center, disable an operator). */
export function ConsoleButton({ api, label, variant = "outline", body }: {
  api: string;
  label: string;
  variant?: ButtonProps["variant"];
  body?: unknown;
}) {
  const { error, pending, send } = useConsoleAction();
  return (
    <span className="inline-flex flex-col gap-1">
      <Button type="button" size="sm" variant={variant} disabled={pending} onClick={() => send(api, body)}>
        {label}
      </Button>
      {error && <span role="alert" className="text-xs text-destructive">{error}</span>}
    </span>
  );
}
