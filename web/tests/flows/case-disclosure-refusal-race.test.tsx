import { caseViewInput } from "../support/inputs";
import { mockEndpoint } from "../support/endpoint-mocks";
import { caseAdvice } from "../support/case-advice.fixtures";
import { act } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { expect, it, vi } from "vitest";
import type { ApiResult, ServiceResponse } from "../../src/api/service";
import { CaseDetail } from "../../src/views/CaseDetail";
import { render, screen, waitFor } from "../support/presentation-test-support";
import { definition, fields, unavailable } from "../support/service-ui.fixtures";
const pending = <T,>() => {
  let release!: (result: T) => void;
  const promise = new Promise<T>((resolve) => {
    release = resolve;
  });
  return {
    promise,
    release,
  };
};
const current: ApiResult<ServiceResponse<"case.get">> = {
  kind: "outcome",
  status: 200,
  value: {
    outcome: {
      kind: "SUCCEEDED",
      data: caseViewInput({
        caseId: "20000000-0000-4000-8000-000000000001",
        caseReference: fields.caseReference,
        revision: 1,
        advice: {
          ...caseAdvice([]),
          history: ["SUMMARY", "FULL"],
        },
        state: {
          kind: "ACTIVE",
          fields,
        },
      }),
    },
  },
};
const refused: ApiResult<ServiceResponse<"case.history">> = {
  kind: "outcome",
  status: 200,
  value: {
    outcome: {
      kind: "REJECTED",
      data: unavailable,
    },
  },
};
const empty: ApiResult<ServiceResponse<"case.history">> = {
  kind: "outcome",
  status: 200,
  value: {
    outcome: {
      kind: "SUCCEEDED",
      data: {
        caseId: "20000000-0000-4000-8000-000000000001",
        caseReference: fields.caseReference,
        entries: [],
        nextCursor: null,
      },
    },
  },
};
it("invalidates the pending current read synchronously on a second history refusal", async () => {
  const earlier = pending<typeof current>();
  const latest = pending<typeof current>();
  const history = pending<typeof refused>();
  const get = mockEndpoint("case.get")
    .mockResolvedValueOnce(current)
    .mockReturnValueOnce(earlier.promise)
    .mockReturnValueOnce(latest.promise);
  mockEndpoint("case.history")
    .mockResolvedValueOnce(empty)
    .mockResolvedValueOnce(refused)
    .mockReturnValueOnce(history.promise);
  const user = userEvent.setup();
  render(
    <CaseDetail
      caseReference={fields.caseReference}
      reloadSignal={0}
      definition={definition.catalog}
      onBack={vi.fn()}
      onAct={vi.fn()}
    />,
  );
  const picker = await screen.findByRole("combobox", {
    name: "History detail",
  });
  await user.selectOptions(picker, "FULL");
  await waitFor(() => {
    expect(get).toHaveBeenCalledTimes(2);
  });
  await user.selectOptions(picker, "SUMMARY");
  await act(async () => {
    history.release(refused);
    await Promise.resolve();
    earlier.release(current);
  });
  await waitFor(() => {
    expect(get).toHaveBeenCalledTimes(3);
  });
  expect(screen.queryByText(fields.registration.claimantName)).toBeNull();
  expect(screen.getByRole("alert")).toHaveTextContent("The requested resource is unavailable.");
  await act(async () => {
    latest.release(current);
    await latest.promise;
  });
  expect((await screen.findAllByText(fields.registration.claimantName))[0]).toBeVisible();
  expect(get).toHaveBeenCalledTimes(3);
});
