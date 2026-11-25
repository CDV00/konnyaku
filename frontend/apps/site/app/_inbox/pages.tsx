import { cookies } from "next/headers";
import { getTranslations } from "next-intl/server";
import { ArrowLeft } from "lucide-react";
import { requireMe, type Realm } from "@konnyaku/session";
import { InboxList, NotificationSettings } from "@konnyaku/session/inbox-pages";
import { PageHeader } from "@konnyaku/ui/kk/blocks";
import { pushCookie } from "../api/inbox/push";

/** Inbox page of an area (`/learn/inbox`, `/teach/inbox`, `/ops/inbox`); the bell of the layout provides the live data. */
export async function InboxPage({ realm, home }: { realm: Realm; home: string }) {
  await requireMe(realm);
  const t = await getTranslations("inbox");
  return (
    <div className="grid gap-6">
      <PageHeader title={t("title")} description={t("description")} />
      <InboxList settingsHref={`${home}/inbox/settings`} />
    </div>
  );
}

/** Notification settings of an area: channels per kind, push on this device. */
export async function InboxSettingsPage({ realm, home }: { realm: Realm; home: string }) {
  await requireMe(realm);
  const t = await getTranslations("inbox");
  const deviceOn = Boolean((await cookies()).get(pushCookie[realm])?.value);
  return (
    <div className="grid gap-6">
      <PageHeader
        title={t("prefs.title")}
        description={t("prefs.description")}
        actions={
          <a href={`${home}/inbox`} className="inline-flex items-center gap-1.5 text-sm font-medium text-primary hover:underline">
            <ArrowLeft className="size-4" />
            {t("prefs.back")}
          </a>
        }
      />
      <NotificationSettings deviceOn={deviceOn} />
    </div>
  );
}
