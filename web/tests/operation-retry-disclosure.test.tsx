import { beforeEach, expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { act } from "@testing-library/react";
import { render, screen } from "./presentation-test-support";
import { current, deferredResponse, draftAt, editor } from "./presentation-state.fixtures";
import { generatedWebValue } from "./contract-corpus.fixtures";
import { fields, response, runtime, unavailable } from "./service-ui.fixtures";

beforeEach(() => vi.stubGlobal("fetch", vi.fn()));
const refusal = (kind: "access" | "session") =>
  kind === "access"
    ? response("case.execute", "REJECTED", unavailable)
    : new Response(
        JSON.stringify({
          kind: "HOST_FAILURE",
          code: "SESSION_REJECTED",
          status: 401,
          diagnostic: { id: "HOST_SESSIONREJECTED", parameters: {} },
          message: "Synthetic session refusal",
          executionPhase: "NOT_STARTED",
        }),
        { status: 401, headers: { "content-type": "application/json" } },
      );
const currentCanary = "CURRENT_DISCLOSURE_CANARY";
const reviewCanary = "REVIEW_DISCLOSURE_CANARY";
const disclosedCurrent = {
  ...current,
  state: {
    ...current.state,
    tag: "LIVE" as const,
    disposition: "ACTIVE" as const,
    fields: { ...fields, claimantName: currentCanary },
  },
};
const preview = () =>
  response("case.preview", "REVIEWED", {
    review: {
      before: disclosedCurrent,
      after: {
        ...disclosedCurrent,
        revision: 2,
        state: {
          ...disclosedCurrent.state,
          fields: { ...fields, claimantName: reviewCanary },
        },
      },
      changes: [{ field: "claimantName", before: currentCanary, after: reviewCanary }],
      context: runtime,
    },
  });
const replay = () => {
  const accepted = generatedWebValue("case.execute");
  if (accepted.outcome.tag !== "ACCEPTED") {
    throw new Error("Missing acceptance fixture");
  }
  return response("case.execute", "ALREADY_ACCEPTED", {
    ...accepted.outcome.data,
    snapshot: { tag: "WITHHELD" },
  });
};
const beginUnknown = async (onMutationLockChange: (locked: boolean) => void) => {
  const user = userEvent.setup();
  render(
    editor({
      onMutationLockChange,
      current: disclosedCurrent,
      initialCommand: "AMEND_REGISTRATION",
    }),
  );
  const claimant = screen.getByLabelText("Claimant name", { exact: true });
  expect(claimant).toHaveValue(currentCanary);
  await user.clear(claimant);
  await user.type(claimant, reviewCanary);
  await user.click(screen.getByRole("button", { name: "Review changes" }));
  await screen.findByRole("dialog", { name: "Review changes" });
  expect(document.body).toHaveTextContent(currentCanary);
  expect(document.body).toHaveTextContent(reviewCanary);
  await user.click(screen.getByRole("checkbox", { name: "I confirm these changes." }));
  await user.click(screen.getByRole("button", { name: "Record changes" }));
  await screen.findByRole("button", { name: "Retry the same operation" });
  return user;
};
const expectPendingOnly = (locked: (locked: boolean) => void) => {
  expect(screen.queryByRole("dialog")).toBeNull();
  expect(screen.queryByRole("button", { name: "Review changes" })).toBeNull();
  expect(screen.queryByRole("button", { name: "Back to case" })).toBeNull();
  expect(screen.queryByRole("heading", { name: "Recorded change" })).toBeNull();
  expect(document.body).not.toHaveTextContent(currentCanary);
  expect(document.body).not.toHaveTextContent(reviewCanary);
  expect(locked).toHaveBeenLastCalledWith(true);
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
    await user.click(screen.getByRole("button", { name: "Retry the same operation" }));
    expectPendingOnly(locked);
    await user.click(screen.getByRole("button", { name: "Retry the same operation" }));
    expectPendingOnly(locked);
    expect(screen.getByRole("button", { name: "Retry the same operation" })).toBeDisabled();
    await act(async () => {
      pending.resolve(response("case.execute", "OUTCOME_UNKNOWN", {}));
      await pending.promise;
    });
    expect(screen.getByRole("button", { name: "Retry the same operation" })).not.toBeDisabled();
    expectPendingOnly(locked);
    await user.click(screen.getByRole("button", { name: "Retry the same operation" }));
    expect(await screen.findByRole("heading", { name: "Recorded change" })).toBeVisible();
    expect(
      screen.getByText("The recorded fields are withheld under your current access rights."),
    ).toBeVisible();
    expect(document.body).not.toHaveTextContent(currentCanary);
    expect(document.body).not.toHaveTextContent(reviewCanary);
    expect(screen.queryByRole("alert")).toBeNull();
    expect(screen.queryByRole("button", { name: "Retry the same operation" })).toBeNull();
    expect(locked).toHaveBeenLastCalledWith(false);
    expect(
      vi
        .mocked(fetch)
        .mock.calls.slice(1)
        .map((call) => call[1]?.body),
    ).toEqual([original, original, original, original]);
  },
);
