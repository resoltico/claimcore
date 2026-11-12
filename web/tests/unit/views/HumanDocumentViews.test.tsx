import { afterEach, expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { render, screen } from "../../support/presentation-test-support";
import { AuthorityListing } from "../../../src/views/AuthorityListing";
import { SummaryBand } from "../../../src/views/worklist/SummaryBand";

const actorId = "00000000-0000-4000-8000-000000000001";
const basis = {
  kind: "SNAPSHOT",
  observation: {
    observedAt: "2026-11-09T12:00:00.000000Z",
    businessDate: "2026-11-09",
    timeZone: "Etc/UTC",
  },
};
const response = (document: unknown) =>
  new Response(
    JSON.stringify({ representation: "HUMAN", outcome: { kind: "SUCCEEDED", document } }),
    { status: 200, headers: { "Content-Type": "application/json" } },
  );
afterEach(() => vi.unstubAllGlobals());

it("renders the supplied person caption and navigates with its typed identity without rebuilding an actor label", async () => {
  const document = {
    screen: {
      regions: [
        {
          role: "RESULTS",
          content: {
            kind: "TABLE",
            columns: [
              {
                key: "actor",
                labelId: "field.profile.displayName.label",
                prominence: "PRIMARY",
                isMoney: false,
              },
            ],
            rows: [
              {
                key: actorId,
                values: [{ kind: "TEXT", value: "Core opaque directory caption Ω" }],
              },
            ],
          },
        },
      ],
    },
    context: { kind: "PEOPLE", actorIds: [actorId], nextCursor: null },
    basis,
    disclosure: "CURRENT",
  };
  vi.stubGlobal("fetch", vi.fn<typeof globalThis.fetch>().mockResolvedValue(response(document)));
  const select = vi.fn();
  render(
    <AuthorityListing
      mayReadGrants
      onSelect={select}
      onDisclosureRefused={vi.fn()}
      onDisclosureRestored={vi.fn()}
    />,
  );
  const link = await screen.findByRole("link", { name: "Core opaque directory caption Ω" });
  await userEvent.click(link);
  expect(select).toHaveBeenCalledWith(actorId);
  expect(screen.queryByText(actorId)).not.toBeInTheDocument();
});

it("renders supplied aggregate precision and opaque totals content from a HUMAN document", async () => {
  const document = {
    screen: {
      regions: [
        {
          role: "TOTALS",
          content: {
            kind: "ENTRIES",
            entries: [
              {
                key: "total",
                labelId: "label.details",
                prominence: "PRIMARY",
                value: {
                  kind: "MONEY_TOTAL",
                  amount: "1999999999999999999.9998",
                  currency: "EUR",
                  displayScale: 4,
                },
              },
              {
                key: "caption",
                labelId: "label.details",
                prominence: "PRIMARY",
                value: { kind: "TEXT", value: "Core opaque totals caption Ψ" },
              },
            ],
          },
        },
      ],
    },
    context: { kind: "EMPTY" },
    basis,
    disclosure: "CURRENT",
  };
  vi.stubGlobal("fetch", vi.fn<typeof globalThis.fetch>().mockResolvedValue(response(document)));
  render(<SummaryBand filter={{ text: "authored filter" }} />);
  await screen.findByText("Core opaque totals caption Ψ");
  expect(screen.getByText("1,999,999,999,999,999,999.9998 EUR")).toBeVisible();
  expect(fetch).toHaveBeenCalledWith(
    "/api/case.summary",
    expect.objectContaining({ body: JSON.stringify({ text: "authored filter" }) }),
  );
});
