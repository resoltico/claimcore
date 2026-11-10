import {
  reviewConfirm,
  queryReviewConfirm,
  waitForReviewConfirm,
} from "../support/review-controls";
import { beforeEach, it, expect, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { render, screen, waitFor } from "../support/presentation-test-support";
import { editor, previewReply, deferredResponse } from "../support/presentation-state.fixtures";
import { wireResponse } from "../support/reply.fixtures";
import { endpointResponse } from "../support/transport-response.fixtures";
beforeEach(() => vi.stubGlobal("fetch", vi.fn()));
it("returns focus to the drawer review action after returning to editing", async () => {
  const pending = deferredResponse();
  vi.mocked(fetch).mockReturnValueOnce(pending.promise);
  render(editor());
  const user = userEvent.setup();
  const preview = screen.getByRole("button", { name: "Review changes" });
  await user.click(preview);
  expect(preview).toBeDisabled();
  expect(screen.queryByRole("radio", { name: /English/u })).toBeNull();
  pending.resolve(previewReply());
  await user.click(await screen.findByRole("button", { name: "Return to editing" }));
  await waitFor(() => {
    expect(screen.getByRole("button", { name: "Review changes" })).toHaveFocus();
  });
});
const reviewAndConfirm = async () => {
  const user = userEvent.setup();
  await user.click(screen.getByRole("button", { name: "Review changes" }));
  await waitForReviewConfirm();
  await user.click(reviewConfirm());
  return user;
};
it("reviews before execution and sends the exact reviewed request", async () => {
  vi.mocked(fetch)
    .mockImplementationOnce(() => Promise.resolve(previewReply()))
    .mockResolvedValueOnce(wireResponse("operation.execute"));
  const committed = vi.fn();
  render(editor({ onCommitted: committed }));
  const user = await reviewAndConfirm();
  expect(await screen.findByRole("heading", { name: "Recorded change" })).toBeVisible();
  expect(vi.mocked(fetch).mock.calls[0]?.[0]).toBe("/api/operation.preview");
  expect(vi.mocked(fetch).mock.calls[1]?.[0]).toBe("/api/operation.execute");
  expect(vi.mocked(fetch).mock.calls[0]?.[1]?.body).toBe(vi.mocked(fetch).mock.calls[1]?.[1]?.body);
  await user.click(screen.getByRole("button", { name: "Return to case" }));
  expect(committed).toHaveBeenCalledOnce();
});
it.each(["lost", "unknown", "malformed"] as const)(
  "%s execution keeps one exact retry action",
  async (mode) => {
    vi.mocked(fetch).mockImplementationOnce(() => Promise.resolve(previewReply()));
    if (mode === "lost") {
      vi.mocked(fetch).mockRejectedValueOnce(new Error("Synthetic loss"));
    } else if (mode === "unknown") {
      vi.mocked(fetch).mockResolvedValueOnce(
        endpointResponse("operation.execute", {
          representation: "HUMAN",
          outcome: { kind: "OUTCOME_UNKNOWN" },
        }),
      );
    }
    if (mode === "malformed") {
      vi.mocked(fetch).mockResolvedValueOnce(
        endpointResponse("operation.execute", {
          representation: "HUMAN",
          outcome: { kind: "ACCEPTED", document: { missing: "PRIVATE_CANARY" } },
        }),
      );
    }
    vi.mocked(fetch).mockResolvedValueOnce(wireResponse("operation.execute"));
    const locked = vi.fn();
    render(editor({ onMutationLockChange: locked }));
    const user = await reviewAndConfirm();
    expect(await screen.findByRole("button", { name: "Send again" })).toBeVisible();
    expect(screen.getByRole("alert")).toHaveTextContent("could not confirm");
    expect(locked).toHaveBeenCalledWith(true, expect.any(Object));
    const before = vi.mocked(fetch).mock.calls[1]?.[1]?.body;
    await user.click(screen.getByRole("button", { name: "Send again" }));
    await screen.findByRole("heading", { name: "Recorded change" });
    expect(vi.mocked(fetch).mock.calls[2]?.[1]?.body).toBe(before);
  },
);
it("readonly preview failure stays editable and cannot submit", async () => {
  vi.mocked(fetch).mockRejectedValueOnce(new Error("Synthetic failure"));
  render(editor());
  await userEvent.setup().click(screen.getByRole("button", { name: "Review changes" }));
  await screen.findByRole("alert");
  for (const close of screen.getAllByRole("button", { name: "Close" })) {
    expect(close).not.toBeDisabled();
  }
  expect(queryReviewConfirm()).toBeNull();
});
it("an invalid input rejection focuses and labels the exact authoring field", async () => {
  vi.mocked(fetch).mockResolvedValueOnce(
    endpointResponse("operation.preview", {
      representation: "HUMAN",
      outcome: {
        kind: "REJECTED",
        rejection: {
          kind: "INPUT",
          advice: {
            message: { messageId: "diagnostic.INVALID_INPUT", arguments: [] },
            action: "CORRECT_INPUT",
          },
          issues: [
            {
              input: "field:caseReference",
              row: null,
              related: null,
              message: { messageId: "issue.TEXT_REQUIRED", arguments: [] },
            },
          ],
        },
      },
    }),
  );
  render(editor({ current: null, initialAct: "OPEN" }));
  await userEvent.setup().click(screen.getByRole("button", { name: "Review changes" }));
  const input = screen.getByLabelText("Case reference", { exact: true });
  await waitFor(() => {
    expect(screen.getByRole("alert")).toHaveFocus();
  });
  expect(input).toHaveAttribute("aria-invalid", "true");
  expect(document.body).not.toHaveTextContent("PRIVATE_CANARY");
});
