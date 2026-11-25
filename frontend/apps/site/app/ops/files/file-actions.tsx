"use client";

import { useRef, useState, type ChangeEvent } from "react";
import { useRouter } from "next/navigation";
import { useTranslations } from "next-intl";
import { Download, Upload } from "lucide-react";
import { Button } from "@konnyaku/ui/components/animate-ui/components/buttons/button";

type Ticket = { fileId: string; url: string; method: string; headers: Record<string, string>; expiresAt: string };
type ApiError = { code?: string; params?: Record<string, unknown> };

/** Types the center library accepts (the API checks again, by content). */
const accept = [
  "application/pdf",
  "image/jpeg",
  "image/png",
  "image/webp",
  "image/gif",
  "audio/mpeg",
  "audio/mp4",
  "audio/ogg",
  "audio/wav",
  "video/mp4",
  "video/webm",
  ".docx",
  ".xlsx",
  ".pptx",
  ".doc",
  ".xls",
  ".ppt",
].join(",");

function useErrorText() {
  const t = useTranslations("admin");
  return (body: ApiError) => {
    const code = body.code ?? "unknown";
    const key = `errors.${code.replaceAll(".", "_")}`;
    const params = Object.fromEntries(
      Object.entries(body.params ?? {}).filter(([, v]) => typeof v === "number" || typeof v === "string"),
    ) as Record<string, string | number>;
    return t.has(key) ? t(key, params) : t("errors.unknown", { code });
  };
}

/**
 * Uploads teaching material: asks the API for a signed URL, sends the file straight to the object storage (never
 * through the web or API servers), then asks the API to check it.
 */
export function UploadButton() {
  const t = useTranslations("files");
  const errorText = useErrorText();
  const router = useRouter();
  const input = useRef<HTMLInputElement>(null);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<{ ok: boolean; text: string } | null>(null);

  async function onChange(event: ChangeEvent<HTMLInputElement>) {
    const file = event.target.files?.[0];
    event.target.value = "";
    if (!file) return;
    setBusy(true);
    setMessage(null);
    try {
      const created = await fetch("/ops/api/relay/content/uploads", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ fileName: file.name, contentType: file.type, size: file.size, purpose: "Material" }),
      });
      const ticket = (await created.json().catch(() => ({}))) as Ticket & ApiError;
      if (!created.ok) {
        setMessage({ ok: false, text: errorText(ticket) });
        return;
      }
      const sent = await fetch(ticket.url, { method: ticket.method, headers: ticket.headers, body: file }).catch(() => null);
      if (!sent?.ok) {
        setMessage({ ok: false, text: t("uploadFailed") });
        return;
      }
      const completed = await fetch(`/ops/api/relay/content/uploads/${ticket.fileId}/complete`, { method: "POST", body: "{}" });
      if (!completed.ok) {
        setMessage({ ok: false, text: errorText((await completed.json().catch(() => ({}))) as ApiError) });
        return;
      }
      setMessage({ ok: true, text: t("uploaded", { name: file.name }) });
      router.refresh();
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="flex flex-col items-end gap-1">
      <input ref={input} type="file" className="sr-only" accept={accept} onChange={onChange} aria-label={t("chooseFile")} tabIndex={-1} />
      <Button type="button" disabled={busy} onClick={() => input.current?.click()}>
        <Upload className="size-4" />
        {busy ? t("uploading") : t("upload")}
      </Button>
      {message && (
        <p role={message.ok ? "status" : "alert"} className={message.ok ? "text-xs text-success" : "text-xs text-destructive"}>
          {message.text}
        </p>
      )}
    </div>
  );
}

/** Opens the file through a signed URL valid 5 minutes, requested at the click (links never outlive the page). */
export function DownloadButton({ id }: { id: string }) {
  const t = useTranslations("files");
  const errorText = useErrorText();
  const [error, setError] = useState<string | null>(null);

  async function onClick() {
    setError(null);
    // Opened during the click (popup blockers allow it), pointed at the file once the URL is known.
    const tab = window.open("", "_blank");
    if (tab) tab.opener = null;
    const response = await fetch(`/ops/api/relay/content/files/${id}/download`);
    const body = (await response.json().catch(() => ({}))) as { url?: string } & ApiError;
    if (!response.ok || !body.url) {
      tab?.close();
      setError(errorText(body));
      return;
    }
    if (tab) tab.location.href = body.url;
    else window.location.assign(body.url);
  }

  return (
    <span className="inline-flex flex-col gap-1">
      <Button type="button" size="sm" variant="outline" onClick={() => void onClick()}>
        <Download className="size-4" />
        {t("download")}
      </Button>
      {error && <span role="alert" className="text-xs text-destructive">{error}</span>}
    </span>
  );
}
