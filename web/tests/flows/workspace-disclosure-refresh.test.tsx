import { workBasis, actorContextInput } from "../support/inputs";
import userEvent from "@testing-library/user-event";
import { expect, it, vi } from "vitest";
import { act } from "@testing-library/react";
import { render, screen, waitFor } from "../support/presentation-test-support";
import { mockEndpoint } from "../support/endpoint-mocks";
import { corpusCases } from "../support/contract-corpus.fixtures";
import { isResponse } from "../../src/protocol/client";
import { authorityPreview } from "../support/authority-operation.fixtures";
import { current } from "../support/presentation-state.fixtures";
import { useEndpoint } from "../../src/hooks/useEndpoint";
import { navigate } from "../../src/routing/navigation";
import { referencePath } from "../../src/routing/routes";
import { Dashboard } from "../../src/views/Dashboard";
import { service, type ApiResult, type ServiceResponse } from "../../src/api/service";
const outcome = <const T,>(value: T) => ({ kind: "outcome" as const, status: 200, value });
const unavailable = {
  kind: "REJECTED",
  data: { code: "RESOURCE_UNAVAILABLE", action: "NONE_REQUIRED" },
} as const;
const contextFor = (
  views: ReadonlyArray<"CASES" | "AUTHORITY" | "OPERATIONS">,
): ApiResult<ServiceResponse<"context">> => {
  const sample = corpusCases().find((row) => row.id === "context-all-advice");
  if (
    sample === undefined ||
    !isResponse("context", sample.value) ||
    sample.value.outcome.kind !== "SUCCEEDED"
  ) {
    throw new Error("Missing complete context fixture.");
  }
  const source = sample.value.outcome.data;
  const capabilities = source.capabilities.filter((capability) =>
    capability === "MANAGE_AUTHORITY" || capability === "READ_AUTHORITY"
      ? views.includes("AUTHORITY")
      : views.includes("CASES"),
  );
  const offers = source.offers.filter((offer) =>
    views.includes(offer.kind === "OPEN" ? "CASES" : "AUTHORITY"),
  );
  return outcome({
    outcome: {
      kind: "SUCCEEDED",
      data: actorContextInput({ ...source, views, capabilities, offers }),
    },
  });
};
const deferred = <T,>() => {
  let finish: (value: T) => void = () => undefined;
  const promise = new Promise<T>((resolve) => {
    finish = resolve;
  });
  return { promise, finish };
};
const beginUnknownAuthority = async (user: ReturnType<typeof userEvent.setup>) => {
  await user.click(await screen.findByRole("link", { name: "Access" }));
  await screen.findByText("PREVIOUS_ACTOR_CANARY");
  await waitFor(() => {
    expect(document.querySelector('[name="target.subject"]')).not.toBeNull();
  });
  for (const [name, value] of [
    ["target.issuer", "https://issuer.example/"],
    ["target.subject", "pending-target"],
  ]) {
    await user.type(document.querySelector<HTMLInputElement>(`[name="${name}"]`)!, value!);
  }
  await user.click(screen.getByRole("button", { name: "Review changes" }));
  await user.click(await screen.findByRole("checkbox"));
  await user.click(screen.getByRole("button", { name: "Record changes" }));
  await screen.findByRole("button", { name: "Send again" });
};
const authorityReads = () => {
  mockEndpoint("case.list").mockResolvedValue(
    outcome({
      outcome: {
        kind: "SUCCEEDED",
        data: { basis: workBasis, offers: [], items: [], nextCursor: null },
      },
    }),
  );
  const actor = {
    actorId: "00000000-0000-4000-8000-000000000001",
    principal: {
      kind: "HUMAN" as const,
      issuer: "https://issuer.example/",
      subject: "PREVIOUS_ACTOR_CANARY",
    },
    enabled: true,
  };
  mockEndpoint("authority.list")
    .mockResolvedValueOnce(
      outcome({ outcome: { kind: "SUCCEEDED", data: { items: [actor], nextCursor: null } } }),
    )
    .mockResolvedValue(outcome({ outcome: unavailable }));
};
it("retains the exact uncertain authority request when delayed context removes its navigation view", async () => {
  const delayed = deferred<ApiResult<ServiceResponse<"context">>>();
  const metadata = mockEndpoint("context")
    .mockResolvedValueOnce(contextFor(["CASES", "AUTHORITY", "OPERATIONS"]))
    .mockReturnValue(delayed.promise);
  authorityReads();
  authorityPreview();
  const execute = vi
    .spyOn(service, "execute")
    .mockResolvedValueOnce({
      kind: "deliveryFailure",
      notice: { kind: "local", reason: "unreachable" },
      dispatched: true,
    })
    .mockImplementation((prepared) =>
      Promise.resolve(outcome({ operationId: prepared.input.operationId, outcome: unavailable })),
    );
  const user = userEvent.setup();
  render(<Dashboard />);
  await beginUnknownAuthority(user);
  const original = execute.mock.calls[0]![0].bytes;
  await user.click(screen.getByRole("button", { name: "Send again" }));
  await waitFor(() => {
    expect(metadata.mock.calls.length).toBeGreaterThan(1);
  });
  await act(async () => {
    delayed.finish(contextFor(["CASES", "OPERATIONS"]));
    await delayed.promise;
  });
  expect(screen.getByRole("heading", { name: "Access", level: 1 })).toBeVisible();
  expect(screen.queryByRole("link", { name: "Access" })).toBeNull();
  expect(document.body).not.toHaveTextContent("PREVIOUS_ACTOR_CANARY");
  await user.click(screen.getByRole("button", { name: "Send again" }));
  expect(execute.mock.calls.map(([prepared]) => prepared.bytes)).toEqual([
    original,
    original,
    original,
  ]);
});
const RefreshDisclosure = () => {
  const context = useEndpoint("context", {});
  return (
    <button
      type="button"
      onClick={() => {
        context.reload();
      }}
    >
      Refresh disclosure
    </button>
  );
};
it("keeps the requested case route while an actual read refusal removes every current fact", async () => {
  mockEndpoint("context")
    .mockResolvedValueOnce(contextFor(["CASES", "AUTHORITY", "OPERATIONS"]))
    .mockResolvedValueOnce(contextFor(["OPERATIONS"]))
    .mockResolvedValue(contextFor(["CASES", "AUTHORITY", "OPERATIONS"]));
  const visible = {
    ...current,
    state: {
      ...current.state,
      fields: {
        ...current.state.fields,
        registration: { ...current.state.fields.registration, claimantName: "CURRENT_FACT_CANARY" },
      },
    },
  };
  const caseRead = mockEndpoint("case.get")
    .mockResolvedValueOnce(outcome({ outcome: { kind: "SUCCEEDED", data: visible } }))
    .mockResolvedValue(outcome({ outcome: unavailable }));
  mockEndpoint("case.history").mockResolvedValue(
    outcome({
      outcome: {
        kind: "SUCCEEDED",
        data: {
          caseId: current.caseId,
          caseReference: current.caseReference,
          entries: [],
          nextCursor: null,
        },
      },
    }),
  );
  navigate(referencePath(current.caseReference));
  const user = userEvent.setup();
  render(
    <>
      <Dashboard />
      <RefreshDisclosure />
    </>,
  );
  await waitFor(() => {
    expect(document.body).toHaveTextContent("CURRENT_FACT_CANARY");
  });
  await user.click(screen.getByRole("button", { name: "Refresh disclosure" }));
  await waitFor(() => {
    expect(screen.queryByRole("link", { name: "Cases" })).toBeNull();
  });
  expect(document.body).not.toHaveTextContent("CURRENT_FACT_CANARY");
  expect(caseRead).toHaveBeenCalledOnce();
  expect(screen.getByRole("heading", { name: "Case detail", level: 1 })).toBeVisible();
  await user.click(screen.getByRole("button", { name: "Refresh disclosure" }));
  await waitFor(() => {
    expect(caseRead).toHaveBeenCalledTimes(2);
    expect(screen.getByRole("alert")).toBeVisible();
  });
  expect(screen.getByRole("heading", { name: "Case detail", level: 1 })).toBeVisible();
  expect(document.body).not.toHaveTextContent("CURRENT_FACT_CANARY");
  expect(document.querySelector(".case-fields")).toBeNull();
});
