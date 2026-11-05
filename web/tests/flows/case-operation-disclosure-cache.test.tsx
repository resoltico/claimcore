import { useState } from "react";
import { expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { render, screen, waitFor } from "../support/presentation-test-support";
import { mockEndpoint } from "../support/endpoint-mocks";
import { CaseDetail } from "../../src/views/CaseDetail";
import { OperationEditor } from "../../src/views/OperationEditor";
import { service, type CurrentCase } from "../../src/api/service";
import { caseViewInput } from "../support/inputs";
import { caseAdvice } from "../support/case-advice.fixtures";
import { definition, fields, unavailable } from "../support/service-ui.fixtures";
const before = caseViewInput({
  caseId: "20000000-0000-4000-8000-000000000001",
  caseReference: "CASE-1",
  revision: 1,
  advice: { ...caseAdvice(["CLOSE"]), history: [] },
  state: {
    kind: "ACTIVE",
    fields: {
      ...fields,
      registration: { ...fields.registration, claimantName: "PRIOR_CASE_DISCLOSURE_CANARY" },
    },
  },
});
const after = {
  ...before,
  revision: 2,
  state: {
    ...before.state,
    fields: {
      ...before.state.fields,
      registration: {
        ...before.state.fields.registration,
        claimantName: "POSITIVE_CURRENT_DISCLOSURE",
      },
    },
  },
};
const Workspace = () => {
  const [selected, setSelected] = useState<CurrentCase | null>(null);
  return selected === null ? (
    <CaseDetail
      target={{ kind: "ID", caseId: before.caseId }}
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
        value: { outcome: { kind: "SUCCEEDED", data: before } },
      })
      .mockResolvedValue(
        readable
          ? { kind: "outcome", status: 200, value: { outcome: { kind: "SUCCEEDED", data: after } } }
          : {
              kind: "outcome",
              status: 200,
              value: { outcome: { kind: "REJECTED", data: unavailable } },
            },
      );
    const preview = vi
      .spyOn(service, "preview")
      .mockImplementation((prepared) =>
        Promise.resolve({
          kind: "outcome",
          status: 200,
          value: {
            operationId: prepared.input.operationId,
            outcome: { kind: "REJECTED", data: unavailable },
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
        await screen.findByRole("heading", { name: "POSITIVE_CURRENT_DISCLOSURE" }),
      ).toBeVisible();
    } else {
      expect(await screen.findByRole("alert")).toBeVisible();
    }
    expect(preview).toHaveBeenCalledTimes(1);
    expect(execute).not.toHaveBeenCalled();
  },
);
