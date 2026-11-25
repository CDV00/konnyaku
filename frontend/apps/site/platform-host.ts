/**
 * Host serving the platform console (/platform). It must match the API's Platform:AdminHost
 * (default admin.{BASE_DOMAIN}); production sets KONNYAKU_ADMIN_HOST=admin.<base domain> on the web container.
 */
export const adminHost = (process.env.KONNYAKU_ADMIN_HOST ?? "admin.localhost").toLowerCase();

export function isAdminHost(host: string | null | undefined): boolean {
  return (host ?? "").split(":")[0]?.toLowerCase() === adminHost;
}

/** Time zone of dates in the console (operators' office); the console has no tenant to take it from. */
export const platformTimeZone = process.env.KONNYAKU_PLATFORM_TIME_ZONE ?? "Asia/Ho_Chi_Minh";

/** Base domain of tenant hosts ({slug}.{baseDomain}), for hints in the console. */
export const baseDomain = (process.env.KONNYAKU_BASE_DOMAIN ?? adminHost.split(".").slice(1).join(".")).toLowerCase();

/** Link to a tenant host from the console: same scheme and port as the console itself. */
export function siteUrl(host: string, consoleHost: string | null): string {
  const port = (consoleHost ?? "").split(":")[1];
  const secure = process.env.NODE_ENV === "production";
  return `${secure ? "https" : "http"}://${host}${port && !secure ? `:${port}` : ""}`;
}
