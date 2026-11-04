import type { CaseListRequest } from "@contracts/client";
import { isTerm } from "../presentation/terms";
export type WorklistQuery = Omit<CaseListRequest, "cursor" | "limit">;
const unique = <T>(values: ReadonlyArray<T>): T[] => [...new Set(values)];
const responsibilityQuery = (
  assignment: string | null,
  actor: string | null,
): Pick<WorklistQuery, "assignment" | "responsibleActor"> => ({
  ...(assignment === "MINE" || assignment === "UNASSIGNED" ? { assignment } : {}),
  ...(actor === null || actor === "" ? {} : { responsibleActor: actor }),
});
const dueQuery = (due: string | null): Pick<WorklistQuery, "due"> =>
  due !== null && isTerm("due", due) ? { due } : {};
export const parseWorklistQuery = (query: string): WorklistQuery => {
  const params = new URLSearchParams(query);
  const rawView = params.get("view");
  const view = rawView !== null && isTerm("caseView", rawView) ? rawView : undefined;
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
    ...(view === undefined ? {} : { view }),
    ...(lifecycle.length ? { lifecycle } : {}),
    ...(status.length ? { status } : {}),
    ...(progress.length ? { progress } : {}),
    ...(text === "" ? {} : { text }),
    ...(order === undefined ? {} : { order }),
    ...responsibilityQuery(assignment, params.get("responsibleActor")),
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
  if (query.assignment !== undefined) {
    params.set("assignment", query.assignment);
  }
  if (query.responsibleActor !== undefined) {
    params.set("responsibleActor", query.responsibleActor);
  }
  if (query.due !== undefined) {
    params.set("due", query.due);
  }
  const suffix = params.toString();
  return suffix === "" ? "/cases" : `/cases?${suffix}`;
};
export const listRequest = (query: WorklistQuery, cursor: string | null): CaseListRequest => ({
  ...query,
  limit: 50,
  ...(cursor === null ? {} : { cursor }),
});
