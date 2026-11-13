import { wireReply } from "../support/reply.fixtures";
import { humanReadReply } from "../support/human-read-reply.fixtures";
import { activeRequester } from "../support/human-requester.fixtures";
import {
  openPersonEditor,
  chooseAdmissionRole,
  DisclosureRefreshDriver,
} from "../support/people-drawer.fixtures";
import { reviewConfirm, waitForReviewConfirm } from "../support/review-controls";
import { workBasis } from "../support/inputs";
import { stubViewCounts } from "../support/dashboard-context.fixtures";
import { mockEndpoint } from "../support/endpoint-mocks";
import { peoplePreview, peopleAcceptance } from "../support/people-operation.fixtures";
import { act, fireEvent, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { render, screen, TestSession } from "../support/presentation-test-support";
import userEvent from "@testing-library/user-event";
import { Dashboard } from "../../src/views/Dashboard";
import { PeoplePanel } from "../../src/views/PeoplePanel";
import { service, type ApiResult, type ServiceResponse } from "../../src/api/service";
beforeEach(() => {
  stubViewCounts();
});

beforeEach(() => {
  peoplePreview();
});
afterEach(() => vi.restoreAllMocks());
const requester = (admit: boolean): ServiceResponse<"context"> =>
  humanReadReply("context", {
    ...activeRequester,
    context: {
      ...activeRequester.context,
      tasks: admit
        ? [
            "people.admit",
            "people.list",
            "people.grants",
            "people.history",
            "people.requests",
            "people.requests.dismiss",
          ]
        : [
            "people.list",
            "people.grants",
            "people.history",
            "people.requests",
            "people.requests.dismiss",
          ],
      peopleIssuerSeed: admit ? "https://issuer.example/" : null,
    },
    basis: { kind: "SNAPSHOT", observation: workBasis },
  });
const disclosedListing = humanReadReply("people.list", {
  screen: {
    regions: [
      {
        role: "RESULTS",
        content: {
          kind: "TABLE",
          columns: [
            {
              key: "person",
              labelId: "field.profile.displayName.label",
              prominence: "PRIMARY",
              isMoney: false,
            },
          ],
          rows: [
            {
              key: "10000000-0000-4000-8000-000000000001",
              values: [
                {
                  kind: "ACTOR",
                  value: {
                    actorId: "10000000-0000-4000-8000-000000000001",
                    displayName: { kind: "NAMED", value: "synthetic-disclosed" },
                    principalKind: "SERVICE",
                    availability: "AVAILABLE",
                  },
                },
              ],
            },
          ],
        },
      },
    ],
  },
  context: { kind: "PEOPLE", actorIds: ["10000000-0000-4000-8000-000000000001"], nextCursor: null },
  basis: { kind: "SNAPSHOT", observation: workBasis },
  disclosure: "CURRENT",
});
const successReplies: { [K in "context" | "people.list"]: ServiceResponse<K> } = {
  context: requester(true),
  "people.list": disclosedListing,
};
const success = <K extends "context" | "people.list">(
  endpoint: K,
): ApiResult<ServiceResponse<K>> => ({
  kind: "outcome",
  status: 200,
  value: successReplies[endpoint],
});
const refused = (): Extract<
  ApiResult<ServiceResponse<"operation.execute">>,
  { kind: "hostFailure" }
> => ({
  kind: "hostFailure",
  status: 401,
  failure: { code: "SESSION_REJECTED", phase: "NOT_STARTED" },
});
const finishRead = (complete: () => void) => act(() => Promise.resolve().then(complete));
const unavailable = (): ApiResult<ServiceResponse<"operation.execute">> => ({
  kind: "outcome",
  status: 200,
  value: {
    representation: "HUMAN",
    operationId: "10000000-0000-4000-8000-000000000001",
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
const author = async () => {
  await openPersonEditor();
  await waitFor(() => {
    expect(document.querySelector('[name="input:principal.subject"]')).not.toBeNull();
  });
  for (const [name, value] of [
    ["input:principal.subject", "synthetic-authored"],
    ["input:profile.displayName", "Synthetic registered person"],
  ]) {
    fireEvent.change(document.querySelector(`[name="${name}"]`)!, { target: { value } });
  }
};
const prepare = async () => {
  await author();
  await chooseAdmissionRole();
  fireEvent.click(screen.getByRole("button", { name: "Review changes" }));
  await waitForReviewConfirm();
};
const listPrincipal = () => "synthetic-disclosed";
const emptyOffers = (): ApiResult<ServiceResponse<"context">> => ({
  kind: "outcome",
  status: 200,
  value: requester(false),
});
it.each([false, true])(
  "revalidates people disclosure after management refusal; retained read=%s",
  async (retainsRead) => {
    const context = mockEndpoint("context")
      .mockResolvedValueOnce(success("context"))
      .mockResolvedValue(emptyOffers());
    const listing = mockEndpoint("people.list").mockResolvedValueOnce(success("people.list"));
    let finish: ((value: Awaited<ReturnType<typeof service.peopleList>>) => void) | undefined;
    listing.mockImplementation(
      () =>
        new Promise((resolve) => {
          finish = resolve;
        }),
    );
    vi.spyOn(service, "execute").mockResolvedValue(unavailable());
    render(<PeoplePanel onLockChange={vi.fn()} />);
    await screen.findByText(listPrincipal());
    await prepare();
    fireEvent.click(reviewConfirm());
    await waitFor(() => {
      expect(context).toHaveBeenCalledTimes(2);
    });
    expect(screen.queryByText(listPrincipal())).toBeNull();
    expect(screen.queryByText("synthetic-authored")).toBeNull();
    await finishRead(() => {
      finish?.(retainsRead ? success("people.list") : refused());
    });
    if (retainsRead) {
      await screen.findByText(listPrincipal());
    } else {
      expect(screen.queryByText(listPrincipal())).toBeNull();
    }
    expect(screen.queryByRole("option")).toBeNull();
    expect(listing).toHaveBeenCalledTimes(2);
  },
);
it("aborts prior people reads and ignores their late completion after an uncertain dispatch refusal", async () => {
  mockEndpoint("context")
    .mockResolvedValueOnce(success("context"))
    .mockResolvedValue(emptyOffers());
  let priorFinish: ((value: Awaited<ReturnType<typeof service.peopleList>>) => void) | undefined;
  const listing = mockEndpoint("people.list")
    .mockImplementationOnce(
      () =>
        new Promise((resolve) => {
          priorFinish = resolve;
        }),
    )
    .mockResolvedValue(refused());
  const execute = vi
    .spyOn(service, "execute")
    .mockResolvedValueOnce({
      kind: "deliveryFailure",
      notice: { kind: "local", reason: "unreachable" },
    })
    .mockResolvedValue(unavailable());
  render(<PeoplePanel onLockChange={vi.fn()} />);
  await prepare();
  fireEvent.click(reviewConfirm());
  fireEvent.click(await screen.findByRole("button", { name: "Send again" }));
  await waitFor(() => {
    expect(listing).toHaveBeenCalledTimes(2);
  });
  expect(listing.mock.calls[0]![2]!.aborted).toBe(true);
  await finishRead(() => {
    priorFinish?.(success("people.list"));
  });
  expect(screen.queryByText(listPrincipal())).toBeNull();
  fireEvent.click(await screen.findByRole("button", { name: "Continue current request" }));
  expect(await screen.findByRole("button", { name: "Send again" })).toBeVisible();
  expect(execute.mock.calls[1]![0]).toBe(execute.mock.calls[0]![0]);
  expect(execute.mock.calls[1]![0].bytes).toBe(execute.mock.calls[0]![0].bytes);
});
const revalidatingReads = async () => {
  let finishContext: ((value: Awaited<ReturnType<typeof service.context>>) => void) | undefined;
  let finishListing:
    ((value: Awaited<ReturnType<typeof service.peopleList>>) => void) | undefined;
  const context = mockEndpoint("context")
    .mockResolvedValueOnce(success("context"))
    .mockImplementation(
      () =>
        new Promise((resolve) => {
          finishContext = resolve;
        }),
    );
  const listing = mockEndpoint("people.list")
    .mockResolvedValueOnce(success("people.list"))
    .mockImplementation(
      () =>
        new Promise((resolve) => {
          finishListing = resolve;
        }),
    );
  vi.spyOn(service, "execute").mockResolvedValue(unavailable());
  render(<PeoplePanel onLockChange={vi.fn()} />);
  await screen.findByText(listPrincipal());
  await prepare();
  fireEvent.click(reviewConfirm());
  await waitFor(() => {
    expect(listing).toHaveBeenCalledTimes(2);
  });
  return { context, listing, finishContext, finishListing };
};
it("keeps discard actionable when readonly restoration completes during a pointer press", async () => {
  const { finishContext, finishListing } = await revalidatingReads();
  const user = userEvent.setup();
  await user.click(screen.getByRole("button", { name: "Continue current request" }));
  const discard = screen.getAllByRole("button", { name: "Close" })[0]!;
  expect(discard).not.toBeDisabled();
  await user.pointer({ target: discard, keys: "[MouseLeft>]" });
  await finishRead(() => {
    finishContext?.(emptyOffers());
    finishListing?.(success("people.list"));
  });
  expect(screen.getAllByRole("button", { name: "Close" })[0]).toBe(discard);
  await user.pointer({ keys: "[/MouseLeft]" });
  const dialog = screen.getByRole("dialog");
  expect(dialog).toBeVisible();
  await user.click(within(dialog).getByRole("button", { name: "Discard and leave" }));
  expect(screen.queryByRole("dialog")).toBeNull();
});
it.each(["context", "listing"] as const)(
  "a %s success during revalidation cannot survive the other read's refusal",
  async (first) => {
    const { context, listing, finishContext, finishListing } = await revalidatingReads();
    await finishRead(() => {
      if (first === "context") {
        finishContext?.(success("context"));
      } else {
        finishListing?.(success("people.list"));
      }
    });
    if (first === "listing") {
      expect(screen.getByText(listPrincipal())).toBeVisible();
    }
    await finishRead(() => {
      if (first === "context") {
        finishListing?.(refused());
      } else {
        finishContext?.(refused());
      }
    });
    expect(screen.queryByText(listPrincipal())).toBeNull();
    expect(context).toHaveBeenCalledTimes(2);
    expect(listing).toHaveBeenCalledTimes(2);
    expect(screen.queryByText("synthetic-authored")).toBeNull();
    expect(screen.queryByRole("option")).toBeNull();
    fireEvent.click(screen.getByRole("button", { name: "Reload people" }));
    await waitFor(() => {
      expect(listing).toHaveBeenCalledTimes(3);
    });
    expect(context).toHaveBeenCalledTimes(3);
  },
);
const loseManagement = async (
  user: ReturnType<typeof userEvent.setup>,
  reviewed: boolean,
  returned: boolean,
  context: ReturnType<typeof mockEndpoint<"context">>,
  listing: ReturnType<typeof mockEndpoint<"people.list">>,
  refresh: () => void,
) => {
  if (reviewed) {
    await prepare();
    if (returned) {
      await user.click(screen.getByRole("button", { name: "Return to editing" }));
      context.mockResolvedValue(emptyOffers());
      listing.mockResolvedValue(refused());
      await act(() => Promise.resolve().then(refresh));
    } else {
      await user.click(reviewConfirm());
    }
  } else {
    await author();
    context.mockResolvedValue(emptyOffers());
    listing.mockResolvedValue(refused());
    await act(() => Promise.resolve().then(refresh));
  }
};
it.each([
  { stage: "unprepared", reviewed: false, returned: false },
  { stage: "reviewed", reviewed: true, returned: false },
  { stage: "returned to editing", reviewed: true, returned: true },
])(
  "explicitly discards a $stage draft after management loss and restores dashboard navigation",
  async ({ reviewed, returned }) => {
    const user = userEvent.setup();
    const logout = vi.fn().mockResolvedValue(undefined);
    const context = mockEndpoint("context").mockResolvedValue(success("context"));
    const listing = mockEndpoint("people.list").mockResolvedValue(success("people.list"));
    mockEndpoint("case.list").mockResolvedValue({
      kind: "outcome",
      status: 200,
      value: wireReply("case.list"),
    });
    vi.spyOn(service, "execute").mockImplementation(() => {
      context.mockResolvedValue(emptyOffers());
      return Promise.resolve(unavailable());
    });
    let refresh: () => void = () => {
      throw new Error("Refresh driver not mounted");
    };
    render(
      <TestSession logout={logout}>
        <DisclosureRefreshDriver
          capture={(invalidate) => {
            refresh = invalidate;
          }}
        />
        <Dashboard />
      </TestSession>,
    );
    await user.click(await screen.findByRole("link", { name: "People & access" }));
    await loseManagement(user, reviewed, returned, context, listing, () => {
      refresh();
    });
    await waitFor(() => {
      // External cache invalidation refreshes the global context as well as the
      // People workspace; its ensuing refusal revalidation remains bounded.
      expect(context).toHaveBeenCalledTimes(reviewed && !returned ? 2 : 4);
    });
    expect(screen.getByRole("button", { name: "Sign out" })).not.toBeDisabled();
    expect(screen.getByRole("link", { name: /^Cases(?: \d+)?$/u })).not.toHaveAttribute(
      "aria-disabled",
      "true",
    );
    // Leaving the area requests discard through the global navigation boundary,
    // even when current disclosure has removed the operation drawer entirely.
    await user.click(screen.getByRole("link", { name: /^Cases(?: \d+)?$/u }));
    await user.click(
      within(screen.getByRole("dialog")).getByRole("button", { name: "Discard and leave" }),
    );
    expect(screen.getByRole("button", { name: "Sign out" })).not.toBeDisabled();
    expect(screen.getByRole("link", { name: /^Cases(?: \d+)?$/u })).not.toHaveAttribute(
      "aria-disabled",
      "true",
    );
    expect(screen.queryByText("synthetic-authored")).toBeNull();
    await user.click(screen.getByRole("link", { name: /^Cases(?: \d+)?$/u }));
    await user.click(screen.getByRole("button", { name: "Sign out" }));
    expect(logout).toHaveBeenCalledOnce();
  },
);
const peopleRecoveryDelivery = (first: "lost" | "unknown") =>
  vi
    .spyOn(service, "execute")
    .mockImplementationOnce((prepared) =>
      Promise.resolve(
        first === "lost"
          ? {
              kind: "deliveryFailure",
              notice: { kind: "local", reason: "unreachable" },
            }
          : {
              kind: "outcome",
              status: 200,
              value: {
                representation: "HUMAN",
                operationId: prepared.input.operationId,
                outcome: { kind: "OUTCOME_UNKNOWN" },
              },
            },
      ),
    )
    .mockImplementation((prepared) => {
      const accepted = peopleAcceptance(prepared);
      if (accepted.kind !== "outcome" || accepted.value.outcome.kind !== "ACCEPTED") {
        throw new Error("Expected authored acceptance");
      }
      return Promise.resolve({
        ...accepted,
        value: {
          representation: "HUMAN",
          operationId: prepared.input.operationId,
          outcome: {
            kind: "ALREADY_ACCEPTED" as const,
            document: accepted.value.outcome.document,
          },
        },
      });
    });
it.each(["lost", "unknown"] as const)(
  "removes the obsolete %s attempt alert after exact people replay acceptance",
  async (first) => {
    mockEndpoint("context").mockResolvedValue(success("context"));
    mockEndpoint("people.list").mockResolvedValue(success("people.list"));
    const execute = peopleRecoveryDelivery(first);
    mockEndpoint("operation.get").mockImplementation((_credential, input) =>
      Promise.resolve({
        kind: "outcome",
        status: 200,
        value: {
          representation: "HUMAN",
          operationId: input.operationId,
          outcome: {
            kind: "ACCEPTED",
            byRequester: true,
            document: {
              screen: {
                regions: [
                  {
                    role: "DETAILS",
                    titleId: "label.recordDetails",
                    content: {
                      kind: "ENTRIES",
                      entries: [
                        {
                          key: "operationId",
                          labelId: "field.operationId.label",
                          prominence: "DETAIL",
                          value: { kind: "IDENTIFIER", value: input.operationId },
                        },
                      ],
                    },
                  },
                ],
              },
              context: {
                kind: "RECEIPT",
                recordedFields: [],
                target: { kind: "PEOPLE", actorId: "10000000-0000-4000-8000-000000000001" },
                caseLink: null,
                refreshCase: null,
              },
              basis: { kind: "SNAPSHOT", observation: workBasis },
              disclosure: "CURRENT",
            },
          },
        },
      }),
    );
    render(<PeoplePanel onLockChange={vi.fn()} />);
    await prepare();
    fireEvent.click(reviewConfirm());
    const retry = await screen.findByRole("button", { name: "Send again" });
    expect(screen.getByRole("alert")).toBeVisible();
    const [pending] = execute.mock.calls[0]!;
    fireEvent.click(retry);
    await waitFor(() => {
      expect(screen.queryByRole("alert")).toBeNull();
    });
    const identifier = await screen.findByText(pending.input.operationId, { exact: true });
    expect(identifier.closest(".screen-details")).not.toBeNull();
    expect(identifier).not.toBeVisible();
    await waitFor(() => {
      expect(screen.getByRole("heading", { name: "Recorded change" })).toHaveFocus();
    });
    expect(screen.queryByRole("button", { name: "Send again" })).toBeNull();
    expect(execute.mock.calls[1]![0]).toBe(pending);
    expect(execute.mock.calls[1]![0].bytes).toBe(pending.bytes);
  },
);
