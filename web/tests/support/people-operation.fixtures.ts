import type { GrantScope, HumanReview } from "@contracts/client";
import { workBasis } from "./inputs";
import { vi } from "vitest";
import { service, type ApiResult, type ServiceResponse } from "../../src/api/service";
import { taskForCommand } from "../../src/protocol/taskCatalog";

type PeopleInput = Extract<
  Parameters<typeof service.preview>[0]["input"],
  { binding: { kind: "PEOPLE" } }
>;
const actorId = "10000000-0000-4000-8000-000000000001";
const basis = { kind: "SNAPSHOT", observation: workBasis } as const;
const resolvedScope = (input: PeopleInput): GrantScope => {
  if (
    (input.task !== "people.grant" && input.task !== "people.revoke") ||
    input.inputs["input:scope"] === "INSTALLATION"
  ) {
    return { kind: "INSTALLATION" };
  }
  return {
    kind: "CASE",
    caseId: input.inputs["input:caseId"] ?? "20000000-0000-4000-8000-000000000001",
    caseReference: input.inputs["field:caseReference"] ?? "SYNTHETIC-GRANT",
  };
};
/** Interaction fixtures borrow only declared binding metadata; independent policy and rendering oracles live in their focused tests. */
export const peopleReview = (input: PeopleInput): HumanReview => {
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
          kind: "PEOPLE",
          actorId: ["people.register", "people.admit"].includes(input.task) ? null : actorId,
          scope:
            input.task === "people.grant" || input.task === "people.revoke"
              ? resolvedScope(input)
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
export const peoplePreview = () =>
  vi.spyOn(service, "preview").mockImplementation((prepared) => {
    if (prepared.input.binding.kind !== "PEOPLE") {
      throw new Error("People preview fixture required");
    }
    return Promise.resolve({
      kind: "outcome",
      status: 200,
      value: {
        representation: "HUMAN",
        operationId: prepared.input.operationId,
        outcome: { kind: "REVIEWED", review: peopleReview(prepared.input as PeopleInput) },
      },
    });
  });
export const peopleAcceptance = (
  prepared: Parameters<typeof service.execute>[0],
): ApiResult<ServiceResponse<"operation.execute">> => {
  const { input } = prepared;
  if (input.binding.kind !== "PEOPLE") {
    throw new Error("People fixture required.");
  }
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
            recordedFields: [],
            target: {
              kind: "PEOPLE",
              actorId: ["people.register", "people.admit"].includes(input.task)
                ? input.operationId
                : actorId,
            },
            caseLink: null,
            refreshCase: null,
          },
          basis,
          disclosure: "CURRENT",
        },
      },
    },
  };
};
