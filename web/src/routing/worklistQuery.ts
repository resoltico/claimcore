import type { CaseListRequest } from "@contracts/client";
import { isTerm } from "../presentation/terms";
export const savedViews = [
  "mine",
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
      return { view, assignment: "ASSIGNED_TO_ME" };
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
export const parseWorklistQuery = (query: string): WorklistQuery => {
  const params = new URLSearchParams(query);
  const view = savedViews.find((value) => value === params.get("view"));
  const lifecycle = unique(
    params.getAll("lifecycle").filter((value) => isTerm("lifecycle", value)),
  );
  const status = unique(params.getAll("status").filter((value) => isTerm("status", value)));
  const progress = unique(params.getAll("progress").filter((value) => isTerm("progress", value)));
  const text = params.get("q") ?? "";
  const order = params.get("order");
  return {
    ...(view === undefined ? {} : viewQuery(view)),
    ...(lifecycle.length ? { lifecycle } : {}),
    ...(status.length ? { status } : {}),
    ...(progress.length ? { progress } : {}),
    ...(text === "" ? {} : { text }),
    ...(order === "RECENT" || order === "NOTIFIED" ? { order } : {}),
    ...(params.get("assignment") === "ASSIGNED_TO_ME" ? { assignment: "ASSIGNED_TO_ME" } : {}),
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
  if (query.order === "RECENT" || query.order === "NOTIFIED") {
    params.set("order", query.order);
  }
  if (query.assignment !== undefined && query.view !== "mine") {
    params.set("assignment", query.assignment);
  }
  const suffix = params.toString();
  return suffix === "" ? "/cases" : `/cases?${suffix}`;
};
export const listRequest = (query: WorklistQuery, cursor: string | null): CaseListRequest => {
  const { view: _view, ...filters } = query;
  return { ...filters, limit: 50, ...(cursor === null ? {} : { cursor }) };
};
