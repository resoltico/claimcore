import { it, expect } from "vitest";
import { render, screen } from "./presentation-test-support";
import { LifecycleStatus } from "../src/views/LifecycleStatus";
import { fields } from "./service-ui.fixtures";
it("renders active voided and erased states from the accepted case view", () => {
  const { rerender } = render(
    <LifecycleStatus
      value={{
        state: "LIVE",
        disposition: "ACTIVE",
        case: { fields, revision: "1" },
        availableCommands: [],
      }}
    />,
  );
  expect(screen.getByRole("status")).toHaveTextContent("Active");
  rerender(
    <LifecycleStatus
      value={{
        state: "LIVE",
        disposition: "VOIDED",
        case: { fields, revision: "2" },
        availableCommands: [],
      }}
    />,
  );
  expect(screen.getByRole("status")).toHaveTextContent("Voided");
  rerender(<LifecycleStatus value={{ state: "ERASED", caseReference: "CASE-1", revision: "3" }} />);
  expect(screen.getByRole("status")).toHaveTextContent("Erased");
});
