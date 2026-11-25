import { notFound } from "next/navigation";

/** Unknown paths of the zone render its not-found page inside the zone layout. */
export default function UnknownPath() {
  notFound();
}
