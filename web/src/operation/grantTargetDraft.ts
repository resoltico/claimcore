import { submissionSelection } from "@contracts/client";
import { submissionScopeSelection } from "./submissionAssembly";
import {
  formValueText,
  type FormValue,
  type FormValues,
  type GrantTargetDraft,
} from "../protocol/forms";
export const isGrantTarget = (value: FormValue | undefined): value is GrantTargetDraft =>
  typeof value === "object" && value !== null && "kind" in value && value.kind === "GRANT_TARGET";
/** Core seeds are scalar inputs. Once authored, one raw alternative owns the target. */
export const grantTarget = (values: FormValues): GrantTargetDraft => {
  const value = values[submissionSelection.scopeInput];
  if (isGrantTarget(value)) {
    return value;
  }
  const selected = submissionScopeSelection(values);
  if (selected.kind === "INSTALLATION") {
    return { kind: "GRANT_TARGET", selection: { kind: "INSTALLATION" } };
  }
  return {
    kind: "GRANT_TARGET",
    selection: {
      kind: selected.kind,
      text: selected.input === null ? "" : formValueText(values[selected.input]),
    },
  };
};
export const grantScope = (values: FormValues) =>
  grantTarget(values).selection.kind === submissionSelection.installationScope
    ? submissionSelection.installationScope
    : submissionSelection.caseScope;
export const grantFields = (values: FormValues): FormValues => {
  const value = values[submissionSelection.scopeInput];
  if (!isGrantTarget(value)) {
    return values;
  }
  const {
    [submissionSelection.scopeInput]: _scope,
    [submissionSelection.caseIdInput]: _id,
    [submissionSelection.referenceInput]: _reference,
    ...rest
  } = values;
  const selected = submissionSelection.rules.find((rule) => rule.kind === value.selection.kind);
  if (selected === undefined) {
    throw new Error("Invalid grant selection.");
  }
  return selected.input === null
    ? { ...rest, [submissionSelection.scopeInput]: submissionSelection.installationScope }
    : {
        ...rest,
        [submissionSelection.scopeInput]: submissionSelection.caseScope,
        [selected.input]: value.selection.kind === "INSTALLATION" ? "" : value.selection.text,
      };
};
export const withGrantTarget = (values: FormValues, value: GrantTargetDraft): FormValues => {
  const {
    [submissionSelection.caseIdInput]: _id,
    [submissionSelection.referenceInput]: _reference,
    ...rest
  } = values;
  return { ...rest, [submissionSelection.scopeInput]: value };
};
