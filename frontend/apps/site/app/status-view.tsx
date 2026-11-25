import type { ReactNode } from "react";
import type { LucideIcon } from "lucide-react";

export const primaryAction =
  "inline-flex h-10 items-center gap-2 rounded-lg bg-primary px-4 text-sm font-medium text-primary-foreground shadow-xs transition-colors hover:bg-primary/90";
export const secondaryAction =
  "inline-flex h-10 items-center gap-2 rounded-lg border bg-card px-4 text-sm font-medium transition-colors hover:bg-accent";

/** Centered page for 404, errors and "center not found": icon, status code, title, explanation, way back. */
export function StatusView({ icon: Icon, code, title, body, footnote, children }: {
  icon: LucideIcon;
  code?: string;
  title: string;
  body: string;
  footnote?: string;
  children?: ReactNode;
}) {
  return (
    <div className="mx-auto flex min-h-[60dvh] max-w-xl flex-col items-center justify-center gap-4 px-4 py-16 text-center">
      <span className="grid size-16 place-items-center rounded-2xl bg-primary/10 text-primary">
        <Icon className="size-8" aria-hidden />
      </span>
      {code && <p className="text-sm font-semibold tracking-[0.3em] text-muted-foreground">{code}</p>}
      <h1 className="text-2xl font-bold tracking-tight text-balance sm:text-3xl">{title}</h1>
      <p className="text-pretty text-muted-foreground">{body}</p>
      {children && <div className="mt-2 flex flex-wrap justify-center gap-3">{children}</div>}
      {footnote && <p className="font-mono text-xs text-muted-foreground">{footnote}</p>}
    </div>
  );
}
