/** Title and body of an inbox item, shared by the bell (client) and the push preview (server). */

export type InboxItem = {
  id: string;
  kind: string;
  titleKey: string;
  params: Record<string, string>;
  link: string | null;
  createdAt: string;
  readAt: string | null;
  seq: number;
};

/** next-intl translator of the `inbox` namespace, loosely typed (keys come from the API). */
export type InboxTranslator = ((key: string, values?: Record<string, string>) => string) & { has: (key: string) => boolean };

type DateTimeFormatter = (value: Date, options: Intl.DateTimeFormatOptions) => string;

/** Keys whose empty value selects the "none" branch of an ICU select in the catalog. */
const selectKeys = ["reason", "dueAt"];

/**
 * Parameters are raw (ISO times, plain numbers): times are written in the reader's language, in the time zone of the
 * class when the item names one; dates (`…On`, `from`, `to`) as calendar dates.
 */
export function describeInboxItem(item: InboxItem, t: InboxTranslator, dateTime: DateTimeFormatter): { title: string; body: string } {
  const key = item.titleKey.replaceAll(".", "_");
  const timeZone = item.params.timeZone || undefined;
  const values: Record<string, string> = {};
  for (const [name, raw] of Object.entries(item.params)) {
    if (!raw) values[name] = selectKeys.includes(name) ? "none" : "";
    else if (name.endsWith("At")) {
      values[name] = dateTime(new Date(raw), { dateStyle: "medium", timeStyle: "short", ...(timeZone ? { timeZone } : {}) });
    } else if (name.endsWith("On") || name === "from" || name === "to") {
      values[name] = dateTime(new Date(`${raw}T00:00:00Z`), { dateStyle: "medium", timeZone: "UTC" });
    } else values[name] = raw;
  }
  for (const name of selectKeys) values[name] ??= "none";
  const title = t.has(`titles.${key}`) ? t(`titles.${key}`, values) : item.kind;
  const body = t.has(`bodies.${key}`) ? t(`bodies.${key}`, values) : "";
  return { title, body };
}
