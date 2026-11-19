import userEvent from "@testing-library/user-event";
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
          notices: [
            {
              messageId: "lifecycle.VOIDED.meaning",
              tone: "WARNING",
              reason: { kind: "TEXT", value: "Duplicate case" },
            },
          ],
        },
      }}
    />,
  );
  expect(screen.getByRole("status")).toHaveTextContent("Duplicate case");
});

it("announces mixed notices in authored order without interrupting keyboard tab order", async () => {
  render(
    <>
      <button type="button">Before notices</button>
      <RegionView
        region={{
          role: "NOTICE",
          content: {
            kind: "NOTICE",
            notices: [
              {
                messageId: "task.people.changeSignInIdentity.meaning",
                tone: "INFO",
                reason: { kind: "TEXT", value: "Routine consequence" },
              },
              {
                messageId: "notice.changesOwnSignInIdentity",
                tone: "WARNING",
                reason: { kind: "TEXT", value: "Explicit warning" },
              },
            ],
          },
        }}
      />
      <button type="button">After notices</button>
    </>,
  );
  const notices = screen.getAllByRole("status");
  expect(notices).toHaveLength(2);
  expect(notices[0]).toHaveAttribute("data-tone", "INFO");
  expect(notices[0]).toHaveTextContent("Routine consequence");
  expect(notices[1]).toHaveAttribute("data-tone", "WARNING");
  expect(notices[1]).toHaveTextContent("Explicit warning");
  const user = userEvent.setup();
  await user.tab();
  expect(screen.getByRole("button", { name: "Before notices" })).toHaveFocus();
  await user.tab();
  expect(screen.getByRole("button", { name: "After notices" })).toHaveFocus();
});
