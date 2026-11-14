import { humanReadReply } from "../support/human-read-reply.fixtures";
import type { HumanReadDocument_people_grants } from "@contracts/client";
import { openPersonEditor, chooseAdmissionRole } from "../support/people-drawer.fixtures";
import { workBasis } from "../support/inputs";
import {
  reviewConfirm,
  queryReviewConfirm,
  waitForReviewConfirm,
} from "../support/review-controls";
import userEvent from "@testing-library/user-event";
import { mockEndpoint } from "../support/endpoint-mocks";
import { peopleAcceptance, peoplePreview } from "../support/people-operation.fixtures";
import { act, fireEvent, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { render, screen } from "../support/presentation-test-support";
import { PeoplePanel } from "../../src/views/PeoplePanel";
import { service, type ApiResult, type ServiceResponse } from "../../src/api/service";
import { wireReply } from "../support/reply.fixtures";

beforeEach(() => {
  peoplePreview();
});
afterEach(() => vi.restoreAllMocks());
const pending = <T,>() => {
  let finish: (value: T) => void = () => undefined;
  const promise = new Promise<T>((resolve) => {
    finish = resolve;
  });
  return { promise, finish };
};
const context = (): ApiResult<ServiceResponse<"context">> => {
  const value = wireReply("context");
  if (value.outcome.kind !== "SUCCEEDED" || value.outcome.document.context.state !== "ACTIVE") {
    throw new Error("Missing context fixture");
  }
  return {
    kind: "outcome",
    status: 200,
    value: {
      ...value,
      outcome: {
        ...value.outcome,
        document: {
          ...value.outcome.document,
          context: {
            ...value.outcome.document.context,
            tasks: [
              "people.admit",
              "people.grant",
              "people.disable",
              "people.list",
              "people.grants",
            ],
            peopleIssuerSeed: "https://issuer.example/",
            views: ["PEOPLE", "OPERATIONS"],
          },
        },
      },
    },
  };
};
const existing: HumanReadDocument_people_grants = {
  screen: {
    regions: [
      {
        role: "ACCOUNT",
        content: {
          kind: "ENTRIES",
          entries: [
            {
              key: "actor",
              labelId: "field.profile.displayName.label",
              prominence: "PRIMARY",
              value: {
                kind: "ACTOR",
                value: {
                  actorId: "10000000-0000-4000-8000-000000000001",
                  displayName: { kind: "NAMED", value: "Synthetic existing person" },
                  principalKind: "HUMAN",
                  availability: "AVAILABLE",
                },
              },
            },
          ],
        },
      },
    ],
  },
  context: {
    kind: "PERSON",
    actorId: "10000000-0000-4000-8000-000000000001",
    principal: { kind: "HUMAN", issuer: "https://issuer.example/", subject: "synthetic-first" },
    tasks: ["people.disable"],
    profileSeed: null,
    revocations: [],
    responsibleCases: null,
    nextCursor: null,
  },
  basis: { kind: "SNAPSHOT", observation: workBasis },
  disclosure: "CURRENT",
};
const listing = (): ApiResult<ServiceResponse<"people.list">> => ({
  kind: "outcome",
  status: 200,
  value: humanReadReply("people.list", {
    screen: {
      regions: [
        {
          role: "RESULTS",
          content: {
            kind: "TABLE",
            columns: [
              {
                key: "actor",
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
                      displayName: { kind: "NAMED", value: "Synthetic existing person" },
                      principalKind: "HUMAN",
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
    context: {
      kind: "PEOPLE",
      actorIds: ["10000000-0000-4000-8000-000000000001"],
      nextCursor: null,
    },
    basis: { kind: "SNAPSHOT", observation: workBasis },
    disclosure: "CURRENT",
  }),
});
const acceptance = (
  prepared: Parameters<typeof service.execute>[0],
): ApiResult<ServiceResponse<"operation.execute">> => peopleAcceptance(prepared);
const author = async (subject: string) => {
  await openPersonEditor();
  await waitFor(() => {
    expect(document.querySelector('[name="input:principal.subject"]')).not.toBeNull();
  });
  for (const [name, value] of [["input:principal.subject", subject]]) {
    fireEvent.change(document.querySelector(`[name="${name}"]`)!, { target: { value } });
  }
  const profile = document.querySelector('[name="input:profile.displayName"]');
  if (profile !== null) {
    fireEvent.change(profile, { target: { value: "Synthetic registered person" } });
  }
  await chooseAdmissionRole();
};
const finish = async <T,>(read: ReturnType<typeof pending<T>>, value: T) => {
  await act(async () => {
    read.finish(value);
    await read.promise;
  });
};
const selectEnabled = async () => {
  mockEndpoint("people.grants").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: humanReadReply("people.grants", existing),
  });
  const user = userEvent.setup();
  await user.click(await screen.findByRole("link", { name: /Synthetic existing person/u }));
  await user.click(await screen.findByRole("button", { name: /^Disable account/u }));
};

it.each(["context", "listing"] as const)(
  "accepted account change permits a fresh authored request after %s-first disclosure refresh",
  async (first) => {
    const contextRead = pending<Awaited<ReturnType<typeof service.context>>>();
    const listingRead = pending<Awaited<ReturnType<typeof service.peopleList>>>();
    const execution = pending<Awaited<ReturnType<typeof service.execute>>>();
    mockEndpoint("context").mockResolvedValueOnce(context()).mockReturnValue(contextRead.promise);
    mockEndpoint("people.list")
      .mockResolvedValueOnce(listing())
      .mockReturnValue(listingRead.promise);
    const execute = vi.spyOn(service, "execute").mockReturnValue(execution.promise);
    render(<PeoplePanel onLockChange={vi.fn()} />);
    await selectEnabled();
    fireEvent.click(screen.getByRole("button", { name: "Review changes" }));
    await waitForReviewConfirm();
    fireEvent.click(reviewConfirm());
    await waitFor(() => {
      expect(execute).toHaveBeenCalledOnce();
    });
    const [prepared] = execute.mock.calls[0]!;
    expect({ task: prepared.input.task, inputs: prepared.input.inputs }).toEqual({
      task: "people.disable",
      inputs: {},
    });
    await finish(execution, acceptance(prepared));
    expect(queryReviewConfirm()).toBeNull();
    if (first === "listing") {
      await finish(listingRead, listing());
      expect(screen.queryByRole("combobox", { name: "People action" })).toBeNull();
    }
    await finish(contextRead, context());
    if (first === "context") {
      await openPersonEditor();
      expect(document.querySelector('[name="input:principal.subject"]')).toBeNull();
      await finish(listingRead, listing());
      await waitFor(() => {
        expect(document.querySelector('[name="input:principal.subject"]')).not.toBeNull();
      });
      fireEvent.change(document.querySelector('[name="input:principal.subject"]')!, {
        target: { value: "synthetic-next" },
      });
      fireEvent.change(document.querySelector('[name="input:profile.displayName"]')!, {
        target: { value: "Synthetic registered person" },
      });
      await chooseAdmissionRole();
    } else {
      await author("synthetic-next");
    }
    expect(screen.getByRole("dialog", { name: "Add person" })).toBeVisible();
    expect(screen.queryByRole("combobox", { name: "People action" })).toBeNull();
    expect(document.querySelector('[name="input:principal.subject"]')).toHaveValue(
      "synthetic-next",
    );
    fireEvent.click(screen.getByRole("button", { name: "Review changes" }));
    await waitForReviewConfirm();
    expect(reviewConfirm()).toBeVisible();
    const [next] = vi.mocked(service.preview).mock.calls.at(-1)!;
    expect(next.input).toMatchObject({
      task: "people.admit",
      binding: { kind: "PEOPLE" },
      inputs: { "input:principal.subject": "synthetic-next" },
    });
    expect(next.input.operationId).not.toBe(prepared.input.operationId);
    expect(next.bytes).not.toBe(prepared.bytes);
    expect(execute).toHaveBeenCalledOnce();
  },
);

const failedContext = (
  kind: "transport" | "host" | "core",
): ApiResult<ServiceResponse<"context">> => {
  if (kind === "transport") {
    return { kind: "deliveryFailure", notice: { kind: "local", reason: "unreachable" } };
  }
  if (kind === "host") {
    return {
      kind: "hostFailure",
      status: 400,
      failure: { code: "INPUT_INVALID", phase: "NOT_STARTED" },
    };
  }
  return {
    kind: "outcome",
    status: 200,
    value: {
      representation: "HUMAN",
      outcome: {
        kind: "FAILED",
        fault: {
          code: "CORE_STORE_UNAVAILABLE" as const,
          messageId: "fault.CORE_STORE_UNAVAILABLE" as const,
          action: "RETRY_SAFE" as const,
        },
      },
    },
  };
};
it.each(["transport", "host", "core"] as const)(
  "accepted operation surfaces %s context refresh failure and allows explicit fresh reload",
  async (kind) => {
    const reads = mockEndpoint("context")
      .mockResolvedValueOnce(context())
      .mockResolvedValueOnce(failedContext(kind))
      .mockResolvedValue(context());
    mockEndpoint("people.list").mockResolvedValue(listing());
    vi.spyOn(service, "execute").mockImplementation((prepared) =>
      Promise.resolve(acceptance(prepared)),
    );
    render(<PeoplePanel onLockChange={vi.fn()} />);
    await author("synthetic-first");
    fireEvent.click(screen.getByRole("button", { name: "Review changes" }));
    await waitForReviewConfirm();
    fireEvent.click(reviewConfirm());
    expect(await screen.findByRole("alert")).toBeVisible();
    expect(screen.queryByRole("combobox", { name: "People action" })).toBeNull();
    expect(reads).toHaveBeenCalledTimes(2);
    fireEvent.click(screen.getByRole("button", { name: /^Try again$/u }));
    await openPersonEditor();
    expect(screen.getByRole("dialog", { name: "Add person" })).toBeVisible();
    expect(document.querySelector('[name="input:principal.subject"]')).toHaveValue("");
    expect(screen.queryByRole("alert")).toBeNull();
    expect(reads).toHaveBeenCalledTimes(3);
  },
);
it("context reload preserves an uncertain prepared people request and exact retry identity", async () => {
  const oldListing = pending<Awaited<ReturnType<typeof service.peopleList>>>();
  mockEndpoint("context")
    .mockResolvedValueOnce(context())
    .mockResolvedValueOnce(failedContext("transport"))
    .mockResolvedValue(context());
  mockEndpoint("people.list").mockReturnValueOnce(oldListing.promise).mockResolvedValue(listing());
  const execute = vi
    .spyOn(service, "execute")
    .mockResolvedValueOnce({
      kind: "deliveryFailure",
      notice: { kind: "local", reason: "unreachable" },
    })
    .mockImplementation((prepared) => Promise.resolve(acceptance(prepared)));
  render(<PeoplePanel onLockChange={vi.fn()} />);
  await author("synthetic-pending");
  fireEvent.click(screen.getByRole("button", { name: "Review changes" }));
  await waitForReviewConfirm();
  fireEvent.click(reviewConfirm());
  await screen.findByRole("button", { name: "Send again" });
  const [prepared] = execute.mock.calls[0]!;
  await finish(oldListing, {
    kind: "hostFailure",
    status: 401,
    failure: { code: "SESSION_REJECTED", phase: "NOT_STARTED" },
  });
  fireEvent.click(await screen.findByRole("button", { name: /^Try again$/u }));
  await waitFor(() => {
    expect(screen.queryByRole("button", { name: /^Try again$/u })).toBeNull();
  });
  expect(execute.mock.calls[0]![0]).toBe(prepared);
  expect(screen.queryByText("synthetic-pending")).toBeNull();
  expect(screen.queryByRole("button", { name: "Review changes" })).toBeNull();
  fireEvent.click(screen.getByRole("button", { name: "Continue current request" }));
  fireEvent.click(await screen.findByRole("button", { name: "Send again" }));
  await waitFor(() => {
    expect(screen.queryByRole("button", { name: "Send again" })).toBeNull();
  });
  expect(execute.mock.calls[1]![0]).toBe(prepared);
  expect(execute.mock.calls[1]![0].bytes).toBe(prepared.bytes);
});
