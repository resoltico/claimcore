import type { CaseState } from "../api/types";
import { usePresentation } from "../presentation/context";
const stateKey = (value: CaseState) => {
  if (value.state === "ERASED") {
    return "ui.lifecycleErased" as const;
  }
  if (value.disposition === "VOIDED") {
    return "ui.lifecycleVoided" as const;
  }
  return "ui.lifecycleActive" as const;
};
export const LifecycleStatus = ({ value }: { value: CaseState }) => {
  const p = usePresentation();
  return <p role="status">{p.text(stateKey(value))}</p>;
};
