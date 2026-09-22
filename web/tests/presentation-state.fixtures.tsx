import { screen } from "@testing-library/react";
import type { ComponentProps } from "react";
import { vi } from "vitest";
import type { CommandDraft, CurrentCase } from "../src/api/v2";
import { PresentationControls } from "../src/presentation/PresentationControls";
import { OperationEditor } from "../src/views/OperationEditor";
import { definition, fields, preparation, response } from "./v2-ui.fixtures";

export const current: CurrentCase = {
  case: { fields, revision: "1" },
  availableCommands: ["CLOSE"],
};
export const editor = (overrides: Partial<ComponentProps<typeof OperationEditor>> = {}) => (
  <>
    <PresentationControls />
    <OperationEditor
      token="token"
      definition={definition}
      current={current}
      initialCommand="CLOSE"
      onClose={vi.fn()}
      onCommitted={vi.fn()}
      onMutationLockChange={vi.fn()}
      {...overrides}
    />
  </>
);

export const draftAt = (index: number): CommandDraft => {
  const body = vi.mocked(globalThis.fetch).mock.calls[index]?.[1]?.body;
  if (typeof body !== "string") throw new Error("Expected one serialized canonical request.");
  return JSON.parse(body) as CommandDraft;
};
export const preparedReply = (index = 0): Response => {
  const draft = draftAt(index);
  return response("command.prepare", "PREPARED", {
    details: {
      ...preparation,
      expectedRevision: draft.expectedRevision,
      summary: {
        ...preparation.summary,
        operationId: draft.operationId,
        caseReference: draft.caseReference,
        command: draft.command.kind,
      },
    },
    review: {
      before: { fields, revision: draft.expectedRevision },
      proposed: { fields: { ...fields, status: "CLOSED" }, revision: "2" },
      changes: [{ fieldName: "status", before: "OPENED", after: "CLOSED" }],
      context: definition.runtime,
      advisory: true,
    },
  });
};
export const deferredResponse = () => {
  let resolve: (value: Response) => void = () => undefined;
  let reject: (reason: Error) => void = () => undefined;
  const promise = new Promise<Response>((yes, no) => {
    resolve = yes;
    reject = no;
  });
  return { promise, resolve, reject };
};
export const languageControl = (): HTMLSelectElement => {
  const result = screen
    .getAllByRole("combobox")
    .find((item) => item.querySelector('option[value="en-XA"]'));
  if (!(result instanceof HTMLSelectElement))
    throw new Error("Expected the accessible language selector.");
  return result;
};
