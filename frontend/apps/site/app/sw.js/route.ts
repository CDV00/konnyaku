/**
 * Service worker of the site, for Web Push only (no offline cache: the app is not a PWA yet). Served at /sw.js so its
 * scope is the whole site. Pushes carry no content: on a push it asks /api/inbox/push-preview (signed-in cookies) what
 * to show, and a click opens the page of the notification.
 */
const script = `
self.addEventListener("install", () => self.skipWaiting());
self.addEventListener("activate", (event) => event.waitUntil(self.clients.claim()));

self.addEventListener("push", (event) => {
  event.waitUntil((async () => {
    let notice = null;
    try {
      const subscription = await self.registration.pushManager.getSubscription();
      const response = await fetch("/api/inbox/push-preview", {
        method: "POST",
        credentials: "same-origin",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ endpoint: subscription ? subscription.endpoint : null }),
      });
      if (response.ok) notice = await response.json();
    } catch (error) {
      notice = null;
    }
    const title = (notice && notice.title) || "Konnyaku";
    await self.registration.showNotification(title, {
      body: (notice && notice.body) || "",
      tag: (notice && notice.id) || "konnyaku",
      data: { link: (notice && notice.link) || "/" },
    });
  })());
});

self.addEventListener("notificationclick", (event) => {
  event.notification.close();
  const link = new URL((event.notification.data && event.notification.data.link) || "/", self.location.origin).href;
  event.waitUntil((async () => {
    const windows = await self.clients.matchAll({ type: "window", includeUncontrolled: true });
    for (const client of windows) {
      if (client.url === link && "focus" in client) return client.focus();
    }
    return self.clients.openWindow(link);
  })());
});
`;

export function GET() {
  return new Response(script, {
    headers: {
      "Content-Type": "text/javascript; charset=utf-8",
      // Always check for a new version; the browser compares bytes anyway.
      "Cache-Control": "no-cache",
      "Service-Worker-Allowed": "/",
    },
  });
}
