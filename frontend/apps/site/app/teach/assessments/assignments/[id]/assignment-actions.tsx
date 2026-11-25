"use client";

import { useState } from "react";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { Megaphone, Trash2 } from "lucide-react";
import { Button } from "@konnyaku/ui/components/animate-ui/components/buttons/button";
import { FormMessage } from "@konnyaku/ui/kk/blocks";
import { callApi } from "@/assessment/client";
import { errorKey } from "@/assessment/types";

/** Release the scores now; delete an assignment nobody started (a mistake). */
export function AssignmentActions({ id, canRelease, canDelete }: { id: string; canRelease: boolean; canDelete: boolean }) {
  const t = useTranslations("assessment");
  const router = useRouter();
  const [pending, setPending] = useState(false);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function run(path: string, method: "POST" | "DELETE", then: () => void) {
    setPending(true);
    setError(null);
    const result = await callApi(`/teach/api/assessment/${path}`, method);
    setPending(false);
    if (result.ok) {
      then();
      return;
    }
    const key = `errors.${errorKey(result.code)}`;
    setError(t.has(key) ? t(key as "errors.unknown") : t("errors.unknown"));
  }

  return (
    <div className="flex flex-col items-end gap-2">
      <div className="flex flex-wrap justify-end gap-2">
        {canRelease && (
          <Button type="button" variant="outline" disabled={pending} onClick={() => void run(`assignments/${id}/release`, "POST", () => router.refresh())}>
            <Megaphone />
            {t("teach.releaseNow")}
          </Button>
        )}
        {canDelete &&
          (confirmDelete ? (
            <Button type="button" variant="destructive" disabled={pending} onClick={() => void run(`assignments/${id}`, "DELETE", () => router.push("/teach/assessments"))}>
              <Trash2 />
              {t("teach.confirmDelete")}
            </Button>
          ) : (
            <Button type="button" variant="ghost" disabled={pending} onClick={() => setConfirmDelete(true)}>
              <Trash2 />
              {t("teach.delete")}
            </Button>
          ))}
      </div>
      {error && <FormMessage>{error}</FormMessage>}
    </div>
  );
}
