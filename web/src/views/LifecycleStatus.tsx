import type { CaseView } from "../api/types";
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
      {value.state.kind === "ERASED" ? null : <> · {p.token(value.state.fields.status)}</>}
    </p>
  );
};
