import type { ReminderListRequest } from "@contracts/client";
import { isTerm } from "../presentation/terms";
export type ReminderQuery = Omit<ReminderListRequest, "target" | "cursor" | "limit">;
export const parseReminderQuery = (search: string): ReminderQuery => {
  const params = new URLSearchParams(search);
  const view = params.get("view");
  const state = params.get("state");
  const due = params.get("due");
  const assignment = params.get("assignment");
  const text = params.get("q");
  return {
    ...(view !== null && isTerm("reminderView", view) ? { view } : {}),
    ...(state !== null && isTerm("reminderState", state) ? { state } : {}),
    ...(due !== null && isTerm("reminderDue", due) ? { due } : {}),
    ...(assignment !== null && isTerm("assignment", assignment) ? { assignment } : {}),
    ...(text === null || text === "" ? {} : { text }),
    ...(params.get("includeInactive") === "true" ? { includeInactive: true } : {}),
  };
};
export const remindersPath = (query: ReminderQuery): string => {
  const params = new URLSearchParams();
  for (const name of ["view", "state", "due", "assignment"] as const) {
    if (query[name] !== undefined) {
      params.set(name, query[name]);
    }
  }
  if (query.text !== undefined && query.text !== "") {
    params.set("q", query.text);
  }
  if (query.includeInactive === true) {
    params.set("includeInactive", "true");
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
