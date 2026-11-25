"use client";

import { useState, useSyncExternalStore } from "react";
import Link from "next/link";
import { ArrowRight, BookOpen, CheckCircle2, Circle, Lightbulb, X } from "lucide-react";
import { cn } from "@konnyaku/ui/lib/utils";

export type OnboardingStep = {
  key: string;
  title: string;
  description: string;
  href?: string;
  /** Known from real data (e.g. a branch exists); undefined for steps that only explain the page. */
  done?: boolean;
};

export type OnboardingLabels = { show: string; hide: string; progress?: string };

/** Link to the full user guide, opened in a new tab. */
export type OnboardingGuide = { href: string; label: string };

function subscribe(onChange: () => void) {
  window.addEventListener("storage", onChange);
  return () => window.removeEventListener("storage", onChange);
}

function readHidden(key: string): boolean {
  try {
    return window.localStorage.getItem(key) === "hidden";
  } catch {
    return false;
  }
}

/**
 * First-run guide shown at the top of an area's home page: numbered steps with links, ticked off from real data
 * where possible. Hiding it is remembered per user in this browser; the "show" button brings it back at any time.
 */
export function Onboarding({ storageKey, title, intro, steps, labels, guide }: {
  storageKey: string;
  title: string;
  intro: string;
  steps: OnboardingStep[];
  labels: OnboardingLabels;
  guide?: OnboardingGuide;
}) {
  const key = `kk:onboarding:${storageKey}`;
  // null during server rendering and hydration: the flag lives in this browser's localStorage.
  const stored = useSyncExternalStore(subscribe, () => readHidden(key), () => null);
  // A click wins over storage, so hiding still works where storage is blocked (private mode).
  const [choice, setChoice] = useState<boolean | null>(null);
  const hidden = choice ?? stored;

  function remember(next: boolean) {
    setChoice(next);
    try {
      if (next) window.localStorage.setItem(key, "hidden");
      else window.localStorage.removeItem(key);
    } catch {
      // Not persisted; the choice still applies until the page is reloaded.
    }
  }

  if (hidden === null) return null;

  if (hidden) {
    return (
      <button
        type="button"
        onClick={() => remember(false)}
        className="inline-flex items-center gap-1.5 rounded-md border bg-background px-3 py-1.5 text-sm text-muted-foreground shadow-xs transition-colors hover:text-foreground"
      >
        <Lightbulb className="size-4 text-primary" />
        {labels.show}
      </button>
    );
  }

  return (
    <section aria-label={title} className="rounded-xl border border-primary/25 bg-primary/5 p-5">
      <div className="flex items-start gap-3">
        <span className="grid size-9 shrink-0 place-items-center rounded-lg bg-primary/10 text-primary">
          <Lightbulb className="size-5" />
        </span>
        <div className="min-w-0 flex-1">
          <h2 className="font-semibold">{title}</h2>
          <p className="mt-1 text-sm text-muted-foreground">{intro}</p>
          {labels.progress && <p className="mt-1 text-xs font-medium text-primary">{labels.progress}</p>}
          {guide && (
            <a
              href={guide.href}
              target="_blank"
              rel="noopener noreferrer"
              className="mt-2 inline-flex items-center gap-1.5 text-sm font-medium text-primary underline-offset-4 hover:underline"
            >
              <BookOpen className="size-4" />
              {guide.label}
            </a>
          )}
        </div>
        <button
          type="button"
          onClick={() => remember(true)}
          aria-label={labels.hide}
          title={labels.hide}
          className="grid size-8 shrink-0 place-items-center rounded-md text-muted-foreground hover:bg-accent hover:text-foreground"
        >
          <X className="size-4" />
        </button>
      </div>
      <ol className="mt-4 grid gap-2 sm:grid-cols-2">
        {steps.map((step, index) => {
          const body = (
            <>
              <span className="mt-0.5 shrink-0">
                {step.done === true ? (
                  <CheckCircle2 className="size-5 text-success" />
                ) : step.done === false ? (
                  <Circle className="size-5 text-muted-foreground" />
                ) : (
                  <span className="grid size-5 place-items-center rounded-full bg-primary/10 text-xs font-semibold text-primary">
                    {index + 1}
                  </span>
                )}
              </span>
              <span className="min-w-0 flex-1">
                <span className={cn("flex items-center gap-1 font-medium", step.done && "text-muted-foreground")}>
                  {step.title}
                  {step.href && <ArrowRight className="size-3.5 opacity-60" />}
                </span>
                <span className="mt-0.5 block text-sm text-muted-foreground">{step.description}</span>
              </span>
            </>
          );
          const classes = "flex gap-3 rounded-lg border bg-background p-3 text-left";
          return (
            <li key={step.key}>
              {step.href ? (
                <Link href={step.href} className={cn(classes, "transition-colors hover:border-primary/40")}>
                  {body}
                </Link>
              ) : (
                <div className={classes}>{body}</div>
              )}
            </li>
          );
        })}
      </ol>
    </section>
  );
}
