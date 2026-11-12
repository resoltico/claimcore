import type {
  EndpointId,
  HumanDocument,
  HumanResponseByEndpoint,
  ResponseFor,
} from "@contracts/client";
import { isResponseFor } from "../../src/protocol/client";
import { endpointResponse } from "./transport-response.fixtures";
type ReadEndpoint = {
  [K in EndpointId]: Extract<
    HumanResponseByEndpoint[K]["outcome"],
    { readonly kind: "SUCCEEDED" }
  > extends { readonly document: HumanDocument }
    ? K
    : never;
}[EndpointId];
type DocumentFor<K extends ReadEndpoint> =
  Extract<HumanResponseByEndpoint[K]["outcome"], { readonly kind: "SUCCEEDED" }> extends {
    readonly document: infer D;
  }
    ? D
    : never;
/** A transport envelope for an authored HUMAN document. This helper does not project DATA or choose display facts. */
export const humanReadReply = <K extends ReadEndpoint>(
  endpoint: K,
  document: DocumentFor<K>,
): ResponseFor<K, "HUMAN"> => {
  const reply = { representation: "HUMAN", outcome: { kind: "SUCCEEDED", document } };
  if (!isResponseFor(endpoint, "HUMAN", reply)) {
    throw new Error(`Authored ${endpoint} HUMAN document is invalid.`);
  }
  return reply;
};
export const humanReadHttpReply = <K extends ReadEndpoint>(
  endpoint: K,
  document: DocumentFor<K>,
): Response => endpointResponse(endpoint, humanReadReply(endpoint, document));
