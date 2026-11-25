"use client";

import { ErrorScreen } from "../zone-error";

export default function ZoneError({ error, retry }: { error: Error & { digest?: string }; retry: () => void }) {
  return <ErrorScreen error={error} retry={retry} home="/ops" homeLabel="zoneHome" />;
}
