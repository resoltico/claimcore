import { screen } from "@testing-library/react";
import type { ComponentProps } from "react";
import { vi } from "vitest";
import type { CommandDraft, CurrentCase } from "../src/api/service";
import { PresentationControls } from "../src/presentation/PresentationControls";
import { OperationEditor } from "../src/views/OperationEditor";
import { definition, fields, digest, response, runtime } from "./service-ui.fixtures";

export const current: CurrentCase = {
  state: "LIVE",
  disposition: "ACTIVE",
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
  if (typeof body !== "string") {
    throw new Error("Expected one serialized canonical request.");
  }
  return JSON.parse(body) as CommandDraft;
};
export const previewReply = (index = 0): Response => {
  const draft = draftAt(index);
  return response("command.preview", "REVIEWED", {
    requestSha256: digest,
    review: {
      before: { fields, revision: draft.expectedRevision },
      proposed: { fields: { ...fields, status: "CLOSED" }, revision: "2" },
      changes: [{ fieldName: "status", before: "OPENED", after: "CLOSED" }],
      context: runtime,
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
  const result = screen.getAllByRole("combobox").find((item) => item.id.endsWith("-language"));
  if (!(result instanceof HTMLSelectElement)) {
    throw new Error("Expected the accessible language selector.");
  }
  return result;
};
