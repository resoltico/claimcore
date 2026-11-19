import { afterEach, expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { consumerContractIdentity, type Affordance } from "@contracts/client";
import { render, screen, waitFor } from "../support/presentation-test-support";
import { mockEndpoint } from "../support/endpoint-mocks";
import { humanReadReply } from "../support/human-read-reply.fixtures";
import { activeRequester } from "../support/human-requester.fixtures";
import { service } from "../../src/api/service";
import { App } from "../../src/App";

const own: Affordance = {
  task: "session.terminateOwn",
  labelId: "endpoint.session.terminateOwn.label",
  binding: { kind: "COMMAND", request: { command: "session.terminateOwn" } },
  seeds: {},
  editable: [],
  choices: {},
  blocked: null,
};
const label = "End my ClaimCore browser sessions";
const setup = (state: "NOT_REGISTERED" | "NO_ROLES", actions: ReadonlyArray<Affordance>) => {
  vi.spyOn(service, "session").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: {
      outcome: {
        kind: "SUCCEEDED",
        view: { regions: [] },
        data: {
          authenticated: true,
          antiforgeryToken: "csrf",
          contractIdentity: consumerContractIdentity,
        },
      },
    },
  });
  mockEndpoint("context").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: humanReadReply("context", {
      screen: {
        regions: [
          {
            role: "IDENTITY",
            titleId: "label.accessPending",
            content: { kind: "ENTRIES", entries: [] },
          },
        ],
      },
      subject: {
        kind: "REQUESTER",
        state,
        actorId: state === "NO_ROLES" ? activeRequester.subject.actorId : null,
        installationId: state === "NO_ROLES" ? activeRequester.subject.installationId : null,
      },
      actions,
      links: [],
      history: null,
      nextCursor: null,
      basis: { kind: "UNAVAILABLE" },
      disclosure: "CURRENT",
    }),
  });
  render(<App />);
};
const succeeded = () =>
  new Response(
    JSON.stringify({
      representation: "HUMAN",
      outcome: {
        kind: "SUCCEEDED",
        self: true,
        document: {
          subject: { kind: "LIST" },
          screen: {
            regions: [
              {
                role: "NOTICE",
                content: {
                  kind: "NOTICE",
                  notices: [{ messageId: "label.sessionsTerminated", tone: "INFO" }],
                },
              },
            ],
          },
          actions: [],
          links: [],
          history: null,
          nextCursor: null,
          basis: { kind: "UNAVAILABLE" },
          disclosure: "CURRENT",
        },
      },
    }),
    { status: 200, headers: { "Content-Type": "application/json" } },
  );
afterEach(() => vi.unstubAllGlobals());

it.each(["NOT_REGISTERED", "NO_ROLES"] as const)(
  "%s can confirm its supplied own-session command through the real inactive App and clears the confirmed session",
  async (state) => {
    const gate = Promise.withResolvers<Response>();
    const transport = vi.fn<typeof fetch>().mockReturnValue(gate.promise);
    vi.stubGlobal("fetch", transport);
    setup(state, [own]);
    const user = userEvent.setup();
    await user.click(await screen.findByRole("button", { name: label }));
    const dialog = screen.getByRole("dialog", { name: label });
    expect(transport).not.toHaveBeenCalled();
    await user.click(dialog.querySelector("footer button")!);
    await waitFor(() => {
      expect(transport).toHaveBeenCalledOnce();
    });
    expect(transport.mock.calls[0]?.[0]).toBe("/api/session.terminateOwn");
    expect(transport.mock.calls[0]?.[1]?.body).toBe("{}");
    expect(screen.queryByRole("link", { name: "Sign in" })).toBeNull();
    gate.resolve(succeeded());
    expect(await screen.findByRole("link", { name: "Sign in" })).toHaveAttribute(
      "href",
      "/auth/login",
    );
    expect(screen.queryByRole("button", { name: label })).toBeNull();
    expect(transport).toHaveBeenCalledOnce();
  },
);

it.each(["NOT_REGISTERED", "NO_ROLES"] as const)(
  "%s does not invent an own-session control when Core supplies none",
  async (state) => {
    const transport = vi.fn<typeof fetch>();
    vi.stubGlobal("fetch", transport);
    setup(state, []);
    await screen.findByRole("heading", { name: "You don’t have access yet" });
    expect(screen.queryByRole("button", { name: label })).toBeNull();
    expect(transport).not.toHaveBeenCalled();
  },
);
