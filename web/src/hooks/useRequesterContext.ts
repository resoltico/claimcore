import { operationActions } from "../operation/affordances";
import { operationTask } from "../protocol/taskCatalog";
import { createContext, useContext } from "react";
import type { HumanDocument } from "@contracts/client";

import type { RequesterDocument } from "../protocol/types";

export type ActiveRequester = RequesterDocument & {
  readonly disclosure: "CURRENT";
  readonly subject: RequesterDocument["subject"] & {
    readonly state: "ACTIVE";
    readonly installationId: string;
    readonly actorId: string;
  };
  readonly basis: Extract<HumanDocument["basis"], { kind: "SNAPSHOT" }>;
};
export const isActiveRequester = (value: HumanDocument): value is ActiveRequester =>
  value.subject.kind === "REQUESTER" &&
  value.disclosure === "CURRENT" &&
  value.subject.state === "ACTIVE" &&
  value.subject.installationId !== null &&
  value.subject.actorId !== null &&
  value.basis.kind === "SNAPSHOT";

/** One authorized Core document owns requester controls, presentation and observed calendar. */
export const RequesterContext = createContext<ActiveRequester | null>(null);
export const useRequesterContext = () => useContext(RequesterContext);

export const useCaseCreationControl = () => {
  const requester = useRequesterContext();
  return operationActions(requester?.actions ?? []).find((action) => {
    const task = operationTask(action.task);
    return task.creates && task.subject === "CASE" && action.blocked === null;
  });
};
