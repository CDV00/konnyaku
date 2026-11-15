"use client";

import { useTheme } from "next-themes";
import { Monitor, Moon, Sun } from "lucide-react";
import { ThemeTogglerButton, ThemeTogglerButtonIcon } from "@konnyaku/ui/components/animate-ui/buttons/theme-toggler";

/** Animated light / dark / system switch (cycles through the three; the icon morphs between them). */
export function ThemeToggle({ label }: { label: string }) {
  const { theme, resolvedTheme, setTheme } = useTheme();
  return (
    <ThemeTogglerButton
      aria-label={label}
      title={label}
      variant="ghost"
      size="sm"
      theme={(theme as "light" | "dark" | "system" | undefined) ?? "system"}
      resolvedTheme={resolvedTheme === "dark" ? "dark" : "light"}
      setTheme={setTheme}
    >
      <ThemeTogglerButtonIcon light={<Sun />} dark={<Moon />} system={<Monitor />} />
    </ThemeTogglerButton>
  );
}
