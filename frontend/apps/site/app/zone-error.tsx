"use client";

import { useEffect } from "react";
import { useTranslations } from "next-intl";
import { RotateCcw, TriangleAlert } from "lucide-react";
import { primaryAction, secondaryAction, StatusView } from "./status-view";

/** Error boundary content of a segment: what happened, retry, a way home, and the digest to match server logs. */
export function ErrorScreen({ error, retry, home, homeLabel }: {
  error: Error & { digest?: string };
  retry: () => void;
  home: string;
  homeLabel: "home" | "zoneHome";
}) {
  const t = useTranslations("status.error");
  useEffect(() => console.error(error), [error]);
  return (
    <StatusView
      icon={TriangleAlert}
      code="500"
      title={t("title")}
      body={t("body")}
      footnote={error.digest ? t("reference", { digest: error.digest }) : undefined}
    >
      <button type="button" onClick={() => retry()} className={primaryAction}>
        <RotateCcw className="size-4" aria-hidden />
        {t("retry")}
      </button>
      <a href={home} className={secondaryAction}>{t(homeLabel)}</a>
    </StatusView>
  );
}
