import { it, expect, vi, beforeEach } from "vitest";
import userEvent from "@testing-library/user-event";
import { render, screen } from "./presentation-test-support";
import { editor, previewReply } from "./presentation-state.fixtures";
import { wireReply } from "./contract-corpus.fixtures";
import { response } from "./service-ui.fixtures";
beforeEach(() => vi.stubGlobal("fetch", vi.fn()));
it("readonly preview can be repeated with its unchanged exact draft after lost output", async () => {
  vi.mocked(fetch)
    .mockRejectedValueOnce(new Error("Synthetic loss"))
    .mockImplementationOnce(() => Promise.resolve(previewReply()));
  render(editor());
  const user = userEvent.setup();
  await user.click(screen.getByRole("button", { name: "Review changes" }));
  await screen.findByRole("alert");
  await user.click(screen.getByRole("button", { name: "Review changes" }));
  await screen.findByRole("dialog", { name: "Review changes" });
  expect(vi.mocked(fetch).mock.calls[1]?.[1]?.body).toBe(vi.mocked(fetch).mock.calls[0]?.[1]?.body);
});
it("already accepted preview returns its recorded receipt without another execution", async () => {
  const value = wireReply("operation.execute");
  if (value.outcome.tag !== "ACCEPTED") {
    throw new Error("Accepted fixture required.");
  }
  vi.mocked(fetch).mockResolvedValueOnce(
    response("operation.preview", "ALREADY_ACCEPTED", value.outcome.data),
  );
  render(editor());
  await userEvent.setup().click(screen.getByRole("button", { name: "Review changes" }));
  await screen.findByRole("heading", { name: "Recorded change" });
  expect(fetch).toHaveBeenCalledOnce();
});
