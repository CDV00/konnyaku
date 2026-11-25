"use client";

import { useEffect } from "react";
import type { Realm } from "./index";

/**
 * Notices, in a tab left open, that the sign-in session ended elsewhere (signed out, password changed, account locked):
 * checks every 30 seconds and whenever the tab becomes visible, then sends the person to the login page. The API caches
 * session checks for up to 30 seconds, so an ended session shows here within about a minute.
 */
export function SessionWatch({ realm, intervalMs = 30_000 }: { realm: Realm; intervalMs?: number }) {
  useEffect(() => {
    let stopped = false;
    async function check() {
      if (stopped || document.visibilityState !== "visible") return;
      try {
        const response = await fetch(`/api/session/${realm}`, { cache: "no-store" });
        if (response.status === 401 && !stopped) {
          stopped = true;
          // Full navigation: the login page has its own root layout.
          const loginPage = new URL(`/login?realm=${realm}&ended=1`, window.location.href);
          window.location.assign(loginPage);
        }
      } catch {
        // Offline or the server restarting: try again at the next tick.
      }
    }
    const timer = window.setInterval(check, intervalMs);
    document.addEventListener("visibilitychange", check);
    return () => {
      stopped = true;
      window.clearInterval(timer);
      document.removeEventListener("visibilitychange", check);
    };
  }, [realm, intervalMs]);
  return null;
}
