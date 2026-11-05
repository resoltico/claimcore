import { caseViewInput } from "./inputs";
import { caseAdvice } from "./case-advice.fixtures";
import { screen, waitFor } from "@testing-library/react";
import type { ComponentProps } from "react";
import { vi } from "vitest";
import type { OperationDraft, CurrentCase } from "../../src/api/service";
import { SettingsWorkspace } from "../../src/views/SettingsWorkspace";
import { useRoute } from "../../src/routing/useRoute";
import type userEvent from "@testing-library/user-event";
import { OperationEditor } from "../../src/views/OperationEditor";
import { definition, fields, response, runtime } from "./service-ui.fixtures";
export const current: CurrentCase = caseViewInput({
  caseId: "20000000-0000-4000-8000-000000000001",
  caseReference: fields.caseReference,
  revision: 1,
  advice: {
    ...caseAdvice(["CLOSE"]),
    history: ["SUMMARY", "FULL"],
  },
  state: {
    kind: "ACTIVE",
    fields,
  },
});
const EditorWorkspace = (overrides: Partial<ComponentProps<typeof OperationEditor>>) => {
  const route = useRoute();
  return (
    <SettingsWorkspace route={route}>
      {() => (
        <OperationEditor
          definition={definition}
          current={current}
          initialAct="CLOSE"
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

export const draftAt = (index: number): OperationDraft => {
  const body = vi.mocked(globalThis.fetch).mock.calls[index]?.[1]?.body;
  if (typeof body !== "string") {
    throw new Error("Expected one serialized canonical request.");
  }
  return JSON.parse(body) as OperationDraft;
};
export const previewReply = (index = 0): Response => {
  const draft = draftAt(index);
  return response("operation.preview", "REVIEWED", {
    review: {
      presentation: { changes: [], unchanged: [] },
      subject: {
        kind: "CASE",
        before: {
          caseId: "20000000-0000-4000-8000-000000000001",
          caseReference: fields.caseReference,
          revision: draft.expectedRevision,
          state: {
            kind: "ACTIVE",
            fields,
          },
        },
        after: {
          caseId: "20000000-0000-4000-8000-000000000001",
          caseReference: {
            ...fields,
            status: "CLOSED",
          }.caseReference,
          revision: 2,
          state: {
            kind: "ACTIVE",
            fields: {
              ...fields,
              status: "CLOSED",
            },
          },
        },

        context: runtime,
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
