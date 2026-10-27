import userEvent from "@testing-library/user-event";
import { expect, it, vi } from "vitest";
import { getButton, render, screen } from "./presentation-test-support";
import { editor, current } from "./presentation-state.fixtures";
import { fields, response, runtime } from "./service-ui.fixtures";
const stateLabels = {
  ACTIVE: "Active",
  VOIDED: "Voided as a data-entry error",
  ERASED: "Erased",
};
const stubReview = (before: "ACTIVE" | "VOIDED", after: "ACTIVE" | "VOIDED" | "ERASED") => {
  vi.stubGlobal(
    "fetch",
    vi.fn().mockImplementation(() =>
      Promise.resolve(
        response("operation.preview", "REVIEWED", {
          review: {
            subject: {
              kind: "CASE",
              before: {
                caseReference: fields.caseReference,
                revision: 1,
                state: {
                  kind: before,
                  fields,
                },
              },
              after: {
                caseReference: fields.caseReference,
                revision: 2,
                state:
                  after === "ERASED"
                    ? {
                        kind: "ERASED",
                      }
                    : {
                        kind: after,
                        fields,
                      },
              },
              changes: [],
              context: runtime,
            },
          },
        }),
      ),
    ),
  );
};
it.each([
  {
    act: "VOID",
    label: "Void case record",
    before: "ACTIVE",
    after: "VOIDED",
    reason: "Exact العربية 1.0000",
  },
  {
    act: "REINSTATE",
    label: "Reinstate case record",
    before: "VOIDED",
    after: "ACTIVE",
    reason: "Exact reinstatement",
  },
  {
    act: "ERASE",
    label: "Erase case payload",
    before: "ACTIVE",
    after: "ERASED",
    reason: null,
  },
] as const)(
  "$act review includes prepared intent and authoritative record transition outside business fields",
  async ({ act, label, before, after, reason }) => {
    stubReview(before, after);
    render(
      editor({
        initialAct: act,
        current: {
          ...current,
          advice: {
            history: current.advice.history,
            acts: [act],
          },
        },
      }),
    );
    const user = userEvent.setup();
    if (reason !== null) {
      await user.type(
        screen.getByRole("textbox", {
          name: "Stewardship reason",
        }),
        reason,
      );
    }
    await user.click(getButton("Review changes"));
    const dialog = await screen.findByRole("dialog");
    expect(dialog).toHaveTextContent(label);
    if (reason !== null) {
      expect(dialog).toHaveTextContent(reason);
    }
    expect(dialog).toHaveTextContent(`Before: ${stateLabels[before]}`);
    expect(dialog).toHaveTextContent(`After: ${stateLabels[after]}`);
    if (act === "ERASE") {
      expect(dialog).toHaveTextContent("WAL, backups and copies elsewhere are not erased.");
    }
    expect(getButton("Record changes")).toBeDisabled();
    expect(vi.mocked(fetch)).toHaveBeenCalledOnce();
  },
);
