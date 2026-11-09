import { expect, it } from "vitest";
import { ScreenView } from "../../../src/ui/ScreenView";
import { render, screen } from "../../support/presentation-test-support";

it("renders supplied RTL narrative, absent values and isolated exact money", () => {
  const view = render(
    <ScreenView
      view={{
        regions: (
          [
            {
              titleId: "label.recordedFacts",
              entries: [
                {
                  key: "future-narrative",
                  labelId: "field.claimantName.label",
                  prominence: "PRIMARY",
                  value: { kind: "NARRATIVE", value: "العربية\n<script>literal</script>\u202E" },
                },
                {
                  key: "future-money",
                  labelId: "field.claimedAmount.label",
                  prominence: "SECONDARY",
                  value: {
                    kind: "MONEY",
                    amount: "123456789012345678.0001",
                    currency: "EUR",
                    displayScale: 4,
                  },
                },
                {
                  key: "future-absent",
                  labelId: "field.paymentDate.label",
                  prominence: "DETAIL",
                  value: { kind: "ABSENT", reasonId: "label.notRecorded" },
                },
              ],
            },
          ] as const
        ).map((part) => ({
          role: "FACTS" as const,
          titleId: part.titleId,
          content: { kind: "ENTRIES" as const, entries: part.entries },
        })),
      }}
    />,
  );
  expect(screen.getByText("Not recorded")).toBeVisible();
  expect(view.container.querySelector('bdi[dir="auto"]')).toHaveTextContent("العربية");
  expect(view.container.querySelector('bdi[dir="ltr"]')).toHaveTextContent("0001 EUR");
  expect(view.container.querySelector("script")).toBeNull();
  expect(screen.getByText("Contains U+202E")).toBeVisible();
});
it("gives simultaneously displayed sections distinct accessible heading IDs", () => {
  const section = {
    titleId: "label.recordedFacts",
    entries: [
      {
        key: "future",
        labelId: "field.claimantName.label",
        prominence: "PRIMARY" as const,
        value: { kind: "TEXT" as const, value: "Provided" },
      },
    ],
  };
  render(
    <>
      <ScreenView
        view={{
          regions: ([section] as const).map((part) => ({
            role: "FACTS" as const,
            titleId: part.titleId,
            content: { kind: "ENTRIES" as const, entries: part.entries },
          })),
        }}
      />
      <ScreenView
        view={{
          regions: ([section] as const).map((part) => ({
            role: "FACTS" as const,
            titleId: part.titleId,
            content: { kind: "ENTRIES" as const, entries: part.entries },
          })),
        }}
      />
    </>,
  );
  const ids = screen
    .getAllByRole("heading", { name: "Recorded facts" })
    .map((element) => element.id);
  expect(new Set(ids).size).toBe(2);
  for (const id of ids) {
    expect(document.getElementById(id ?? "")).toHaveTextContent("Recorded facts");
  }
});
