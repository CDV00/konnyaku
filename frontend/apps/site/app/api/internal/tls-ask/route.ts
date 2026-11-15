import { apiUrl } from "@konnyaku/session";

const hostname = /^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$/i;

/**
 * Caddy on-demand TLS asks here before requesting a certificate for a host (infra/deploy/Caddyfile):
 * 200 only for hosts that belong to a tenant, so nobody can make the server issue certificates for random names.
 * Caddy blocks /api/internal/* from the internet; it calls this route on the internal network.
 */
export async function GET(request: Request) {
  const domain = new URL(request.url).searchParams.get("domain") ?? "";
  if (!hostname.test(domain)) return new Response(null, { status: 400 });
  const response = await fetch(`${apiUrl}/api/platform/tenant`, {
    headers: { "X-Tenant-Host": domain.toLowerCase() },
    cache: "no-store",
  });
  return new Response(null, { status: response.ok ? 200 : 404 });
}
