import { expect, it, vi } from "vitest";
import { act } from "@testing-library/react";
import { CaseDetail } from "../../src/features/case/CaseDetail";
import { render, screen, waitFor } from "../support/presentation-test-support";
import { definition } from "../support/service-ui.fixtures";
import { caseDocumentInput } from "../support/case-document.fixtures";
import { humanReadReply } from "../support/human-read-reply.fixtures";
import { mockEndpoint } from "../support/endpoint-mocks";
import { navigate } from "../../src/routing/navigation";

it.each([
  { kind: "missing", reminderId: "00000000-0000-4000-8000-000000000099" },
  { kind: "wrong-parent", reminderId: "00000000-0000-4000-8000-000000000098" },
])(
  "bounds $kind exact-reminder refusal through the real parent loading/remount lifecycle",
  async ({ reminderId }) => {
    navigate(`/cases/reference/CC-55KW-SSJ4-8HMS-G1BH-73/reminders/${reminderId}`, true);
    let observations = 0;
    const get = mockEndpoint("case.get").mockImplementation(async () => {
      await new Promise((resolve) => {
        setTimeout(resolve, 0);
      });
      observations += 1;
      const document = caseDocumentInput({ history: null });
      return {
        kind: "outcome",
        status: 200,
        value: humanReadReply("case.get", {
          ...document,
          basis: {
            kind: "SNAPSHOT",
            observation: {
              ...document.basis.observation,
              observedAt: new Date(Date.UTC(2026, 10, 9, 12, 0, 0, observations)).toISOString(),
            },
          },
        }),
      };
    });
    const reminder = mockEndpoint("reminder.get").mockResolvedValue({
      kind: "outcome",
      status: 200,
      value: {
        representation: "HUMAN",
        outcome: {
          kind: "REJECTED",
          rejection: {
            kind: "REFUSED",
            advice: {
              message: { messageId: "diagnostic.RESOURCE_UNAVAILABLE", arguments: [] },
              action: "ASK_FOR_ACCESS",
            },
          },
        },
      },
    });
    render(
      <CaseDetail
        target={{ kind: "REFERENCE", caseReference: "CC-55KW-SSJ4-8HMS-G1BH-73" }}
        reloadSignal={0}
        definition={definition.catalog}
        onBack={vi.fn()}
        onAct={vi.fn()}
      />,
    );
    await waitFor(() => {
      expect(get).toHaveBeenCalledTimes(2);
      expect(reminder).toHaveBeenCalledTimes(2);
    });
    // Let cache effects and the parent's loading/success cycle settle; a remount loop makes extra reads here.
    await act(async () => {
      await new Promise((resolve) => {
        setTimeout(resolve, 100);
      });
    });
    expect(get).toHaveBeenCalledTimes(2);
    expect(reminder).toHaveBeenCalledTimes(2);
    expect(screen.queryByRole("button", { name: /^Reopen reminder/u })).toBeNull();
  },
);
