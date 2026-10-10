import { recoveryArtifact } from "./recovery-artifact.fixtures";
import { webV3WireContractFingerprint } from "../src/generated/contracts/web-v3.endpoint-catalog";
import { createPresenter } from "../src/presentation/presenter";
import { defaults } from "../src/presentation/preferences";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { isMutationUncertain, resultNotice, v3 } from "../src/api/v3";
import { commandFor, commandInputs, createDraft, prefilledValues } from "../src/domain/metadata";
import { generatedResponse, generatedWebValue } from "./contract-corpus.fixtures";
import { caseFields, definition } from "./v3-foundation.fixtures";

const fieldHint = createPresenter(defaults).hint;

describe("metadata-driven drafts", () => {
  it("derives command fields, prefill and a canonical draft without a browser command registry", () => {
    expect(commandFor(definition, "OPEN").label).toBe("Open");
    expect(commandInputs(definition, "AMEND_REGISTRATION")[0]?.field.name).toBe("paymentDate");
    expect(prefilledValues(definition, "OPEN", null)).toEqual({ caseReference: "" });
    expect(
      prefilledValues(definition, "AMEND_REGISTRATION", {
        revision: "3",
        fields: { ...caseFields, paymentDate: "2026-09-09" },
      }),
    ).toEqual({ paymentDate: "2026-09-09" });
    expect(
      createDraft("00000000-0000-4000-8000-000000000001", "CASE-1", "3", "AMEND_REGISTRATION", {
        paymentDate: "2026-09-09",
      }),
    ).toEqual({
      operationId: "00000000-0000-4000-8000-000000000001",
      caseReference: "CASE-1",
      expectedRevision: "3",
      command: { kind: "AMEND_REGISTRATION", values: { paymentDate: "2026-09-09" } },
    });
  });

  it("uses descriptor-only presentation hints", () => {
    expect(fieldHint(definition.fields[0]!)).toContain("80");
    expect(fieldHint(definition.fields[1]!)).toContain("YYYY-MM-DD");
    expect(fieldHint(definition.fields[2]!)).toContain("decimal");
    expect(fieldHint(definition.fields[3]!)).toContain("3");
    expect(fieldHint(definition.fields[4]!)).toContain("Open");
    expect(() => commandFor(definition, "CLOSE")).toThrow("CLOSE");
  });
});

const json = (value: unknown, status = 200): Response =>
  new Response(JSON.stringify(value), { status, headers: { "content-type": "application/json" } });
type EndpointId = Parameters<typeof generatedResponse>[0];
const outcome = (endpoint: EndpointId, tag = "SUCCEEDED", data: unknown = {}) => ({
  endpoint,
  outcome: { tag, data },
});
const operationId = "00000000-0000-4000-8000-000000000001";
const openValues = {
  incidentDate: "2026-09-01",
  incidentNotificationDate: "2026-09-02",
  incidentCountry: "Latvia",
  claimantName: "Synthetic claimant",
  insurerName: "Synthetic insurer",
  claimedAmount: "12.34",
  claimedCurrency: "EUR",
};
const openDraft = createDraft(operationId, "CASE-1", "0", "OPEN", openValues);
const recoveryDownload = (status: number): Response =>
  new Response(JSON.stringify({ ...recoveryArtifact, operationId }), {
    status,
    headers: {
      "content-type": "application/vnd.claimcore.recovery+json",
      "content-disposition": `attachment; filename=claimcore-recovery-${operationId}.json; filename*=UTF-8''claimcore-recovery-${operationId}.json`,
    },
  });
const jsonCalls: [() => Promise<unknown>, string, EndpointId][] = [
  [() => v3.logout("token"), "/api/v3/session/logout", "session.logout"],
  [() => v3.definition(), "/api/v3/definition", "definition"],
  [() => v3.list(null, 50, "token"), "/api/v3/cases/list", "case.list"],
  [
    () => v3.history({ caseReference: "CASE-1", cursor: null, limit: 50 }, "token"),
    "/api/v3/cases/history",
    "case.history",
  ],
  [() => v3.observe(operationId, "token"), "/api/v3/operations/observe", "operation.observe"],
  [
    () =>
      v3.prepare(
        {
          operationId,
          caseReference: "CASE-1",
          expectedRevision: "0",
          command: { kind: "OPEN", values: openValues },
        },
        "token",
      ),
    "/api/v3/operations/prepare",
    "command.prepare",
  ],
  [() => v3.submit(openDraft, "token"), "/api/v3/operations/submit", "command.execute"],
  [() => v3.recoveryList("PENDING", null, 50, "token"), "/api/v3/recovery/list", "recovery.list"],
  [
    () => v3.recoveryInspect(operationId, null, 50, "token"),
    "/api/v3/recovery/inspect",
    "recovery.inspect",
  ],
  [
    () => v3.recoveryResolve(operationId, "a".repeat(64), "token"),
    "/api/v3/recovery/resolve",
    "recovery.resolve",
  ],
  [
    () => v3.recoveryDismiss(operationId, "a".repeat(64), "token"),
    "/api/v3/recovery/dismiss",
    "recovery.dismiss",
  ],
];

beforeEach(() => vi.stubGlobal("fetch", vi.fn()));

it("uses generated v3 paths and typed endpoint outcomes", async () => {
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockResolvedValueOnce(
    json(
      outcome("session", "SNAPSHOT", {
        authenticated: false,
        antiforgeryToken: "token",
        webFingerprint: webV3WireContractFingerprint,
      }),
    ),
  );
  const session = await v3.session();
  if (session.kind !== "outcome") {
    throw new Error("Expected a validated session response.");
  }
  expect(session.value.outcome.data).toEqual({
    authenticated: false,
    antiforgeryToken: "token",
    webFingerprint: webV3WireContractFingerprint,
  });
  expect(fetch.mock.calls[0]?.[0]).toBe("/api/v3/session");
  fetch.mockResolvedValueOnce(
    json(
      {
        kind: "HOST_FAILURE",
        code: "WEB_SESSION_REJECTED",
        status: 401,
        diagnostic: { id: "WEB_HOST_SESSION_REJECTED", parameters: {} },
        message: "No",
        executionPhase: "NOT_STARTED",
      },
      401,
    ),
  );
  const rejected = await v3.get("CASE-1", "token");
  expect(fetch.mock.calls[1]?.[0]).toBe("/api/v3/cases/get");
  expect(rejected).toMatchObject({ kind: "hostFailure", status: 401 });
  expect(isMutationUncertain(rejected)).toBe(false);
  expect(resultNotice(rejected)).toEqual({
    kind: "diagnostic",
    diagnostic: { id: "WEB_HOST_SESSION_REJECTED", parameters: {} },
  });
});

it("maps every JSON endpoint to v3 and preserves nullable cursors and mutation identity", async () => {
  const fetch = vi.mocked(globalThis.fetch);
  for (const [, , endpoint] of jsonCalls) {
    fetch.mockResolvedValueOnce(generatedResponse(endpoint));
  }
  for (const [call, path] of jsonCalls) {
    await call();
    expect(fetch.mock.calls.at(-1)?.[0]).toBe(path);
  }
});

it("fails closed on malformed delivery and validates recovery export", async () => {
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockResolvedValueOnce(
    new Response("not json", { status: 200, headers: { "content-type": "text/plain" } }),
  );
  const malformed = await v3.prepare(
    {
      operationId: "id",
      caseReference: "CASE",
      expectedRevision: "0",
      command: { kind: "OPEN", values: openValues },
    },
    "token",
  );
  expect(malformed.kind).toBe("deliveryFailure");
  expect(isMutationUncertain(malformed)).toBe(true);
  fetch.mockResolvedValueOnce(recoveryDownload(200));
  const exported = await v3.recoveryExport(operationId, "a".repeat(64), "token");
  expect(exported).toMatchObject({
    kind: "outcome",
    value: { filename: "claimcore-recovery-00000000-0000-4000-8000-000000000001.json" },
  });
  fetch.mockResolvedValueOnce(recoveryDownload(201));
  expect((await v3.recoveryExport(operationId, "a".repeat(64), "token")).kind).toBe(
    "deliveryFailure",
  );
  fetch.mockResolvedValueOnce(
    json(
      {
        kind: "HOST_FAILURE",
        code: "BAD",
        message: "bad",
        executionPhase: "STARTED_UNCONFIRMED",
      },
      503,
    ),
  );
  expect(isMutationUncertain(await v3.submit(openDraft, "token"))).toBe(true);
});

it("rejects JSON-prefix spoofing and accepts an exact JSON media type with parameters", async () => {
  const fetch = vi.mocked(globalThis.fetch);
  const value = generatedWebValue("case.list");
  fetch.mockResolvedValueOnce(
    new Response(JSON.stringify(value), {
      headers: { "content-type": "application/jsonp" },
    }),
  );
  expect((await v3.list(null, 1, "token")).kind).toBe("deliveryFailure");
  fetch.mockResolvedValueOnce(
    new Response(JSON.stringify(value), {
      headers: { "content-type": "Application/JSON; Charset=UTF-8" },
    }),
  );
  expect((await v3.list(null, 1, "token")).kind).toBe("outcome");
});

it("sends bounded raw imports with generated media type and digest header", async () => {
  const fetch = vi.mocked(globalThis.fetch);
  const source = new File(["{}"], "artifact.json", { type: "application/json" });
  fetch.mockResolvedValueOnce(generatedResponse("recovery.importEnvelopePreview"));
  await v3.importEnvelopePreview(source, "token");
  expect(fetch.mock.calls[0]?.[1]?.headers).toMatchObject({
    "Content-Type": "application/vnd.claimcore.recovery+json",
  });
  fetch.mockResolvedValueOnce(generatedResponse("recovery.importEnvelopeRetain"));
  await v3.importEnvelopeRetain(source, "a".repeat(64), "token");
  expect(fetch.mock.calls[1]?.[1]?.headers).toMatchObject({
    "X-ClaimCore-Source-Sha256": "a".repeat(64),
  });
  const oversized = new File(["x".repeat(131_073)], "large.json");
  expect((await v3.importEnvelopePreview(oversized, "token")).kind).toBe("deliveryFailure");
});
