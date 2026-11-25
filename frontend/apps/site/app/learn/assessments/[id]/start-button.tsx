"use client";

import { useState } from "react";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { Play, RotateCcw } from "lucide-react";
import { Button } from "@konnyaku/ui/components/animate-ui/components/buttons/button";
import { FormMessage } from "@konnyaku/ui/kk/blocks";
import { callApi } from "@/assessment/client";
import { errorKey, type AttemptPlay } from "@/assessment/types";

/** Starts an attempt (or resumes the open one) and opens the player. */
export function StartButton({ assignmentId, resume }: { assignmentId: string; resume?: boolean }) {
  const t = useTranslations("assessment");
  const router = useRouter();
  const [pending, setPending] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function start() {
    setPending(true);
    setError(null);
    const result = await callApi<AttemptPlay>(`/learn/api/assessment/assignments/${assignmentId}/attempts`, "POST");
    if (result.ok) {
      router.push(`/learn/attempts/${result.data.id}`);
      return;
    }
    setPending(false);
    const key = `errors.${errorKey(result.code)}`;
    setError(t.has(key) ? t(key as "errors.unknown") : t("errors.unknown"));
  }

  return (
    <div className="space-y-3">
      <Button type="button" size="lg" className="w-full sm:w-auto" onClick={() => void start()} disabled={pending}>
        {resume ? <RotateCcw /> : <Play />}
        {resume ? t("learn.resume") : t("learn.start")}
      </Button>
      {error && <FormMessage>{error}</FormMessage>}
    </div>
  );
}
