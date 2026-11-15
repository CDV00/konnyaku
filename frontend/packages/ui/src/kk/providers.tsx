"use client";

import type { ReactNode } from "react";
import { ThemeProvider } from "next-themes";

/** Client-side providers shared by every app: light/dark theme (class on <html>, follows the OS by default). */
export function AppProviders({ children }: { children: ReactNode }) {
  return (
    <ThemeProvider attribute="class" defaultTheme="system" enableSystem disableTransitionOnChange>
      {children}
    </ThemeProvider>
  );
}
