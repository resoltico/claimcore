import { sameOwnerScope } from "../protocol/ownerScope";
import { isCurrentCase } from "../protocol/types";
import type {
  DisclosedResource,
  EffectIdentity,
  OperationAction,
  RevisionConflictSnapshot,
} from "./operationTypes";

export const ownsDisclosure = (resource: DisclosedResource, identity: EffectIdentity): boolean =>
  sameOwnerScope(resource.context.owner, identity.owner) &&
  resource.context.generation === identity.contextGeneration &&
  resource.context.credential === identity.credential &&
  resource.context.target === identity.target;
export const invalidateResource = (
  resource: DisclosedResource,
  refused: boolean,
): DisclosedResource => ({
  ...resource,
  refused: resource.refused || refused,
  revision: resource.revision + (refused ? 1 : 0),
  read: null,
  review: null,
  receipt: null,
  caseObservation: null,
  conflict:
    resource.conflict === null ? null : { ...resource.conflict, diagnostic: null, latest: null },
  notice: null,
  fieldErrors: {},
  rejection: null,
});
export const beginCaseRead = (
  resource: DisclosedResource,
  identity: EffectIdentity,
  caseId: string,
): DisclosedResource => ({
  ...resource,
  read: { resource: "case.get", identity, query: { target: { kind: "ID", caseId } } },
  notice: null,
});
export const beginConflictRead = (
  resource: DisclosedResource,
  identity: EffectIdentity,
  snapshot: RevisionConflictSnapshot,
): DisclosedResource => ({
  ...resource,
  conflict: { ...snapshot, latest: null },
  caseObservation: null,
  read: { resource: "conflict", identity, query: snapshot.source },
  notice: null,
});
export const observeCase = (
  resource: DisclosedResource,
  action: Extract<OperationAction, { type: "DISCLOSED" | "DISCLOSURE_READ_FAILED" }>,
): DisclosedResource => {
  const { read } = resource;
  if (
    !ownsDisclosure(resource, action.identity) ||
    read?.resource !== "case.get" ||
    read.identity.id !== action.identity.id
  ) {
    return resource;
  }
  if (action.type === "DISCLOSURE_READ_FAILED") {
    return {
      ...resource,
      read: null,
      notice: action.notice,
      refused: resource.refused || action.refused,
      revision: resource.revision + 1,
    };
  }
  if (read.query.target.kind === "ID" && action.value.context.caseId !== read.query.target.caseId) {
    return {
      ...resource,
      read: null,
      notice: { kind: "local", reason: "incomplete" },
      revision: resource.revision + 1,
    };
  }
  return {
    ...resource,
    caseObservation: action.value,
    read: null,
    confirmedContext: resource.context,
    refused: false,
    notice: null,
  };
};
export const observeConflict = (
  resource: DisclosedResource,
  action: Extract<OperationAction, { type: "CONFLICT_OBSERVED" | "CONFLICT_READ_FAILED" }>,
): DisclosedResource => {
  const snapshot = resource.conflict;
  if (
    snapshot === null ||
    snapshot.identity.id !== action.conflictId ||
    resource.read?.resource !== "conflict" ||
    resource.read.identity.id !== action.identity.id ||
    !ownsDisclosure(resource, action.identity)
  ) {
    return resource;
  }
  if (action.type === "CONFLICT_READ_FAILED") {
    return {
      ...resource,
      read: null,
      notice: action.notice,
      refused: resource.refused || action.refused,
      revision: resource.revision + 1,
    };
  }
  const { target } = snapshot.source.prepared.input.binding;
  if (target.kind === "ID" && action.value.current.context.caseId !== target.caseId) {
    return {
      ...resource,
      read: null,
      notice: { kind: "local", reason: "incomplete" },
      revision: resource.revision + 1,
    };
  }
  return {
    ...resource,
    conflict: { ...snapshot, latest: action.value },
    caseObservation: isCurrentCase(action.value.current) ? action.value.current : null,
    read: null,
    confirmedContext: resource.context,
    refused: false,
    notice: null,
  };
};
