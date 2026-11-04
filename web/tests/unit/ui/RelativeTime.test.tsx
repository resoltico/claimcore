import { expect, it, vi } from "vitest";
import { render } from "../../support/presentation-test-support";
import { RelativeTime } from "../../../src/ui/RelativeTime";
it("uses the core observation time instead of the local clock and preserves the exact instant", () => {
  vi.spyOn(Date, "now").mockReturnValue(new Date("2030-01-01T00:00:00Z").getTime());
  const view = render(
    <RelativeTime value="2026-11-04T12:01:00.000000Z" observedAt="2026-11-04T12:03:00.000000Z" />,
  );
  expect(view.container.querySelector("time")).toHaveTextContent("2 minutes ago");
  expect(view.container.querySelector("time")).toHaveAttribute(
    "datetime",
    "2026-11-04T12:01:00.000000Z",
  );
});
it("shows future instants without silently changing their direction", () => {
  const view = render(
    <RelativeTime value="2026-11-05T12:00:00.000000Z" observedAt="2026-11-04T12:00:00.000000Z" />,
  );
  expect(view.container).toHaveTextContent("tomorrow");
});
