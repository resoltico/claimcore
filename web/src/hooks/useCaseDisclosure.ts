import { useMemo } from "react";
import type { CaseSelector } from "@contracts/client";
import { useEndpoint } from "./useEndpoint";
export const useCaseDisclosure = (target: CaseSelector) => {
  const query = useEndpoint("case.get", { target });
  const value = useMemo(
    () =>
      query.value?.outcome.kind === "SUCCEEDED"
        ? { ...query.value.outcome.data, view: query.value.outcome.view }
        : null,
    [query.value],
  );
  const previous =
    query.previous?.outcome.kind === "SUCCEEDED" ? query.previous.outcome.data : null;
  return {
    value,
    view: query.value?.outcome.kind === "SUCCEEDED" ? query.value.outcome.view : null,
    historyAccess: value?.advice.history ?? previous?.advice.history ?? "NONE",
    message: query.message,
    loading: query.loading,
    revalidate: query.reload,
  };
};
