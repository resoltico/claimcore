import { apiEndpoints } from "../api/service";
import { isDisclosureRefused } from "../api/outcomes";
import { useLayoutEffect } from "react";
import { useEndpointCache } from "./EndpointProvider";
import type { EndpointCache } from "./endpointCache";
import type { CaseSelector, HumanDocument } from "@contracts/client";
import { useEndpoint } from "./useEndpoint";
type CaseRead = ReturnType<typeof useEndpoint<typeof apiEndpoints.caseGet>>;
const caseDocument = (reply: CaseRead["value"]) =>
  reply?.outcome.kind === "SUCCEEDED" ? reply.outcome.document : null;
const knownErased = (document: HumanDocument | null, cache: EndpointCache) =>
  document?.subject.kind === "CASE" && cache.attachmentsErased(document.subject.caseId);

const disclosedCase = (admitted: HumanDocument | null, cache: EndpointCache) =>
  knownErased(admitted, cache) && admitted?.disclosure !== "ERASED" ? null : admitted;

const historyAccess = (value: HumanDocument | null, query: CaseRead, cache: EndpointCache) => {
  const previous = query.loading ? caseDocument(query.previous) : null;
  return value?.history ?? (knownErased(previous, cache) ? previous?.history : null) ?? "NONE";
};

export const useCaseDisclosure = (target: CaseSelector) => {
  const query = useEndpoint(apiEndpoints.caseGet, { target });
  const cache = useEndpointCache()!;
  const admitted = caseDocument(query.value);
  useLayoutEffect(() => {
    if (admitted !== null) {
      cache.observeCaseErasure(admitted);
    }
  }, [cache, admitted]);
  const value = disclosedCase(admitted, cache);
  return {
    value,
    denied:
      query.value?.outcome.kind === "REJECTED" ||
      (query.result !== null && isDisclosureRefused(query.result)),
    screen: value?.screen ?? null,
    historyAccess: historyAccess(value, query, cache),
    message: query.message,
    loading: query.loading,
    revalidate: query.reload,
  };
};
