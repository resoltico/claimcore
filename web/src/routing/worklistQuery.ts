import {
  caseSelectionDateFields,
  caseSelectionDateRanges,
  caseSelectionMember,
  caseSelectionTextConstraints,
  isCaseListSelection,
  isResponseStructure,
  type CaseListRequest,
  type CaseListSelection,
  type ResponseSchema,
} from "@contracts/client";

export type WorklistQuery = CaseListSelection;
export const reportDateFields = caseSelectionDateFields;

type SelectionFragment = {
  position: number;
  raw: string;
  key: string | undefined;
  value: string | undefined;
};
export type SelectionIssue = {
  fragments: ReadonlyArray<SelectionFragment>;
  key?: string;
  upper?: (typeof reportDateFields)[number];
};
export type WorklistAdmission =
  | { kind: "valid"; selection: WorklistQuery }
  | { kind: "invalid"; fragments: ReadonlyArray<SelectionFragment>; issues: SelectionIssue[] };

const source = (query: string) => query.replace(/^\?/u, "");
const decodePart = (value: string) => {
  try {
    return decodeURIComponent(value.replace(/\+/gu, " "));
  } catch {
    return undefined;
  }
};
const parseFragment = (raw: string, position: number): SelectionFragment => {
  const separator = raw.indexOf("=");
  const key = decodePart(separator < 0 ? raw : raw.slice(0, separator));
  const value = decodePart(separator < 0 ? "" : raw.slice(separator + 1));
  return { raw, key, value, position };
};

const validText = (value: string) => {
  const constraints = caseSelectionTextConstraints;
  const scalars = Array.from(value);
  return (
    scalars.length <= constraints.maximumCharacters &&
    new TextEncoder().encode(value).length <= constraints.maximumBytes &&
    !/^\p{White_Space}|\p{White_Space}$|\p{Cc}/u.test(value) &&
    !scalars.some((character) => {
      const point = character.codePointAt(0) ?? 0;
      return constraints.formattingRanges.some(([first, last]) => point >= first && point <= last);
    })
  );
};

const groupFragments = (fragments: ReadonlyArray<SelectionFragment>, issues: SelectionIssue[]) => {
  const groups = new Map<string, SelectionFragment[]>();
  for (const fragment of fragments) {
    if (fragment.key === undefined) {
      issues.push({ fragments: [fragment] });
    } else {
      const group = groups.get(fragment.key) ?? [];
      group.push(fragment);
      groups.set(fragment.key, group);
    }
  }
  return groups;
};

const memberValue = (shape: ResponseSchema, group: ReadonlyArray<SelectionFragment>) => {
  const values = group.map((fragment) => fragment.value);
  if (!values.every((value) => value !== undefined)) {
    return undefined;
  }
  if (shape[0] === "array") {
    return values;
  }
  return values.length === 1 ? values[0] : undefined;
};

const allowsSearchText = (member: string, value: string | string[]) =>
  member !== "text" || value === "" || (typeof value === "string" && validText(value));

const admitMembers = (
  groups: ReadonlyMap<string, SelectionFragment[]>,
  issues: SelectionIssue[],
) => {
  const fields: [string, string | string[]][] = [];
  for (const [key, group] of groups) {
    const member = key === "q" ? "text" : key;
    const shape = key === "text" ? undefined : caseSelectionMember(member);
    if (shape === undefined) {
      issues.push({ fragments: group });
      continue;
    }
    const value = memberValue(shape, group);
    if (
      value === undefined ||
      !isResponseStructure(shape, value) ||
      !allowsSearchText(member, value)
    ) {
      issues.push({ fragments: group, key });
    } else if (key !== "q" || value !== "") {
      fields.push([member, value]);
    }
  }

  const selection: unknown = Object.fromEntries(fields);
  if (!isCaseListSelection(selection)) {
    throw new Error("Cases URL members must form the declared selection.");
  }
  return selection;
};

const checkRanges = (
  selection: WorklistQuery,
  groups: ReadonlyMap<string, SelectionFragment[]>,
  issues: SelectionIssue[],
) => {
  for (const [lower, upper] of caseSelectionDateRanges) {
    const first = selection[lower];
    const last = selection[upper];
    const group = groups.get(upper);
    if (first !== undefined && last !== undefined && first > last && group !== undefined) {
      issues.push({ fragments: group, key: upper, upper });
    }
  }
};

const normalize = (selection: WorklistQuery): WorklistQuery => ({
  ...selection,
  ...(selection.lifecycle === undefined ? {} : { lifecycle: [...new Set(selection.lifecycle)] }),
  ...(selection.status === undefined ? {} : { status: [...new Set(selection.status)] }),
  ...(selection.progress === undefined ? {} : { progress: [...new Set(selection.progress)] }),
});

export const parseWorklistQuery = (query: string): WorklistAdmission => {
  const raw = source(query);
  const fragments = raw === "" ? [] : raw.split("&").map(parseFragment);
  const issues: SelectionIssue[] = [];
  const groups = groupFragments(fragments, issues);
  const selection = admitMembers(groups, issues);
  checkRanges(selection, groups, issues);
  return issues.length === 0
    ? { kind: "valid", selection: normalize(selection) }
    : { kind: "invalid", fragments, issues };
};

export const repairWorklistQuery = (
  fragments: ReadonlyArray<SelectionFragment>,
  issue: SelectionIssue,
  values: ReadonlyArray<string>,
): string => {
  const remaining = fragments
    .filter((fragment) => !issue.fragments.includes(fragment))
    .map((fragment) => fragment.raw);
  const { key } = issue;
  if (key !== undefined && values.length > 0) {
    const replacement = new URLSearchParams();
    for (const value of values) {
      replacement.append(key, value);
    }
    remaining.push(replacement.toString());
  }
  return remaining.length === 0 ? "/cases" : `/cases?${remaining.join("&")}`;
};

export const worklistPath = (query: WorklistQuery): string => {
  const params = new URLSearchParams();
  if (query.view !== undefined) {
    params.set("view", query.view);
  }
  for (const name of ["status", "progress", "lifecycle"] as const) {
    for (const value of query[name] ?? []) {
      params.append(name, value);
    }
  }
  if (query.text !== undefined && query.text !== "") {
    params.set("q", query.text);
  }
  const scalarFields = [
    "order",
    "assignment",
    "responsibleActor",
    "due",
    ...reportDateFields,
  ] as const;
  for (const field of scalarFields) {
    const value = query[field];
    if (value !== undefined) {
      params.set(field, value);
    }
  }
  const suffix = params.toString();
  return suffix === "" ? "/cases" : `/cases?${suffix}`;
};
export const listRequest = (query: WorklistQuery, cursor: string | null): CaseListRequest => ({
  ...query,
  limit: 50,
  ...(cursor === null ? {} : { cursor }),
});
