import { operationAffordance } from "./affordances.fixtures";
import type {
  HumanReview,
  OperationSubmission,
  ResponseFor,
  ReviewPresentation,
  Screen,
  WorkDateBasis,
} from "@contracts/client";
import { taskForCommand } from "../../src/protocol/taskCatalog";

/** Borrow only declared task bindings; the caller independently authors displayed facts and target. */
export const humanReviewEnvelope = (
  command: OperationSubmission,
  binding: { target: unknown; confirmation: HumanReview["document"]["subject"]["confirmation"] },
  basis: WorkDateBasis,
  screen: Screen,
  presentation: ReviewPresentation,
): ResponseFor<"operation.preview", "HUMAN"> => {
  const task = taskForCommand(command);
  const operationPreviewReply = {
    representation: "HUMAN",
    operationId: command.operationId,
    outcome: {
      kind: "REVIEWED",
      review: {
        document: {
          screen,
          subject: {
            kind: "REVIEW",
            confirmation: binding.confirmation,
            consequence: task.consequence,
          },
          actions: [operationAffordance(command.task, command.binding, command.inputs, [])],
          links: [],
          history: null,
          nextCursor: null,
          basis: {
            kind: "SNAPSHOT",
            observation: basis,
          },
          disclosure: "CURRENT",
        },
        presentation,
      },
    },
  } as ResponseFor<"operation.preview", "HUMAN">;
  if (operationPreviewReply.representation !== "HUMAN") {
    throw new Error("Authored review facts do not match current HUMAN bindings.");
  }
  return operationPreviewReply;
};
