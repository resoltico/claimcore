import { afterEach, expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { render, screen, waitFor } from "../../support/presentation-test-support";
import {
  AccessRequests,
  type AccessRequestControl,
} from "../../../src/views/authority/AccessRequests";
import type { HumanReadDocument_authorityRequests } from "@contracts/client";

const handles: AccessRequestControl[] = ["first", "second"].map((key) => ({
  key,
  principal: { kind: "HUMAN", issuer: "https://issuer.example/", subject: key },
  profileSeed: { displayName: `${key} unverified hint`, contact: null },
}));
const document: HumanReadDocument_authorityRequests = {
  screen: {
    regions: [
      {
        role: "RESULTS",
        content: {
          kind: "TABLE",
          columns: [
            { key: "caption", labelId: "label.details", prominence: "PRIMARY", isMoney: false },
          ],
          rows: [
            { key: "second", values: [{ kind: "TEXT", value: "Core second caption Ω" }] },
            { key: "first", values: [{ kind: "TEXT", value: "Core first caption Ψ" }] },
          ],
        },
      },
    ],
  },
  context: { kind: "ACCESS_REQUESTS", requests: [...handles].reverse(), nextCursor: null },
  basis: {
    kind: "SNAPSHOT",
    observation: {
      observedAt: "2026-11-09T12:00:00.000000Z",
      businessDate: "2026-11-09",
      timeZone: "Etc/UTC",
    },
  },
  disclosure: "CURRENT",
};
const reply = (value: unknown) =>
  new Response(
    JSON.stringify({ representation: "HUMAN", outcome: { kind: "SUCCEEDED", document: value } }),
    { status: 200, headers: { "Content-Type": "application/json" } },
  );
afterEach(() => vi.unstubAllGlobals());

it("binds Core request row keys to unverified admission controls without reconstructing captions from claims", async () => {
  vi.stubGlobal("fetch", vi.fn().mockResolvedValue(reply(document)));
  const admit = vi.fn();
  render(<AccessRequests available busy={false} onAdmit={admit} onDisclosureRefused={vi.fn()} />);
  await screen.findByText("Core second caption Ω");
  await userEvent.click(screen.getAllByRole("button", { name: "Add person" })[0]!);
  expect(admit).toHaveBeenCalledWith(handles[1]);
  expect(screen.queryByText("second unverified hint")).not.toBeInTheDocument();
});

it("dismisses only the selected principal and refreshes once after a confirmed HUMAN result", async () => {
  const fetch = vi
    .fn()
    .mockResolvedValueOnce(reply(document))
    .mockResolvedValueOnce(
      reply({
        screen: { regions: [] },
        context: { kind: "EMPTY" },
        basis: document.basis,
        disclosure: "CURRENT",
      }),
    )
    .mockResolvedValueOnce(
      reply({
        ...document,
        screen: { regions: [] },
        context: { ...document.context, requests: [] },
      }),
    );
  vi.stubGlobal("fetch", fetch);
  render(<AccessRequests available busy={false} onAdmit={vi.fn()} onDisclosureRefused={vi.fn()} />);
  await screen.findByText("Core second caption Ω");
  await userEvent.click(screen.getAllByRole("button", { name: /dismiss/i })[0]!);
  const dialog = screen.getByRole("dialog");
  expect(dialog).toHaveTextContent("Core second caption Ω");
  expect(dialog).not.toHaveTextContent("Core first caption Ψ");
  const { within } = await import("@testing-library/react");
  await userEvent.click(within(dialog).getByRole("button", { name: /dismiss/i }));
  await waitFor(() => expect(fetch).toHaveBeenCalledTimes(3));
  expect(fetch.mock.calls[1]![0]).toBe("/api/authority.requests.dismiss");
  expect(JSON.parse(fetch.mock.calls[1]![1].body)).toEqual(handles[1]!.principal);
});
