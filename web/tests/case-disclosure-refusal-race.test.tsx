import { caseOffers } from "./case-advice.fixtures";
import { act } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { expect, it, vi } from "vitest";
import { service, type ApiResult, type ServiceResponse } from "../src/api/service";
import { CaseDetail } from "../src/views/CaseDetail";
import { render, screen, waitFor } from "./presentation-test-support";
import { definition, fields, unavailable } from "./service-ui.fixtures";

const pending = <T,>() => {
  let release!: (result: T) => void;
  const promise = new Promise<T>((resolve) => {
    release = resolve;
  });
  return { promise, release };
};
const current: ApiResult<ServiceResponse<"case.get">> = {
  kind: "outcome",
  status: 200,
  value: {
    outcome: {
      tag: "SUCCEEDED",
      data: {
        caseReference: fields.caseReference,
        revision: 1,
        advice: { acts: caseOffers([]), history: ["SUMMARY", "FULL"] },

        state: { tag: "LIVE", disposition: "ACTIVE", fields },
      },
    },
  },
};
const refused: ApiResult<ServiceResponse<"case.history">> = {
  kind: "outcome",
  status: 200,
  value: { outcome: { tag: "REJECTED", data: unavailable } },
};
const empty: ApiResult<ServiceResponse<"case.history">> = {
  kind: "outcome",
  status: 200,
  value: {
    outcome: { tag: "SUCCEEDED", data: { entries: [], nextCursor: null } },
  },
};

it("invalidates the pending current read synchronously on a second history refusal", async () => {
  const earlier = pending<typeof current>();
  const latest = pending<typeof current>();
  const history = pending<typeof refused>();
  const get = vi
    .spyOn(service, "get")
    .mockResolvedValueOnce(current)
    .mockReturnValueOnce(earlier.promise)
    .mockReturnValueOnce(latest.promise);
  vi.spyOn(service, "history")
    .mockResolvedValueOnce(empty)
    .mockResolvedValueOnce(refused)
    .mockReturnValueOnce(history.promise);
  const user = userEvent.setup();
  render(
    <CaseDetail
      token="token"
      caseReference={fields.caseReference}
      reloadSignal={0}
      definition={definition.descriptors}
      onBack={vi.fn()}
      onCommand={vi.fn()}
    />,
  );
  const picker = await screen.findByRole("combobox", { name: "History detail" });
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
  expect(screen.queryByText(fields.claimantName)).toBeNull();
  expect(screen.getByRole("alert")).toHaveTextContent("The requested resource is unavailable.");
  await act(async () => {
    latest.release(current);
    await latest.promise;
  });
  expect(await screen.findByText(fields.claimantName)).toBeVisible();
  expect(get).toHaveBeenCalledTimes(3);
});
