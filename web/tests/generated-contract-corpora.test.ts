import { describe, it, expect } from "vitest";
import { endpoints } from "../src/generated/contracts/endpoint-catalog";
import { isResponse } from "../src/generated/contracts/validation";
import { corpusCases, schemaValidator } from "./contract-corpus.fixtures";
describe("native contract corpora", () => {
  it("all request and response cases agree with browser validators", async () => {
    const requests = new Map(
      endpoints.map((e) => [e.id, schemaValidator(`endpoint.${e.id}.request.schema.json`)]),
    );
    const samples = corpusCases();
    expect(samples.length).toBe(199);
    expect(samples.filter((c) => c.valid)).toHaveLength(100);
    expect(samples.filter((c) => !c.valid)).toHaveLength(99);
    for (const sample of samples) {
      const admitted =
        sample.direction === "request"
          ? requests.get(sample.endpoint)!(sample.value)
          : await isResponse(sample.endpoint, sample.value);
      expect(admitted, sample.id).toBe(sample.valid);
    }
  });
});
