import { requestText } from "../../support/transport-response.fixtures";
import { afterEach, expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { render, screen, waitFor } from "../../support/presentation-test-support";
import { WorklistFilters } from "../../../src/views/worklist/WorklistFilters";
import { PeopleListing } from "../../../src/views/PeopleListing";

afterEach(() => vi.unstubAllGlobals());
it("renders semantic worklist sets as independent Core-labelled checkboxes", async () => {
  const change = vi.fn();
  render(
    <WorklistFilters query={{ lifecycle: ["ACTIVE"], progress: ["DECIDED"] }} onChange={change} />,
  );
  const active = screen.getByRole("checkbox", { name: "Active" });
  expect(active).toBeChecked();
  await userEvent.click(active);
  expect(change).toHaveBeenCalledWith({ lifecycle: [], progress: ["DECIDED"] });
  await userEvent.click(screen.getByRole("checkbox", { name: "Voided" }));
  expect(change).toHaveBeenCalledWith({ lifecycle: ["ACTIVE", "VOIDED"], progress: ["DECIDED"] });
});
it("takes role choices from the semantic people query and sends only authored filters", async () => {
  const fetch = vi.fn<typeof globalThis.fetch>().mockImplementation(() =>
    Promise.resolve(
      new Response(
        JSON.stringify({
          representation: "HUMAN",
          outcome: {
            kind: "SUCCEEDED",
            document: {
              screen: { regions: [] },
              context: { kind: "PEOPLE", actorIds: [], nextCursor: null },
              basis: {
                kind: "SNAPSHOT",
                observation: {
                  observedAt: "2026-11-09T12:00:00.000000Z",
                  businessDate: "2026-11-09",
                  timeZone: "Etc/UTC",
                },
              },
              disclosure: "CURRENT",
            },
          },
        }),
        { status: 200, headers: { "Content-Type": "application/json" } },
      ),
    ),
  );
  vi.stubGlobal("fetch", fetch);
  render(
    <PeopleListing
      mayReadGrants={false}
      onSelect={vi.fn()}
      onDisclosureRefused={vi.fn()}
      onDisclosureRestored={vi.fn()}
    />,
  );
  await userEvent.type(screen.getByRole("searchbox"), "Authored filter Ω");
  await userEvent.click(screen.getByRole("combobox", { name: /role/iu }));
  await userEvent.click(screen.getByRole("option", { name: "Case reader" }));
  await userEvent.click(screen.getByRole("button", { name: "Search" }));
  await waitFor(() => {
    expect(JSON.parse(requestText(fetch.mock.calls.at(-1)?.[1]?.body))).toEqual({
      text: "Authored filter Ω",
      role: "CASE_READER",
      limit: 50,
    });
  });
});
