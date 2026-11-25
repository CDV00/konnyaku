"use client";

import { useState, type FormEvent } from "react";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { Button, type ButtonProps } from "@konnyaku/ui/components/animate-ui/components/buttons/button";

/** Sends one booking action through `/learn/api/booking/*`, then reloads the page data; errors are shown in place. */
function useBookingAction() {
  const t = useTranslations("booking");
  const router = useRouter();
  const [error, setError] = useState<string | null>(null);
  const [pending, setPending] = useState(false);

  async function send(path: string, body?: unknown): Promise<boolean> {
    setError(null);
    setPending(true);
    try {
      const response = await fetch(`/learn/api/booking/${path}`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body ?? {}),
      });
      if (response.ok) {
        router.refresh();
        return true;
      }
      const data = (await response.json().catch(() => ({}))) as { code?: string; params?: Record<string, unknown> };
      const key = `errors.${(data.code ?? "unknown").replaceAll(".", "_")}`;
      const params = Object.fromEntries(
        Object.entries(data.params ?? {}).filter(([, v]) => typeof v === "number" || typeof v === "string"),
      ) as Record<string, string | number>;
      setError(t.has(key) ? t(key, params) : t("errors.unknown"));
      return false;
    } finally {
      setPending(false);
    }
  }

  return { error, pending, send };
}

export function BookingButton({ path, body, label, confirm, variant = "outline" }: {
  path: string;
  body?: unknown;
  label: string;
  confirm?: string;
  variant?: ButtonProps["variant"];
}) {
  const { error, pending, send } = useBookingAction();
  return (
    <span className="inline-flex flex-col items-start gap-1">
      <Button
        type="button"
        size="sm"
        variant={variant}
        disabled={pending}
        onClick={() => {
          if (confirm && !window.confirm(confirm)) return;
          void send(path, body);
        }}
      >
        {label}
      </Button>
      {error && <span role="alert" className="max-w-72 text-xs text-destructive">{error}</span>}
    </span>
  );
}

/** Asks staff to place a make-up by hand, with an optional note. */
export function MakeupRequestForm({ entitlementId, label, noteLabel }: { entitlementId: string; label: string; noteLabel: string }) {
  const { error, pending, send } = useBookingAction();
  const [note, setNote] = useState("");
  async function onSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    await send(`makeups/${entitlementId}/request`, { note });
  }
  return (
    <form onSubmit={onSubmit} className="flex flex-col gap-2 sm:flex-row sm:items-end">
      <label className="flex-1 space-y-1 text-sm">
        <span className="text-muted-foreground">{noteLabel}</span>
        <input
          value={note}
          onChange={(e) => setNote(e.target.value)}
          maxLength={300}
          className="h-9 w-full rounded-md border bg-background px-3 text-sm shadow-xs outline-none focus-visible:ring-2 focus-visible:ring-ring/50"
        />
      </label>
      <Button type="submit" size="sm" disabled={pending}>{label}</Button>
      {error && <span role="alert" className="text-xs text-destructive">{error}</span>}
    </form>
  );
}
