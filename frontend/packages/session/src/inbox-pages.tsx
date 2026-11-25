"use client";

import { useCallback, useEffect, useState } from "react";
import { useTranslations } from "next-intl";
import { BellOff, CheckCheck, Settings } from "lucide-react";
import { Button } from "@konnyaku/ui/components/animate-ui/components/buttons/button";
import { Switch } from "@konnyaku/ui/components/animate-ui/components/radix/switch";
import { EmptyState, FormMessage, Panel } from "@konnyaku/ui/kk/blocks";
import { cn } from "@konnyaku/ui/lib/utils";
import { InboxEntry, useInbox, useOpenItem, type InboxItem } from "./inbox";

const PAGE_SIZE = 20;

/** The full inbox of an area: all or unread, newest first, paged; refreshes live when something arrives or is read. */
export function InboxList({ settingsHref }: { settingsHref: string }) {
  const { base, version, unread, markAllRead } = useInbox();
  const open = useOpenItem();
  const t = useTranslations("inbox");
  const [onlyUnread, setOnlyUnread] = useState(false);
  const [pages, setPages] = useState(1);
  const [items, setItems] = useState<InboxItem[] | null>(null);
  const [total, setTotal] = useState(0);
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    let cancelled = false;
    async function load() {
      try {
        const response = await fetch(`${base}/inbox?page=1&size=${PAGE_SIZE * pages}&unread=${onlyUnread}`, { cache: "no-store" });
        if (!response.ok) throw new Error(String(response.status));
        const page = (await response.json()) as { items: InboxItem[]; total: number };
        if (cancelled) return;
        setItems(page.items);
        setTotal(page.total);
        setFailed(false);
      } catch {
        if (!cancelled) setFailed(true);
      }
    }
    void load();
    return () => {
      cancelled = true;
    };
  }, [base, version, onlyUnread, pages]);

  const filter = (value: boolean) => {
    setOnlyUnread(value);
    setPages(1);
  };

  return (
    <Panel
      bodyClassName="p-2 sm:p-3"
      title={
        <span className="flex flex-wrap gap-1" role="group">
          {[false, true].map((value) => (
            <button
              key={String(value)}
              type="button"
              aria-pressed={onlyUnread === value}
              onClick={() => filter(value)}
              className={cn(
                "rounded-md px-3 py-1 text-sm font-medium transition-colors",
                onlyUnread === value ? "bg-primary/10 text-primary" : "text-muted-foreground hover:bg-accent",
              )}
            >
              {value ? t("filterUnread") : t("filterAll")}
            </button>
          ))}
        </span>
      }
      action={
        <span className="flex flex-wrap gap-2">
          <Button variant="outline" size="sm" disabled={unread === 0} onClick={() => void markAllRead()}>
            <CheckCheck />
            {t("markAllRead")}
          </Button>
          <Button variant="ghost" size="sm" asChild>
            <a href={settingsHref}>
              <Settings />
              {t("settings")}
            </a>
          </Button>
        </span>
      }
    >
      {failed && <FormMessage>{t("loadFailed")}</FormMessage>}
      {items && items.length === 0 && !failed && (
        <EmptyState icon={BellOff} title={onlyUnread ? t("emptyUnread") : t("empty")} className="m-2" />
      )}
      {items && items.length > 0 && (
        <div className="grid gap-0.5" data-testid="inbox-page-list">
          {items.map((item) => <InboxEntry key={item.id} item={item} onOpen={open} />)}
        </div>
      )}
      {items && items.length < total && (
        <div className="mt-2 flex justify-center">
          <Button variant="ghost" size="sm" onClick={() => setPages((p) => p + 1)}>
            {t("more")}
          </Button>
        </div>
      )}
    </Panel>
  );
}

type Preference = { kind: string; email: boolean; push: boolean; emailAvailable: boolean };
type PushState = "unsupported" | "notConfigured" | "denied" | "off" | "on";

function base64UrlToBytes(value: string): Uint8Array<ArrayBuffer> {
  const padded = (value + "=".repeat((4 - (value.length % 4)) % 4)).replaceAll("-", "+").replaceAll("_", "/");
  const binary = window.atob(padded);
  const bytes = new Uint8Array(new ArrayBuffer(binary.length));
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
  return bytes;
}

/**
 * Channels per kind (the inbox is always on; email and push can be turned off) and Web Push on this device.
 * `deviceOn`: this browser subscribed while signed in to this area (cookie set by the subscribe route).
 */
export function NotificationSettings({ deviceOn }: { deviceOn: boolean }) {
  const { base } = useInbox();
  const t = useTranslations("inbox");
  const kinds = useTranslations("inbox.kinds") as unknown as ((key: string) => string) & { has: (key: string) => boolean };
  const [preferences, setPreferences] = useState<Preference[] | null>(null);
  const [message, setMessage] = useState<{ tone: "error" | "success"; text: string } | null>(null);
  const [saving, setSaving] = useState(false);
  const [push, setPush] = useState<PushState>(deviceOn ? "on" : "off");
  const [publicKey, setPublicKey] = useState<string | null>(null);
  const [pushError, setPushError] = useState(false);

  useEffect(() => {
    let cancelled = false;
    async function load() {
      const [prefs, config] = await Promise.all([
        fetch(`${base}/preferences`, { cache: "no-store" }).then((r) => (r.ok ? (r.json() as Promise<Preference[]>) : null)).catch(() => null),
        fetch(`${base}/push/config`, { cache: "no-store" })
          .then((r) => (r.ok ? (r.json() as Promise<{ enabled: boolean; publicKey: string | null }>) : null))
          .catch(() => null),
      ]);
      if (cancelled) return;
      setPreferences(prefs ?? []);
      const supported = "serviceWorker" in navigator && "PushManager" in window && "Notification" in window;
      if (!supported) setPush("unsupported");
      else if (!config?.enabled || !config.publicKey) setPush("notConfigured");
      else if (Notification.permission === "denied") setPush("denied");
      setPublicKey(config?.publicKey ?? null);
    }
    void load();
    return () => {
      cancelled = true;
    };
  }, [base]);

  const toggle = (kind: string, channel: "email" | "push", value: boolean) => {
    setMessage(null);
    setPreferences((current) => current?.map((p) => (p.kind === kind ? { ...p, [channel]: value } : p)) ?? null);
  };

  const save = async () => {
    if (!preferences) return;
    setSaving(true);
    try {
      const response = await fetch(`${base}/preferences`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(preferences.map(({ kind, email, push: pushOn }) => ({ kind, email, push: pushOn }))),
      });
      if (!response.ok) throw new Error(String(response.status));
      setPreferences((await response.json()) as Preference[]);
      setMessage({ tone: "success", text: t("prefs.saved") });
    } catch {
      setMessage({ tone: "error", text: t("prefs.saveFailed") });
    } finally {
      setSaving(false);
    }
  };

  const enablePush = useCallback(async () => {
    setPushError(false);
    if (!publicKey) return;
    try {
      const permission = await Notification.requestPermission();
      if (permission !== "granted") {
        setPush(permission === "denied" ? "denied" : "off");
        return;
      }
      const registration = await navigator.serviceWorker.register("/sw.js", { scope: "/" });
      await navigator.serviceWorker.ready;
      const subscription =
        (await registration.pushManager.getSubscription()) ??
        (await registration.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: base64UrlToBytes(publicKey) }));
      const json = subscription.toJSON() as { endpoint: string; keys?: { p256dh?: string; auth?: string } };
      const response = await fetch(`${base}/push/subscriptions`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ endpoint: json.endpoint, keys: { p256dh: json.keys?.p256dh ?? "", auth: json.keys?.auth ?? "" } }),
      });
      if (!response.ok) throw new Error(String(response.status));
      setPush("on");
    } catch {
      setPushError(true);
    }
  }, [base, publicKey]);

  const disablePush = useCallback(async () => {
    setPushError(false);
    try {
      await fetch(`${base}/push/subscriptions`, { method: "DELETE" });
      const registration = await navigator.serviceWorker.getRegistration("/");
      await (await registration?.pushManager.getSubscription())?.unsubscribe();
      setPush("off");
    } catch {
      setPushError(true);
    }
  }, [base]);

  const pushNote: Record<PushState, string> = {
    unsupported: t("prefs.pushUnsupported"),
    notConfigured: t("prefs.pushNotConfigured"),
    denied: t("prefs.pushDenied"),
    off: t("prefs.pushOff"),
    on: t("prefs.pushOn"),
  };

  return (
    <div className="grid gap-6">
      <Panel title={t("prefs.pushTitle")} description={t("prefs.pushHint")}>
        <div className="flex flex-wrap items-center justify-between gap-3">
          <p className="text-sm text-muted-foreground" data-testid="push-state">{pushNote[push]}</p>
          {push === "off" && <Button onClick={() => void enablePush()}>{t("prefs.pushEnable")}</Button>}
          {push === "on" && (
            <Button variant="outline" onClick={() => void disablePush()}>
              {t("prefs.pushDisable")}
            </Button>
          )}
        </div>
        {pushError && (
          <div className="mt-3">
            <FormMessage>{t("prefs.pushFailed")}</FormMessage>
          </div>
        )}
      </Panel>

      <Panel bodyClassName="p-0">
        <div className="overflow-x-auto">
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b text-left text-muted-foreground">
                <th className="px-5 py-3 font-medium">{t("prefs.kind")}</th>
                <th className="hidden px-3 py-3 text-center font-medium sm:table-cell">{t("prefs.inApp")}</th>
                <th className="px-3 py-3 text-center font-medium">{t("prefs.email")}</th>
                <th className="px-3 py-3 text-center font-medium">{t("prefs.push")}</th>
              </tr>
            </thead>
            <tbody>
              {preferences?.map((p) => {
                const key = p.kind.replaceAll(".", "_");
                const label = kinds.has(key) ? kinds(key) : p.kind;
                return (
                  <tr key={p.kind} className="border-b last:border-0" data-testid="pref-row" data-kind={p.kind}>
                    <td className="px-5 py-3 font-medium">{label}</td>
                    <td className="hidden px-3 py-3 text-center text-xs text-muted-foreground sm:table-cell">{t("prefs.always")}</td>
                    <td className="px-3 py-3">
                      <span className="flex justify-center">
                        {p.emailAvailable ? (
                          <Switch
                            checked={p.email}
                            onCheckedChange={(value: boolean) => toggle(p.kind, "email", value)}
                            aria-label={`${label} · ${t("prefs.email")}`}
                            data-testid="pref-email"
                          />
                        ) : (
                          <span className="text-xs text-muted-foreground">{t("prefs.noEmail")}</span>
                        )}
                      </span>
                    </td>
                    <td className="px-3 py-3">
                      <span className="flex justify-center">
                        <Switch
                          checked={p.push}
                          onCheckedChange={(value: boolean) => toggle(p.kind, "push", value)}
                          aria-label={`${label} · ${t("prefs.push")}`}
                          data-testid="pref-push"
                        />
                      </span>
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
        <div className="flex flex-wrap items-center gap-3 border-t px-5 py-4">
          <Button onClick={() => void save()} disabled={saving || !preferences}>
            {t("prefs.save")}
          </Button>
          {message && <FormMessage tone={message.tone}>{message.text}</FormMessage>}
        </div>
      </Panel>
    </div>
  );
}
