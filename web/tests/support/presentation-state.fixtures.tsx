import { caseSubject, caseDocumentInput } from "./case-document.fixtures";
import { operationAffordance } from "./affordances.fixtures";

import { screen, waitFor } from "@testing-library/react";
import type { ComponentProps } from "react";
import { vi } from "vitest";
import type { CaseSubmission, CurrentCase } from "../../src/api/service";
import { SettingsWorkspace } from "../../src/views/SettingsWorkspace";
import { useRoute } from "../../src/routing/useRoute";
import type userEvent from "@testing-library/user-event";
import { OperationEditor } from "../../src/views/OperationEditor";
import { definition, fields } from "./service-ui.fixtures";
import { taskForCommand } from "../../src/protocol/taskCatalog";
import { endpointResponse } from "./transport-response.fixtures";
export const current: CurrentCase = caseDocumentInput({
  subject: { ...caseSubject, caseId: "20000000-0000-4000-8000-000000000001", revision: 1 },
  actions: [
    operationAffordance(
      "case.close",
      {
        kind: "CASE",
        target: { kind: "ID", caseId: "20000000-0000-4000-8000-000000000001" },
        expectedRevision: 1,
      },
      {},
      undefined,
      {},
    ),
  ],
  history: "FULL",
});
const EditorWorkspace = (overrides: Partial<ComponentProps<typeof OperationEditor>>) => {
  const route = useRoute();
  return (
    <SettingsWorkspace route={route}>
      {() => (
        <OperationEditor
          definition={definition}
          current={current}
          initialAct="case.close"
          onClose={vi.fn()}
          onCommitted={vi.fn()}
          onMutationLockChange={vi.fn()}
          {...overrides}
        />
      )}
    </SettingsWorkspace>
  );
};
export const editor = (overrides: Partial<ComponentProps<typeof OperationEditor>> = {}) => (
  <EditorWorkspace {...overrides} />
);

export const draftAt = (index: number): CaseSubmission => {
  const body = vi.mocked(globalThis.fetch).mock.calls[index]?.[1]?.body;
  if (typeof body !== "string") {
    throw new Error("Expected one serialized canonical request.");
  }
  return JSON.parse(body) as CaseSubmission;
};
/** Interaction review borrows only declared task bindings; display/policy oracles are independently authored. */
export const previewReply = (index = 0): Response => {
  const draft = draftAt(index);
  const task = taskForCommand(draft);
  if (
    draft.task !== "case.close" &&
    draft.task !== "case.amendRegistration" &&
    draft.task !== "case.correct"
  ) {
    throw new Error("This interaction fixture reviews CLOSE, AMEND_REGISTRATION or CORRECT only");
  }
  return endpointResponse("operation.preview", {
    representation: "HUMAN",
    outcome: {
      kind: "REVIEWED",
      review: {
        document: {
          screen: { regions: [] },
          subject: {
            kind: "REVIEW",
            confirmation: {
              kind: "CASE_REFERENCE",
              reference:
                draft.binding.target.kind === "REFERENCE"
                  ? draft.binding.target.caseReference
                  : fields.caseReference,
            },
            consequence: task.consequence,
          },
          actions: [
            operationAffordance(
              task.id,
              {
                kind: "CASE",
                target: { kind: "ID", caseId: current.subject.caseId },
                expectedRevision: draft.binding.expectedRevision,
              },
              {},
              [],
            ),
          ],
          links: [],
          history: null,
          nextCursor: null,
          basis: current.basis,
          disclosure: "CURRENT",
        },
        presentation: { changes: [], unchanged: [] },
      },
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
  return {
    promise,
    resolve,
    reject,
  };
};
const languageControl = (language: string): HTMLInputElement => {
  const result = screen
    .getAllByRole("radio")
    .find(
      (item) =>
        item instanceof HTMLInputElement &&
        item.name.endsWith("-language") &&
        item.value === language,
    );
  if (!(result instanceof HTMLInputElement)) {
    throw new Error("Expected the accessible language radio.");
  }
  return result;
};
/** Follow the same permitted Settings detour used by the actual Dashboard. */
export const setEditorLanguage = async (
  user: ReturnType<typeof userEvent.setup>,
  language: string,
) => {
  const open = document.querySelector<HTMLButtonElement>("[data-open-settings]");
  if (open === null) {
    throw new Error("Expected current drawer Settings action.");
  }
  await user.click(open);
  const control = await waitFor(() => languageControl(language));
  await user.click(control);
  const back = document.querySelector<HTMLButtonElement>("[data-return-workspace]");
  if (back === null) {
    throw new Error("Expected Settings return action.");
  }
  await user.click(back);
  await screen.findByRole("dialog");
};
