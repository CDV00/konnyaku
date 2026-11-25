import { notFound } from "next/navigation";

/** Unknown public paths render [locale]/not-found inside the site chrome. */
export default function UnknownPath() {
  notFound();
}
