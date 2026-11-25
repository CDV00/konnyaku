/** One change log entry as the API returns it (`GET /api/<module>/history/{id}`). */
export type HistoryEntry = {
  id: string;
  entityType: string;
  action: string;
  actorName: string | null;
  occurredAt: string;
  changes: { field: string; old: string | null; new: string | null }[];
};

/** Newest first across several module histories (a class: its own and its schedule's). */
export function mergeHistory(lists: HistoryEntry[][]): HistoryEntry[] {
  return lists.flat().sort((a, b) => b.occurredAt.localeCompare(a.occurredAt));
}
