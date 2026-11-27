import { within } from "@testing-library/react";
import { mockEndpoint } from "../../../support/endpoint-mocks";
import { humanReadReply } from "../../../support/human-read-reply.fixtures";
import {
  retainTermination,
  retiredAt,
  terminationScope,
} from "../../../../src/features/settings/terminationRecovery";
import { operationId } from "../../../../src/operation/operationId";
import { requestText } from "../../../support/transport-response.fixtures";
import { afterEach, expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import type { Affordance, HumanDocument } from "@contracts/client";
import { render, screen, waitFor } from "../../../support/presentation-test-support";
import { SessionContext } from "../../../../src/session/SessionContext";
import {
  BrowserSessionCommands,
  OwnBrowserSessions,
} from "../../../../src/features/settings/BrowserSessions";
import { RequesterContext } from "../../../../src/hooks/useRequesterContext";
import { activeRequester } from "../../../support/human-requester.fixtures";

const scope = {
  installationId: activeRequester.subject.installationId,
  requesterKey: activeRequester.subject.actorId,
  observedAt: activeRequester.subject.terminationScope!.observedAt,
  retirementCutoff: null as string | null,
};
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
const terminationContext = (next: typeof scope) => ({
  kind: "outcome" as const,
  status: 200,
  value: humanReadReply("context", {
    ...activeRequester,
    subject: { ...activeRequester.subject, terminationScope: next },
    actions: [own],
  }),
});
const freshContext = (next = scope) =>
  mockEndpoint("context").mockResolvedValue(terminationContext(next));
const context = (refreshAfterTermination = vi.fn(), epoch = 4) => ({
  state: { kind: "authenticated" as const, token: "csrf", epoch },
  logout: () => Promise.resolve(),
  refresh: () => Promise.resolve(),
  refreshAfterTermination,
});
afterEach(() => {
  sessionStorage.clear();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});
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
      <BrowserSessionCommands scope={scope} actions={[own]} />
    </SessionContext>,
  );
  await confirm("End my ClaimCore browser sessions");
  await waitFor(() => {
    expect(ended).toHaveBeenCalledExactlyOnceWith(4);
  });
  expect(fetch).toHaveBeenCalledTimes(1);
  expect(vi.mocked(fetch).mock.calls[0]?.[0]).toBe("/api/session.terminateOwn");
  expect(requestText(vi.mocked(fetch).mock.calls[0]?.[1]?.body)).toMatch(
    /^\{"input:operationId":"[0-9a-f-]{14}7[0-9a-f-]{21}"\}$/u,
  );
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
        <BrowserSessionCommands scope={scope} actions={[person]} />
      </SessionContext>,
    );
    await confirm("End this person’s ClaimCore browser sessions");
    await screen.findByText("ClaimCore browser sessions ended.");
    expect(vi.mocked(fetch).mock.calls[0]?.[0]).toBe("/api/people.terminateSessions");
    const request = requestText(vi.mocked(fetch).mock.calls[0]?.[1]?.body);
    expect(JSON.parse(request)).toHaveProperty(
      "input:actorId",
      "10000000-0000-4000-8000-000000000001",
    );
    expect(request).toMatch(/"input:operationId":"[0-9a-f-]{14}7[0-9a-f-]{21}"/u);
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
    const fetch = vi.fn<typeof globalThis.fetch>();
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
        <BrowserSessionCommands scope={scope} actions={[own]} />
      </SessionContext>,
    );
    await confirm("End my ClaimCore browser sessions");
    await screen.findByText(/Repeat it using the same request ID/u);
    expect(ended).not.toHaveBeenCalled();
    expect(fetch).toHaveBeenCalledTimes(1);
    expect(screen.queryByText("Browser sessions ended")).not.toBeInTheDocument();
    freshContext();
    const retry = screen.getByRole("dialog").querySelector("footer button")!;
    await userEvent.click(retry);
    await waitFor(() => {
      expect(fetch).toHaveBeenCalledTimes(2);
    });
    const first: unknown = JSON.parse(requestText(fetch.mock.calls[0]?.[1]?.body));
    if (typeof first !== "object" || first === null) {
      throw new Error("Expected the original termination request object.");
    }
    expect(JSON.parse(requestText(fetch.mock.calls[1]?.[1]?.body))).toEqual({
      ...first,
      "input:settleOnly": true,
    });
  },
);
it("ignores an old owner's late self result", async () => {
  const oldEnded = vi.fn();
  const newEnded = vi.fn();
  const gate = Promise.withResolvers<Response>();
  vi.stubGlobal("fetch", vi.fn().mockReturnValue(gate.promise));
  const view = render(
    <SessionContext value={context(oldEnded)}>
      <BrowserSessionCommands scope={scope} actions={[own]} />
    </SessionContext>,
  );
  await confirm("End my ClaimCore browser sessions");
  view.rerender(
    <SessionContext value={context(newEnded, 5)}>
      <BrowserSessionCommands scope={scope} actions={[own]} />
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
        authorityVersion: null,
        terminationScope: scope,
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

it("resumes the same request after Escape and remount, isolated by requester and target", async () => {
  const transport = vi
    .fn<typeof globalThis.fetch>()
    .mockRejectedValueOnce(new Error("Synthetic acknowledgement loss"))
    .mockResolvedValueOnce(response({ kind: "SUCCEEDED", self: true, document }));
  vi.stubGlobal("fetch", transport);
  const refresh = vi.fn();
  const mounted = render(
    <SessionContext value={context(refresh)}>
      <BrowserSessionCommands scope={scope} actions={[own]} />
    </SessionContext>,
  );
  await confirm("End my ClaimCore browser sessions");
  await screen.findByText(/Repeat it using the same request ID/u);
  await userEvent.keyboard("{Escape}");
  await waitFor(() => {
    expect(screen.queryByRole("dialog")).toBeNull();
  });
  mounted.unmount();

  const fresh = context(refresh, 5);
  const other = { ...scope, requesterKey: "10000000-0000-4000-8000-000000000002" };
  const resumed = render(
    <SessionContext value={fresh}>
      <BrowserSessionCommands scope={other} actions={[own]} />
    </SessionContext>,
  );
  expect(screen.queryByRole("button", { name: "Resume unconfirmed termination" })).toBeNull();
  expect(screen.getByText(/different account, register or selected person/u)).toBeVisible();

  resumed.rerender(
    <SessionContext value={fresh}>
      <BrowserSessionCommands scope={scope} actions={[person]} />
    </SessionContext>,
  );
  expect(screen.queryByRole("button", { name: "Resume unconfirmed termination" })).toBeNull();
  expect(screen.getByRole("button", { name: "Start a new termination" })).toBeVisible();
  expect(transport).toHaveBeenCalledOnce();

  resumed.rerender(
    <SessionContext value={fresh}>
      <BrowserSessionCommands scope={scope} actions={[own]} />
    </SessionContext>,
  );
  freshContext();
  await confirm("Resume unconfirmed termination");
  await screen.findByText("ClaimCore browser sessions ended.");
  const original: unknown = JSON.parse(requestText(transport.mock.calls[0]?.[1]?.body));
  if (typeof original !== "object" || original === null) {
    throw new Error("Expected the original termination request object.");
  }
  expect(JSON.parse(requestText(transport.mock.calls[1]?.[1]?.body))).toEqual({
    ...original,
    "input:settleOnly": true,
  });
  expect(refresh).not.toHaveBeenCalled();
  expect(screen.queryByRole("button", { name: "Resume unconfirmed termination" })).toBeNull();
});

it("shows expiry without sending and can forget only the expired request", async () => {
  const clock = Date.now();
  const now = vi.spyOn(Date, "now").mockReturnValue(clock - 11 * 60_000);
  const scopeForOwn = terminationScope(scope, "self");
  retainTermination({ ...scopeForOwn, id: operationId() }, null);
  now.mockReturnValue(clock);
  const transport = vi.fn<typeof globalThis.fetch>();
  vi.stubGlobal("fetch", transport);
  render(
    <SessionContext value={context()}>
      <BrowserSessionCommands
        scope={{ ...scope, retirementCutoff: new Date(clock - 10 * 60_000).toISOString() }}
        actions={[own]}
      />
    </SessionContext>,
  );

  expect(screen.getByText(/earlier termination request has expired/u)).toBeVisible();
  expect(screen.queryByRole("button", { name: "Resume unconfirmed termination" })).toBeNull();
  await userEvent.click(screen.getByRole("button", { name: "Forget expired request" }));
  expect(screen.queryByText(/earlier termination request has expired/u)).toBeNull();
  expect(screen.getByRole("button", { name: "End my ClaimCore browser sessions" })).toBeVisible();
  expect(transport).not.toHaveBeenCalled();
});

const recoveryKey = "claimcore.sessionTerminations.v1";
const foreignAttempt = (index: number, issuedAt: number) => {
  const requesterKey = `10000000-0000-4000-8000-${index.toString(16).padStart(12, "0")}`;
  const timestamp = issuedAt.toString(16).padStart(12, "0");
  return {
    ...terminationScope({ ...scope, requesterKey }, "self"),
    id: `${timestamp.slice(0, 8)}-${timestamp.slice(8)}-7000-8000-${index.toString(16).padStart(12, "0")}`,
  };
};

const currentAttempts = (): unknown => JSON.parse(sessionStorage.getItem(recoveryKey) ?? "[]");

const renderOwnTermination = (current = scope) =>
  render(
    <SessionContext value={context()}>
      <BrowserSessionCommands scope={current} actions={[own]} />
    </SessionContext>,
  );

it("makes room for a fresh request when expired foreign entries fill the tab", async () => {
  const cutoff = Date.parse(scope.observedAt) - 11 * 60_000;
  const foreign = Array.from({ length: 16 }, (_, index) => foreignAttempt(index + 16, cutoff));
  sessionStorage.setItem(recoveryKey, JSON.stringify(foreign));
  const transport = vi.fn<typeof globalThis.fetch>().mockRejectedValue(new Error("Lost reply"));
  vi.stubGlobal("fetch", transport);
  renderOwnTermination({
    ...scope,
    retirementCutoff: new Date(cutoff + 1).toISOString(),
  });

  await confirm("End my ClaimCore browser sessions");
  await screen.findByText(/Repeat it using the same request ID/u);
  expect(transport).toHaveBeenCalledOnce();
  expect(currentAttempts()).toHaveLength(1);
  expect(currentAttempts()).toMatchObject([{ requesterKey: scope.requesterKey, target: "self" }]);
  expect(window.document.body).not.toHaveTextContent(foreign[0]!.requesterKey);
});

it("preserves active evidence beside an independently damaged entry", async () => {
  const live = foreignAttempt(32, Date.parse(scope.observedAt) - 20_000);
  const damaged = { ...foreignAttempt(33, Date.parse(scope.observedAt)), id: "unreadable" };
  sessionStorage.setItem(recoveryKey, JSON.stringify([live, damaged]));
  const transport = vi.fn<typeof globalThis.fetch>().mockRejectedValue(new Error("Lost reply"));
  vi.stubGlobal("fetch", transport);
  renderOwnTermination();
  expect(screen.getByText("Some saved termination requests could not be read.")).toBeVisible();
  expect(screen.queryByRole("button", { name: "Resume unconfirmed termination" })).toBeNull();

  await confirm("Start a new termination");
  await screen.findByText(/Repeat it using the same request ID/u);
  expect(transport).toHaveBeenCalledOnce();
  expect(currentAttempts()).toHaveLength(2);
  expect(currentAttempts()).toMatchObject([
    live,
    { requesterKey: scope.requesterKey, target: "self" },
  ]);
  expect(window.document.body).not.toHaveTextContent(live.requesterKey);
  expect(window.document.body).not.toHaveTextContent(damaged.requesterKey);
});

it("retains in-window attempts despite a fast browser clock and refuses an over-capacity write", async () => {
  const observed = Date.parse(scope.observedAt);
  const live = Array.from({ length: 16 }, (_, index) =>
    foreignAttempt(index + 16, observed - 20_000),
  );
  sessionStorage.setItem(recoveryKey, JSON.stringify(live));
  vi.spyOn(Date, "now").mockReturnValue(observed + 60 * 60_000);
  const transport = vi.fn<typeof globalThis.fetch>();
  vi.stubGlobal("fetch", transport);
  renderOwnTermination();

  const checked = freshContext();
  await confirm("Start a new termination");
  expect(await screen.findByRole("alert")).toHaveTextContent(/cannot be read or updated/u);
  expect(checked).toHaveBeenCalledOnce();
  expect(transport).not.toHaveBeenCalled();
  expect(currentAttempts()).toEqual(live);
});

it.each([true, false])(
  "refuses dispatch when the current write fails (throws=%s)",
  async (throws) => {
    const transport = vi.fn<typeof globalThis.fetch>();
    vi.stubGlobal("fetch", transport);
    vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => {
      if (throws) {
        throw new Error("Synthetic current write blocked");
      }
    });
    renderOwnTermination();

    await confirm("End my ClaimCore browser sessions");
    expect(screen.getByRole("alert")).toHaveTextContent(/cannot be read or updated/u);
    expect(transport).not.toHaveBeenCalled();
    expect(currentAttempts()).toEqual([]);
  },
);

it("refreshes a stale mounted observation before giving up on a fresh request", async () => {
  const observed = Date.parse(scope.observedAt);
  const full = Array.from({ length: 16 }, (_, index) =>
    foreignAttempt(index + 16, observed - 9 * 60_000),
  );
  const otherInstall = "90000000-0000-4000-8000-000000000099";
  const other = Array.from({ length: 16 }, (_, index) => ({
    ...foreignAttempt(index + 32, observed - 9 * 60_000),
    installationId: otherInstall,
  }));
  sessionStorage.setItem(recoveryKey, JSON.stringify([...full, ...other]));
  const revalidation = freshContext({
    ...scope,
    observedAt: new Date(observed + 2 * 60_000).toISOString(),
  });
  revalidation.mockResolvedValueOnce(
    terminationContext({
      ...scope,
      observedAt: new Date(observed + 2 * 60_000).toISOString(),
    }),
  );
  revalidation.mockResolvedValueOnce(
    terminationContext({
      ...scope,
      retirementCutoff: new Date(observed - 8 * 60_000).toISOString(),
    }),
  );
  const transport = vi
    .fn<typeof globalThis.fetch>()
    .mockResolvedValueOnce(response({ kind: "RETIREMENT_CONFIRMED", document }))
    .mockRejectedValueOnce(new Error("Lost reply"));
  vi.stubGlobal("fetch", transport);
  renderOwnTermination();

  await confirm("Start a new termination");
  await screen.findByText(/Repeat it using the same request ID/u);
  expect(revalidation).toHaveBeenCalledTimes(2);
  expect(transport).toHaveBeenCalledTimes(2);
  const prepared: unknown = JSON.parse(requestText(transport.mock.calls[1]?.[1]?.body));
  expect(JSON.parse(requestText(transport.mock.calls[0]?.[1]?.body))).toMatchObject({
    ...(prepared as object),
    "input:confirmRetirement": true,
  });
  expect(currentAttempts()).toHaveLength(17);
  expect(currentAttempts()).toMatchObject([...other, { requesterKey: scope.requesterKey }]);
});

it("revalidates an expiry on a mounted resume and leaves the older request intact", async () => {
  const observed = Date.parse(scope.observedAt);
  const attempt = {
    ...foreignAttempt(17, observed - 9 * 60_000),
    requesterKey: scope.requesterKey,
  };
  sessionStorage.setItem(recoveryKey, JSON.stringify([attempt]));
  const checked = freshContext({
    ...scope,
    observedAt: new Date(observed + 2 * 60_000).toISOString(),
  });
  const transport = vi.fn<typeof globalThis.fetch>();
  vi.stubGlobal("fetch", transport);
  renderOwnTermination();

  await confirm("Resume unconfirmed termination");
  expect(await screen.findByRole("alert")).toHaveTextContent(
    /outside the current recovery window/u,
  );
  expect(screen.queryByRole("button", { name: "Forget expired request" })).toBeNull();
  expect(checked).toHaveBeenCalledOnce();
  expect(transport).not.toHaveBeenCalled();
  expect(currentAttempts()).toEqual([attempt]);
});

it("keeps the earlier same-target identity after a forward read and a lost transport reply", async () => {
  const observed = Date.parse(scope.observedAt);
  const attempt = {
    ...foreignAttempt(17, observed - 9 * 60_000),
    requesterKey: scope.requesterKey,
  };
  sessionStorage.setItem(recoveryKey, JSON.stringify([attempt]));
  const forward = { ...scope, observedAt: new Date(observed + 2 * 60_000).toISOString() };
  const transport = vi.fn<typeof globalThis.fetch>().mockRejectedValueOnce(new Error("Lost reply"));
  vi.stubGlobal("fetch", transport);
  renderOwnTermination(forward);

  expect(screen.getByText(/outside the current recovery window/u)).toBeVisible();
  expect(screen.queryByRole("button", { name: "Forget expired request" })).toBeNull();
  await confirm("Start a new termination");
  await screen.findByText(/Repeat it using the same request ID/u);
  expect(currentAttempts()).toMatchObject([attempt, { target: "self" }]);

  // The earlier ID is still selectable after rollback even while the newer request is unresolved.
  freshContext();
  await userEvent.keyboard("{Escape}");
  transport.mockResolvedValueOnce(response({ kind: "SUCCEEDED", self: true, document }));
  const earlier = screen.getByText(attempt.id).closest("li")!;
  await userEvent.click(
    within(earlier).getByRole("button", { name: "Resume unconfirmed termination" }),
  );
  await userEvent.click(screen.getByRole("dialog").querySelector("footer button")!);
  await screen.findByText("ClaimCore browser sessions ended.");
  expect(currentAttempts()).toMatchObject([{ target: "self" }]);
  expect(currentAttempts()).not.toContainEqual(attempt);
  expect(screen.queryByText(/outside the current recovery window/u)).toBeNull();
  expect(transport).toHaveBeenCalledTimes(2);
  expect(JSON.parse(requestText(transport.mock.calls[1]?.[1]?.body))).toEqual({
    "input:operationId": attempt.id,
    "input:settleOnly": true,
  });
});

it("preserves a full tab if the retirement confirmation check loses its reply", async () => {
  const observed = Date.parse(scope.observedAt);
  const full = Array.from({ length: 16 }, (_, index) =>
    foreignAttempt(index + 16, observed - 9 * 60_000),
  );
  sessionStorage.setItem(recoveryKey, JSON.stringify(full));
  const checked = freshContext({
    ...scope,
    observedAt: new Date(observed + 2 * 60_000).toISOString(),
  });
  const transport = vi.fn<typeof globalThis.fetch>().mockRejectedValue(new Error("Lost reply"));
  vi.stubGlobal("fetch", transport);
  renderOwnTermination();

  await confirm("Start a new termination");
  expect(await screen.findByRole("alert")).toHaveTextContent(/cannot be read or updated/u);
  expect(checked).toHaveBeenCalledOnce();
  expect(transport).toHaveBeenCalledOnce();
  expect(JSON.parse(requestText(transport.mock.calls[0]?.[1]?.body))).toMatchObject({
    "input:confirmRetirement": true,
  });
  expect(currentAttempts()).toEqual(full);
});

it("keeps IDs at cutoff equality and prunes only after the committed cutoff passes them", () => {
  const observed = Date.parse(scope.observedAt);
  const older = foreignAttempt(16, observed);
  const next = foreignAttempt(17, observed + 1000);
  const equality = new Date(observed).toISOString();
  const definitive = equality.replace("Z", "001Z");

  expect(retiredAt(older.id, equality)).toBe(false);
  expect(retiredAt(older.id, definitive)).toBe(true);
  retainTermination(older, null);
  retainTermination(next, equality);
  expect(currentAttempts()).toEqual([older, next]);
  retainTermination(next, definitive);
  expect(currentAttempts()).toEqual([next]);
});

it.each(["requester", "installation", "unavailable"] as const)(
  "retains the exact request through a %s revalidation failure",
  async (failure) => {
    const observed = Date.parse(scope.observedAt);
    const attempt = { ...foreignAttempt(17, observed - 20_000), requesterKey: scope.requesterKey };
    sessionStorage.setItem(recoveryKey, JSON.stringify([attempt]));
    const changed = {
      ...scope,
      ...(failure === "requester" ? { requesterKey: "10000000-0000-4000-8000-000000000099" } : {}),
      ...(failure === "installation"
        ? { installationId: "90000000-0000-4000-8000-000000000099" }
        : {}),
    };
    const checked = freshContext(scope);
    if (failure === "unavailable") {
      checked.mockResolvedValueOnce({
        kind: "deliveryFailure",
        notice: { kind: "local", reason: "unreachable" },
      });
    } else {
      checked.mockResolvedValueOnce({
        kind: "outcome",
        status: 200,
        value: humanReadReply("context", {
          ...activeRequester,
          subject: { ...activeRequester.subject, terminationScope: changed },
          actions: [own],
        }),
      });
    }
    const transport = vi.fn<typeof globalThis.fetch>();
    vi.stubGlobal("fetch", transport);
    renderOwnTermination();
    await confirm("Resume unconfirmed termination");
    expect(await screen.findByRole("alert")).toBeVisible();
    expect(transport).not.toHaveBeenCalled();
    expect(currentAttempts()).toEqual([attempt]);
  },
);

it("revalidates the selected person's exact control before settling it", async () => {
  const observed = Date.parse(scope.observedAt);
  const target = "10000000-0000-4000-8000-000000000001";
  const attempt = {
    ...foreignAttempt(17, observed - 20_000),
    requesterKey: scope.requesterKey,
    target,
  };
  sessionStorage.setItem(recoveryKey, JSON.stringify([attempt]));
  freshContext();
  const grants = mockEndpoint("people.grants").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: humanReadReply("people.grants", {
      ...document,
      subject: { kind: "PERSON", actorId: "10000000-0000-4000-8000-000000000018" },
      actions: [person],
    }),
  });
  const transport = vi.fn<typeof globalThis.fetch>();
  vi.stubGlobal("fetch", transport);
  render(
    <SessionContext value={context()}>
      <BrowserSessionCommands scope={scope} actions={[person]} />
    </SessionContext>,
  );
  await confirm("Resume unconfirmed termination");
  expect(await screen.findByRole("alert")).toHaveTextContent(
    /different account, register or selected person/u,
  );
  expect(grants).toHaveBeenCalledOnce();
  expect(transport).not.toHaveBeenCalled();
  expect(currentAttempts()).toEqual([attempt]);
});
