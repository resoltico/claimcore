import userEvent from "@testing-library/user-event";
import { expect, it, vi } from "vitest";
import { render, screen } from "./presentation-test-support";
import { preferenceKey } from "../src/presentation/preferences";
import { current, draftAt, editor, preparedReply } from "./presentation-state.fixtures";
import { preparation } from "./v3-ui.fixtures";
import { preview } from "./v3-recovery.fixtures";
import {
  RecoveryImportDialog,
  RecoveryDetailsDialog,
  RecoveryList,
} from "../src/views/recovery/RecoveryPanels";
import type { RecoveryActions } from "../src/views/recovery/RecoveryState";

const reference = "A\u0308 العربية\u2069\u202E\u200D <script>";
const actions: RecoveryActions = {
  inspectId: vi.fn(),
  inspect: vi.fn(),
  loadAttempts: vi.fn(),
  choose: vi.fn(),
  act: vi.fn(),
  exportItem: vi.fn(),
  preview: vi.fn(),
  retain: vi.fn(),
};
const safeReference = (element: HTMLElement) => {
  expect([...element.querySelectorAll("bdi")].some((node) => node.textContent === reference)).toBe(
    true,
  );
  expect(element).toHaveTextContent("U+2069, U+202E, U+200D");
  expect(element.querySelector("script")).toBeNull();
};

const checkImport = () => {
  const importing = render(
    <RecoveryImportDialog
      importing={{
        preview: {
          ...preview,
          artifactKind: "ENVELOPE" as const,
          decodedEffect: {
            ...preview.decodedEffect,
            command: "CLOSE" as const,
            canonicalCommandFormat: 3 as const,
            caseReference: reference,
          },
        },
        file: new File(["synthetic"], "synthetic.json"),
      }}
      busy={null}
      onClose={vi.fn()}
      actions={actions}
    />,
  );
  safeReference(screen.getByRole("dialog"));
  importing.unmount();
};

const checkRecovery = () => {
  const retained = {
    ...preparation,
    summary: {
      ...preparation.summary,
      caseReference: reference,
      command: "CORRECT_CASE" as const,
    },
  };
  const inspection = render(
    <RecoveryDetailsDialog
      selected={{
        tag: "RETAINED",
        value: {
          preparation: retained,
          observation: { tag: "NOT_FOUND", identity: retained.summary.operationId },
        },
      }}
      onClose={vi.fn()}
      actions={actions}
    />,
  );
  safeReference(screen.getByRole("dialog"));
  inspection.unmount();
  const list = render(
    <RecoveryList
      listing={{
        items: [{ tag: "RETAINED", summary: retained.summary }],
        cursor: null,
        message: null,
        loading: false,
        page: null,
        view: "PENDING",
        setView: vi.fn(),
        load: vi.fn(),
      }}
      busy={null}
      actions={actions}
    />,
  );
  safeReference(list.container);
  expect(retained.summary.caseReference).toBe(reference);
};

it.each(["en", "lv", "ar"] as const)(
  "warns and isolates exact references at review and recovery decisions in %s [CC-WEB-001]",
  async (language) => {
    localStorage.setItem(
      preferenceKey,
      JSON.stringify({ version: 1, language, displayLocale: "en-GB" }),
    );
    const user = userEvent.setup();
    vi.stubGlobal(
      "fetch",
      vi.fn(() => Promise.resolve(preparedReply())),
    );
    const edit = render(
      editor({
        current: {
          ...current,
          case: { ...current.case, fields: { ...current.case.fields, caseReference: reference } },
        },
      }),
    );
    safeReference(edit.container);
    await user.click(edit.container.querySelector('button[type="submit"]')!);
    safeReference(await screen.findByRole("dialog"));
    expect(draftAt(0).caseReference).toBe(reference);
    edit.unmount();
    checkRecovery();
    checkImport();
    vi.unstubAllGlobals();
  },
);
