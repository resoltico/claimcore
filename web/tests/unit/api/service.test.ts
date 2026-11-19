import { mutationUncertain } from "../../../src/protocol/delivery";
import { afterEach, it, expect, vi } from "vitest";
import {
  type ApiResult,
  type EndpointOutcome,
  service,
  requestEndpoint,
} from "../../../src/api/service";

it("explicit uncertainty is retained while definite rejection and cancellation remain definite", () => {
  const unknown: ApiResult<EndpointOutcome> = {
    kind: "outcome",
    status: 200,
    value: {
      representation: "HUMAN",
      operationId: "10000000-0000-7000-8000-000000000001",
      outcome: {
        kind: "OUTCOME_UNKNOWN",
      },
    },
  };
  expect(mutationUncertain(unknown)).toBe(true);
  expect(
    mutationUncertain({
      kind: "outcome",
      status: 200,
      value: {
        representation: "DATA",
        operationId: "10000000-0000-7000-8000-000000000001",
        outcome: {
          kind: "FAILED",
          fault: {
            code: "CORE_STORE_UNAVAILABLE",
            action: "RETRY_SAFE",
            messageId: "fault.CORE_STORE_UNAVAILABLE",
          },
        },
      },
    }),
  ).toBe(false);
  expect(
    mutationUncertain({
      kind: "deliveryFailure",
      notice: {
        kind: "local",
        reason: "unreachable",
      },
    }),
  ).toBe(true);
});

import { prepareRequest } from "../../../src/protocol/preparedRequest";
const operation = "10000000-0000-7000-8000-000000000001";
const installation = "90000000-0000-4000-8000-000000000001";
const observation = {
  kind: "SNAPSHOT",
  observation: {
    observedAt: "2026-11-04T12:00:00.000000Z",
    businessDate: "2026-11-04",
    timeZone: "Etc/UTC",
  },
};
const receipt = {
  screen: {
    regions: [],
  },
  subject: { kind: "RECEIPT", operationId: "10000000-0000-7000-8000-000000000001" },
  actions: [],
  links: [
    {
      rel: "OPEN_CASE",
      labelId: "field.caseReference.label",
      link: { endpoint: "case.get", input: { target: { kind: "ID", caseId: operation } } },
    },
    {
      rel: "FOLLOW_UP",
      labelId: "field.caseReference.label",
      link: { endpoint: "case.get", input: { target: { kind: "ID", caseId: operation } } },
    },
  ],
  history: null,
  nextCursor: null,
  basis: observation,
  disclosure: "WITHHELD",
};
const current = {
  screen: {
    regions: [],
  },
  subject: { kind: "CASE", caseId: operation, revision: 1, workRevision: 0 },
  actions: [],
  links: [],
  history: "NONE",
  nextCursor: null,
  basis: observation,
  disclosure: "CURRENT",
};
const prepared = () =>
  prepareRequest("operation.execute", {
    operationId: operation,
    task: "case.close",
    binding: {
      kind: "CASE",
      target: {
        kind: "REFERENCE",
        caseReference: "CORRELATION",
      },
      expectedRevision: 1,
    },
    inputs: { "field:outcome": "OTHER" },
  });
const reply = (value: unknown) =>
  new Response(JSON.stringify(value), {
    status: 200,
    headers: {
      "content-type": "application/json",
    },
  });
afterEach(() => {
  vi.unstubAllGlobals();
  vi.useRealTimers();
});
it("checks the typed execute operation echo before accepting delivery", async () => {
  const matching = {
    representation: "HUMAN",
    operationId: operation,
    outcome: {
      kind: "ACCEPTED",
      document: receipt,
    },
  };
  vi.stubGlobal("fetch", vi.fn<typeof globalThis.fetch>().mockResolvedValueOnce(reply(matching)));
  expect((await service.execute(prepared(), "csrf", installation)).kind).toBe("outcome");
  vi.mocked(fetch).mockResolvedValueOnce(
    reply({
      ...matching,
      operationId: "90000000-0000-7000-8000-000000000099",
    }),
  );
  const result = await service.execute(prepared(), "csrf", installation);
  expect(result.kind).toBe("deliveryFailure");
  expect(mutationUncertain(result)).toBe(true);
});
it("lookup keeps its installation binding and rejects an operation echo mismatch", async () => {
  vi.stubGlobal("fetch", vi.fn());
  for (const outcome of [
    {
      kind: "NOT_ACCEPTED",
      document: {
        screen: {
          regions: [],
        },
        subject: { kind: "LIST" },
        actions: [],
        links: [],
        history: null,
        nextCursor: null,
        basis: {
          kind: "UNAVAILABLE",
        },
        disclosure: "NONE",
      },
    },
    {
      kind: "ACCEPTED",
      document: receipt,
      byRequester: true,
    },
  ]) {
    vi.mocked(fetch).mockResolvedValueOnce(
      reply({
        representation: "HUMAN",
        operationId: operation,
        outcome,
      }),
    );
    const matching = await service.operationGet(operation, "csrf", installation);
    expect(matching.kind).toBe("outcome");
    expect(vi.mocked(fetch).mock.calls.at(-1)?.[1]?.headers).toMatchObject({
      "ClaimCore-Installation": installation,
    });
    vi.mocked(fetch).mockResolvedValueOnce(
      reply({
        representation: "HUMAN",
        operationId: "90000000-0000-4000-8000-000000000099",
        outcome,
      }),
    );
    expect((await service.operationGet(operation, "csrf", installation)).kind).toBe(
      "deliveryFailure",
    );
  }
});
it("admits structurally valid server reads without reconstructing business correlation", async () => {
  const sample = {
    representation: "HUMAN",
    outcome: {
      kind: "SUCCEEDED",
      document: current,
    },
  };
  vi.stubGlobal("fetch", vi.fn<typeof globalThis.fetch>().mockResolvedValueOnce(reply(sample)));
  expect((await service.get("AUTHORED-REFERENCE", "csrf")).kind).toBe("outcome");
});
it("dispatches independent reads immediately and cancels an obsolete fetch", async () => {
  const signals: AbortSignal[] = [];
  vi.stubGlobal(
    "fetch",
    vi.fn<typeof fetch>().mockImplementation((_path, init) => {
      signals.push(init!.signal!);
      return new Promise<Response>(() => {
        /* A fetch that ignores cancellation must not retain its caller. */
      });
    }),
  );
  const caller = new AbortController();
  const list = requestEndpoint(
    "case.list",
    "csrf",
    {
      limit: 50,
    },
    caller.signal,
  );
  const second = new AbortController();
  const summary = requestEndpoint("case.summary", "csrf", { limit: 50 }, second.signal);
  const third = new AbortController();
  const views = requestEndpoint("work.views", "csrf", {}, third.signal);
  expect(fetch).toHaveBeenCalledTimes(3);
  caller.abort();
  expect(signals[0]!.aborted).toBe(true);
  expect(await list).toMatchObject({
    kind: "deliveryFailure",
    notice: {
      reason: "cancelled",
    },
  });
  second.abort();
  third.abort();
  await Promise.all([summary, views]);
});
it("reports a busy read once without an automatic retry", async () => {
  const busy = new Response(
    JSON.stringify({
      hostFailure: {
        code: "RATE_LIMITED",
        phase: "NOT_STARTED",
      },
    }),
    {
      status: 429,
      headers: {
        "content-type": "application/json",
        "Retry-After": "1",
      },
    },
  );
  vi.stubGlobal("fetch", vi.fn<typeof globalThis.fetch>().mockResolvedValueOnce(busy));
  const result = await requestEndpoint("case.list", "csrf", {
    limit: 50,
  });
  expect(result).toMatchObject({
    kind: "hostFailure",
    status: 429,
    failure: {
      code: "RATE_LIMITED",
    },
  });
  expect(fetch).toHaveBeenCalledTimes(1);
});
it("retains the independent response deadline for an unresponsive fetch", async () => {
  vi.useFakeTimers();
  let delivered: AbortSignal | undefined;
  vi.stubGlobal(
    "fetch",
    vi.fn<typeof fetch>().mockImplementation((_path, init) => {
      delivered = init!.signal!;
      return new Promise<Response>(() => {
        /* Network never replies. */
      });
    }),
  );
  const pending = requestEndpoint("case.list", "csrf", {
    limit: 50,
  });
  await vi.advanceTimersByTimeAsync(20_000);
  expect(await pending).toMatchObject({
    kind: "deliveryFailure",
    notice: {
      reason: "responseTimeout",
    },
  });
  expect(delivered!.aborted).toBe(true);
});

it.each([undefined, null, "false", 0])(
  "rejects malformed byRequester %s without settling uncertain delivery",
  async (byRequester) => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(
        reply({
          representation: "HUMAN",
          operationId: operation,
          outcome: {
            kind: "ACCEPTED",
            document: receipt,
            ...(byRequester === undefined ? {} : { byRequester }),
          },
        }),
      ),
    );
    const result = await service.operationGet(operation, "csrf", installation);
    expect(result.kind).toBe("deliveryFailure");
    expect(mutationUncertain(result)).toBe(true);
    expect(fetch).toHaveBeenCalledTimes(1);
  },
);
it.each([true, false])("retains valid byRequester %s distinctly", async (byRequester) => {
  vi.stubGlobal(
    "fetch",
    vi.fn().mockResolvedValue(
      reply({
        representation: "HUMAN",
        operationId: operation,
        outcome: { kind: "ACCEPTED", document: receipt, byRequester },
      }),
    ),
  );
  expect(await service.operationGet(operation, "csrf", installation)).toMatchObject({
    kind: "outcome",
    value: { outcome: { byRequester } },
  });
});
it.each(["2024-02-30", "0000-01-01", "2023-02-29", "2024-02-29\n"])(
  "rejects invalid Gregorian observation %s",
  async (businessDate) => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(
        reply({
          representation: "HUMAN",
          outcome: {
            kind: "SUCCEEDED",
            document: {
              ...current,
              basis: {
                kind: "SNAPSHOT",
                observation: { ...observation.observation, businessDate },
              },
            },
          },
        }),
      ),
    );
    expect((await service.get("REF", "csrf")).kind).toBe("deliveryFailure");
  },
);
it.each(["2024-02-29T23:59:59.123456Z", "2024-02-29T23:59:59.1234567Z"])(
  "preserves exact admitted instant %s",
  async (observedAt) => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(
        reply({
          representation: "HUMAN",
          outcome: {
            kind: "SUCCEEDED",
            document: {
              ...current,
              basis: {
                kind: "SNAPSHOT",
                observation: { ...observation.observation, observedAt },
              },
            },
          },
        }),
      ),
    );
    const result = await service.get("REF", "csrf");
    // Observation instants use the six-fraction codec; seven-fraction values are admitted by their own declared roots only.
    expect(result.kind).toBe(observedAt.endsWith("123456Z") ? "outcome" : "deliveryFailure");
  },
);

it("admits complete dismissal controls but rejects impossible task associations and malformed principal fields", async () => {
  const action = {
    task: "people.requests.dismiss",
    labelId: "endpoint.people.requests.dismiss.label",
    binding: {
      kind: "COMMAND",
      request: {
        command: "people.requests.dismiss",
        "input:principal.kind": "HUMAN",
        "input:principal.issuer": "https://issuer.example/",
        "input:principal.subject": "synthetic",
      },
    },
    seeds: {},
    editable: [],
    choices: {},
    blocked: null,
  };
  const sample = {
    representation: "HUMAN",
    outcome: {
      kind: "SUCCEEDED",
      document: { ...current, subject: { kind: "LIST" }, history: null, actions: [action] },
    },
  };
  vi.stubGlobal("fetch", vi.fn().mockResolvedValueOnce(reply(sample)));
  expect((await requestEndpoint("people.requests", "csrf", { limit: 20 })).kind).toBe("outcome");
  for (const invalid of [
    { ...action, task: "session.terminateOwn" },
    {
      ...action,
      binding: {
        ...action.binding,
        request: { ...action.binding.request, "input:principal.issuer": "" },
      },
    },
    {
      ...action,
      binding: {
        ...action.binding,
        request: { ...action.binding.request, "input:principal.subject": "" },
      },
    },
  ]) {
    vi.mocked(fetch).mockResolvedValueOnce(
      reply({
        ...sample,
        outcome: {
          ...sample.outcome,
          document: { ...sample.outcome.document, actions: [invalid] },
        },
      }),
    );
    expect((await requestEndpoint("people.requests", "csrf", { limit: 20 })).kind).toBe(
      "deliveryFailure",
    );
  }
  expect(fetch).toHaveBeenCalledTimes(4);
});
