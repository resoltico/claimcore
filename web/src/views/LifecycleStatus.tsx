import type { CaseView } from "../protocol/types";
import { usePresentation } from "../presentation/context";
export const lifecycleMessageIds = {
  ACTIVE: "lifecycle.ACTIVE.label",
  VOIDED: "lifecycle.VOIDED.meaning",
  ERASED: "lifecycle.ERASED.meaning",
} as const;
export const LifecycleStatus = ({ value }: { value: CaseView }) => {
  const p = usePresentation();
  if (value.state.kind === "ACTIVE") {
    return null;
  }
  return <p role="status">{p.text(lifecycleMessageIds[value.state.kind])}</p>;
};
