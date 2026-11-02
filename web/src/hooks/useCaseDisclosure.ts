import type { CaseSelector } from "@contracts/client";
import { useEndpoint } from "./useEndpoint";
export const useCaseDisclosure = (target: CaseSelector) => {
  const query = useEndpoint("case.get", { target });
  const value = query.value?.outcome.kind === "SUCCEEDED" ? query.value.outcome.data : null;
  const previous =
    query.previous?.outcome.kind === "SUCCEEDED" ? query.previous.outcome.data : null;
  return {
    value,
    historyModes: value?.advice.history ?? previous?.advice.history ?? [],
    message: query.message,
    loading: query.loading,
    revalidate: query.reload,
  };
};
