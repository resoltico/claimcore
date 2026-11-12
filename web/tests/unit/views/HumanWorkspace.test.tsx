import { requestAddress } from "../../support/transport-response.fixtures";
import { afterEach, expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { render, screen, waitFor } from "../../support/presentation-test-support";
import { Dashboard } from "../../../src/views/Dashboard";
import { navigate } from "../../../src/routing/navigation";
import { activeRequester } from "../../support/human-requester.fixtures";
import { isResponse, type HumanReadDocument_context } from "@contracts/client";

const reply = (document: unknown) => ({
  representation: "HUMAN",
  outcome: { kind: "SUCCEEDED", document },
});
const response = (document: unknown) =>
  new Response(JSON.stringify(reply(document)), {
    status: 200,
    headers: { "Content-Type": "application/json" },
  });
const inactive = (
  state: "NO_ROLES" | "DISABLED" | "NOT_REGISTERED",
): HumanReadDocument_context => ({
  screen: {
    regions: [
      {
        role: "IDENTITY",
        content: {
          kind: "ENTRIES",
          entries: [
            {
              key: "guidance",
              labelId: "label.account",
              prominence: "PRIMARY",
              value: { kind: "TEXT", value: `Core caller guidance ${state} Ω` },
            },
          ],
        },
      },
      {
        role: "DETAILS",
        titleId: "label.recordDetails",
        content: {
          kind: "ENTRIES",
          entries: [
            {
              key: "own-identity",
              labelId: "label.details",
              prominence: "DETAIL",
              value: { kind: "IDENTIFIER", value: "Core caller technical identity Ψ" },
            },
          ],
        },
      },
    ],
  },
  context: {
    kind: "REQUESTER",
    state,
    installationId: state === "NO_ROLES" ? activeRequester.context.installationId : null,
    actorId: state === "NOT_REGISTERED" ? null : activeRequester.context.actorId,
    tasks: [],
    views: [],
    authorityIssuerSeed: null,
  },
  basis: { kind: "UNAVAILABLE" },
  disclosure: "CURRENT",
});
afterEach(() => vi.unstubAllGlobals());
it.each(["NO_ROLES", "DISABLED", "NOT_REGISTERED"] as const)(
  "renders Core caller-owned %s guidance and Details without issuing resource reads",
  async (state) => {
    const document = inactive(state);
    expect(isResponse("context", reply(document))).toBe(true);
    const fetch = vi.fn<typeof globalThis.fetch>().mockResolvedValue(response(document));
    vi.stubGlobal("fetch", fetch);
    navigate("/cases", true);
    render(<Dashboard />);
    expect(await screen.findByText(`Core caller guidance ${state} Ω`)).toBeVisible();
    expect(screen.getByText("Core caller technical identity Ψ")).not.toBeVisible();
    await userEvent.click(screen.getByText("Record details"));
    expect(screen.getByText("Core caller technical identity Ψ")).toBeVisible();
    expect(fetch.mock.calls.map((call) => call[0])).toEqual(["/api/context"]);
  },
);
it("uses advertised query tasks for deep-link availability and the supplied own-account caption", async () => {
  const document: HumanReadDocument_context = {
    ...activeRequester,
    screen: {
      regions: [
        {
          role: "IDENTITY",
          content: {
            kind: "ENTRIES",
            entries: [
              {
                key: "self",
                labelId: "label.account",
                prominence: "PRIMARY",
                value: { kind: "TEXT", value: "Core own account caption Ω" },
              },
            ],
          },
        },
      ],
    },
    context: { ...activeRequester.context, views: ["OPERATIONS"], tasks: [] },
  };
  const fetch = vi.fn<typeof globalThis.fetch>().mockImplementation((url) =>
    Promise.try(() => {
      if (requestAddress(url) !== "/api/context") {
        throw new Error(`Unadvertised resource read: ${requestAddress(url)}`);
      }
      return response(document);
    }),
  );
  vi.stubGlobal("fetch", fetch);
  navigate("/authority-history", true);
  render(<Dashboard />);
  await screen.findByText("Core own account caption Ω");
  expect(screen.getByRole("heading", { name: "Authority history", level: 1 })).toBeVisible();
  expect(screen.getByRole("alert")).toBeVisible();
  expect(fetch.mock.calls.map((call) => call[0])).toEqual(["/api/context"]);
});
it("masks the old requester Screen during a failed access refresh", async () => {
  const fetch = vi
    .fn<typeof globalThis.fetch>()
    .mockResolvedValueOnce(response(inactive("NO_ROLES")))
    .mockRejectedValue(new TypeError("offline"));
  vi.stubGlobal("fetch", fetch);
  navigate("/cases", true);
  render(<Dashboard />);
  await screen.findByText("Core caller guidance NO_ROLES Ω");
  await userEvent.click(screen.getByRole("button", { name: "Check access" }));
  await waitFor(() => {
    expect(fetch).toHaveBeenCalledTimes(2);
  });
  await screen.findByRole("alert");
  expect(screen.queryByText("Core caller guidance NO_ROLES Ω")).not.toBeInTheDocument();
  expect(screen.queryByText("Core caller technical identity Ψ")).not.toBeInTheDocument();
});
it("rejects inconsistent requester identities, basis, inactive offers and unauthorized issuer seeds", () => {
  expect(isResponse("context", reply(activeRequester))).toBe(true);
  for (const document of [
    { ...activeRequester, context: { ...activeRequester.context, actorId: null } },
    { ...activeRequester, basis: { kind: "UNAVAILABLE" } },
    { ...inactive("NO_ROLES"), context: { ...inactive("NO_ROLES").context, tasks: ["CASE:OPEN"] } },
    {
      ...activeRequester,
      context: { ...activeRequester.context, authorityIssuerSeed: "https://issuer.example/" },
    },
  ]) {
    expect(isResponse("context", reply(document))).toBe(false);
  }
});

it("renders the Core maintenance notice without a requester maintenance DTO", async () => {
  const document: HumanReadDocument_context = {
    ...activeRequester,
    context: { ...activeRequester.context, tasks: [], views: ["OPERATIONS"] },
    screen: {
      regions: [
        {
          role: "NOTICE",
          content: {
            kind: "NOTICE",
            messageId: "diagnostic.REGISTER_MAINTENANCE",
            tone: "WARNING",
          },
        },
      ],
    },
  };
  expect(isResponse("context", reply(document))).toBe(true);
  vi.stubGlobal("fetch", vi.fn<typeof globalThis.fetch>().mockResolvedValue(response(document)));
  navigate("/authority-history", true);
  render(<Dashboard />);
  expect(
    await screen.findByText(
      "Changes are paused for maintenance. You can still view cases and check earlier changes.",
    ),
  ).toBeVisible();
});
