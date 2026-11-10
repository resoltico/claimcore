import { DisclosureRefreshDriver } from "../support/people-drawer.fixtures";
import { humanReadResponse } from "../support/human-read-response.fixtures";
import { timelinePage } from "../support/timeline.fixtures";
import { caseViewInput } from "../support/inputs";
import { mockEndpoint } from "../support/endpoint-mocks";
import { caseAdvice } from "../support/case-advice.fixtures";
import { act } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { expect, it, vi } from "vitest";
import type { ApiResult, ServiceResponse } from "../../src/api/service";
import { CaseDetail } from "../../src/views/CaseDetail";
import { render, screen, waitFor } from "../support/presentation-test-support";
import { definition, fields } from "../support/service-ui.fixtures";
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
    ...humanReadResponse(
      "case.get",
      "SUCCEEDED",
      caseViewInput({
        caseId: "20000000-0000-4000-8000-000000000001",
        caseReference: fields.caseReference,
        revision: 1,
        advice: {
          ...caseAdvice([]),
          history: "FULL",
        },
        state: {
          kind: "ACTIVE",
          fields,
        },
      }),
    ),
  },
};
const refused: ApiResult<ServiceResponse<"case.timeline">> = {
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
};
const empty: ApiResult<ServiceResponse<"case.timeline">> = {
  kind: "outcome",
  status: 200,
  value: {
    ...humanReadResponse("case.timeline", "SUCCEEDED", timelinePage()),
  },
};
it("keeps a history-triggered current read obsolete after a later cache invalidation", async () => {
  const earlier = pending<typeof current>();
  const latest = pending<typeof current>();
  const get = mockEndpoint("case.get")
    .mockResolvedValueOnce(current)
    .mockReturnValueOnce(earlier.promise)
    .mockReturnValueOnce(latest.promise);
  mockEndpoint("case.timeline")
    .mockResolvedValueOnce(empty)
    .mockResolvedValueOnce(refused)
    .mockResolvedValue(empty);
  const user = userEvent.setup();
  let invalidate: (() => void) | undefined;
  render(
    <>
      <DisclosureRefreshDriver
        capture={(next) => {
          invalidate = next;
        }}
      />
      <CaseDetail
        target={{ kind: "REFERENCE", caseReference: fields.caseReference }}
        reloadSignal={0}
        definition={definition.catalog}
        onBack={vi.fn()}
        onAct={vi.fn()}
      />
    </>,
  );
  await user.click(await screen.findByRole("tab", { name: "Case facts" }));
  await waitFor(() => {
    expect(get).toHaveBeenCalledTimes(2);
  });
  expect(screen.queryByRole("tab")).toBeNull();
  expect(screen.queryByText(fields.registration.claimantName)).toBeNull();
  act(() => {
    invalidate!();
  });
  await waitFor(() => {
    expect(get).toHaveBeenCalledTimes(3);
  });
  await act(async () => {
    earlier.release(current);
    await earlier.promise;
  });
  expect(screen.queryByText(fields.registration.claimantName)).toBeNull();
  await act(async () => {
    latest.release(current);
    await latest.promise;
  });
  expect((await screen.findAllByText(fields.registration.claimantName))[0]).toBeVisible();
  expect(get).toHaveBeenCalledTimes(3);
});
