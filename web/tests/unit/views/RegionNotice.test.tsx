import { it, expect } from "vitest";
import { render, screen } from "../../support/presentation-test-support";
import { RegionView } from "../../../src/ui/regions/RegionView";
it("renders the supplied void notice while the tracker remains available separately", () => {
  render(
    <RegionView
      region={{
        role: "NOTICE",
        content: {
          kind: "NOTICE",
          messageId: "lifecycle.VOIDED.meaning",
          tone: "WARNING",
          reason: { kind: "TEXT", value: "Duplicate case" },
        },
      }}
    />,
  );
  expect(screen.getByRole("status")).toHaveTextContent("Duplicate case");
});
