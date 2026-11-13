import type {
  HumanReview,
  OperationInput,
  ResponseFor,
  ReviewPresentation,
  Screen,
  WorkDateBasis,
} from "@contracts/client";
import { taskForCommand } from "../../src/protocol/taskCatalog";

/** Borrow only declared task bindings; the caller independently authors displayed facts and target. */
export const humanReviewEnvelope = (
  command: OperationInput,
  binding: Pick<HumanReview["document"]["context"], "target" | "confirmation">,
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
          context: {
            kind: "REVIEW",
            task: task.id,
            inputs: [
              ...new Set(
                [
                  ...task.inputs,
                  ...task.collections.flatMap((collection) => collection.inputs),
                ].map((input) => input.id),
              ),
            ],
            consequence: task.consequence,
            ...binding,
          },
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
