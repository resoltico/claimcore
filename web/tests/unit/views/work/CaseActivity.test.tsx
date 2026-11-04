import { chooseOption } from "../../../support/choice-entry";
import { afterEach, expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { render, screen, waitFor } from "../../../support/presentation-test-support";
import { response } from "../../../support/service-ui.fixtures";
import { CaseActivityDisclosure } from "../../../../src/views/work/CaseActivity";
const caseId = "20000000-0000-4000-8000-000000000001";
const actorId = "10000000-0000-4000-8000-000000000001";
const operationId = "30000000-0000-4000-8000-000000000001";
const receipt = {
  operationId,
  position: { sequence: 2, entryHash: "a".repeat(64) },
  recordedAt: "2026-11-02T12:00:00.000000Z",
  recordedBy: actorId,
  subject: {
    kind: "WORK",
    caseId,
    caseReference: "ACTIVITY-1",
    act: "UPDATE_WORK",
    revision: 1,
    snapshot: {
      kind: "DISCLOSED",
      view: {
        caseId,
        revision: 1,
        work: { responsibleActor: null, handlingSummary: "HISTORY_PAYLOAD_CANARY" },
      },
    },
  },
};
afterEach(() => vi.unstubAllGlobals());
it("hides prior activity payload synchronously when full-history advice is removed", async () => {
  const fetch = vi.fn<typeof globalThis.fetch>().mockImplementation((_url, options) => {
    if (typeof options?.body !== "string") {
      throw new Error("Expected an exact activity request body.");
    }
    const input: unknown = JSON.parse(options.body);
    const full =
      typeof input === "object" && input !== null && "detail" in input && input.detail === "FULL";
    return Promise.resolve(
      response("case.activity", "SUCCEEDED", {
        caseId,
        caseReference: "ACTIVITY-1",
        items: [
          {
            operationId,
            position: { sequence: 2, entryHash: "a".repeat(64) },
            subject: { kind: "WORK", revision: 1, act: "UPDATE_WORK" },
            recordedAt: "2026-11-02T12:00:00.000000Z",
            recordedBy: actorId,
            detail: full ? receipt : null,
          },
        ],
        nextCursor: null,
      }),
    );
  });
  vi.stubGlobal("fetch", fetch);
  const target = { kind: "ID", caseId } as const;
  const refused = vi.fn();
  const view = render(
    <CaseActivityDisclosure
      target={target}
      modes={["SUMMARY", "FULL"]}
      onDisclosureRefused={refused}
    />,
  );
  const user = userEvent.setup();
  await user.click(screen.getByText("Case activity", { selector: "summary" }));
  await chooseOption(user, await screen.findByRole("combobox", { name: "Case activity" }), "FULL");
  await screen.findByText(/HISTORY_PAYLOAD_CANARY/u);
  view.rerender(
    <CaseActivityDisclosure target={target} modes={["SUMMARY"]} onDisclosureRefused={refused} />,
  );
  expect(screen.queryByText(/HISTORY_PAYLOAD_CANARY/u)).toBeNull();
  await waitFor(() => {
    expect(
      fetch.mock.calls.some(
        ([, options]) =>
          typeof options?.body === "string" && options.body.includes('"detail":"SUMMARY"'),
      ),
    ).toBe(true);
  });
  expect(refused).not.toHaveBeenCalled();
});
