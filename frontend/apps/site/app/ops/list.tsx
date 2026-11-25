"use client";

import { createContext, useContext, useEffect, useRef, useState, useTransition, type ReactNode } from "react";
import { usePathname, useRouter, useSearchParams } from "next/navigation";
import { useTranslations } from "next-intl";
import { ArrowDown, ArrowUp, ArrowUpDown, ChevronLeft, ChevronRight, Loader2, Search, X } from "lucide-react";
import { Input } from "@konnyaku/ui/components/ui/input";
import { NativeSelect } from "@konnyaku/ui/kk/field";
import { cn } from "@konnyaku/ui/lib/utils";

type Navigate = (changes: Record<string, string | null>) => void;

const ListContext = createContext<{ pending: boolean; navigate: Navigate } | null>(null);

/**
 * A server-rendered list whose search, filters, sort and page live in the URL (a filtered list is a link to share).
 * Changes replace the URL inside a transition: the current rows stay, dimmed, until the new page arrives.
 */
export function ListFrame({ children, className }: { children: ReactNode; className?: string }) {
  const router = useRouter();
  const pathname = usePathname();
  const searchParams = useSearchParams();
  const [pending, startTransition] = useTransition();
  const navigate: Navigate = (changes) => {
    const next = new URLSearchParams(searchParams.toString());
    for (const [key, value] of Object.entries(changes)) {
      if (value === null || value === "") next.delete(key);
      else next.set(key, value);
    }
    // Any change other than the page itself starts again from page 1.
    if (!("page" in changes)) next.delete("page");
    const query = next.toString();
    startTransition(() => router.replace(query ? `${pathname}?${query}` : pathname, { scroll: false }));
  };
  return (
    <ListContext.Provider value={{ pending, navigate }}>
      <div className={cn("space-y-3", className)}>{children}</div>
    </ListContext.Provider>
  );
}

function useList() {
  const context = useContext(ListContext);
  if (!context) throw new Error("List controls must be inside <ListFrame>.");
  return context;
}

/** The rows: dimmed and marked busy while a new page loads. */
export function ListBody({ children }: { children: ReactNode }) {
  const { pending } = useList();
  return (
    <div aria-busy={pending} className={cn("transition-opacity", pending && "pointer-events-none opacity-60")}>
      {children}
    </div>
  );
}

/** Search box (debounced) followed by the filters; a reset link appears when anything is set. */
export function ListToolbar({ placeholder, children, actions }: { placeholder: string; children?: ReactNode; actions?: ReactNode }) {
  const t = useTranslations("admin.list");
  const { pending, navigate } = useList();
  const searchParams = useSearchParams();
  const current = searchParams.get("q") ?? "";
  const [value, setValue] = useState(current);
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const lastSent = useRef(current);

  // Back/forward or a reset changes the URL: show its term.
  useEffect(() => {
    if (current !== lastSent.current) {
      lastSent.current = current;
      setValue(current);
    }
  }, [current]);

  const search = (text: string) => {
    setValue(text);
    if (timer.current) clearTimeout(timer.current);
    timer.current = setTimeout(() => {
      lastSent.current = text.trim();
      navigate({ q: text.trim() || null });
    }, 350);
  };
  const filtered = [...searchParams.keys()].some((key) => !["page", "pageSize", "sort", "dir"].includes(key));

  return (
    <div className="flex flex-wrap items-center gap-2">
      <div className="relative min-w-0 flex-1 basis-56">
        <Search className="pointer-events-none absolute top-1/2 left-2.5 size-4 -translate-y-1/2 text-muted-foreground" aria-hidden="true" />
        <Input
          type="search"
          value={value}
          onChange={(e) => search(e.target.value)}
          placeholder={placeholder}
          aria-label={placeholder}
          className="pl-8"
        />
        {pending && <Loader2 className="absolute top-1/2 right-2.5 size-4 -translate-y-1/2 animate-spin text-muted-foreground" aria-label={t("loading")} />}
      </div>
      {children}
      {filtered && (
        <button
          type="button"
          onClick={() => {
            const reset: Record<string, null> = {};
            for (const key of searchParams.keys()) if (!["pageSize", "sort", "dir"].includes(key)) reset[key] = null;
            setValue("");
            lastSent.current = "";
            navigate(reset);
          }}
          className="inline-flex h-9 items-center gap-1 rounded-md px-2 text-sm text-muted-foreground hover:bg-accent hover:text-foreground"
        >
          <X className="size-4" />
          {t("reset")}
        </button>
      )}
      {actions && <div className="ml-auto flex flex-wrap items-center gap-2">{actions}</div>}
    </div>
  );
}

export type FilterOption = { value: string; label: string };

/** One filter as a native select; the empty option means "all" (or `fallback`, the value the API assumes). */
export function FilterSelect({ name, label, options, allLabel, fallback = "" }: {
  name: string;
  label: string;
  options: FilterOption[];
  allLabel?: string;
  fallback?: string;
}) {
  const { navigate } = useList();
  const searchParams = useSearchParams();
  return (
    <div className="w-auto min-w-36 basis-36">
      <NativeSelect
        aria-label={label}
        title={label}
        value={searchParams.get(name) ?? fallback}
        onChange={(e) => navigate({ [name]: e.target.value === fallback ? null : e.target.value })}
      >
        {allLabel !== undefined && <option value="">{allLabel}</option>}
        {options.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
      </NativeSelect>
    </div>
  );
}

/** A date bound of a range filter (YYYY-MM-DD in the URL). */
export function FilterDate({ name, label }: { name: string; label: string }) {
  const { navigate } = useList();
  const searchParams = useSearchParams();
  return (
    <label className="flex items-center gap-1.5 text-sm text-muted-foreground">
      <span className="whitespace-nowrap">{label}</span>
      <Input type="date" className="w-38" value={searchParams.get(name) ?? ""} onChange={(e) => navigate({ [name]: e.target.value || null })} />
    </label>
  );
}

/** Column title that sorts the list by `field` (click again to reverse). `defaultField` is the list's own order. */
export function SortHeader({ field, label, defaultField, defaultDesc = false, className }: {
  field: string;
  label: string;
  defaultField?: string;
  defaultDesc?: boolean;
  className?: string;
}) {
  const { navigate } = useList();
  const searchParams = useSearchParams();
  const sort = searchParams.get("sort") ?? defaultField;
  const desc = searchParams.has("sort") ? searchParams.get("dir") === "desc" : defaultDesc;
  const active = sort === field;
  const Icon = active ? (desc ? ArrowDown : ArrowUp) : ArrowUpDown;
  return (
    <button
      type="button"
      onClick={() => navigate({ sort: field, dir: active && !desc ? "desc" : null })}
      className={cn("inline-flex items-center gap-1 hover:text-foreground", active && "text-foreground", className)}
    >
      {label}
      <Icon className={cn("size-3.5", !active && "opacity-40")} />
    </button>
  );
}

const pageSizes = [25, 50, 100];

/** "1–25 of 1,234", page size, previous / next. */
export function Pagination({ total, page, pageSize }: { total: number; page: number; pageSize: number }) {
  const t = useTranslations("admin.list");
  const { navigate } = useList();
  const pages = Math.max(1, Math.ceil(total / pageSize));
  if (total === 0) return null;
  const from = (page - 1) * pageSize + 1;
  const to = Math.min(total, page * pageSize);
  const go = (target: number) => navigate({ page: target > 1 ? String(target) : null });
  const button = "inline-grid size-8 place-items-center rounded-md border bg-card hover:bg-accent disabled:pointer-events-none disabled:opacity-40";
  return (
    <nav className="flex flex-wrap items-center justify-between gap-3 text-sm text-muted-foreground" aria-label={t("pages")}>
      <span className="tabular-nums">{t("range", { from, to, total })}</span>
      <div className="flex items-center gap-2">
        <label className="flex items-center gap-1.5">
          <span className="hidden sm:inline">{t("pageSize")}</span>
          <NativeSelect className="h-8 w-20" value={String(pageSize)} onChange={(e) => navigate({ pageSize: e.target.value === "25" ? null : e.target.value })}>
            {pageSizes.map((size) => <option key={size} value={size}>{size}</option>)}
          </NativeSelect>
        </label>
        <button type="button" className={button} disabled={page <= 1} onClick={() => go(page - 1)} aria-label={t("previous")}>
          <ChevronLeft className="size-4" />
        </button>
        <span className="tabular-nums">{t("page", { page: Math.min(page, pages), pages })}</span>
        <button type="button" className={button} disabled={page >= pages} onClick={() => go(page + 1)} aria-label={t("next")}>
          <ChevronRight className="size-4" />
        </button>
      </div>
    </nav>
  );
}
