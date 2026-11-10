import { testJournal, recoveryScope } from "../support/recovery.fixtures";
import { reviewConfirm, waitForReviewConfirm } from "../support/review-controls";
import { selectFact } from "../support/field-entry";
import { caseAdvice } from "../support/case-advice.fixtures";
import { beforeEach, expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { act } from "@testing-library/react";
import {
  findButton,
  getButton,
  render,
  screen,
  waitFor,
} from "../support/presentation-test-support";
import { deferredResponse, draftAt, editor } from "../support/presentation-state.fixtures";
import { wireReply } from "../support/reply.fixtures";
import { fields } from "../support/service-ui.fixtures";
import { currentCaseInput, workBasis } from "../support/inputs";
import { endpointResponse } from "../support/transport-response.fixtures";
import { operationTask } from "../../src/protocol/taskCatalog";
beforeEach(() => vi.stubGlobal("fetch", vi.fn()));
const refusal = (kind: "access" | "session") =>
  kind === "access"
    ? endpointResponse("operation.execute", {
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
      })
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
const disclosedCurrent = currentCaseInput({
  caseId: "20000000-0000-4000-8000-000000000001",
  caseReference: fields.caseReference,
  revision: 1,
  advice: { ...caseAdvice(["AMEND_REGISTRATION"]), history: "FULL" },
  state: {
    kind: "ACTIVE",
    fields: { ...fields, registration: { ...fields.registration, claimantName: currentCanary } },
  },
});
const preview = () => {
  const task = operationTask("CASE:AMEND_REGISTRATION");
  return endpointResponse("operation.preview", {
    representation: "HUMAN",
    outcome: {
      kind: "REVIEWED",
      review: {
        document: {
          screen: { regions: [] },
          context: {
            kind: "REVIEW",
            task: task.id,
            inputs: task.inputs.map((input) => input.id),
            consequence: task.consequence,
            target: { kind: "CASE", caseId: disclosedCurrent.context.caseId, beforeRevision: 1 },
            confirmation: { kind: "CASE_REFERENCE", reference: fields.caseReference },
          },
          basis: { kind: "SNAPSHOT", observation: workBasis },
          disclosure: "CURRENT",
        },
        presentation: {
          changes: [
            {
              key: "claimantName",
              labelId: "field.claimantName.label",
              before: { kind: "PARTY", value: currentCanary },
              after: { kind: "PARTY", value: reviewCanary },
            },
          ],
          unchanged: [],
        },
      },
    },
  });
};
const replay = () => {
  const accepted = wireReply("operation.execute");
  if (accepted.outcome.kind !== "ACCEPTED") {
    throw new Error("Acceptance fixture required");
  }
  return endpointResponse("operation.execute", {
    representation: "HUMAN",
    outcome: {
      kind: "ALREADY_ACCEPTED",
      document: {
        ...accepted.outcome.document,
        disclosure: "WITHHELD",
        screen: {
          regions: [
            {
              role: "NOTICE",
              content: { kind: "NOTICE", tone: "INFO", messageId: "label.detailsWithheld" },
            },
          ],
        },
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
  await screen.findByRole("dialog");
  expect(document.body).toHaveTextContent(currentCanary);
  expect(document.body).toHaveTextContent(reviewCanary);
  await waitForReviewConfirm();
  await user.click(reviewConfirm());
  await findButton("Send again");
  return user;
};
const expectPendingOnly = (locked: (locked: boolean) => void, busy = false) => {
  expect(screen.queryByRole("dialog", { name: "Discard these draft changes?" })).toBeNull();
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
  expect(locked).toHaveBeenLastCalledWith(busy, expect.any(Object));
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
    expect((await testJournal.list(recoveryScope)).map((entry) => entry.operationId)).toContain(
      draftAt(1).operationId,
    );
    expect(draftAt(1).expectedRevision).toBe(1);
    await user.click(getButton("Send again"));
    expectPendingOnly(locked);
    await user.click(getButton("Send again"));
    expectPendingOnly(locked, true);
    expect(getButton("Send again")).toBeDisabled();
    await act(async () => {
      pending.resolve(
        endpointResponse("operation.execute", {
          representation: "HUMAN",
          outcome: { kind: "OUTCOME_UNKNOWN" },
        }),
      );
      await pending.promise;
    });
    expect(getButton("Send again")).not.toBeDisabled();
    expectPendingOnly(locked);
    await user.click(getButton("Send again"));
    await waitFor(async () => {
      expect(await testJournal.list(recoveryScope)).toEqual([]);
    });
    expect(screen.queryByRole("heading", { name: "Recorded change" })).toBeNull();
    expect(screen.queryByText("You do not have access to these details.")).toBeNull();
    expect(document.body).not.toHaveTextContent(currentCanary);
    expect(document.body).not.toHaveTextContent(reviewCanary);
    expect(screen.queryByRole("alert")).toBeNull();
    expect(
      screen.queryByRole("button", {
        name: "Send again",
      }),
    ).toBeNull();
    expect(locked).toHaveBeenLastCalledWith(false, expect.any(Object));
    expect(
      vi
        .mocked(fetch)
        .mock.calls.slice(1)
        .map((call) => call[1]?.body),
    ).toEqual([original, original, original, original]);
  },
);
