/** URL filters of each list, forwarded to the API (and, for students and leads, to export and print). */
export const studentFilters = ["q", "status", "branch", "hasAccount", "createdFrom", "createdTo"] as const;
export const leadFilters = ["q", "status", "language", "branch", "consultant", "createdFrom", "createdTo"] as const;
export const classFilters = ["q", "status", "branch", "language", "mode", "startsFrom", "startsTo"] as const;
export const orderFilters = ["q", "status", "branch", "package", "createdFrom", "createdTo"] as const;
export const staffFilters = ["q", "role", "status", "branch"] as const;
