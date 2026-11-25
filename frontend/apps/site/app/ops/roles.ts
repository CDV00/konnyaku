/** Staff roles in display order; usable from client components (no server-only imports here). */
export const staffRoles = ["TenantAdmin", "BranchManager", "Coordinator", "Consultant", "Accountant", "Teacher"] as const;

/** Roles that may be limited to chosen branches (the API refuses a branch on any other role). */
export const branchScopedRoles: ReadonlySet<string> = new Set(["BranchManager", "Coordinator"]);

export type RoleGrant = { role: string; branchId: string | null };
