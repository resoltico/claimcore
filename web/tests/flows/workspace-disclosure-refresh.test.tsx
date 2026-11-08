import { presentedOutcome } from "../support/read-view.fixtures";
import {
  openPersonEditor,
  chooseAdmissionRole,
  DisclosureRefreshDriver,
} from "../support/people-drawer.fixtures";
import { reviewConfirm, waitForReviewConfirm } from "../support/review-controls";
import { timelinePage } from "../support/timeline.fixtures";
import { stubViewCounts } from "../support/dashboard-context.fixtures";
import { caseViewInput, workBasis, actorContextInput } from "../support/inputs";
import userEvent from "@testing-library/user-event";
import { beforeEach, expect, it, vi } from "vitest";
import { act } from "@testing-library/react";
import { render, screen, waitFor } from "../support/presentation-test-support";
import { mockEndpoint } from "../support/endpoint-mocks";
import { wireReply } from "../support/reply.fixtures";
import { authorityPreview } from "../support/authority-operation.fixtures";
import { current } from "../support/presentation-state.fixtures";
import { useEndpoint } from "../../src/hooks/useEndpoint";
import { navigate } from "../../src/routing/navigation";
import { referencePath } from "../../src/routing/routes";
import { Dashboard } from "../../src/views/Dashboard";
import { service, type ApiResult, type ServiceResponse } from "../../src/api/service";
beforeEach(() => {
  stubViewCounts();
});
const outcome = <const T,>(value: T) => ({ kind: "outcome" as const, status: 200, value });
const unavailable = {
  kind: "REJECTED",
  data: { code: "RESOURCE_UNAVAILABLE", action: "ASK_FOR_ACCESS" },
} as const;
const contextFor = (
  views: ReadonlyArray<"CASES" | "AUTHORITY" | "OPERATIONS">,
): ApiResult<ServiceResponse<"context">> => {
  const sample = wireReply("context");
  if (sample.outcome.kind !== "SUCCEEDED" || sample.outcome.data.requester.kind !== "ACTIVE") {
    throw new Error("Active requester builder required.");
  }
  const source = sample.outcome.data.requester;
  const capabilities = source.capabilities.filter((capability) =>
    capability === "MANAGE_AUTHORITY" || capability === "READ_AUTHORITY"
      ? views.includes("AUTHORITY")
      : views.includes("CASES"),
  );
  const offers = [
    ...source.offers.filter((offer) =>
      views.includes(offer.kind === "OPEN" ? "CASES" : "AUTHORITY"),
    ),
    // This synthetic owner snapshot explicitly offers the current new-person entry.
    ...(views.includes("AUTHORITY")
      ? [{ kind: "AUTHORITY_ACT" as const, act: "ADMIT" as const }]
      : []),
  ];
  return outcome({
    outcome: presentedOutcome("context", "SUCCEEDED", {
      requester: actorContextInput({ ...source, views, capabilities, offers }),
    }),
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
  await user.click(await screen.findByRole("link", { name: "People & access" }));
  await screen.findByText("PREVIOUS_ACTOR_CANARY");
  await openPersonEditor();
  await waitFor(() => {
    expect(document.querySelector('[name="target.subject"]')).not.toBeNull();
  });
  for (const [name, value] of [
    ["target.subject", "pending-target"],
    ["act.profile.displayName", "Synthetic pending person"],
  ]) {
    await user.type(document.querySelector<HTMLInputElement>(`[name="${name}"]`)!, value!);
  }
  await chooseAdmissionRole();
  await user.click(screen.getByRole("button", { name: "Review changes" }));
  await waitForReviewConfirm();
  await user.click(reviewConfirm());
  await screen.findByRole("button", { name: "Send again" });
};
const authorityReads = () => {
  mockEndpoint("case.list").mockResolvedValue(
    outcome({
      outcome: presentedOutcome("case.list", "SUCCEEDED", {
        basis: workBasis,
        exactReference: null,
        offers: [],
        items: [],
        nextCursor: null,
      }),
    }),
  );
  const actor = {
    actor: {
      actorId: "00000000-0000-4000-8000-000000000001",
      displayName: { kind: "NAMED" as const, value: "PREVIOUS_ACTOR_CANARY" },
      principalKind: "HUMAN" as const,
      availability: "AVAILABLE" as const,
    },
    contact: null,
    state: "NO_ROLES" as const,
    roles: [],
    principal: {
      kind: "HUMAN" as const,
      issuer: "https://issuer.example/",
      subject: "PREVIOUS_ACTOR_CANARY",
    },
  };
  mockEndpoint("authority.list")
    .mockResolvedValueOnce(
      outcome({
        outcome: presentedOutcome("authority.list", "SUCCEEDED", {
          basis: workBasis,
          items: [actor],
          nextCursor: null,
        }),
      }),
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
  expect(screen.getByRole("heading", { name: "People & access", level: 1 })).toBeVisible();
  expect(screen.queryByRole("link", { name: "People & access" })).toBeNull();
  expect(document.body).not.toHaveTextContent("PREVIOUS_ACTOR_CANARY");
  await user.click(screen.getByRole("button", { name: "Continue current request" }));
  await user.click(await screen.findByRole("button", { name: "Send again" }));
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
    .mockResolvedValueOnce(
      outcome({
        outcome: presentedOutcome("case.get", "SUCCEEDED", caseViewInput(visible)),
      }),
    )
    .mockResolvedValue(outcome({ outcome: unavailable }));
  mockEndpoint("case.timeline").mockResolvedValue(
    outcome({
      outcome: presentedOutcome("case.timeline", "SUCCEEDED", timelinePage()),
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
  expect(document.querySelector('.record-view [data-entry="claimantName"]')).toBeNull();
});

it.each(["NO_ROLES", "DISABLED"] as const)(
  "retains same-requester uncertain bytes through %s without replay and restores the draft after access returns",
  async (kind) => {
    const active = contextFor(["CASES", "AUTHORITY", "OPERATIONS"]);
    if (
      active.kind !== "outcome" ||
      active.value.outcome.kind !== "SUCCEEDED" ||
      active.value.outcome.data.requester.kind !== "ACTIVE"
    ) {
      throw new Error("Expected active requester fixture.");
    }
    const self = active.value.outcome.data.requester;
    const inactive =
      kind === "NO_ROLES"
        ? {
            kind,
            self: self.self,
            installationId: self.installationId,
            businessDate: self.businessDate,
            timeZone: self.timeZone,
          }
        : { kind, self: { ...self.self, availability: "DISABLED" as const } };
    mockEndpoint("context")
      .mockResolvedValueOnce(active)
      .mockResolvedValueOnce(
        outcome({
          outcome: presentedOutcome("context", "SUCCEEDED", { requester: inactive }),
        }),
      )
      .mockResolvedValue(active);
    authorityReads();
    authorityPreview();
    const execute = vi.spyOn(service, "execute").mockResolvedValue({
      kind: "deliveryFailure",
      notice: { kind: "local", reason: "unreachable" },
      dispatched: true,
    });
    const user = userEvent.setup();
    let refresh: () => void = () => {
      throw new Error("Refresh driver not mounted");
    };
    render(
      <>
        <Dashboard />
        <DisclosureRefreshDriver
          capture={(invalidate) => {
            refresh = invalidate;
          }}
        />
      </>,
    );
    await beginUnknownAuthority(user);
    const [{ bytes }] = execute.mock.calls[0]!;
    await act(() => Promise.resolve().then(refresh));
    await screen.findByRole("heading", {
      name: kind === "DISABLED" ? "This account is disabled" : "You don’t have access yet",
    });
    expect(screen.queryByRole("button", { name: "Send again" })).toBeNull();
    expect(execute).toHaveBeenCalledOnce();
    await user.click(screen.getByRole("button", { name: "Sign out" }));
    expect(await screen.findByRole("dialog")).toBeVisible();
    await user.click(screen.getByRole("button", { name: "Keep editing" }));
    await user.click(screen.getByRole("button", { name: "Check access" }));
    await user.click(await screen.findByRole("button", { name: "Continue current request" }));
    const retry = await screen.findByRole("button", { name: "Send again" });
    await user.click(retry);
    expect(execute).toHaveBeenCalledTimes(2);
    expect(execute.mock.calls[1]![0].bytes).toBe(bytes);
  },
);

it.each(["actor", "installation"] as const)(
  "does not retain a previous workspace for a NO_ROLES requester with a different %s",
  async (difference) => {
    const active = contextFor(["CASES", "AUTHORITY", "OPERATIONS"]);
    if (
      active.kind !== "outcome" ||
      active.value.outcome.kind !== "SUCCEEDED" ||
      active.value.outcome.data.requester.kind !== "ACTIVE"
    ) {
      throw new Error("Expected active requester fixture.");
    }
    const source = active.value.outcome.data.requester;
    const requester = {
      kind: "NO_ROLES" as const,
      self: {
        ...source.self,
        actorId:
          difference === "actor" ? "10000000-0000-4000-8000-000000000099" : source.self.actorId,
      },
      installationId:
        difference === "installation"
          ? "90000000-0000-4000-8000-000000000099"
          : source.installationId,
      businessDate: source.businessDate,
      timeZone: source.timeZone,
    };
    mockEndpoint("context")
      .mockResolvedValueOnce(active)
      .mockResolvedValueOnce(
        outcome({
          outcome: presentedOutcome("context", "SUCCEEDED", { requester }),
        }),
      )
      .mockResolvedValue(active);
    authorityReads();
    authorityPreview();
    const execute = vi.spyOn(service, "execute").mockResolvedValue({
      kind: "deliveryFailure",
      notice: { kind: "local", reason: "unreachable" },
      dispatched: true,
    });
    const user = userEvent.setup();
    let refresh: () => void = () => {
      throw new Error("Refresh driver not mounted");
    };
    render(
      <>
        <Dashboard />
        <DisclosureRefreshDriver
          capture={(invalidate) => {
            refresh = invalidate;
          }}
        />
      </>,
    );
    await beginUnknownAuthority(user);
    await act(() => Promise.resolve().then(refresh));
    await screen.findByRole("heading", { name: "You don’t have access yet" });
    expect(document.querySelector(".workspace-shell")).toBeNull();
    expect(execute).toHaveBeenCalledOnce();
    await user.click(screen.getByRole("button", { name: "Check access" }));
    await screen.findByRole("heading", { name: "People & access", level: 1 });
    expect(screen.queryByRole("button", { name: "Send again" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Continue current request" })).toBeNull();
    expect(execute).toHaveBeenCalledOnce();
  },
);
