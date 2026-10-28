import { caseAdvice } from "../support/case-advice.fixtures";
import { beforeEach, describe, it, expect, vi } from "vitest";
import { service } from "../../src/api/service";
import {
  initialOperationValues,
  assembleOperation,
  operationEntry,
} from "../../src/operation/operationForm";
import { fields, definition } from "../support/service-ui.fixtures";
import { endpoints, consumerContractIdentity } from "@contracts/client";
import { wireResponse } from "../support/contract-corpus.fixtures";
describe("core act metadata", () => {
  it("prefills registration from catalog and builds scoped input from the root schema", () => {
    expect(definition.catalog.fields).toHaveLength(13);
    expect(
      definition.catalog.entries.filter(
        (entry) => entry.endpoint === "operation.execute" && entry.subject === "CASE",
      ),
    ).toHaveLength(12);
    const current = {
      caseReference: fields.caseReference,
      revision: 1,
      advice: {
        ...caseAdvice([]),
        history: ["SUMMARY", "FULL"] as const,
      },
      state: {
        kind: "ACTIVE" as const,
        fields,
      },
    };
    const values = initialOperationValues(
      "CASE",
      "AMEND_REGISTRATION",
      current,
      "10000000-0000-4000-8000-000000000001",
    );
    expect(values["act.values.claimantName"]).toBe(fields.registration.claimantName);
    const close = initialOperationValues(
      "CASE",
      "CLOSE",
      current,
      "10000000-0000-4000-8000-000000000001",
    );
    expect(assembleOperation(operationEntry({ subject: "CASE", kind: "CLOSE" }), close)).toEqual({
      kind: "CASE",
      operationId: "10000000-0000-4000-8000-000000000001",
      caseReference: fields.caseReference,
      expectedRevision: 1,
      act: {
        kind: "CLOSE",
      },
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
  it("rejects wrong endpoints missing consumed fields and non-JSON replies", async () => {
    const valid = (await wireResponse("case.list").json()) as object;
    for (const response of [
      new Response(
        JSON.stringify({
          ...valid,
          outcome: {
            kind: "SUCCEEDED",
            data: {
              items: null,
              nextCursor: null,
            },
          },
        }),
        {
          headers: {
            "content-type": "application/json",
          },
        },
      ),
      wireResponse("case.get"),
      new Response(JSON.stringify(valid), {
        headers: {
          "content-type": "application/jsonp",
        },
      }),
    ]) {
      vi.mocked(fetch).mockResolvedValueOnce(response);
      expect((await service.list(null, 50, "csrf")).kind).toBe("deliveryFailure");
    }
  });
});
