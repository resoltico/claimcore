import { type RequestByEndpoint } from "@contracts/client";
import { requestText, endpointResponse } from "../support/transport-response.fixtures";
import { it, expect, vi, beforeEach } from "vitest";
import userEvent from "@testing-library/user-event";
import { render, screen } from "../support/presentation-test-support";
import { editor, previewReply } from "../support/presentation-state.fixtures";
import { wireReply } from "../support/reply.fixtures";
beforeEach(() => vi.stubGlobal("fetch", vi.fn()));
it("readonly preview can be repeated with unchanged command meaning after lost output", async () => {
  vi.mocked(fetch)
    .mockRejectedValueOnce(new Error("Synthetic loss"))
    .mockImplementationOnce(() => Promise.resolve(previewReply()));
  render(editor());
  const user = userEvent.setup();
  await user.click(
    screen.getByRole("button", {
      name: "Review changes",
    }),
  );
  await screen.findByRole("alert");
  await user.click(
    screen.getByRole("button", {
      name: "Review changes",
    }),
  );
  await screen.findByRole("dialog", {
    name: "Close the case",
  });
  const first: unknown = JSON.parse(requestText(vi.mocked(fetch).mock.calls[0]?.[1]?.body));
  const next: unknown = JSON.parse(requestText(vi.mocked(fetch).mock.calls[1]?.[1]?.body));
  const operationPreviewRequest = first as RequestByEndpoint["operation.preview"];
  const { operationId: firstId, ...firstCommand } = operationPreviewRequest;
  const operationPreviewRequest2 = next as RequestByEndpoint["operation.preview"];
  const { operationId: nextId, ...nextCommand } = operationPreviewRequest2;
  expect(nextCommand).toEqual(firstCommand);
  expect(nextId).not.toBe(firstId);
});
it("already accepted preview returns its recorded receipt without another execution", async () => {
  const value = wireReply("operation.execute");
  if (value.outcome.kind !== "ACCEPTED") {
    throw new Error("Accepted fixture required.");
  }
  vi.mocked(fetch).mockResolvedValueOnce(
    endpointResponse("operation.preview", {
      representation: "HUMAN",
      outcome: {
        kind: "ALREADY_ACCEPTED",
        document: value.outcome.document,
      },
    }),
  );
  render(editor());
  await userEvent.setup().click(
    screen.getByRole("button", {
      name: "Review changes",
    }),
  );
  await screen.findByRole("heading", {
    name: "Recorded change",
  });
  expect(fetch).toHaveBeenCalledOnce();
});
