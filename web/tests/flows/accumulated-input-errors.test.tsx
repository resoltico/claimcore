import { endpointResponse } from "../support/transport-response.fixtures";
import { expect, it, vi, afterEach } from "vitest";
import userEvent from "@testing-library/user-event";
import { render, screen, getButton, waitFor } from "../support/presentation-test-support";
import { OperationEditor } from "../../src/views/OperationEditor";
import { definition } from "../support/service-ui.fixtures";
import { fillRegistrationInputs } from "../support/registration-inputs.fixtures";
import type { HumanInputIssue } from "@contracts/client";

afterEach(() => vi.unstubAllGlobals());

it.each([1, 2])(
  "shows every one of %s business input issues and focuses the first",
  async (count) => {
    const allIssues: HumanInputIssue[] = [
      {
        input: "field:incidentDate",
        row: null,
        related: null,
        message: { messageId: "diagnostic.TEXT_REQUIRED", arguments: [] },
      },
      {
        input: "field:claimantName",
        row: null,
        related: null,
        message: { messageId: "diagnostic.TEXT_REQUIRED", arguments: [] },
      },
    ];
    const issues = allIssues.slice(0, count);
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(
        endpointResponse("operation.preview", {
          representation: "HUMAN",
          outcome: {
            kind: "REJECTED",
            rejection: {
              kind: "INPUT",
              advice: {
                message: {
                  messageId: "diagnostic.INVALID_INPUT",
                  arguments: [{ key: "issueCount", value: { kind: "COUNT", value: count } }],
                },
                action: "CORRECT_INPUT",
              },
              issues,
            },
          },
        }),
      ),
    );
    render(
      <OperationEditor
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
    expect(summary).toHaveTextContent(`Fields to check: ${count}.`);
    const first = screen.getByRole("group", { name: "Incident date" });
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
      expect(summary).toHaveFocus();
    });
  },
);
