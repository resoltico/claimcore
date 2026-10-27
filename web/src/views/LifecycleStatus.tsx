import type { CaseView } from "../protocol/types";
import { usePresentation } from "../presentation/context";
export const lifecycleLabels = {
  ACTIVE: "ui.lifecycleActive",
  VOIDED: "ui.lifecycleVoided",
  ERASED: "ui.lifecycleErased",
} as const;
export const LifecycleStatus = ({ value }: { value: CaseView }) => {
  const p = usePresentation();
  return (
    <p role="status">
      {p.text(lifecycleLabels[value.state.kind])}
      {value.state.kind === "ERASED" ? null : <> · {p.term("status", value.state.fields.status)}</>}
    </p>
  );
};
