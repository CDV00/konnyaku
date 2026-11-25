/**
 * Rich text of questions is stored as sanitized HTML (the API allow-lists tags). Authors type a light syntax instead:
 *
 * - `[漢字|かんじ]` → `<ruby>漢字<rt>かんじ</rt></ruby>` (furigana, or pinyin: `[你好|nǐ hǎo]`);
 * - `**bold**`, `_italic_`;
 * - new lines → `<br>`.
 *
 * Blanks of a fill-in-the-blank question stay as `[[1]]`, `[[2]]` (no `|`, so they never look like ruby).
 */

const escape = (text: string) => text.replaceAll("&", "&amp;").replaceAll("<", "&lt;").replaceAll(">", "&gt;").replaceAll('"', "&quot;");

const unescape = (text: string) =>
  text.replaceAll("&lt;", "<").replaceAll("&gt;", ">").replaceAll("&quot;", '"').replaceAll("&#39;", "'").replaceAll("&nbsp;", " ").replaceAll("&amp;", "&");

export function toHtml(source: string): string {
  return escape(source.trim())
    .replace(/\[([^[\]|\n]+)\|([^[\]\n]+)\]/g, "<ruby>$1<rt>$2</rt></ruby>")
    .replace(/\*\*([^*\n]+)\*\*/g, "<strong>$1</strong>")
    .replace(/(^|[\s(])_([^_\n]+)_(?=$|[\s.,;:!?)])/g, "$1<em>$2</em>")
    .replace(/\r?\n/g, "<br>");
}

export function fromHtml(html: string | null | undefined): string {
  if (!html) return "";
  return unescape(
    html
      .replace(/<ruby>(.*?)<rt>(.*?)<\/rt><\/ruby>/g, (_, base: string, reading: string) => `[${base.replace(/<rp>.*?<\/rp>/g, "")}|${reading}]`)
      .replace(/<(strong|b)>(.*?)<\/\1>/g, "**$2**")
      .replace(/<(em|i)>(.*?)<\/\1>/g, "_$2_")
      .replace(/<br\s*\/?>/g, "\n")
      .replace(/<\/p>\s*<p>/g, "\n")
      .replace(/<[^>]+>/g, ""),
  );
}

/** Wraps the selected text of a textarea as `[selection|reading]`, keeping the caret after it. */
export function wrapRuby(value: string, start: number, end: number, reading: string): { value: string; caret: number } {
  const base = value.slice(start, end) || "?";
  const inserted = `[${base}|${reading}]`;
  return { value: value.slice(0, start) + inserted + value.slice(end), caret: start + inserted.length };
}

/** Plain text of rich text (lists, previews): ruby readings dropped. */
export function plainText(html: string | null | undefined, max = 140): string {
  const text = unescape((html ?? "").replace(/<rt>.*?<\/rt>/g, "").replace(/<br\s*\/?>/g, " ").replace(/<[^>]+>/g, "")).replace(/\s+/g, " ").trim();
  return text.length > max ? `${text.slice(0, max - 1)}…` : text;
}

/** Splits a stem around its `[[n]]` blanks: text, blank 0, text, blank 1, … */
export function splitBlanks(html: string): { parts: string[]; blanks: number[] } {
  const parts: string[] = [];
  const blanks: number[] = [];
  let last = 0;
  for (const match of html.matchAll(/\[\[(\d{1,2})\]\]/g)) {
    parts.push(html.slice(last, match.index));
    blanks.push(Number(match[1]) - 1);
    last = (match.index ?? 0) + match[0].length;
  }
  parts.push(html.slice(last));
  return { parts, blanks };
}

/** Words, or characters for Japanese / Chinese essays (mirrors the API's count). */
export function countText(text: string | null | undefined, mode: "Words" | "Characters"): number {
  const value = (text ?? "").trim();
  if (!value) return 0;
  if (mode === "Characters") return [...value].filter((ch) => !/[\s\p{P}\p{S}]/u.test(ch)).length;
  return value.split(/\s+/).length;
}
