"use client";

import { useEffect, useState } from "react";
import { useFormatter, useLocale, useTranslations } from "next-intl";
import { ArrowRight, History } from "lucide-react";
import { mergeHistory, type HistoryEntry } from "./history-entry";

/** Status-like values are translated with the first catalog that knows them. */
const valueNamespaces = ["classStatus", "orderStatus", "leadStatus", "sessionStatus", "staffStatus", "modes", "roles"] as const;
const isoDateTime = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}/;

/**
 * Change log of one record (and of what is logged under it): who changed which field, when, from what to what.
 * `sources` lists the module histories to merge (a class shows its own and its schedule's), fetched when the component
 * mounts (inside a drawer: each time it opens); a page passes `entries` it loaded itself so a refresh shows new changes.
 * `names` turns ids found in values (branches, levels, teachers) into readable names.
 */
export function ChangeHistory({ sources = [], entries: preloaded, names = {} }: {
  sources?: { module: string; id: string }[];
  entries?: HistoryEntry[];
  names?: Record<string, string>;
}) {
  const t = useTranslations("admin");
  const format = useFormatter();
  const locale = useLocale();
  const [fetched, setFetched] = useState<HistoryEntry[] | null>(null);
  const [failed, setFailed] = useState(false);
  const key = sources.map((s) => `${s.module}/${s.id}`).join(",");
  const entries = preloaded ?? fetched;

  useEffect(() => {
    if (preloaded || !key) return;
    let cancelled = false;
    Promise.all(
      key.split(",").map(async (path) => {
        const response = await fetch(`/ops/api/relay/${path.replace("/", "/history/")}`);
        if (!response.ok) throw new Error(String(response.status));
        return (await response.json()) as HistoryEntry[];
      }),
    )
      .then((lists) => {
        if (!cancelled) setFetched(mergeHistory(lists));
      })
      .catch(() => {
        if (!cancelled) setFailed(true);
      });
    return () => {
      cancelled = true;
    };
  }, [key, preloaded]);

  function label(field: string) {
    return t.has(`history.fields.${field}`) ? t(`history.fields.${field}`) : field;
  }

  function value(field: string, raw: string | null): string {
    if (raw === null || raw === "") return "—";
    if (names[raw]) return names[raw];
    if (raw === "true" || raw === "false") return t(raw === "true" ? "yes" : "no");
    if (isoDateTime.test(raw)) return format.dateTime(new Date(raw), { dateStyle: "short", timeStyle: "short" });
    if (raw.startsWith("{") || raw.startsWith("[")) {
      try {
        const parsed: unknown = JSON.parse(raw);
        if (Array.isArray(parsed)) {
          return parsed
            .map((item) => {
              if (typeof item === "number" && field === "isoDays" && t.has(`days.${item}`)) return t(`days.${item}`);
              if (item && typeof item === "object" && "role" in item) {
                const grant = item as { role: string; branchId: string | null };
                const role = t.has(`roles.${grant.role}`) ? t(`roles.${grant.role}`) : grant.role;
                return grant.branchId ? `${role} (${names[grant.branchId] ?? grant.branchId.slice(0, 8)})` : role;
              }
              return String(item);
            })
            .join(", ");
        }
        if (parsed && typeof parsed === "object") {
          const localized = parsed as Record<string, string>;
          return localized[locale] ?? localized.vi ?? Object.values(localized)[0] ?? raw;
        }
      } catch {
        // Not JSON after all: show as is.
      }
    }
    for (const ns of valueNamespaces) if (t.has(`${ns}.${raw}`)) return t(`${ns}.${raw}`);
    return raw;
  }

  if (failed) return <p className="text-sm text-muted-foreground">{t("history.unavailable")}</p>;
  if (!entries) return <p className="text-sm text-muted-foreground">{t("history.loading")}</p>;
  if (entries.length === 0) return <p className="text-sm text-muted-foreground">{t("history.empty")}</p>;

  return (
    <ol className="space-y-3">
      {entries.map((entry) => (
        <li key={entry.id} className="rounded-lg border bg-card p-3 text-sm">
          <div className="flex flex-wrap items-baseline justify-between gap-x-3 gap-y-1">
            <span className="flex items-center gap-1.5 font-medium">
              <History className="size-3.5 text-muted-foreground" />
              {t("history.line", {
                action: t.has(`history.actions.${entry.action}`) ? t(`history.actions.${entry.action}`) : entry.action,
                entity: t.has(`history.entities.${entry.entityType}`) ? t(`history.entities.${entry.entityType}`) : entry.entityType,
              })}
            </span>
            <span className="text-xs text-muted-foreground">
              {t("history.by", {
                actor: entry.actorName ?? t("history.system"),
                when: format.dateTime(new Date(entry.occurredAt), { dateStyle: "short", timeStyle: "short" }),
              })}
            </span>
          </div>
          {entry.changes.length > 0 && (
            <dl className="mt-2 grid gap-1">
              {entry.changes.map((change) => (
                <div key={change.field} className="grid grid-cols-[minmax(6rem,auto)_1fr] gap-2">
                  <dt className="text-muted-foreground">{label(change.field)}</dt>
                  <dd className="flex min-w-0 flex-wrap items-center gap-1.5 break-words">
                    {change.old !== null && (
                      <>
                        <span className="text-muted-foreground line-through decoration-muted-foreground/50">{value(change.field, change.old)}</span>
                        <ArrowRight className="size-3 shrink-0 text-muted-foreground" />
                      </>
                    )}
                    <span className="font-medium">{value(change.field, change.new)}</span>
                  </dd>
                </div>
              ))}
            </dl>
          )}
        </li>
      ))}
    </ol>
  );
}
