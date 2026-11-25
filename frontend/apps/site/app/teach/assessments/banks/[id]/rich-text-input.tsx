"use client";

import { useRef, useState } from "react";
import { useTranslations } from "next-intl";
import { Languages } from "lucide-react";
import { Button } from "@konnyaku/ui/components/animate-ui/components/buttons/button";
import { Input } from "@konnyaku/ui/components/ui/input";
import { Textarea } from "@konnyaku/ui/components/ui/textarea";
import { cn } from "@konnyaku/ui/lib/utils";
import { toHtml, wrapRuby } from "@/assessment/rich-text";

/** Rendered rich text (furigana / pinyin as ruby). Only the author's own text or API-sanitized HTML is passed in. */
export function RichPreview({ html, className }: { html: string; className?: string }) {
  return (
    <div
      className={cn("text-sm leading-loose break-words [&_rt]:text-[0.65em] [&_rt]:text-muted-foreground", className)}
      dangerouslySetInnerHTML={{ __html: html }}
    />
  );
}

/**
 * Textarea in the light syntax (`[漢字|かんじ]`, `**bold**`, `_italic_`) with a furigana / pinyin helper and a live preview.
 * The label is rendered by the caller's layout (a plain heading, not a <label>, so the helper input stays separate).
 */
export function RichTextInput({ label, value, onChange, rows = 3, hint, disabled, compact }: {
  label: string;
  value: string;
  onChange: (value: string) => void;
  rows?: number;
  hint?: string;
  disabled?: boolean;
  compact?: boolean;
}) {
  const t = useTranslations("assessmentAuthoring.richText");
  const area = useRef<HTMLTextAreaElement>(null);
  const [reading, setReading] = useState("");
  const [helper, setHelper] = useState(false);

  function addRuby() {
    const el = area.current;
    if (!el || !reading.trim()) return;
    const wrapped = wrapRuby(value, el.selectionStart, el.selectionEnd, reading.trim());
    onChange(wrapped.value);
    setReading("");
    requestAnimationFrame(() => {
      el.focus();
      el.setSelectionRange(wrapped.caret, wrapped.caret);
    });
  }

  const html = value.trim() ? toHtml(value) : "";

  return (
    <div className="grid gap-1.5 text-sm">
      <div className="flex items-center justify-between gap-2">
        <span className="font-medium text-foreground">{label}</span>
        {!disabled && (
          <Button type="button" variant="ghost" size="sm" className="h-7 px-2 text-xs" onClick={() => setHelper((x) => !x)} aria-expanded={helper}>
            <Languages />
            {t("ruby")}
          </Button>
        )}
      </div>
      <Textarea ref={area} aria-label={label} rows={rows} value={value} disabled={disabled} onChange={(e) => onChange(e.target.value)} className={compact ? "min-h-10" : undefined} />
      {helper && !disabled && (
        <div className="flex flex-wrap items-center gap-2 rounded-md border border-dashed p-2">
          <Input
            aria-label={t("reading")}
            placeholder={t("readingPlaceholder")}
            value={reading}
            onChange={(e) => setReading(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === "Enter") {
                e.preventDefault();
                addRuby();
              }
            }}
            className="h-8 w-40 flex-1"
          />
          <Button type="button" size="sm" variant="secondary" onMouseDown={(e) => e.preventDefault()} onClick={addRuby} disabled={!reading.trim()}>
            {t("apply")}
          </Button>
          <p className="w-full text-xs text-muted-foreground">{t("rubyHint")}</p>
        </div>
      )}
      {hint && <span className="text-xs text-muted-foreground">{hint}</span>}
      {html && (
        <div className="rounded-md bg-muted/50 px-3 py-1.5">
          <span className="sr-only">{t("preview")}</span>
          <RichPreview html={html} />
        </div>
      )}
    </div>
  );
}
