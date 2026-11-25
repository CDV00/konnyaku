import type { ReactNode } from "react";
import { LogoMark } from "@konnyaku/ui/kk/logo";

/** Centered card of the sign-in pages (login, forgot, reset). */
export function LoginCard({ title, subtitle, children, footer }: { title: string; subtitle?: string; children: ReactNode; footer?: ReactNode }) {
  return (
    <main className="relative isolate flex min-h-[calc(100dvh-4rem)] items-center justify-center px-4 py-12">
      <div className="absolute inset-0 -z-10 bg-[radial-gradient(60%_50%_at_50%_0%,color-mix(in_oklch,var(--primary)_14%,transparent),transparent)]" />
      <div className="w-full max-w-md space-y-6">
        <div className="flex flex-col items-center gap-3 text-center">
          <LogoMark className="size-12" />
          <div className="space-y-1">
            <h1 className="text-2xl font-semibold tracking-tight">{title}</h1>
            {subtitle && <p className="text-sm text-muted-foreground">{subtitle}</p>}
          </div>
        </div>
        <div className="rounded-2xl border bg-card p-6 shadow-sm">{children}</div>
        {footer && <div className="text-center text-sm">{footer}</div>}
      </div>
    </main>
  );
}
