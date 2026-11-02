import { selectFact } from "../support/field-entry";
import { caseAdvice } from "../support/case-advice.fixtures";
import { beforeEach, expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { act } from "@testing-library/react";
import { findButton, getButton, render, screen } from "../support/presentation-test-support";
import { current, deferredResponse, draftAt, editor } from "../support/presentation-state.fixtures";
import { wireReply } from "../support/contract-corpus.fixtures";
import { fields, response, runtime, unavailable } from "../support/service-ui.fixtures";
beforeEach(() => vi.stubGlobal("fetch", vi.fn()));
const refusal = (kind: "access" | "session") =>
  kind === "access"
    ? response("operation.execute", "REJECTED", unavailable)
    : new Response(
        JSON.stringify({
          hostFailure: {
            code: "SESSION_REJECTED",
            phase: "NOT_STARTED",
          },
        }),
        {
          status: 401,
          headers: {
            "content-type": "application/json",
          },
        },
      );
const currentCanary = "CURRENT_DISCLOSURE_CANARY";
const reviewCanary = "REVIEW_DISCLOSURE_CANARY";
const disclosedCurrent = {
  ...current,
  advice: {
    ...current.advice,
    ...caseAdvice(["AMEND_REGISTRATION"]),
  },
  state: {
    ...current.state,
    kind: "ACTIVE" as const,
    fields: {
      ...fields,
      registration: {
        ...fields.registration,
        claimantName: currentCanary,
      },
    },
  },
};
const preview = () =>
  response("operation.preview", "REVIEWED", {
    review: {
      subject: {
        kind: "CASE",
        before: disclosedCurrent,
        after: {
          ...disclosedCurrent,
          revision: 2,
          state: {
            ...disclosedCurrent.state,
            fields: {
              ...fields,
              registration: {
                ...fields.registration,
                claimantName: reviewCanary,
              },
            },
          },
        },
        changes: [
          {
            field: "claimantName",
            before: currentCanary,
            after: reviewCanary,
          },
        ],
        context: runtime,
      },
    },
  });
const replay = () => {
  const accepted = wireReply("operation.execute");
  if (accepted.outcome.kind !== "ACCEPTED") {
    throw new Error("Missing acceptance fixture");
  }
  return response("operation.execute", "ALREADY_ACCEPTED", {
    ...accepted.outcome.data,
    subject: {
      ...accepted.outcome.data.subject,
      snapshot: {
        kind: "WITHHELD",
      },
    },
  });
};
const beginUnknown = async (onMutationLockChange: (locked: boolean) => void) => {
  const user = userEvent.setup();
  render(
    editor({
      onMutationLockChange,
      current: disclosedCurrent,
      initialAct: "AMEND_REGISTRATION",
    }),
  );
  selectFact("claimantName");
  const claimant = screen.getByLabelText("Claimant name", {
    exact: true,
  });
  expect(claimant).toHaveValue(currentCanary);
  await user.clear(claimant);
  await user.type(claimant, reviewCanary);
  await user.click(getButton("Review changes"));
  await screen.findByRole("dialog", {
    name: "Review changes",
  });
  expect(document.body).toHaveTextContent(currentCanary);
  expect(document.body).toHaveTextContent(reviewCanary);
  await user.click(
    screen.getByRole("checkbox", {
      name: "I confirm these changes.",
    }),
  );
  await user.click(getButton("Record changes"));
  await findButton("Send again");
  return user;
};
const expectPendingOnly = (locked: (locked: boolean) => void, busy = false) => {
  expect(screen.queryByRole("dialog")).toBeNull();
  expect(
    screen.queryByRole("button", {
      name: "Review changes",
    }),
  ).toBeNull();
  expect(
    screen.queryByRole("button", {
      name: "Back to case",
    }),
  ).toBeNull();
  expect(
    screen.queryByRole("heading", {
      name: "Recorded change",
    }),
  ).toBeNull();
  expect(document.body).not.toHaveTextContent(currentCanary);
  expect(document.body).not.toHaveTextContent(reviewCanary);
  expect(locked).toHaveBeenLastCalledWith(busy);
};
it.each(["access", "session"] as const)(
  "%s refusal during uncertainty preserves exact retry through in-flight recovery and replay",
  async (kind) => {
    const pending = deferredResponse();
    vi.mocked(fetch)
      .mockImplementationOnce(() => Promise.resolve(preview()))
      .mockRejectedValueOnce(new Error("Synthetic lost confirmation"))
      .mockImplementationOnce(() => Promise.resolve(refusal(kind)))
      .mockReturnValueOnce(pending.promise)
      .mockImplementationOnce(() => Promise.resolve(replay()));
    const locked = vi.fn();
    const user = await beginUnknown(locked);
    const original = vi.mocked(fetch).mock.calls[1]?.[1]?.body;
    expect(draftAt(1).expectedRevision).toBe(1);
    await user.click(getButton("Send again"));
    expectPendingOnly(locked);
    await user.click(getButton("Send again"));
    expectPendingOnly(locked, true);
    expect(getButton("Send again")).toBeDisabled();
    await act(async () => {
      pending.resolve(response("operation.execute", "OUTCOME_UNKNOWN", {}));
      await pending.promise;
    });
    expect(getButton("Send again")).not.toBeDisabled();
    expectPendingOnly(locked);
    await user.click(getButton("Send again"));
    expect(
      await screen.findByRole("heading", {
        name: "Recorded change",
      }),
    ).toBeVisible();
    expect(
      screen.getByText("The recorded fields are withheld under your current access rights."),
    ).toBeVisible();
    expect(document.body).not.toHaveTextContent(currentCanary);
    expect(document.body).not.toHaveTextContent(reviewCanary);
    expect(screen.queryByRole("alert")).toBeNull();
    expect(
      screen.queryByRole("button", {
        name: "Send again",
      }),
    ).toBeNull();
    expect(locked).toHaveBeenLastCalledWith(false);
    expect(
      vi
        .mocked(fetch)
        .mock.calls.slice(1)
        .map((call) => call[1]?.body),
    ).toEqual([original, original, original, original]);
  },
);
