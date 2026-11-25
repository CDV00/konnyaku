"use client";

import { useLocale } from "next-intl";
import { ErrorScreen } from "../zone-error";

export default function PublicError({ error, retry }: { error: Error & { digest?: string }; retry: () => void }) {
  const locale = useLocale();
  return (
    <main>
      <ErrorScreen error={error} retry={retry} home={`/${locale}`} homeLabel="home" />
    </main>
  );
}
