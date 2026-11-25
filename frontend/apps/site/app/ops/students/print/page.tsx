import { studentFilters } from "../../filters";
import type { SearchParams } from "../../lib";
import { PrintList } from "../../print-list";

export default function PrintStudentsPage({ searchParams }: { searchParams: SearchParams }) {
  return <PrintList kind="students" filters={studentFilters} searchParams={searchParams} />;
}
