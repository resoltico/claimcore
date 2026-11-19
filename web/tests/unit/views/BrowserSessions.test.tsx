import { requestText } from "../../support/transport-response.fixtures";
import { afterEach, expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import type { Affordance, HumanDocument } from "@contracts/client";
import { render, screen, waitFor } from "../../support/presentation-test-support";
import { SessionContext } from "../../../src/session/SessionContext";
import {
  BrowserSessionCommands,
  OwnBrowserSessions,
} from "../../../src/features/settings/BrowserSessions";
import { RequesterContext } from "../../../src/hooks/useRequesterContext";
import { activeRequester } from "../../support/human-requester.fixtures";

const own: Affordance = {
  task: "session.terminateOwn",
  labelId: "endpoint.session.terminateOwn.label",
  binding: { kind: "COMMAND", request: { command: "session.terminateOwn" } },
  seeds: {},
  editable: [],
  choices: {},
  blocked: null,
};
const person: Affordance = {
  task: "people.terminateSessions",
  labelId: "endpoint.people.terminateSessions.label",
  binding: {
    kind: "COMMAND",
    request: {
      command: "people.terminateSessions",
      "input:actorId": "10000000-0000-4000-8000-000000000001",
    },
  },
  seeds: {},
  editable: [],
  choices: {},
  blocked: null,
};
const document: HumanDocument = {
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
};
const response = (outcome: unknown) =>
  new Response(JSON.stringify({ representation: "HUMAN", outcome }), {
    status: 200,
    headers: { "Content-Type": "application/json" },
  });
const context = (endedBrowserSessions = vi.fn(), epoch = 4) => ({
  state: { kind: "authenticated" as const, token: "csrf", epoch },
  logout: () => Promise.resolve(),
  refresh: () => Promise.resolve(),
  endedBrowserSessions,
});
afterEach(() => vi.unstubAllGlobals());
const confirm = async (label: string) => {
  await userEvent.click(screen.getByRole("button", { name: label }));
  const dialog = screen.getByRole("dialog");
  await userEvent.click(dialog.querySelector("footer button")!);
};
it("uses the supplied self command once and clears only confirmed current session ownership", async () => {
  const ended = vi.fn();
  vi.stubGlobal(
    "fetch",
    vi.fn().mockResolvedValue(response({ kind: "SUCCEEDED", self: true, document })),
  );
  render(
    <SessionContext value={context(ended)}>
      <BrowserSessionCommands actions={[own]} />
    </SessionContext>,
  );
  await confirm("End my ClaimCore browser sessions");
  await waitFor(() => {
    expect(ended).toHaveBeenCalledExactlyOnceWith(4);
  });
  expect(fetch).toHaveBeenCalledTimes(1);
  expect(vi.mocked(fetch).mock.calls[0]?.[0]).toBe("/api/session.terminateOwn");
  expect(vi.mocked(fetch).mock.calls[0]?.[1]?.body).toBe("{}");
});
it.each([false, true])(
  "owner termination sends its bound person and honors confirmed self=%s",
  async (self) => {
    const ended = vi.fn();
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(response({ kind: "SUCCEEDED", self, document })),
    );
    render(
      <SessionContext value={context(ended)}>
        <BrowserSessionCommands actions={[person]} />
      </SessionContext>,
    );
    await confirm("End this person’s ClaimCore browser sessions");
    await screen.findByText("ClaimCore browser sessions ended.");
    expect(vi.mocked(fetch).mock.calls[0]?.[0]).toBe("/api/people.terminateSessions");
    expect(JSON.parse(requestText(vi.mocked(fetch).mock.calls[0]?.[1]?.body))).toEqual({
      "input:actorId": "10000000-0000-4000-8000-000000000001",
    });
    expect(ended).toHaveBeenCalledTimes(self ? 1 : 0);
  },
);
it.each(["acknowledgement", "delivery", "malformed"] as const)(
  "keeps %s uncertainty without count, local sign-out or automatic repeat",
  async (kind) => {
    const ended = vi.fn();
    const unknown = {
      ...document,
      screen: {
        regions: [
          {
            role: "NOTICE" as const,
            content: {
              kind: "NOTICE" as const,
              notices: [{ messageId: "label.sessionTerminationUnknown", tone: "WARNING" as const }],
            },
          },
        ],
      },
    };
    const fetch = vi.fn();
    if (kind === "delivery") {
      fetch.mockRejectedValue(new Error("Synthetic delivery loss"));
    } else {
      fetch.mockResolvedValue(
        response(
          kind === "malformed"
            ? { kind: "SUCCEEDED", self: "false", document }
            : { kind: "OUTCOME_UNKNOWN", document: unknown },
        ),
      );
    }
    vi.stubGlobal("fetch", fetch);
    render(
      <SessionContext value={context(ended)}>
        <BrowserSessionCommands actions={[own]} />
      </SessionContext>,
    );
    await confirm("End my ClaimCore browser sessions");
    await screen.findByText(/Repeating it can also end newer sessions/u);
    expect(ended).not.toHaveBeenCalled();
    expect(fetch).toHaveBeenCalledTimes(1);
    expect(screen.queryByText("Browser sessions ended")).not.toBeInTheDocument();
    expect(screen.getByRole("dialog").querySelector("footer button")).toBeDisabled();
  },
);
it("ignores an old owner's late self result", async () => {
  const oldEnded = vi.fn();
  const newEnded = vi.fn();
  const gate = Promise.withResolvers<Response>();
  vi.stubGlobal("fetch", vi.fn().mockReturnValue(gate.promise));
  const view = render(
    <SessionContext value={context(oldEnded)}>
      <BrowserSessionCommands actions={[own]} />
    </SessionContext>,
  );
  await confirm("End my ClaimCore browser sessions");
  view.rerender(
    <SessionContext value={context(newEnded, 5)}>
      <BrowserSessionCommands actions={[own]} />
    </SessionContext>,
  );
  gate.resolve(response({ kind: "SUCCEEDED", self: true, document }));
  await waitFor(() => {
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  });
  expect(oldEnded).not.toHaveBeenCalled();
  expect(newEnded).not.toHaveBeenCalled();
});
it.each(["NOT_REGISTERED", "NO_ROLES"] as const)(
  "offers self termination from the supplied %s context without inferring roles",
  async (state) => {
    const ended = vi.fn();
    vi.stubGlobal("fetch", vi.fn());
    const supplied = {
      ...activeRequester,
      subject: {
        kind: "REQUESTER" as const,
        state,
        installationId: state === "NO_ROLES" ? "90000000-0000-4000-8000-000000000001" : null,
        actorId: state === "NO_ROLES" ? "10000000-0000-4000-8000-000000000001" : null,
      },
      basis: { kind: "UNAVAILABLE" as const },
      actions: [own],
    };
    render(
      <SessionContext value={context(ended)}>
        <RequesterContext value={supplied}>
          <OwnBrowserSessions />
        </RequesterContext>
      </SessionContext>,
    );
    await screen.findByRole("button", { name: "End my ClaimCore browser sessions" });
    expect(fetch).not.toHaveBeenCalled();
  },
);
