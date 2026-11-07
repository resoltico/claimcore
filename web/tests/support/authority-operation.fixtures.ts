import type {
  AuthorityTargetDisclosure,
  Scope,
  GrantScope,
  ActorProfileInput,
  AuthorityReviewEffect,
  OperationReview,
} from "@contracts/client";
import { workBasis } from "./inputs";
import { vi } from "vitest";
import { service, type ApiResult, type ServiceResponse } from "../../src/api/service";
import { wireReply } from "./reply.fixtures";
type AuthorityInput = Extract<
  Parameters<typeof service.preview>[0]["input"],
  { kind: "AUTHORITY" }
>;
const resolvedScope = (scope: Scope): GrantScope =>
  scope.kind === "INSTALLATION"
    ? scope
    : {
        kind: "CASE",
        caseId:
          scope.target.kind === "ID" ? scope.target.caseId : "20000000-0000-4000-8000-000000000001",
        caseReference:
          scope.target.kind === "REFERENCE" ? scope.target.caseReference : "SYNTHETIC-GRANT",
      };
const profile = (value: ActorProfileInput) => ({
  displayName: value.displayName,
  contact: value.contact ?? null,
});
const effectsFor = (input: AuthorityInput): ReadonlyArray<AuthorityReviewEffect> => {
  const { act } = input;
  switch (act.kind) {
    case "REGISTER":
      return [
        { kind: "REGISTER", principal: input.target },
        { kind: "SET_PROFILE", profile: profile(act.profile) },
      ];
    case "ADMIT":
      return [
        { kind: "REGISTER", principal: input.target },
        { kind: "SET_PROFILE", profile: profile(act.profile) },
        ...act.grants.map((grant) => ({
          kind: "GRANT" as const,
          grant: { role: grant.role, scope: resolvedScope(grant.scope) },
        })),
      ];
    case "GRANT":
    case "REVOKE":
      return [{ kind: act.kind, grant: { role: act.role, scope: resolvedScope(act.scope) } }];
    case "SET_PROFILE":
      return [{ kind: "SET_PROFILE", profile: profile(act.profile) }];
    default:
      return [{ kind: act.kind }];
  }
};
/** Explicit synthetic server metadata, with the provided presentation left independent. */
export const authorityReview = (input: AuthorityInput): OperationReview => ({
  presentation: { changes: [], unchanged: [] },
  subject: {
    kind: "AUTHORITY",
    basis: workBasis,
    target: ["REGISTER", "ADMIT"].includes(input.act.kind)
      ? null
      : {
          actor: {
            actorId: "10000000-0000-4000-8000-000000000001",
            displayName: { kind: "NAMED", value: "Reviewed account" },
            principalKind: input.target.kind,
            availability: "NOT_ELIGIBLE",
          },
          principal: input.target,
          contact: null,
          state: "NO_ROLES",
          roles: [],
        },
    accessScope:
      input.act.kind === "GRANT" || input.act.kind === "REVOKE"
        ? resolvedScope(input.act.scope)
        : { kind: "INSTALLATION" },
    before: { enabled: false, profile: null, access: [], grants: [] },
    after: { enabled: true, profile: null, access: [], grants: [] },
    effects: effectsFor(input),
  },
});
export const authorityPreview = () =>
  vi.spyOn(service, "preview").mockImplementation((prepared) => {
    if (prepared.input.kind !== "AUTHORITY") {
      throw new Error("Authority preview fixture required");
    }
    return Promise.resolve({
      kind: "outcome",
      status: 200,
      value: {
        operationId: prepared.input.operationId,
        outcome: { kind: "REVIEWED", data: { review: authorityReview(prepared.input) } },
      },
    });
  });
const createdTarget = (
  target: AuthorityTargetDisclosure,
  operationId: string,
): AuthorityTargetDisclosure => {
  if (target.kind === "DISCLOSED") {
    return { ...target, actor: { ...target.actor, actorId: operationId } };
  }
  return { ...target, actorId: operationId };
};
export const authorityAcceptance = (
  prepared: Parameters<typeof service.execute>[0],
): ApiResult<ServiceResponse<"operation.execute">> => {
  const value = wireReply("operation.execute", "AUTHORITY");
  if (
    value.outcome.kind !== "ACCEPTED" ||
    value.outcome.data.subject.kind !== "AUTHORITY" ||
    prepared.input.kind !== "AUTHORITY"
  ) {
    throw new Error("Authority fixture required.");
  }
  return {
    kind: "outcome",
    status: 200,
    value: {
      operationId: prepared.input.operationId,
      outcome: {
        kind: "ACCEPTED",
        data: {
          ...value.outcome.data,
          operationId: prepared.input.operationId,
          subject: {
            ...value.outcome.data.subject,
            act: prepared.input.act.kind,
            targetActor: ["REGISTER", "ADMIT"].includes(prepared.input.act.kind)
              ? createdTarget(value.outcome.data.subject.targetActor, prepared.input.operationId)
              : value.outcome.data.subject.targetActor,
          },
        },
      },
    },
  };
};
