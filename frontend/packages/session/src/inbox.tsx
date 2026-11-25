"use client";

import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { useFormatter, useTranslations } from "next-intl";
import { Bell, CheckCheck, Settings } from "lucide-react";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuTrigger,
} from "@konnyaku/ui/components/animate-ui/components/radix/dropdown-menu";
import { cn } from "@konnyaku/ui/lib/utils";
import type { Realm } from "./index";
import { describeInboxItem, type InboxItem, type InboxTranslator } from "./inbox-text";

export type { InboxItem } from "./inbox-text";

/**
 * In-app notifications of the signed-in person (doc 10.9): one provider per page holds the unread count and the latest
 * items, keeps ONE realtime socket for the tab (`/rt` of the realtime service, ticket from the API), and falls back to
 * polling while the socket is down. Signals carry ids only: the provider fetches the content through the web app's
 * route handlers (`/api/inbox/{realm}/*`), which call the API with the session cookie.
 */

type InboxPage = { items: InboxItem[]; total: number; unread: number; cursor: number };
type InboxSync = { items: InboxItem[]; unread: number; cursor: number; more: boolean };
type InboxCount = { unread: number; cursor: number };

type InboxState = {
  realm: Realm;
  /** Route handlers of this realm, e.g. `/api/inbox/learn`. */
  base: string;
  unread: number;
  /** Newest first, at most {@link LATEST}. */
  latest: InboxItem[];
  /** The realtime socket is up (false: polling every 30 s). */
  live: boolean;
  /** Bumps whenever something arrived or was read, so a page listing the inbox reloads. */
  version: number;
  markRead: (id: string) => Promise<void>;
  markAllRead: () => Promise<void>;
};

const LATEST = 8;
const InboxContext = createContext<InboxState | null>(null);

export function useInbox(): InboxState {
  const state = useContext(InboxContext);
  if (!state) throw new Error("useInbox needs an <InboxProvider>.");
  return state;
}

/** `/rt` (same host, through Caddy) or an absolute ws(s):// URL (dev) → a WebSocket URL for this page. */
function socketUrl(url: string): string {
  if (/^wss?:\/\//.test(url)) return url;
  const protocol = window.location.protocol === "https:" ? "wss:" : "ws:";
  return `${protocol}//${window.location.host}${url.startsWith("/") ? url : `/${url}`}`;
}

async function getJson<T>(url: string, init?: RequestInit): Promise<T | null> {
  try {
    const response = await fetch(url, { cache: "no-store", ...init });
    return response.ok ? ((await response.json()) as T) : null;
  } catch {
    return null;
  }
}

function merge(fresh: InboxItem[], current: InboxItem[]): InboxItem[] {
  const byId = new Map<string, InboxItem>();
  for (const item of [...fresh, ...current]) if (!byId.has(item.id)) byId.set(item.id, item);
  return [...byId.values()].sort((a, b) => b.seq - a.seq).slice(0, LATEST);
}

export function InboxProvider({ realm, children, pollMs = 30_000 }: { realm: Realm; children: ReactNode; pollMs?: number }) {
  const base = `/api/inbox/${realm}`;
  const [unread, setUnread] = useState(0);
  const [latest, setLatest] = useState<InboxItem[]>([]);
  const [live, setLive] = useState(false);
  const [version, setVersion] = useState(0);
  const cursor = useRef(0);

  /** Count and latest items from scratch (first load, something read elsewhere, a gap too big to sync). */
  const reload = useCallback(async () => {
    const page = await getJson<InboxPage>(`${base}/inbox?size=${LATEST}`);
    if (!page) return;
    cursor.current = Math.max(cursor.current, page.cursor);
    setUnread(page.unread);
    setLatest(page.items);
    setVersion((v) => v + 1);
  }, [base]);

  /** What came after the cursor (a signal, a reconnection, a poll that saw a newer cursor). */
  const sync = useCallback(async () => {
    const result = await getJson<InboxSync>(`${base}/inbox/sync?since=${cursor.current}`);
    if (!result) return;
    if (result.more) {
      await reload();
      return;
    }
    cursor.current = Math.max(cursor.current, result.cursor);
    setUnread(result.unread);
    if (result.items.length > 0) {
      setLatest((current) => merge(result.items, current));
      setVersion((v) => v + 1);
    }
  }, [base, reload]);

  // Realtime socket, reconnected with backoff; a fresh ticket every time.
  useEffect(() => {
    let stopped = false;
    let socket: WebSocket | null = null;
    let timer: number | undefined;
    let backoff = 1000;

    const schedule = (delay: number) => {
      if (stopped) return;
      window.clearTimeout(timer);
      timer = window.setTimeout(() => void connect(), delay);
      backoff = Math.min(backoff * 2, 30_000);
    };

    async function connect() {
      if (stopped) return;
      let ticket: { ticket: string; url: string } | null = null;
      try {
        const response = await fetch(`${base}/realtime/ticket`, { method: "POST", cache: "no-store" });
        // Not configured here (no realtime service): polling only. Signed out: SessionWatch takes over.
        if (response.status === 503 || response.status === 401 || response.status === 403) return;
        if (response.ok) ticket = (await response.json()) as { ticket: string; url: string };
      } catch {
        ticket = null;
      }
      if (stopped) return;
      if (!ticket) {
        schedule(backoff);
        return;
      }
      const ws = new WebSocket(socketUrl(ticket.url));
      socket = ws;
      ws.onopen = () => ws.send(JSON.stringify({ type: "auth", ticket: ticket.ticket }));
      ws.onmessage = (event) => {
        let message: { t?: string; seq?: number; code?: string };
        try {
          message = JSON.parse(String(event.data)) as typeof message;
        } catch {
          return;
        }
        switch (message.t) {
          case "ready":
            backoff = 1000;
            setLive(true);
            void sync();
            break;
          case "inbox":
            if ((message.seq ?? Number.MAX_SAFE_INTEGER) > cursor.current) void sync();
            break;
          case "inbox.read":
            void reload();
            break;
          case "resync":
            void sync();
            break;
          case "reconnect":
            backoff = 250;
            break;
        }
      };
      ws.onclose = () => {
        if (socket === ws) socket = null;
        setLive(false);
        schedule(backoff);
      };
    }

    // After this render (the first load sets state).
    void Promise.resolve().then(reload);
    void connect();
    return () => {
      stopped = true;
      window.clearTimeout(timer);
      socket?.close();
    };
  }, [base, reload, sync]);

  // Fallback while the socket is down, and catch-up when the tab comes back.
  useEffect(() => {
    const check = async () => {
      if (document.visibilityState !== "visible") return;
      const count = await getJson<InboxCount>(`${base}/inbox/unread-count`);
      if (!count) return;
      if (count.cursor > cursor.current) await sync();
      else setUnread(count.unread);
    };
    const timer = live ? undefined : window.setInterval(() => void check(), pollMs);
    const onVisible = () => void check();
    document.addEventListener("visibilitychange", onVisible);
    return () => {
      window.clearInterval(timer);
      document.removeEventListener("visibilitychange", onVisible);
    };
  }, [base, live, pollMs, sync]);

  const markRead = useCallback(async (id: string) => {
    const count = await getJson<InboxCount>(`${base}/inbox/${id}/read`, { method: "POST" });
    const now = new Date().toISOString();
    setLatest((current) => current.map((item) => (item.id === id && !item.readAt ? { ...item, readAt: now } : item)));
    if (count) setUnread(count.unread);
    setVersion((v) => v + 1);
  }, [base]);

  const markAllRead = useCallback(async () => {
    const count = await getJson<InboxCount>(`${base}/inbox/read-all`, { method: "POST" });
    const now = new Date().toISOString();
    setLatest((current) => current.map((item) => (item.readAt ? item : { ...item, readAt: now })));
    setUnread(count?.unread ?? 0);
    setVersion((v) => v + 1);
  }, [base]);

  const value = useMemo<InboxState>(
    () => ({ realm, base, unread, latest, live, version, markRead, markAllRead }),
    [realm, base, unread, latest, live, version, markRead, markAllRead],
  );
  return <InboxContext.Provider value={value}>{children}</InboxContext.Provider>;
}

type DateTimeOptions = Parameters<ReturnType<typeof useFormatter>["dateTime"]>[1];

/** Title and body of an item in the reader's language (see {@link describeInboxItem}). */
export function useInboxText(): (item: InboxItem) => { title: string; body: string } {
  const t = useTranslations("inbox") as unknown as InboxTranslator;
  const format = useFormatter();
  return useCallback((item: InboxItem) => describeInboxItem(item, t, (value, options) => format.dateTime(value, options as DateTimeOptions)), [t, format]);
}

/** One notification: title, body, how long ago; unread ones are marked. Clicking opens its page and marks it read. */
export function InboxEntry({ item, compact, onOpen }: { item: InboxItem; compact?: boolean; onOpen: (item: InboxItem) => void }) {
  const text = useInboxText()(item);
  const format = useFormatter();
  const t = useTranslations("inbox");
  const unread = !item.readAt;
  return (
    <a
      href={item.link ?? "#"}
      onClick={(event) => {
        event.preventDefault();
        onOpen(item);
      }}
      data-testid="inbox-item"
      data-kind={item.kind}
      data-unread={unread ? "true" : "false"}
      className={cn(
        "flex gap-3 rounded-md px-3 py-2.5 text-left text-sm transition-colors hover:bg-accent focus-visible:bg-accent focus-visible:outline-none",
        unread && "bg-primary/5",
      )}
    >
      <span className={cn("mt-1.5 size-2 shrink-0 rounded-full", unread ? "bg-primary" : "bg-transparent")} aria-hidden />
      <span className="min-w-0 flex-1">
        <span className={cn("block", unread ? "font-semibold" : "font-medium")}>{text.title}</span>
        {text.body && <span className={cn("block text-muted-foreground", compact && "line-clamp-2")}>{text.body}</span>}
        <span className="mt-0.5 block text-xs text-muted-foreground">
          {format.relativeTime(new Date(item.createdAt))}
          {unread && <span className="sr-only">{t("unread")}</span>}
        </span>
      </span>
    </a>
  );
}

/** Marks the item read (in the background) and goes to its page. */
export function useOpenItem(): (item: InboxItem) => void {
  const { markRead } = useInbox();
  return useCallback(
    (item: InboxItem) => {
      const read = item.readAt ? Promise.resolve() : markRead(item.id);
      void read.finally(() => {
        if (item.link) window.location.assign(item.link);
      });
    },
    [markRead],
  );
}

/** Bell with the unread count and a dropdown of the latest notifications, for the top bar of every area. */
export function InboxBell({ href, settingsHref }: { href: string; settingsHref: string }) {
  const { unread, latest, live, markAllRead } = useInbox();
  const open = useOpenItem();
  const t = useTranslations("inbox");
  const badge = unread > 99 ? "99+" : String(unread);
  return (
    <DropdownMenu>
      <DropdownMenuTrigger
        aria-label={unread > 0 ? t("bellUnread", { count: unread }) : t("bell")}
        data-testid="inbox-bell"
        data-live={live ? "true" : "false"}
        className="relative grid size-9 place-items-center rounded-md transition-colors hover:bg-accent focus-visible:ring-[3px] focus-visible:ring-ring/50 focus-visible:outline-none"
      >
        <Bell className="size-5" />
        {unread > 0 && (
          <span
            data-testid="inbox-unread"
            className="absolute -top-0.5 -right-0.5 grid h-5 min-w-5 place-items-center rounded-full bg-destructive px-1 text-[10px] leading-none font-semibold text-white tabular-nums"
          >
            {badge}
          </span>
        )}
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end" className="w-[min(24rem,calc(100vw-2rem))] p-0">
        <div className="flex items-center justify-between gap-2 border-b px-3 py-2">
          <span className="text-sm font-semibold">{t("title")}</span>
          <div className="flex items-center gap-1">
            {unread > 0 && (
              <button
                type="button"
                onClick={() => void markAllRead()}
                className="flex items-center gap-1 rounded-md px-2 py-1 text-xs text-muted-foreground hover:bg-accent hover:text-foreground"
              >
                <CheckCheck className="size-3.5" />
                {t("markAllRead")}
              </button>
            )}
            <a href={settingsHref} aria-label={t("settings")} className="grid size-7 place-items-center rounded-md text-muted-foreground hover:bg-accent">
              <Settings className="size-4" />
            </a>
          </div>
        </div>
        <div className="max-h-[min(28rem,70vh)] overflow-y-auto p-1" data-testid="inbox-dropdown">
          {latest.length === 0 ? (
            <p className="px-3 py-8 text-center text-sm text-muted-foreground">{t("empty")}</p>
          ) : (
            latest.map((item) => <InboxEntry key={item.id} item={item} compact onOpen={open} />)
          )}
        </div>
        <a href={href} className="block border-t px-3 py-2 text-center text-sm font-medium text-primary hover:bg-accent">
          {t("viewAll")}
        </a>
      </DropdownMenuContent>
    </DropdownMenu>
  );
}
