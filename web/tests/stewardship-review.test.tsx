import userEvent from "@testing-library/user-event";
import { expect, it, vi } from "vitest";
import { render, screen } from "./presentation-test-support";
import { editor, current } from "./presentation-state.fixtures";
import { fields, response, runtime } from "./service-ui.fixtures";

const stateLabels = { ACTIVE: "Active", VOIDED: "Voided as a data-entry error", ERASED: "Erased" };
const stubReview = (before: "ACTIVE" | "VOIDED", after: "ACTIVE" | "VOIDED" | "ERASED") => {
  vi.stubGlobal(
    "fetch",
    vi.fn().mockImplementation(() =>
      Promise.resolve(
        response("case.preview", "REVIEWED", {
          review: {
            before: {
              caseReference: fields.caseReference,
              revision: 1,
              state: { tag: "LIVE", fields, disposition: before },
            },
            after: {
              caseReference: fields.caseReference,
              revision: 2,
              state:
                after === "ERASED"
                  ? { tag: "ERASED" }
                  : { tag: "LIVE", fields, disposition: after },
            },
            changes: [],
            context: runtime,
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
  { act: "ERASE", label: "Erase case payload", before: "ACTIVE", after: "ERASED", reason: null },
] as const)(
  "$act review includes prepared intent and authoritative record transition outside business fields",
  async ({ act, label, before, after, reason }) => {
    stubReview(before, after);
    render(
      editor({
        initialCommand: act,
        current: {
          ...current,
          advice: { ...current.advice, acts: [{ act, expectedRevision: current.revision }] },
        },
      }),
    );
    const user = userEvent.setup();
    if (reason !== null) {
      await user.type(screen.getByRole("textbox", { name: "Stewardship reason" }), reason);
    }
    await user.click(screen.getByRole("button", { name: "Review changes" }));
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
    expect(screen.getByRole("button", { name: "Record changes" })).toBeDisabled();
    expect(vi.mocked(fetch)).toHaveBeenCalledOnce();
  },
);
