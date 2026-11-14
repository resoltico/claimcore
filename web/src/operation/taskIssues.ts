import type { HumanInputIssue, OperationTaskDefinition } from "@contracts/client";
import type { TaskSubmission } from "./taskForm";
import type { Notice } from "../protocol/notices";

/** Ordinals refer to the immutable submission, never the current editor's row order. */
export const taskRowInputKey = (collection: string, row: string, input: string): string =>
  [collection, row, input].map(encodeURIComponent).join("/");
export type TaskIssueProjection = {
  readonly fields: Readonly<Record<string, Notice>>;
  readonly unbound: ReadonlyArray<HumanInputIssue>;
};
export const projectTaskIssues = (
  issues: ReadonlyArray<HumanInputIssue>,
  task: OperationTaskDefinition,
  submittedRows: TaskSubmission["submittedRows"],
): TaskIssueProjection => {
  const grouped = new Map<string, HumanInputIssue[]>();
  const unbound: HumanInputIssue[] = [];
  for (const issue of issues) {
    let key: string | undefined;
    if (issue.row === null) {
      key = task.inputs.some((input) => input.id === issue.input)
        ? issue.input
        : task.collections.find((collection) => collection.input === issue.input)?.id;
    } else {
      const collection = task.collections.find((candidate) =>
        candidate.inputs.some((input) => input.id === issue.input),
      );
      const row = collection === undefined ? undefined : submittedRows[collection.id]?.[issue.row];
      if (
        collection !== undefined &&
        row !== undefined &&
        collection.inputs.some((input) => input.id === issue.input)
      ) {
        key = taskRowInputKey(collection.id, row, issue.input);
      }
    }
    if (key === undefined) {
      unbound.push(issue);
    } else {
      grouped.set(key, [...(grouped.get(key) ?? []), issue]);
    }
  }
  return {
    fields: Object.fromEntries(
      [...grouped].map(([key, boundIssues]) => [
        key,
        { kind: "humanIssues" as const, issues: boundIssues },
      ]),
    ),
    unbound,
  };
};
