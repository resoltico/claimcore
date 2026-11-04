import type { CaseStageDisplay } from "@contracts/client";
import { usePresentation } from "../presentation/context";
export const lifecycleMessageIds = {
  ACTIVE: "lifecycle.ACTIVE.label",
  VOIDED: "lifecycle.VOIDED.meaning",
  ERASED: "lifecycle.ERASED.meaning",
} as const;
export const LifecycleStatus = ({ value }: { value: CaseStageDisplay }) => {
  const p = usePresentation();
  if (value.kind === "TRACKER") {
    return null;
  }
  return (
    <p role="status" className={`case-lifecycle case-lifecycle-${value.kind.toLowerCase()}`}>
      {p.text(
        value.kind === "REINSTATED" ? "ui.stage.reinstated" : lifecycleMessageIds[value.kind],
      )}
    </p>
  );
};
