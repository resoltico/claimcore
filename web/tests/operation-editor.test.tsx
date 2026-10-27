import { beforeEach, it, expect, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { render, screen, waitFor } from "./presentation-test-support";
import {
  editor,
  previewReply,
  deferredResponse,
  languageControl,
} from "./presentation-state.fixtures";
import { wireResponse } from "./contract-corpus.fixtures";
import { response, operationId } from "./service-ui.fixtures";
beforeEach(() => vi.stubGlobal("fetch", vi.fn()));
it("returns focus to preview after presentation controls were used during pending review", async () => {
  const pending = deferredResponse();
  vi.mocked(fetch).mockReturnValueOnce(pending.promise);
  render(editor());
  const user = userEvent.setup();
  const preview = screen.getByRole("button", { name: "Review changes" });
  await user.click(preview);
  expect(preview).toBeDisabled();
  await user.click(languageControl());
  pending.resolve(previewReply());
  await user.click(await screen.findByRole("button", { name: "Return to editing" }));
  await waitFor(() => {
    expect(document.activeElement).toBe(preview);
  });
});
const reviewAndConfirm = async () => {
  const user = userEvent.setup();
  await user.click(screen.getByRole("button", { name: "Review changes" }));
  await user.click(await screen.findByRole("checkbox", { name: "I confirm these changes." }));
  await user.click(screen.getByRole("button", { name: "Record changes" }));
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
        response("operation.execute", "OUTCOME_UNKNOWN", { operationId }),
      );
    }
    if (mode === "malformed") {
      vi.mocked(fetch).mockResolvedValueOnce(
        response("operation.execute", "ACCEPTED", { operationId }),
      );
    }
    vi.mocked(fetch).mockResolvedValueOnce(wireResponse("operation.execute"));
    const locked = vi.fn();
    render(editor({ onMutationLockChange: locked }));
    const user = await reviewAndConfirm();
    expect(await screen.findByRole("button", { name: "Send again" })).toBeVisible();
    expect(screen.getByRole("alert")).toHaveTextContent("unknown");
    expect(locked).toHaveBeenCalledWith(true);
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
  expect(screen.getByRole("button", { name: "Back to case" })).not.toBeDisabled();
  expect(screen.queryByRole("button", { name: "Record changes" })).toBeNull();
});
it("an invalid input rejection focuses and labels the exact authoring field", async () => {
  vi.mocked(fetch).mockResolvedValueOnce(
    response("operation.preview", "REJECTED", {
      code: "TEXT_REQUIRED",
      action: "CORRECT_INPUT",
      target: "caseReference",
    }),
  );
  render(editor({ current: null, initialAct: "OPEN" }));
  await userEvent.setup().click(screen.getByRole("button", { name: "Review changes" }));
  const input = screen.getByLabelText("Handler's case reference", { exact: true });
  await waitFor(() => {
    expect(document.activeElement).toBe(input);
  });
  expect(input).toHaveAttribute("aria-invalid", "true");
  expect(document.body).not.toHaveTextContent("PRIVATE_CANARY");
});
