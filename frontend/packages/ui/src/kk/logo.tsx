import { cn } from "@konnyaku/ui/lib/utils";

/** Konnyaku mark: a soft jelly cube (the namesake of the brand) with a coral dot; readable on light and dark. */
export function LogoMark({ className }: { className?: string }) {
  return (
    <svg viewBox="0 0 32 32" aria-hidden="true" className={cn("size-8 shrink-0", className)}>
      <defs>
        <linearGradient id="kk-mark" x1="0" y1="0" x2="1" y2="1">
          <stop offset="0" stopColor="oklch(0.62 0.2 285)" />
          <stop offset="1" stopColor="oklch(0.48 0.22 270)" />
        </linearGradient>
      </defs>
      <rect x="3" y="5" width="26" height="23" rx="9" fill="url(#kk-mark)" />
      <path d="M9 11.5c2-2.5 5.5-3 8-2" stroke="white" strokeOpacity=".7" strokeWidth="2.2" strokeLinecap="round" fill="none" />
      <circle cx="12" cy="18" r="1.6" fill="white" />
      <circle cx="20" cy="18" r="1.6" fill="white" />
      <path d="M14 22c1.2 1 2.8 1 4 0" stroke="white" strokeWidth="1.6" strokeLinecap="round" fill="none" />
      <circle cx="25" cy="7" r="3.2" fill="oklch(0.74 0.16 38)" />
    </svg>
  );
}

export function Logo({ name, suffix, className }: { name: string; suffix?: string; className?: string }) {
  return (
    <span className={cn("inline-flex min-w-0 items-center gap-2 font-semibold tracking-tight", className)}>
      <LogoMark />
      <span className="flex min-w-0 flex-col gap-1">
        <span className="truncate text-base leading-none">{name}</span>
        {suffix && <span className="text-xs leading-none font-medium text-muted-foreground">{suffix}</span>}
      </span>
    </span>
  );
}
