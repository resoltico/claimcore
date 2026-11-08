import { expect, it, vi } from "vitest";
import { render, screen } from "../../support/presentation-test-support";
import { RegionView } from "../../../src/ui/regions/RegionView";
it.each([0, 3])(
  "renders the supplied labelled responsibility count %s as a scoped link",
  (count) => {
    const actorId = "10000000-0000-4000-8000-000000000001";
    const href = `/cases?view=ALL&responsibleActor=${actorId}`;
    render(
      <RegionView
        region={{
          role: "ACCOUNT",
          content: {
            kind: "ENTRIES",
            entries: [
              {
                key: "responsibleCases",
                labelId: "label.responsibleCases",
                prominence: "PRIMARY",
                value: { kind: "COUNT", value: count },
              },
            ],
          },
        }}
        countLink={{ href, follow: vi.fn() }}
      />,
    );
    expect(screen.getByRole("link", { name: `Responsible open cases: ${count}` })).toHaveAttribute(
      "href",
      href,
    );
  },
);
it("does not invent a responsibility link without an offered target", () => {
  render(<RegionView region={{ role: "ACCOUNT", content: { kind: "ENTRIES", entries: [] } }} />);
  expect(screen.queryByRole("link")).toBeNull();
});
