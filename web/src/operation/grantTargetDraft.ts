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
  const value = values["input:scope"];
  if (isGrantTarget(value)) {
    return value;
  }
  if (value !== undefined && value !== "INSTALLATION" && value !== "CASE") {
    throw new Error("Invalid grant scope.");
  }
  if (
    value === undefined &&
    (Object.hasOwn(values, "input:caseId") || Object.hasOwn(values, "field:caseReference"))
  ) {
    return {
      kind: "GRANT_TARGET",
      selection: Object.hasOwn(values, "input:caseId")
        ? { kind: "ID", text: formValueText(values["input:caseId"]) }
        : { kind: "REFERENCE", text: formValueText(values["field:caseReference"]) },
    };
  }
  if (value !== "CASE") {
    return { kind: "GRANT_TARGET", selection: { kind: "INSTALLATION" } };
  }
  return {
    kind: "GRANT_TARGET",
    selection: Object.hasOwn(values, "input:caseId")
      ? { kind: "ID", text: formValueText(values["input:caseId"]) }
      : { kind: "REFERENCE", text: formValueText(values["field:caseReference"]) },
  };
};
export const grantScope = (values: FormValues) =>
  grantTarget(values).selection.kind === "INSTALLATION" ? "INSTALLATION" : "CASE";
export const grantFields = (values: FormValues): FormValues => {
  const value = values["input:scope"];
  if (!isGrantTarget(value)) {
    return values;
  }
  const {
    "input:scope": _scope,
    "input:caseId": _id,
    "field:caseReference": _reference,
    ...rest
  } = values;
  const target = value.selection;
  return target.kind === "INSTALLATION"
    ? { ...rest, "input:scope": "INSTALLATION" }
    : {
        ...rest,
        "input:scope": "CASE",
        [target.kind === "ID" ? "input:caseId" : "field:caseReference"]: target.text,
      };
};
export const withGrantTarget = (values: FormValues, value: GrantTargetDraft): FormValues => {
  const { "input:caseId": _id, "field:caseReference": _reference, ...rest } = values;
  return { ...rest, "input:scope": value };
};
