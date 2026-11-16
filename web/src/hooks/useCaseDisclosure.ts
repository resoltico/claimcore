import { apiEndpoints } from "../api/service";
import { isDisclosureRefused } from "../api/outcomes";
import { useMemo } from "react";
import type { CaseSelector } from "@contracts/client";
import { useEndpoint } from "./useEndpoint";
export const useCaseDisclosure = (target: CaseSelector) => {
  const query = useEndpoint(apiEndpoints.caseGet, { target });
  const value = useMemo(
    () => (query.value?.outcome.kind === "SUCCEEDED" ? query.value.outcome.document : null),
    [query.value],
  );
  return {
    value,
    denied:
      query.value?.outcome.kind === "REJECTED" ||
      (query.result !== null && isDisclosureRefused(query.result)),
    screen: value?.screen ?? null,
    historyAccess: value?.history ?? "NONE",
    message: query.message,
    loading: query.loading,
    revalidate: query.reload,
  };
};
