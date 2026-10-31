import { useEndpoint } from "./useEndpoint";
export const useCaseDisclosure = (caseReference: string) => {
  const query = useEndpoint("case.get", { target: { kind: "REFERENCE", caseReference } });
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
