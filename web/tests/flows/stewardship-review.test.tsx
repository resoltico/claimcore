import { reviewConfirm } from "../support/review-controls";
import userEvent from "@testing-library/user-event";
import { expect, it, vi } from "vitest";
import { getButton, render, screen } from "../support/presentation-test-support";
import { editor, current } from "../support/presentation-state.fixtures";
import { fields, response, runtime } from "../support/service-ui.fixtures";
const stateLabels = {
  ACTIVE: "Active",
  VOIDED: "Voided",
  ERASED: "Erased",
};
const stubReview = (before: "ACTIVE" | "VOIDED", after: "ACTIVE" | "VOIDED" | "ERASED") => {
  vi.stubGlobal(
    "fetch",
    vi.fn().mockImplementation(() =>
      Promise.resolve(
        response("operation.preview", "REVIEWED", {
          review: {
            view: { kind: "RECORD" as const, sections: [] },
            presentation: {
              changes: [
                {
                  key: "recordState",
                  labelId: "input.lifecycle.label",
                  before: { kind: "CODE", vocabulary: "lifecycle", token: before },
                  after: { kind: "CODE", vocabulary: "lifecycle", token: after },
                },
              ],
              unchanged: [],
            },
            subject: {
              kind: "CASE",
              before: {
                caseId: "20000000-0000-4000-8000-000000000001",
                caseReference: fields.caseReference,
                revision: 1,
                state: {
                  kind: before,
                  fields,
                },
              },
              after: {
                caseId: "20000000-0000-4000-8000-000000000001",
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
    label: "Erase case",
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
          advice: { ...current.advice, history: current.advice.history, acts: [act] },
        },
      }),
    );
    const user = userEvent.setup();
    if (reason !== null) {
      await user.type(
        screen.getByRole("textbox", {
          name: "Reason",
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
    expect(dialog.querySelector(".review-before")).toHaveTextContent(stateLabels[before]);
    expect(dialog.querySelector(".review-after")).toHaveTextContent(stateLabels[after]);
    if (act === "ERASE") {
      expect(dialog).toHaveTextContent("Copies held elsewhere remain.");
    }
    if (act === "ERASE") {
      expect(reviewConfirm()).toBeDisabled();
      const confirmation = screen.getByRole("textbox", { name: "Case reference (exact)" });
      await user.type(confirmation, fields.caseReference.toLowerCase());
      expect(reviewConfirm()).toBeDisabled();
      await user.clear(confirmation);
      await user.type(confirmation, fields.caseReference);
    }
    expect(reviewConfirm()).not.toBeDisabled();
    expect(screen.queryByRole("checkbox")).toBeNull();
    expect(vi.mocked(fetch)).toHaveBeenCalledOnce();
  },
);
