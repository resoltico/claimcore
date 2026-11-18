import { reminderSelectionDefaults } from "@contracts/client";
import type { CaseSelector, ReminderListRequest } from "@contracts/client";
import type { Terms } from "@contracts/messages";
import { isTerm } from "../presentation/terms";
export type ReminderQuery = Omit<ReminderListRequest, "cursor" | "limit">;
const invalidSelection = (): never => {
  throw new Error("Invalid reminder selection.");
};
const selectionParameters = (search: string) => {
  const params = new URLSearchParams(search);
  const allowed = [
    "view",
    "state",
    "kind",
    "deadlineOutcome",
    "due",
    "assignment",
    "q",
    "includeInactive",
    "caseId",
    "caseReference",
    "filters",
    "cursor",
    "limit",
  ];
  for (const key of params.keys()) {
    if (!allowed.includes(key) || params.getAll(key).length !== 1) {
      invalidSelection();
    }
  }
  const filters = params.get("filters");
  if (filters !== null && filters !== "manual") {
    invalidSelection();
  }
  const inactive = params.get("includeInactive");
  if (inactive !== null && !["true", "false"].includes(inactive)) {
    invalidSelection();
  }
  return params;
};
const caseSelection = (params: URLSearchParams): CaseSelector | undefined => {
  const caseId = params.get("caseId");
  const caseReference = params.get("caseReference");
  if (caseId !== null) {
    if (
      caseReference !== null ||
      !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/u.test(caseId) ||
      caseId === "00000000-0000-0000-0000-000000000000"
    ) {
      invalidSelection();
    }
    return { kind: "ID", caseId };
  }
  if (caseReference === null) {
    return undefined;
  }
  if (caseReference.length === 0) {
    invalidSelection();
  }
  return { kind: "REFERENCE", caseReference };
};
const choice = <V extends keyof Terms>(
  params: URLSearchParams,
  key: string,
  vocabulary: V,
): Terms[V] | undefined => {
  const value = params.get(key);
  if (value === null) {
    return undefined;
  }
  return isTerm(vocabulary, value) ? value : invalidSelection();
};

export const parseReminderQuery = (search: string): ReminderQuery => {
  const params = selectionParameters(search);
  const target = caseSelection(params);
  const view = choice(params, "view", "reminderView");
  const state = choice(params, "state", "reminderStateSelection");
  const kind = choice(params, "kind", "reminderKindSelection");
  const deadlineOutcome = choice(params, "deadlineOutcome", "deadlineOutcome");
  const due = choice(params, "due", "reminderDue");
  const assignment = choice(params, "assignment", "assignment");
  const text = params.get("q");
  return {
    ...(target === undefined ? {} : { target }),
    ...(view === undefined ? {} : { view }),
    ...(state === undefined ? {} : { state }),
    ...(kind === undefined ? {} : { kind }),
    ...(deadlineOutcome === undefined ? {} : { deadlineOutcome }),
    ...(due === undefined ? {} : { due }),
    ...(assignment === undefined ? {} : { assignment }),
    ...(text === null || text === "" ? {} : { text }),
    ...(params.has("includeInactive")
      ? { includeInactive: params.get("includeInactive") === "true" }
      : {}),
  };
};

export const remindersPath = (query: ReminderQuery): string => {
  const params = new URLSearchParams();
  for (const name of ["view", "state", "kind", "deadlineOutcome", "due", "assignment"] as const) {
    if (query[name] !== undefined) {
      params.set(name, query[name]);
    }
  }
  if (query.text !== undefined && query.text !== "") {
    params.set("q", query.text);
  }
  if (query.includeInactive !== undefined) {
    params.set("includeInactive", String(query.includeInactive));
  }
  if (query.target !== undefined) {
    params.set(
      query.target.kind === "ID" ? "caseId" : "caseReference",
      query.target.kind === "ID" ? query.target.caseId : query.target.caseReference,
    );
  }
  const suffix = params.toString();
  return suffix === "" ? "/reminders" : `/reminders?${suffix}`;
};

/** Manual filtering is interface navigation state, never a Core reminder view or request property. */
export const parseReminderPageQuery = (
  search: string,
): { manual: boolean; query: ReminderQuery } => {
  const manual = new URLSearchParams(search).get("filters") === "manual";
  const parsed = parseReminderQuery(search);
  const { view: _view, ...filters } = parsed;
  return { manual, query: manual ? filters : parsed };
};
export const reminderPagePath = (query: ReminderQuery, manual: boolean): string => {
  if (!manual) {
    return remindersPath(query);
  }
  const { view: _view, ...filters } = query;
  const path = remindersPath(filters);
  return `${path}${path.includes("?") ? "&" : "?"}filters=manual`;
};

export const effectiveReminderSelection = (query: ReminderQuery) => {
  const defaults = reminderSelectionDefaults.find(
    (row) => row.hasView === (query.view !== undefined || query.deadlineOutcome !== undefined),
  );
  if (defaults === undefined) {
    throw new Error("The Core reminder defaults are missing.");
  }
  return {
    state: query.state ?? defaults.state,
    includeInactive: query.includeInactive ?? defaults.includeInactive,
  };
};
