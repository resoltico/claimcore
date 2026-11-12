import { humanReadReply } from "../support/human-read-reply.fixtures";
import { useState } from "react";
import { expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { render, screen, waitFor } from "../support/presentation-test-support";
import { mockEndpoint } from "../support/endpoint-mocks";
import { CaseDetail } from "../../src/views/CaseDetail";
import { OperationEditor } from "../../src/views/OperationEditor";
import { service, type CurrentCase } from "../../src/api/service";
import { caseDocumentInput, closeCaseScreen } from "../support/case-document.fixtures";
import type { Screen } from "@contracts/client";
import { definition } from "../support/service-ui.fixtures";
const unavailable = {
  kind: "REFUSED",
  advice: {
    message: { messageId: "diagnostic.RESOURCE_UNAVAILABLE", arguments: [] },
    action: "ASK_FOR_ACCESS",
  },
} as const;
const disclosureScreen = (caption: string): Screen => ({
  regions: [
    ...closeCaseScreen.regions,
    {
      role: "FACTS",
      content: {
        kind: "ENTRIES",
        entries: [
          {
            key: "private",
            labelId: "field.claimantName.label",
            prominence: "PRIMARY",
            value: { kind: "PARTY", value: caption },
          },
        ],
      },
    },
  ],
});
const before = caseDocumentInput(
  { tasks: ["CASE:CLOSE"], history: "NONE" },
  disclosureScreen("PRIOR_CASE_DISCLOSURE_CANARY"),
);
const after = caseDocumentInput(
  { revision: 2, tasks: ["CASE:CLOSE"], history: "NONE" },
  disclosureScreen("POSITIVE_CURRENT_DISCLOSURE"),
);
const Workspace = () => {
  const [selected, setSelected] = useState<CurrentCase | null>(null);
  return selected === null ? (
    <CaseDetail
      target={{ kind: "ID", caseId: before.context.caseId }}
      definition={definition.catalog}
      reloadSignal={0}
      onBack={vi.fn()}
      onAct={(value) => {
        setSelected(value);
      }}
    />
  ) : (
    <OperationEditor
      definition={definition}
      current={selected}
      initialAct="CLOSE"
      onClose={() => {
        setSelected(null);
      }}
      onCommitted={vi.fn()}
      onMutationLockChange={vi.fn()}
    />
  );
};
it.each([false, true])(
  "a mutation refusal invalidates cached case facts before departure; fresh read=%s",
  async (readable) => {
    const read = mockEndpoint("case.get")
      .mockResolvedValueOnce({
        kind: "outcome",
        status: 200,
        value: {
          ...humanReadReply("case.get", before),
        },
      })
      .mockResolvedValue(
        readable
          ? {
              kind: "outcome",
              status: 200,
              value: {
                ...humanReadReply("case.get", after),
              },
            }
          : {
              kind: "outcome",
              status: 200,
              value: {
                representation: "HUMAN",
                outcome: { kind: "REJECTED", rejection: unavailable },
              },
            },
      );
    const preview = vi.spyOn(service, "preview").mockImplementation((prepared) =>
      Promise.resolve({
        kind: "outcome",
        status: 200,
        value: {
          representation: "HUMAN",
          operationId: prepared.input.operationId,
          outcome: { kind: "REJECTED", rejection: unavailable },
        },
      }),
    );
    const execute = vi.spyOn(service, "execute");
    const user = userEvent.setup();
    render(<Workspace />);
    await user.click(await screen.findByRole("button", { name: /^Close the case:/u }));
    await user.click(screen.getByRole("button", { name: "Review changes" }));
    await screen.findByRole("alert");
    expect(document.body).not.toHaveTextContent("PRIOR_CASE_DISCLOSURE_CANARY");
    await user.click(screen.getAllByRole("button", { name: "Close" })[0]!);
    await user.click(await screen.findByRole("button", { name: "Discard and leave" }));
    await waitFor(() => {
      expect(read).toHaveBeenCalledTimes(2);
    });
    expect(document.body).not.toHaveTextContent("PRIOR_CASE_DISCLOSURE_CANARY");
    if (readable) {
      expect(
        await screen.findByRole("heading", {
          name: (_name, element) => element.id === "case-detail-title",
        }),
      ).toBeVisible();
    } else {
      expect(await screen.findByRole("alert")).toBeVisible();
    }
    expect(preview).toHaveBeenCalledTimes(1);
    expect(execute).not.toHaveBeenCalled();
  },
);
