import type { CaseSummaryState } from "@contracts/client";
import { usePresentation, type Presentation } from "../presentation/context";
type State =
  | Pick<Exclude<CaseSummaryState, { kind: "ERASED" }>, "kind" | "status" | "progress">
  | { kind: "ERASED" };
const primaryLabel = (p: Presentation, state: State) => {
  if (state.kind === "ERASED" || state.kind === "VOIDED") {
    return p.term("lifecycle", state.kind);
  }
  if (state.status === "CLOSED") {
    return p.term("status", "CLOSED");
  }
  return p.term("progress", state.progress);
};
const secondaryLabel = (p: Presentation, state: State) => {
  if (state.kind === "VOIDED" && state.progress !== "UNDECIDED") {
    return p.text("ui.wasProgress", { progress: p.term("progress", state.progress) });
  }
  if (state.kind === "ACTIVE" && state.status === "CLOSED") {
    return p.term("progress", state.progress);
  }
  return null;
};
export const CaseStateBadge = ({ state }: { state: State }) => {
  const p = usePresentation();
  const secondary = secondaryLabel(p, state);
  return (
    <span className="case-state">
      <span className="badge">{primaryLabel(p, state)}</span>
      {secondary === null ? null : <small>{secondary}</small>}
    </span>
  );
};
