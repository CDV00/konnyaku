"use client";

import { useEffect, useRef, useState } from "react";
import { useSearchParams } from "next/navigation";
import { useTranslations } from "next-intl";
import { Download, Eye, EyeOff, Loader2, Mail, Phone, Printer } from "lucide-react";
import { Button } from "@konnyaku/ui/components/animate-ui/components/buttons/button";
import { Input } from "@konnyaku/ui/components/ui/input";
import { Skeleton } from "@konnyaku/ui/components/ui/skeleton";
import { Field } from "@konnyaku/ui/kk/field";
import { FormMessage } from "@konnyaku/ui/kk/blocks";
import { cn } from "@konnyaku/ui/lib/utils";

export type ContactKind = "students" | "leads";

/** Masked values as the lists return them ("09xx xxx 567", "a***@example.com", "201*"). */
export type MaskedContact = { phone: string | null; email: string | null; guardianName: string | null; birthYear: string | null };

type Contact = { birthYear: number | null; guardianName: string | null; phone: string | null; email: string | null };

function useErrorText() {
  const t = useTranslations("admin");
  return (code: string, params: Record<string, string | number> = {}) => {
    const key = `errors.${code.replaceAll(".", "_")}`;
    return t.has(key) ? t(key, params) : t("errors.unknown", { code });
  };
}

async function reveal(kind: ContactKind, id: string, purpose: "view" | "edit" | "contact"): Promise<{ contact?: Contact; code?: string }> {
  const response = await fetch(`/ops/api/relay/crm/${kind}/${id}/reveal`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ purpose }),
  });
  const data = (await response.json().catch(() => ({}))) as Contact & { code?: string };
  return response.ok ? { contact: data } : { code: data.code ?? "unknown" };
}

/**
 * Contact details of a list row: masked, with a "show" button for members who keep these records. Every show is
 * logged by the API (who, which record); the full values live in this component's state only.
 */
export function RevealContact({ kind, id, masked, canReveal, showGuardian = true }: {
  kind: ContactKind;
  id: string;
  masked: MaskedContact;
  canReveal: boolean;
  showGuardian?: boolean;
}) {
  const t = useTranslations("admin.privacy");
  const errorText = useErrorText();
  const [full, setFull] = useState<Contact | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const phone = full ? full.phone : masked.phone;
  const email = full ? full.email : masked.email;
  const guardian = full ? full.guardianName : masked.guardianName;
  if (!phone && !email && !guardian) return null;

  const toggle = async () => {
    if (full) {
      setFull(null);
      return;
    }
    setBusy(true);
    setError(null);
    const result = await reveal(kind, id, "contact");
    setBusy(false);
    if (result.contact) setFull(result.contact);
    else setError(errorText(result.code ?? "unknown"));
  };

  return (
    <div className="space-y-0.5 text-xs">
      {phone && (
        full ? (
          <a href={`tel:${phone}`} className="flex items-center gap-1.5 text-foreground hover:text-primary"><Phone className="size-3.5" />{phone}</a>
        ) : (
          <span className="flex items-center gap-1.5 font-mono text-muted-foreground"><Phone className="size-3.5" />{phone}</span>
        )
      )}
      {email && (
        full ? (
          <a href={`mailto:${email}`} className="flex items-center gap-1.5 text-muted-foreground hover:text-primary"><Mail className="size-3.5" />{email}</a>
        ) : (
          <span className="flex items-center gap-1.5 font-mono text-muted-foreground"><Mail className="size-3.5" />{email}</span>
        )
      )}
      {showGuardian && guardian && <span className="block text-muted-foreground">{t("guardian", { name: guardian })}</span>}
      {canReveal && (
        <button
          type="button"
          onClick={toggle}
          disabled={busy}
          className="mt-0.5 inline-flex items-center gap-1 rounded px-1 py-0.5 text-primary hover:bg-primary/10 disabled:opacity-50"
          title={full ? undefined : t("revealHint")}
        >
          {busy ? <Loader2 className="size-3 animate-spin" /> : full ? <EyeOff className="size-3" /> : <Eye className="size-3" />}
          {full ? t("hide") : t("reveal")}
        </button>
      )}
      {error && <span className="block text-destructive">{error}</span>}
    </div>
  );
}

/**
 * Birth year, phone, guardian and email inputs of an edit form, filled from the logged "show" call when the form opens
 * (lists never carry the full values). Until they are loaded the form cannot be sent, so nothing is overwritten blank.
 */
export function ContactFields({ kind, id, guardianLabel }: { kind: ContactKind; id: string; guardianLabel: string }) {
  const t = useTranslations("admin");
  const errorText = useErrorText();
  const [contact, setContact] = useState<Contact | null>(null);
  const [error, setError] = useState<string | null>(null);
  const started = useRef(false);

  useEffect(() => {
    if (started.current) return;
    started.current = true;
    void reveal(kind, id, "edit").then((result) => {
      if (result.contact) setContact(result.contact);
      else setError(errorText(result.code ?? "unknown"));
    });
  }, [kind, id, errorText]);

  if (!contact) {
    return (
      <div className="space-y-3">
        {/* Holds the submit until the values are in: an empty required field the browser will not send past. */}
        <input required tabIndex={-1} aria-hidden="true" className="sr-only" value="" onChange={() => undefined} />
        {error ? <FormMessage>{error}</FormMessage> : <><Skeleton className="h-9 w-full" /><Skeleton className="h-9 w-full" /><Skeleton className="h-9 w-full" /></>}
      </div>
    );
  }

  return (
    <>
      <div className="grid grid-cols-2 gap-3">
        <Field label={t("students.birthYear")}><Input name="birthYear" type="number" min={1900} max={2100} defaultValue={contact.birthYear ?? undefined} /></Field>
        <Field label={t("fields.phone")}><Input name="phone" type="tel" defaultValue={contact.phone ?? undefined} /></Field>
      </div>
      <Field label={guardianLabel}><Input name="guardianName" defaultValue={contact.guardianName ?? undefined} /></Field>
      <Field label={t("fields.email")}><Input name="email" type="email" defaultValue={contact.email ?? undefined} /></Field>
    </>
  );
}

/** Saves a file returned by a GET of the ops relay/export routes, or shows the API's error. */
async function download(url: string): Promise<{ ok: true } | { ok: false; code: string; params: Record<string, string | number> }> {
  const response = await fetch(url);
  if (!response.ok) {
    const data = (await response.json().catch(() => ({}))) as { code?: string; params?: Record<string, string | number> };
    return { ok: false, code: data.code ?? "unknown", params: data.params ?? {} };
  }
  const name = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(response.headers.get("Content-Disposition") ?? "")?.[1] ?? "export";
  const blob = await response.blob();
  const link = document.createElement("a");
  link.href = URL.createObjectURL(blob);
  link.download = decodeURIComponent(name);
  document.body.append(link);
  link.click();
  link.remove();
  setTimeout(() => URL.revokeObjectURL(link.href), 1000);
  return { ok: true };
}

/** Button downloading a file (CSV export, data subject export); errors such as "too many rows" show under it. */
export function DownloadButton({ href, label, variant = "outline", size = "sm" }: {
  href: string;
  label: string;
  variant?: "outline" | "ghost" | "default";
  size?: "sm" | "default";
}) {
  const errorText = useErrorText();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  return (
    <span className="inline-flex flex-col items-end gap-1">
      <Button
        type="button"
        variant={variant}
        size={size}
        disabled={busy}
        onClick={async () => {
          setBusy(true);
          setError(null);
          const result = await download(href);
          setBusy(false);
          if (!result.ok) setError(errorText(result.code, result.params));
        }}
      >
        {busy ? <Loader2 className="size-4 animate-spin" /> : <Download className="size-4" />}
        {label}
      </Button>
      {error && <span className="max-w-72 text-right text-xs text-destructive">{error}</span>}
    </span>
  );
}

/** Export (CSV) and print of the list as currently filtered (the URL's filters, not the page). */
export function ListExport({ kind, filterKeys }: { kind: ContactKind; filterKeys: readonly string[] }) {
  const t = useTranslations("admin.privacy");
  const searchParams = useSearchParams();
  const query = new URLSearchParams();
  for (const key of filterKeys) {
    const value = searchParams.get(key);
    if (value) query.set(key, value);
  }
  const suffix = query.toString() ? `?${query.toString()}` : "";
  return (
    <>
      <DownloadButton href={`/ops/api/export/${kind}${suffix}`} label={t("exportCsv")} />
      <a
        href={`/ops/${kind}/print${suffix}`}
        target="_blank"
        rel="noopener"
        className="inline-flex h-8 items-center gap-1.5 rounded-md border bg-card px-3 text-sm font-medium shadow-xs hover:bg-accent"
      >
        <Printer className="size-4" />
        {t("print")}
      </a>
    </>
  );
}

/** Opens the print dialog once the printable list is on screen. */
export function PrintOnLoad() {
  useEffect(() => {
    const timer = setTimeout(() => window.print(), 300);
    return () => clearTimeout(timer);
  }, []);
  return null;
}

export type PickerItem = { id: string; label: string; detail?: string };

/**
 * Search-as-you-type picker over a list endpoint (thousands of students do not fit a select). Writes the chosen id
 * into a hidden input named `name`, so it works inside ActionForm.
 */
export function RecordPicker({ name, source, placeholder, required, initial, filter = "" }: {
  name: string;
  /** Relay path of a list endpoint taking `q` (e.g. `crm/students`). */
  source: string;
  placeholder: string;
  required?: boolean;
  initial?: PickerItem;
  /** Extra query string for the list (e.g. `status=active`). */
  filter?: string;
}) {
  const t = useTranslations("admin.list");
  const [query, setQuery] = useState("");
  const [items, setItems] = useState<PickerItem[]>([]);
  const [chosen, setChosen] = useState<PickerItem | null>(initial ?? null);
  const [open, setOpen] = useState(false);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (!open) return;
    const controller = new AbortController();
    const timer = setTimeout(async () => {
      setBusy(true);
      const params = new URLSearchParams(filter);
      params.set("pageSize", "8");
      if (query.trim()) params.set("q", query.trim());
      try {
        const response = await fetch(`/ops/api/relay/${source}?${params.toString()}`, { signal: controller.signal });
        const page = response.ok ? ((await response.json()) as { items: { id: string; fullName?: string; studentName?: string; birthYear?: string | null }[] }) : { items: [] };
        setItems(page.items.map((row) => ({ id: row.id, label: row.fullName ?? row.studentName ?? row.id, detail: row.birthYear ?? undefined })));
      } catch {
        // Aborted by the next keystroke.
      } finally {
        setBusy(false);
      }
    }, 250);
    return () => {
      clearTimeout(timer);
      controller.abort();
    };
  }, [query, open, source, filter]);

  return (
    <div className="relative">
      <input type="hidden" name={name} value={chosen?.id ?? ""} />
      {chosen && !open ? (
        <button
          type="button"
          onClick={() => setOpen(true)}
          className="flex h-9 w-full items-center justify-between rounded-md border bg-transparent px-3 text-left text-sm shadow-xs"
        >
          <span className="truncate">{chosen.label}</span>
          <span className="text-xs text-muted-foreground">{t("change")}</span>
        </button>
      ) : (
        <Input
          type="search"
          value={query}
          autoFocus={open}
          required={required && !chosen}
          placeholder={placeholder}
          onFocus={() => setOpen(true)}
          onChange={(e) => setQuery(e.target.value)}
        />
      )}
      {open && (
        <ul className="absolute z-50 mt-1 max-h-64 w-full overflow-auto rounded-md border bg-popover p-1 text-sm shadow-md" role="listbox">
          {busy && items.length === 0 && <li className="px-2 py-1.5 text-muted-foreground">{t("loading")}</li>}
          {!busy && items.length === 0 && <li className="px-2 py-1.5 text-muted-foreground">{t("noMatch")}</li>}
          {items.map((item) => (
            <li key={item.id}>
              <button
                type="button"
                role="option"
                aria-selected={chosen?.id === item.id}
                className={cn("flex w-full items-center justify-between gap-2 rounded px-2 py-1.5 text-left hover:bg-accent", chosen?.id === item.id && "bg-accent")}
                onClick={() => {
                  setChosen(item);
                  setOpen(false);
                  setQuery("");
                }}
              >
                <span className="truncate">{item.label}</span>
                {item.detail && <span className="text-xs text-muted-foreground">{item.detail}</span>}
              </button>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
