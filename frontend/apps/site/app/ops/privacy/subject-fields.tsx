"use client";

import { useState } from "react";
import { useTranslations } from "next-intl";
import { Field, NativeSelect } from "@konnyaku/ui/kk/field";
import { RecordPicker } from "../personal-data";

/** Who the request is about: a student or a lead, picked by searching (accents ignored). */
export function SubjectFields() {
  const t = useTranslations("admin.privacy");
  const [type, setType] = useState<"student" | "lead">("student");
  return (
    <>
      <Field label={t("subjectType")}>
        <NativeSelect name="subjectType" value={type} onChange={(e) => setType(e.target.value as "student" | "lead")}>
          <option value="student">{t("subjects.student")}</option>
          <option value="lead">{t("subjects.lead")}</option>
        </NativeSelect>
      </Field>
      <Field label={t("subject")}>
        <RecordPicker
          key={type}
          name="subjectId"
          source={type === "student" ? "crm/students" : "crm/leads"}
          filter={type === "lead" ? "status=all" : ""}
          placeholder={t("findSubject")}
          required
        />
      </Field>
    </>
  );
}
