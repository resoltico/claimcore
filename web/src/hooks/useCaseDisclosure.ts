import { useMemo } from "react";
import type { CaseSelector } from "@contracts/client";
import { useEndpoint } from "./useEndpoint";
export const useCaseDisclosure = (target: CaseSelector) => {
  const query = useEndpoint("case.get", { target });
  const value = useMemo(
    () => (query.value?.outcome.kind === "SUCCEEDED" ? query.value.outcome.document : null),
    [query.value],
  );
  return {
    value,
    screen: value?.screen ?? null,
    historyAccess: value?.context.history ?? "NONE",
    message: query.message,
    loading: query.loading,
    revalidate: query.reload,
  };
};
