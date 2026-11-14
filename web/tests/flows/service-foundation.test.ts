import { operationAffordance } from "../support/affordances.fixtures";

import { beforeEach, describe, it, expect, vi } from "vitest";
import { service } from "../../src/api/service";
import { initialOperationValues, assembleOperation } from "../../src/operation/operationForm";
import { operationEntry } from "../../src/protocol/operationCatalog";
import { fields } from "../support/service-ui.fixtures";
import { endpoints, consumerContractIdentity } from "@contracts/client";
import { wireResponse } from "../support/reply.fixtures";
describe("core act metadata", () => {
  it("keeps optional facts unselected and assembles the selected semantic task with its binding", () => {
    const values = initialOperationValues(
      operationAffordance("case.amendRegistration", {
        kind: "CASE",
        target: { kind: "REFERENCE", caseReference: "CASE-1" },
        expectedRevision: 0,
      }),
    );
    expect(values["field:claimantName"]).toBeUndefined();
    const close = initialOperationValues(
      operationAffordance("case.close", {
        kind: "CASE",
        target: { kind: "REFERENCE", caseReference: "CASE-1" },
        expectedRevision: 0,
      }),
    );
    expect(
      assembleOperation(
        operationEntry({ subject: "CASE", kind: "case.close" }),
        close,
        {
          kind: "CASE",
          target: { kind: "REFERENCE", caseReference: fields.caseReference },
          expectedRevision: 1,
        },
        "10000000-0000-4000-8000-000000000001",
      ).command,
    ).toEqual({
      operationId: "10000000-0000-4000-8000-000000000001",
      task: "case.close",
      binding: {
        kind: "CASE",
        target: { kind: "REFERENCE", caseReference: fields.caseReference },
        expectedRevision: 1,
      },
      inputs: {},
    });
  });
});
describe("catalogue-bound requests", () => {
  beforeEach(() => vi.stubGlobal("fetch", vi.fn()));
  it("sends the handshake and exact root endpoint path", async () => {
    vi.mocked(fetch).mockResolvedValue(wireResponse("case.list"));
    await service.list(null, 50, "csrf");
    expect(vi.mocked(fetch).mock.calls[0]?.[0]).toBe(
      endpoints.find((e) => e.id === "case.list")!.path,
    );
    expect(vi.mocked(fetch).mock.calls[0]?.[1]?.headers).toMatchObject({
      "ClaimCore-Contract": consumerContractIdentity,
      "X-ClaimCore-Antiforgery": "csrf",
    });
  });
  it("rejects a non-JSON media type", async () => {
    const valid: unknown = await wireResponse("case.list").json();
    vi.mocked(fetch).mockResolvedValueOnce(
      new Response(JSON.stringify(valid), {
        headers: { "content-type": "application/jsonp" },
      }),
    );
    expect((await service.list(null, 50, "csrf")).kind).toBe("deliveryFailure");
  });
});
