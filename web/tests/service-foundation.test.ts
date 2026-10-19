import { caseOffers } from "./case-advice.fixtures";
import { beforeEach, describe, it, expect, vi } from "vitest";
import { service } from "../src/api/service";
import {
  commandFor,
  commandInputs,
  prefilledValues,
  createDraft,
} from "../src/interaction/metadata";
import { fields, definition } from "./service-ui.fixtures";
import { endpoints, wireContractFingerprint } from "@contracts/client";
import { generatedResponse } from "./contract-corpus.fixtures";
describe("core command metadata", () => {
  it("describes all twelve commands and registration inputs", () => {
    expect(definition.descriptors.fields).toHaveLength(13);
    expect(definition.descriptors.commands).toHaveLength(12);
    expect(commandFor(definition.descriptors, "OPEN").kind).toBe("OPEN");
    expect(
      commandInputs(definition.descriptors, "AMEND_REGISTRATION").map((x) => x.field.name),
    ).toEqual([
      "incidentDate",
      "incidentNotificationDate",
      "incidentCountry",
      "claimantName",
      "insurerName",
      "claimedAmount",
      "claimedCurrency",
    ]);
    expect(
      prefilledValues(definition.descriptors, "AMEND_REGISTRATION", {
        caseReference: fields.caseReference,
        revision: 1,
        advice: { acts: caseOffers([]), history: ["SUMMARY", "FULL"] },

        state: { tag: "LIVE", disposition: "ACTIVE", fields },
      }),
    ).toMatchObject({ claimantName: fields.claimantName });
    expect(createDraft("id", "reference", 1, "CLOSE", {})).toEqual({
      operationId: "id",
      caseReference: "reference",
      expectedRevision: 1,
      act: { kind: "CLOSE" },
    });
  });
});
describe("catalogue-bound requests", () => {
  beforeEach(() => vi.stubGlobal("fetch", vi.fn()));
  it("sends the handshake and exact root endpoint path", async () => {
    vi.mocked(fetch).mockResolvedValue(generatedResponse("case.list"));
    await service.list(null, 50, "csrf");
    expect(vi.mocked(fetch).mock.calls[0]?.[0]).toBe(
      endpoints.find((e) => e.id === "case.list")!.path,
    );
    expect(vi.mocked(fetch).mock.calls[0]?.[1]?.headers).toMatchObject({
      "ClaimCore-Contract": wireContractFingerprint,
      "X-ClaimCore-Antiforgery": "csrf",
    });
  });
  it("rejects wrong endpoints missing consumed fields and non-JSON replies", async () => {
    const valid = (await generatedResponse("case.list").json()) as object;
    for (const response of [
      new Response(
        JSON.stringify({
          ...valid,
          outcome: { tag: "SUCCEEDED", data: { items: null, nextCursor: null } },
        }),
        {
          headers: { "content-type": "application/json" },
        },
      ),
      generatedResponse("case.get"),
      new Response(JSON.stringify(valid), { headers: { "content-type": "application/jsonp" } }),
    ]) {
      vi.mocked(fetch).mockResolvedValueOnce(response);
      expect((await service.list(null, 50, "csrf")).kind).toBe("deliveryFailure");
    }
  });
});
