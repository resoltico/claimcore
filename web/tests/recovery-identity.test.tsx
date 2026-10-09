import userEvent from "@testing-library/user-event";
import { expect, it } from "vitest";
import { render, screen } from "./presentation-test-support";
import { RecoveryIdentityLookup } from "../src/views/recovery/RecoveryIdentityLookup";
import { mockRecoveryActions } from "./v3-recovery.fixtures";
import { operationId } from "./v3-ui.fixtures";
import type { Notice } from "../src/api/notices";

it("offers independent known-ID inspection and known-ID plus digest export without listing [CC-REC-001]", async () => {
  const user = userEvent.setup();
  const actions = mockRecoveryActions();
  const view = render(<RecoveryIdentityLookup busy={false} actions={actions} message={null} />);
  const inspecting = screen.getByRole("button", { name: "Inspect operation ID" });
  const exporting = screen.getByRole("button", { name: "Export by ID and digest" });
  expect(inspecting).toBeDisabled();
  expect(exporting).toBeDisabled();
  await user.type(screen.getByLabelText("Exact operation ID"), operationId);
  expect(inspecting).not.toBeDisabled();
  expect(exporting).toBeDisabled();
  await user.click(inspecting);
  expect(actions.inspectId).toHaveBeenCalledWith(operationId);
  await user.type(screen.getByLabelText("Exact request SHA-256 digest"), "a".repeat(64));
  await user.click(exporting);
  expect(actions.exportIdentity).toHaveBeenCalledWith(operationId, "a".repeat(64));
  view.rerender(<RecoveryIdentityLookup busy actions={actions} message={null} />);
  expect(inspecting).toBeDisabled();
  expect(exporting).toBeDisabled();
  expect(screen.getByLabelText("Exact operation ID")).toBeDisabled();
  expect(screen.getByLabelText("Exact request SHA-256 digest")).toBeDisabled();
});

it.each([
  ["WEB_INPUT_INVALID_UUID", "Exact operation ID"],
  ["WEB_INPUT_INVALID_DIGEST", "Exact request SHA-256 digest"],
] as const)(
  "focuses and describes %s and clears its stale lookup guidance [CC-WEB-001]",
  async (id, label) => {
    const user = userEvent.setup();
    const actions = mockRecoveryActions();
    const message: Notice = { kind: "diagnostic", diagnostic: { id, parameters: {} } };
    const view = render(
      <RecoveryIdentityLookup busy={false} actions={actions} message={message} />,
    );
    const input = screen.getByLabelText(label);
    expect(input).toHaveFocus();
    expect(input).toHaveAttribute("aria-invalid", "true");
    const description = input.getAttribute("aria-describedby");
    expect(description).not.toBeNull();
    expect(document.getElementById(description!)).toHaveTextContent(/\S/u);
    await user.type(input, "invalid");
    expect(actions.clearMessage).toHaveBeenCalled();
    view.rerender(<RecoveryIdentityLookup busy={false} actions={actions} message={null} />);
    expect(input).not.toHaveAttribute("aria-invalid", "true");
    expect(input).toHaveValue("invalid");
  },
);
