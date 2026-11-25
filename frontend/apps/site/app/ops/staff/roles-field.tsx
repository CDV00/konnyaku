"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import { CheckRow } from "@konnyaku/ui/kk/field";
import { branchScopedRoles as branchScoped, staffRoles, type RoleGrant } from "../roles";

/**
 * Picks a member's roles; a branch manager or coordinator covers the whole center or only the ticked branches.
 * Writes `[{ role, branchId }]` into a hidden JSON field named `roles` for ActionForm.
 */
export function RolesField({ initial, branches }: { initial: RoleGrant[]; branches: { id: string; name: string }[] }) {
  const t = useTranslations("admin");
  const [roles, setRoles] = useState<Set<string>>(() => new Set(initial.map((g) => g.role)));
  const [scoped, setScoped] = useState<Record<string, string[]>>(() => {
    const result: Record<string, string[]> = {};
    for (const grant of initial) if (grant.branchId) (result[grant.role] ??= []).push(grant.branchId);
    return result;
  });

  const grants: RoleGrant[] = [...roles].flatMap((role): RoleGrant[] => {
    const branchIds = branchScoped.has(role) ? (scoped[role] ?? []) : [];
    return branchIds.length > 0 ? branchIds.map((branchId) => ({ role, branchId })) : [{ role, branchId: null }];
  });

  function toggleRole(role: string, on: boolean) {
    setRoles((current) => {
      const next = new Set(current);
      if (on) next.add(role);
      else next.delete(role);
      return next;
    });
  }

  function toggleBranch(role: string, branchId: string, on: boolean) {
    setScoped((current) => {
      const list = new Set(current[role] ?? []);
      if (on) list.add(branchId);
      else list.delete(branchId);
      return { ...current, [role]: [...list] };
    });
  }

  return (
    <fieldset className="grid gap-2 rounded-lg border p-3">
      <legend className="px-1 text-sm font-medium">{t("staff.roles")}</legend>
      <input type="hidden" name="roles" data-kind="json" value={JSON.stringify(grants)} />
      {staffRoles.map((role) => (
        <div key={role} className="grid gap-2">
          <CheckRow checked={roles.has(role)} onChange={(e) => toggleRole(role, e.currentTarget.checked)}>
            {t(`roles.${role}`)}
          </CheckRow>
          {branchScoped.has(role) && roles.has(role) && branches.length > 0 && (
            <div className="ml-6 grid gap-1.5 rounded-md bg-muted/50 p-2">
              <span className="text-xs text-muted-foreground">{t("staff.branchScopeHint")}</span>
              {branches.map((b) => (
                <CheckRow
                  key={b.id}
                  checked={(scoped[role] ?? []).includes(b.id)}
                  onChange={(e) => toggleBranch(role, b.id, e.currentTarget.checked)}
                >
                  {b.name}
                </CheckRow>
              ))}
            </div>
          )}
        </div>
      ))}
    </fieldset>
  );
}
