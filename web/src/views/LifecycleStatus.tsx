import type { CaseState } from "../api/types";
import { usePresentation } from "../presentation/context";
type RecordState =
  { readonly tag: "ERASED" } | { readonly tag: "LIVE"; readonly disposition: "ACTIVE" | "VOIDED" };
export const lifecycleMessageKey = (state: RecordState) => {
  if (state.tag === "ERASED") {
    return "ui.lifecycleErased" as const;
  }
  if (state.disposition === "VOIDED") {
    return "ui.lifecycleVoided" as const;
  }
  return "ui.lifecycleActive" as const;
};
export const LifecycleStatus = ({ value }: { value: CaseState }) => {
  const p = usePresentation();
  return <p role="status">{p.text(lifecycleMessageKey(value.state))}</p>;
};
