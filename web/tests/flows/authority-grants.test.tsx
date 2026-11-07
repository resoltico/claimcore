import { presentedOutcome } from "../support/read-view.fixtures";
import {
  chooseAdmissionRole,
  closePersonDetails,
  DisclosureRefreshDriver,
} from "../support/people-drawer.fixtures";
import { workBasis } from "../support/inputs";
import { reviewConfirm, waitForReviewConfirm } from "../support/review-controls";
import { stubViewCounts } from "../support/dashboard-context.fixtures";
import { navigate } from "../../src/routing/navigation";
import { mockEndpoint, getEndpointMock } from "../support/endpoint-mocks";
import { authorityPreview, authorityAcceptance } from "../support/authority-operation.fixtures";
import { installationOffers } from "../support/installation-advice.fixtures";
import { act, fireEvent, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { render, screen } from "../support/presentation-test-support";
import { Dashboard } from "../../src/views/Dashboard";
import { AuthorityPanel } from "../../src/views/AuthorityPanel";
import { AuthorityGrants } from "../../src/views/AuthorityGrants";
import { service, type ApiResult, type ServiceResponse } from "../../src/api/service";
import { wireReply } from "../support/reply.fixtures";
beforeEach(() => {
  stubViewCounts();
});
const first = "10000000-0000-4000-8000-000000000001";
const second = "10000000-0000-4000-8000-000000000002";
const actor = (actorId: string, prefix = "header") => ({
  actor: {
    actorId,
    displayName: { kind: "NAMED" as const, value: `${prefix}-${actorId}` },
    principalKind: "SERVICE" as const,
    availability: "DISABLED" as const,
  },
  contact: null,
  state: "DISABLED" as const,
  roles: [{ role: "CASE_READER" as const, installation: false, caseCount: 1 }],
  principal: {
    kind: "SERVICE" as const,
    issuer: "https://issuer.example/",
    subject: `${prefix}-${actorId}`,
  },
});
const grants = (
  actorId: string,
  nextCursor: string | null = null,
  reference = `GRANT-${actorId}`,
  caseId = "20000000-0000-4000-8000-000000000001",
): ApiResult<ServiceResponse<"authority.grants">> => ({
  kind: "outcome",
  status: 200,
  value: {
    outcome: presentedOutcome("authority.grants", "SUCCEEDED", {
      basis: workBasis,
      actor: actor(actorId),
      items: [
        {
          role: "CASE_READER",
          scope: {
            kind: "CASE",
            caseId,
            caseReference: reference,
          },
        },
      ],
      nextCursor,
    }),
  },
});
const pending = <T,>() => {
  let finish: (value: T) => void = () => undefined;
  const promise = new Promise<T>((resolve) => {
    finish = resolve;
  });
  return { promise, finish };
};
const reads = (offered = true) => {
  navigate("/people", true);
  const context = wireReply("context");
  if (context.outcome.kind !== "SUCCEEDED" || context.outcome.data.requester.kind !== "ACTIVE") {
    throw new Error("Context fixture absent.");
  }
  mockEndpoint("context").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: {
      ...context,
      outcome: presentedOutcome("context", "SUCCEEDED", {
        requester: {
          ...context.outcome.data.requester,
          offers: installationOffers(["ADMIT", "GRANT"]),
          views: offered ? (["AUTHORITY"] as const) : [],
        },
      }),
    },
  });
  mockEndpoint("authority.list").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: {
      outcome: presentedOutcome("authority.list", "SUCCEEDED", {
        basis: workBasis,
        items: [actor(first, "listed"), actor(second, "listed")],
        nextCursor: null,
      }),
    },
  });
};
beforeEach(() => {
  authorityPreview();
});
afterEach(() => {
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});
it("loads no grants without explicit core-offered selection and discards a late prior target", async () => {
  reads();
  const old = pending<Awaited<ReturnType<typeof service.authorityGrants>>>();
  const request = mockEndpoint("authority.grants")
    .mockImplementationOnce(() => old.promise)
    .mockResolvedValueOnce(grants(second));
  render(<AuthorityPanel onLockChange={vi.fn()} />);
  const buttons = await screen.findAllByRole("link", { name: / ·/u });
  expect(request).not.toHaveBeenCalled();
  fireEvent.click(buttons[0]!);
  await waitFor(() => {
    expect(request).toHaveBeenCalledTimes(1);
  });
  await closePersonDetails();
  fireEvent.click(buttons[1]!);
  await screen.findByText(`GRANT-${second}`);
  expect(request.mock.calls[0]?.[2]?.aborted).toBe(true);
  await act(() =>
    Promise.resolve().then(() => {
      old.finish(grants(first));
    }),
  );
  expect(screen.queryByText(`GRANT-${first}`)).toBeNull();
  expect(screen.getAllByText(`header-${second}`)[0]).toBeVisible();
  expect(screen.getByRole("dialog", { name: `header-${second}` })).toHaveFocus();
});
it("consumes the core read offer rather than inferring it from actor rows", async () => {
  reads(false);
  const request = mockEndpoint("authority.grants");
  render(<AuthorityPanel onLockChange={vi.fn()} />);
  await screen.findByText(`listed-${first}`);
  expect(screen.queryByRole("link", { name: / ·/u })).toBeNull();
  expect(request).not.toHaveBeenCalled();
});
it("keeps only the same actor's page on a transient error and follows server continuations", async () => {
  const request = mockEndpoint("authority.grants")
    .mockResolvedValueOnce(grants(first, "next"))
    .mockResolvedValueOnce({
      kind: "deliveryFailure",
      notice: { kind: "local", reason: "unreachable" },
    })
    .mockResolvedValueOnce(
      grants(first, null, "SECOND-PAGE", "20000000-0000-4000-8000-000000000002"),
    );
  render(<AuthorityGrants actorId={first} onBack={vi.fn()} onDisclosureRefused={vi.fn()} />);
  fireEvent.click(await screen.findByRole("button", { name: "More grants" }));
  await screen.findByRole("alert");
  expect(screen.getByText(`GRANT-${first}`)).toBeVisible();
  fireEvent.click(screen.getByRole("button", { name: "More grants" }));
  await screen.findByText("All roles are shown.");
  expect(request.mock.calls.map((call) => call[1].cursor ?? null)).toEqual([null, "next", "next"]);
});

it("loads and clears the selected grant page without request-per-row reads", async () => {
  reads();
  const reload = pending<Awaited<ReturnType<typeof service.authorityGrants>>>();
  const request = mockEndpoint("authority.grants")
    .mockResolvedValueOnce(grants(first, "next"))
    .mockResolvedValueOnce(grants(first, null, "SECOND", "20000000-0000-4000-8000-000000000002"))
    .mockImplementationOnce(() => reload.promise);
  render(<AuthorityPanel onLockChange={vi.fn()} />);
  const actors = await screen.findAllByRole("link", { name: / ·/u });
  expect(request).not.toHaveBeenCalled();
  fireEvent.click(actors[0]!);
  await screen.findByText("Only some roles are shown.");
  expect(document.querySelectorAll('[role="dialog"] .table-view tbody > tr')).toHaveLength(1);
  expect(request).toHaveBeenCalledTimes(1);
  fireEvent.click(screen.getByRole("button", { name: "More grants" }));
  await screen.findByText("SECOND");
  expect(document.querySelectorAll('[role="dialog"] .table-view tbody > tr')).toHaveLength(2);
  expect(request).toHaveBeenCalledTimes(2);
  fireEvent.click(screen.getByRole("button", { name: "Reload grants" }));
  await waitFor(() => {
    expect(document.querySelectorAll('[role="dialog"] .table-view tbody > tr')).toHaveLength(0);
  });
  await act(() =>
    Promise.resolve().then(() => {
      reload.finish(grants(first));
    }),
  );
  await screen.findByText(`GRANT-${first}`);
  expect(document.querySelectorAll('[role="dialog"] .table-view tbody > tr')).toHaveLength(1);
  expect(screen.queryByText("SECOND")).toBeNull();
  expect(request).toHaveBeenCalledTimes(3);
  await closePersonDetails();
  expect(document.querySelector(".actor-grant-items")).toBeNull();
});

const refused = (kind: string): ApiResult<ServiceResponse<"authority.grants">> =>
  kind === "access"
    ? {
        kind: "outcome",
        status: 200,
        value: {
          outcome: {
            kind: "REJECTED",
            data: {
              code: "RESOURCE_UNAVAILABLE",
              action: "ASK_FOR_ACCESS",
            },
          },
        },
      }
    : {
        kind: "hostFailure",
        status: 401,
        failure: { code: "SESSION_REJECTED", phase: "NOT_STARTED" },
      };
it.each(["session", "access"])(
  "clears all selected disclosures on %s refusal while preserving exact uncertain mutation bytes",
  async (kind) => {
    reads();
    const late = pending<Awaited<ReturnType<typeof service.authorityGrants>>>();
    const grant = mockEndpoint("authority.grants")
      .mockResolvedValueOnce(grants(first, "next"))
      .mockReturnValueOnce(late.promise);
    const execute = vi.spyOn(service, "execute").mockResolvedValue({
      kind: "deliveryFailure",
      notice: { kind: "local", reason: "unreachable" },
    });
    render(<AuthorityPanel onLockChange={vi.fn()} />);
    fireEvent.click((await screen.findAllByRole("link", { name: / ·/u }))[0]!);
    await screen.findByText(`GRANT-${first}`);
    fireEvent.click(screen.getByRole("button", { name: "More grants" }));
    fireEvent.click(screen.getByRole("button", { name: /^Grant role/u }));
    await chooseAdmissionRole();
    fireEvent.click(screen.getByRole("button", { name: "Review changes" }));
    await waitForReviewConfirm();
    fireEvent.click(reviewConfirm());
    await screen.findByRole("button", { name: "Send again" });
    const original = execute.mock.calls[0]?.[0];
    await act(() =>
      Promise.resolve().then(() => {
        late.finish(refused(kind));
      }),
    );
    await waitFor(() => {
      expect(getEndpointMock("authority.list")).toHaveBeenCalledTimes(2);
    });
    expect(screen.queryByRole("dialog", { name: `header-${first}` })).toBeNull();
    expect(screen.queryByText(`GRANT-${first}`)).toBeNull();
    expect(grant).toHaveBeenCalledTimes(2);
    fireEvent.click(screen.getByRole("button", { name: "Continue current request" }));
    fireEvent.click(await screen.findByRole("button", { name: "Send again" }));
    await waitFor(() => {
      expect(execute).toHaveBeenCalledTimes(2);
    });
    expect(execute.mock.calls[1]?.[0]).toBe(original);
    expect(execute.mock.calls[1]?.[0].bytes).toBe(original?.bytes);
  },
);
it("ignores an in-flight grant page after an accepted authority mutation resets selection", async () => {
  reads();
  const old = pending<Awaited<ReturnType<typeof service.authorityGrants>>>();
  const request = mockEndpoint("authority.grants")
    .mockResolvedValueOnce(grants(first, "next"))
    .mockReturnValueOnce(old.promise);
  vi.spyOn(service, "execute").mockImplementation((prepared) =>
    Promise.resolve(authorityAcceptance(prepared)),
  );
  render(<AuthorityPanel onLockChange={vi.fn()} />);
  fireEvent.click((await screen.findAllByRole("link", { name: / ·/u }))[0]!);
  await screen.findByText(`GRANT-${first}`);
  fireEvent.click(screen.getByRole("button", { name: "More grants" }));
  fireEvent.click(screen.getByRole("button", { name: /^Grant role/u }));
  await chooseAdmissionRole();
  fireEvent.click(screen.getByRole("button", { name: "Review changes" }));
  await waitForReviewConfirm();
  fireEvent.click(reviewConfirm());
  await waitFor(() => {
    expect(getEndpointMock("authority.list")).toHaveBeenCalledTimes(2);
  });
  expect(request.mock.calls[1]?.[2]?.aborted).toBe(true);
  await act(() =>
    Promise.resolve().then(() => {
      old.finish(grants(first));
    }),
  );
  expect(screen.queryByRole("dialog", { name: `header-${first}` })).toBeNull();
  expect(screen.queryByText(`GRANT-${first}`)).toBeNull();
  expect(request).toHaveBeenCalledTimes(2);
});

it("clears pending pagination on back and never restores it on late completion", async () => {
  reads();
  const late = pending<Awaited<ReturnType<typeof service.authorityGrants>>>();
  const request = mockEndpoint("authority.grants")
    .mockResolvedValueOnce(grants(first, "next"))
    .mockReturnValueOnce(late.promise);
  render(<AuthorityPanel onLockChange={vi.fn()} />);
  fireEvent.click((await screen.findAllByRole("link", { name: / ·/u }))[0]!);
  fireEvent.click(await screen.findByRole("button", { name: "More grants" }));
  await closePersonDetails();
  expect(request.mock.calls[1]?.[2]?.aborted).toBe(true);
  await act(() =>
    Promise.resolve().then(() => {
      late.finish(grants(first));
    }),
  );
  expect(screen.queryByRole("heading", { name: `header-${first}` })).toBeNull();
  expect(screen.queryByText(`GRANT-${first}`)).toBeNull();
});

it("resets only the selected grant page on invalid continuation and reloads it without a cursor", async () => {
  const request = mockEndpoint("authority.grants")
    .mockResolvedValueOnce(grants(first, "old"))
    .mockResolvedValueOnce({
      kind: "outcome",
      status: 200,
      value: {
        outcome: {
          kind: "REJECTED",
          data: {
            code: "INVALID_CURSOR",
            action: "READ_CURRENT",
            target: "cursor",
          },
        },
      },
    })
    .mockResolvedValueOnce(grants(first));
  const onRefused = vi.fn();
  render(<AuthorityGrants actorId={first} onBack={vi.fn()} onDisclosureRefused={onRefused} />);
  fireEvent.click(await screen.findByRole("button", { name: "More grants" }));
  await screen.findByRole("alert");
  expect(screen.queryByText(`GRANT-${first}`)).toBeNull();
  expect(screen.queryByText(`header-${first}`)).toBeNull();
  expect(screen.queryByRole("button", { name: "More grants" })).toBeNull();
  expect(onRefused).not.toHaveBeenCalled();
  expect(request).toHaveBeenCalledTimes(2);
  fireEvent.click(screen.getByRole("button", { name: "Reload grants" }));
  await screen.findByText(`GRANT-${first}`);
  expect(request.mock.calls[2]?.[1].cursor ?? null).toBeNull();
  expect(onRefused).not.toHaveBeenCalled();
});

const actorPage = (
  ids: string[],
  nextCursor: string | null = null,
): ApiResult<ServiceResponse<"authority.list">> => ({
  kind: "outcome",
  status: 200,
  value: {
    outcome: presentedOutcome("authority.list", "SUCCEEDED", {
      basis: workBasis,
      items: ids.map((id) => actor(id, "listed")),
      nextCursor,
    }),
  },
});
const focusedBack = closePersonDetails;

it("returns focus to the originating grant control when it is still present", async () => {
  reads();
  mockEndpoint("authority.grants").mockResolvedValue(grants(first));
  render(<Dashboard />);
  const origin = (await screen.findAllByRole("link", { name: / ·/u }))[0]!;
  fireEvent.click(origin);
  await screen.findByText(`GRANT-${first}`);
  await focusedBack();
  await waitFor(() => {
    expect(origin).toHaveFocus();
  });
});
it("focuses the stable actors heading during a pending reload and keeps it after the late page", async () => {
  reads();
  mockEndpoint("authority.grants").mockResolvedValue(grants(first));
  const late = pending<Awaited<ReturnType<typeof service.authorityList>>>();
  let refresh: () => void = () => {
    throw new Error("Refresh driver not mounted");
  };
  render(
    <>
      <DisclosureRefreshDriver
        capture={(invalidate) => {
          refresh = invalidate;
        }}
      />
      <Dashboard />
    </>,
  );
  fireEvent.click((await screen.findAllByRole("link", { name: / ·/u }))[0]!);
  await screen.findByText(`GRANT-${first}`);
  getEndpointMock("authority.list").mockReturnValueOnce(late.promise);
  await act(() => Promise.resolve().then(refresh));
  expect(screen.queryByRole("link", { name: / ·/u })).toBeNull();
  await focusedBack();
  const heading = screen.getByRole("heading", { name: "People & access" });
  await waitFor(() => {
    expect(heading).toHaveFocus();
  });
  await act(() =>
    Promise.resolve().then(() => {
      late.finish(actorPage([first]));
    }),
  );
  await screen.findByRole("link", { name: / ·/u });
  await waitFor(() => {
    expect(heading).toHaveFocus();
  });
});
it("focuses the actors heading when a page-two origin is absent from refreshed page one", async () => {
  reads();
  getEndpointMock("authority.list")
    .mockResolvedValueOnce(actorPage([first], "second"))
    .mockResolvedValueOnce(actorPage([second]))
    .mockResolvedValueOnce(actorPage([first]));
  mockEndpoint("authority.grants").mockResolvedValue(grants(second));
  let refresh: () => void = () => {
    throw new Error("Refresh driver not mounted");
  };
  render(
    <>
      <DisclosureRefreshDriver
        capture={(invalidate) => {
          refresh = invalidate;
        }}
      />
      <Dashboard />
    </>,
  );
  fireEvent.click(await screen.findByRole("button", { name: "More people" }));
  await screen.findByText(`listed-${second}`);
  fireEvent.click(screen.getAllByRole("link", { name: / ·/u })[1]!);
  await screen.findByText(`GRANT-${second}`);
  await act(() => Promise.resolve().then(refresh));
  await screen.findByText(`listed-${first}`);
  expect(screen.queryByText(`listed-${second}`)).toBeNull();
  await focusedBack();
  await waitFor(() => {
    expect(screen.getByRole("heading", { name: "People & access" })).toHaveFocus();
  });
});
it("does not restore actor focus when global disclosure refusal removes the selected pane", async () => {
  reads();
  mockEndpoint("authority.grants")
    .mockResolvedValueOnce(grants(first, "next"))
    .mockResolvedValueOnce(refused("session"));
  render(<AuthorityPanel onLockChange={vi.fn()} />);
  fireEvent.click((await screen.findAllByRole("link", { name: / ·/u }))[0]!);
  await screen.findByText(`GRANT-${first}`);
  screen.getAllByRole("button", { name: "Close" })[0]!.focus();
  const focus = vi.spyOn(HTMLElement.prototype, "focus");
  fireEvent.click(screen.getByRole("button", { name: "More grants" }));
  await waitFor(() => {
    expect(getEndpointMock("authority.list")).toHaveBeenCalledTimes(2);
  });
  await screen.findByText(`listed-${first}`);
  expect(screen.queryByRole("heading", { name: `header-${first}` })).toBeNull();
  expect(focus).not.toHaveBeenCalled();
  expect(document.body).toHaveFocus();
});
it("focuses the authority route heading without restoring an actor after disclosure refusal", async () => {
  reads();
  mockEndpoint("authority.grants")
    .mockResolvedValueOnce(grants(first, "next"))
    .mockResolvedValueOnce(refused("session"));
  render(<Dashboard />);
  const origin = (await screen.findAllByRole("link", { name: / ·/u }))[0]!;
  fireEvent.click(origin);
  await screen.findByText(`GRANT-${first}`);
  screen.getAllByRole("button", { name: "Close" })[0]!.focus();
  const focus = vi.spyOn(HTMLElement.prototype, "focus");
  fireEvent.click(screen.getByRole("button", { name: "More grants" }));
  await waitFor(() => {
    expect(screen.queryByRole("heading", { name: `header-${first}` })).toBeNull();
    expect(screen.getByRole("heading", { name: "People & access", level: 1 })).toHaveFocus();
  });
  expect(
    focus.mock.contexts.some((target) => target instanceof HTMLElement && target.id === origin.id),
  ).toBe(false);
  expect(origin).not.toHaveFocus();
});
