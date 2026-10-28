import type { InputIssue } from "@contracts/client";
import type { FormValues } from "../protocol/forms";
import type { Notice } from "../protocol/notices";
import { refusalPath } from "./operationForm";

export type FieldErrors = Readonly<Record<string, Notice>>;

export const inputErrors = (
  notice: Notice,
  target: string | null,
  values: FormValues,
): FieldErrors => {
  if (
    notice.kind === "diagnostic" &&
    notice.diagnostic.code === "INVALID_INPUT" &&
    "issues" in notice.diagnostic
  ) {
    const grouped = new Map<string, InputIssue[]>();
    for (const issue of notice.diagnostic.issues) {
      const path = refusalPath(issue.field, values);
      if (path !== null) {
        const previous = grouped.get(path) ?? [];
        grouped.set(path, [...previous, issue]);
      }
    }
    return Object.fromEntries(
      [...grouped].map(([path, issues]) => [path, { kind: "issues" as const, issues }]),
    );
  }
  let path: string | null = null;
  if (target !== null) {
    path = Object.hasOwn(values, target) ? target : refusalPath(target, values);
  }
  return path === null ? {} : { [path]: notice };
};
