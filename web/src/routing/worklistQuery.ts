import type { CaseListRequest } from "@contracts/client";
import { isTerm } from "../presentation/terms";
export const savedViews = [
  "mine",
  "unassigned",
  "due-today",
  "overdue",
  "open-undecided",
  "decided-unpaid",
  "recent",
  "closed",
  "voided",
  "all",
] as const;
export type SavedView = (typeof savedViews)[number];
export type WorklistQuery = Omit<CaseListRequest, "cursor" | "limit"> & {
  readonly view?: SavedView;
};
export const viewQuery = (view: SavedView): WorklistQuery => {
  switch (view) {
    case "mine":
      return { view, assignment: "MINE" };
    case "unassigned":
      return { view, assignment: "UNASSIGNED" };
    case "due-today":
      return { view, due: "TODAY", order: "FOLLOW_UP" };
    case "overdue":
      return { view, due: "OVERDUE", order: "FOLLOW_UP" };
    case "open-undecided":
      return { view, lifecycle: ["ACTIVE"], status: ["OPENED"], progress: ["UNDECIDED"] };
    case "decided-unpaid":
      return { view, lifecycle: ["ACTIVE"], progress: ["DECIDED"] };
    case "recent":
      return { view, order: "RECENT" };
    case "closed":
      return { view, status: ["CLOSED"] };
    case "voided":
      return { view, lifecycle: ["VOIDED"] };
    case "all":
      return { view };
  }
};
const unique = <T>(values: ReadonlyArray<T>): T[] => [...new Set(values)];
const responsibilityQuery = (assignment: string | null): Pick<WorklistQuery, "assignment"> =>
  assignment === "MINE" || assignment === "UNASSIGNED" ? { assignment } : {};
const dueQuery = (due: string | null): Pick<WorklistQuery, "due"> =>
  due !== null && isTerm("due", due) ? { due } : {};
export const parseWorklistQuery = (query: string): WorklistQuery => {
  const params = new URLSearchParams(query);
  const view = savedViews.find((value) => value === params.get("view"));
  const lifecycle = unique(
    params.getAll("lifecycle").filter((value) => isTerm("lifecycle", value)),
  );
  const status = unique(params.getAll("status").filter((value) => isTerm("status", value)));
  const progress = unique(params.getAll("progress").filter((value) => isTerm("progress", value)));
  const text = params.get("q") ?? "";
  const rawOrder = params.get("order");
  const order = rawOrder !== null && isTerm("caseOrder", rawOrder) ? rawOrder : undefined;
  const assignment = params.get("assignment");
  const rawDue = params.get("due");

  return {
    ...(view === undefined ? {} : viewQuery(view)),
    ...(lifecycle.length ? { lifecycle } : {}),
    ...(status.length ? { status } : {}),
    ...(progress.length ? { progress } : {}),
    ...(text === "" ? {} : { text }),
    ...(order === undefined ? {} : { order }),
    ...responsibilityQuery(assignment),
    ...dueQuery(rawDue),
  };
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
  if (query.order !== undefined) {
    params.set("order", query.order);
  }
  if (query.assignment !== undefined && query.view !== "mine") {
    params.set("assignment", query.assignment);
  }
  if (query.due !== undefined) {
    params.set("due", query.due);
  }
  const suffix = params.toString();
  return suffix === "" ? "/cases" : `/cases?${suffix}`;
};
export const listRequest = (query: WorklistQuery, cursor: string | null): CaseListRequest => {
  const { view: _view, ...filters } = query;
  return { ...filters, limit: 50, ...(cursor === null ? {} : { cursor }) };
};
