import type { Scope, GrantScope, HumanReview } from "@contracts/client";
import { workBasis } from "./inputs";
import { vi } from "vitest";
import { service, type ApiResult, type ServiceResponse } from "../../src/api/service";
import { taskForCommand } from "../../src/protocol/taskCatalog";

type AuthorityInput = Extract<
  Parameters<typeof service.preview>[0]["input"],
  { kind: "AUTHORITY" }
>;
const actorId = "10000000-0000-4000-8000-000000000001";
const basis = { kind: "SNAPSHOT", observation: workBasis } as const;
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
/** Interaction fixtures borrow only declared binding metadata; independent policy and rendering oracles live in their focused tests. */
export const authorityReview = (input: AuthorityInput): HumanReview => {
  const task = taskForCommand(input);
  const inputs = [
    ...task.inputs,
    ...task.collections.flatMap((collection) => collection.inputs),
  ].map((entry) => entry.id);
  return {
    document: {
      screen: { regions: [] },
      context: {
        kind: "REVIEW",
        task: task.id,
        inputs: [...new Set(inputs)],
        consequence: task.consequence,
        target: {
          kind: "AUTHORITY",
          actorId: ["REGISTER", "ADMIT"].includes(input.act.kind) ? null : actorId,
          scope:
            input.act.kind === "GRANT" || input.act.kind === "REVOKE"
              ? resolvedScope(input.act.scope)
              : { kind: "INSTALLATION" },
        },
        confirmation: { kind: "OPERATION", operationId: input.operationId },
      },
      basis,
      disclosure: "CURRENT",
    },
    presentation: { changes: [], unchanged: [] },
  };
};
export const authorityPreview = () =>
  vi.spyOn(service, "preview").mockImplementation((prepared) => {
    if (prepared.input.kind !== "AUTHORITY") throw new Error("Authority preview fixture required");
    return Promise.resolve({
      kind: "outcome",
      status: 200,
      value: {
        representation: "HUMAN",
        operationId: prepared.input.operationId,
        outcome: { kind: "REVIEWED", review: authorityReview(prepared.input) },
      },
    });
  });
export const authorityAcceptance = (
  prepared: Parameters<typeof service.execute>[0],
): ApiResult<ServiceResponse<"operation.execute">> => {
  const input = prepared.input;
  if (input.kind !== "AUTHORITY") throw new Error("Authority fixture required.");
  return {
    kind: "outcome",
    status: 200,
    value: {
      representation: "HUMAN",
      operationId: input.operationId,
      outcome: {
        kind: "ACCEPTED",
        document: {
          screen: { regions: [] },
          context: {
            kind: "RECEIPT",
            target: {
              kind: "AUTHORITY",
              actorId: ["REGISTER", "ADMIT"].includes(input.act.kind) ? input.operationId : actorId,
            },
            refreshCase: null,
          },
          basis,
          disclosure: "CURRENT",
        },
      },
    },
  };
};
