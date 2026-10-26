import type { CaseFields } from "@contracts/client";

export const caseFieldValue = (fields: CaseFields, name: string): string | null => {
  for (const group of [fields, fields.registration, fields.decision, fields.payment]) {
    if (group !== null) {
      const value: unknown = Object.entries(group).find(([key]) => key === name)?.[1];
      if (typeof value === "string") {
        return value;
      }
    }
  }
  return null;
};
