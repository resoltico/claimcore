import { createContext, useContext } from "react";
import type { HumanReadDocument_context } from "@contracts/client";

export type ActiveRequester = HumanReadDocument_context & {
  readonly disclosure: "CURRENT";
  readonly context: HumanReadDocument_context["context"] & {
    readonly state: "ACTIVE";
    readonly installationId: string;
    readonly actorId: string;
  };
  readonly basis: Extract<HumanReadDocument_context["basis"], { kind: "SNAPSHOT" }>;
};
export const isActiveRequester = (value: HumanReadDocument_context): value is ActiveRequester =>
  value.disclosure === "CURRENT" &&
  value.context.state === "ACTIVE" &&
  value.context.installationId !== null &&
  value.context.actorId !== null &&
  value.basis.kind === "SNAPSHOT";

/** One authorized Core document owns requester controls, presentation and observed calendar. */
export const RequesterContext = createContext<ActiveRequester | null>(null);
export const useRequesterContext = () => useContext(RequesterContext);
