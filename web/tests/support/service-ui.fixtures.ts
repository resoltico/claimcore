import { actorContextInput } from "./inputs";
export { productMetadata as definition } from "../../src/protocol/productMetadata";
export { caseFields as fields, operationId } from "./service-foundation.fixtures";
export const runtime = {
  productVersion: "0.8.0",
  observedAt: "2026-09-09T12:00:00.000000Z",
  businessDate: "2026-09-09",
  timeZone: "Etc/UTC",
};
export const context = actorContextInput({
  ...runtime,
  capabilities: ["REGISTER"],
  views: ["CASES", "AUTHORITY", "OPERATIONS"],
  offers: [{ kind: "OPEN" }],
});
