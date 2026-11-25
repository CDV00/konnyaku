/**
 * Full user guide (docs/huong-dan in the repository), linked from the quick guide cards. A deployment can point it at
 * its own copy with KONNYAKU_GUIDE_URL (the folder holding the pages, without a trailing slash).
 */
const guideBase = (process.env.KONNYAKU_GUIDE_URL ?? "https://github.com/CDV00/konnyaku/blob/main/docs/huong-dan").replace(/\/+$/, "");

/** Pages of the guide, one per audience. */
export type GuidePage = "README" | "bat-dau-nhanh" | "giao-vien" | "hoc-vien";

export function guideUrl(page: GuidePage): string {
  return `${guideBase}/${page}.md`;
}
