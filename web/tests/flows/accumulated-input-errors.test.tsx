import { expect, it, vi, afterEach } from "vitest";
import userEvent from "@testing-library/user-event";
import { render, screen, getButton, waitFor } from "../support/presentation-test-support";
import { OperationEditor } from "../../src/views/OperationEditor";
import { definition, response } from "../support/service-ui.fixtures";
import { fillRegistrationInputs } from "../support/registration-inputs.fixtures";
import type { InputIssue } from "@contracts/client";

afterEach(() => vi.unstubAllGlobals());

it.each([1, 2])(
  "shows every one of %s business input issues and focuses the first",
  async (count) => {
    const allIssues: InputIssue[] = [
      { field: "incidentDate", code: "TEXT_REQUIRED" },
      { field: "claimantName", code: "TEXT_REQUIRED" },
    ];
    const issues = allIssues.slice(0, count);
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(
        response("operation.preview", "REJECTED", {
          code: "INVALID_INPUT",
          action: "CORRECT_INPUT",
          issues,
        }),
      ),
    );
    render(
      <OperationEditor
        token="synthetic-token"
        definition={definition}
        current={null}
        initialAct="OPEN"
        onClose={vi.fn()}
        onCommitted={vi.fn()}
        onMutationLockChange={vi.fn()}
      />,
    );
    fillRegistrationInputs();
    await userEvent.setup().click(getButton("Review changes"));
    const summary = await screen.findByRole("alert");
    expect(summary).toHaveTextContent(`Input problems: ${count}.`);
    const first = screen.getByLabelText("Incident date", { exact: true });
    expect(first).toHaveAttribute("aria-invalid", "true");
    expect(first).toHaveAccessibleDescription(/A non-blank value is required/u);
    const claimant = screen.getByLabelText("Claimant name", { exact: true });
    if (count === 2) {
      expect(claimant).toHaveAttribute("aria-invalid", "true");
      expect(claimant).toHaveAccessibleDescription(/A non-blank value is required/u);
    } else {
      expect(claimant).not.toHaveAttribute("aria-invalid");
    }
    await waitFor(() => {
      expect(first).toHaveFocus();
    });
  },
);
