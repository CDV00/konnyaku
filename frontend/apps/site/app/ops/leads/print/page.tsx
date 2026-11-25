import { leadFilters } from "../../filters";
import type { SearchParams } from "../../lib";
import { PrintList } from "../../print-list";

export default function PrintLeadsPage({ searchParams }: { searchParams: SearchParams }) {
  return <PrintList kind="leads" filters={leadFilters} searchParams={searchParams} />;
}
