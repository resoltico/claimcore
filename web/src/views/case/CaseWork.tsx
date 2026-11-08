import type { CaseWorkView } from "@contracts/client";
import { usePresentation } from "../../presentation/context";

export const CaseWork = ({ value, onUpdate }: { value: CaseWorkView; onUpdate?: () => void }) => {
  const p = usePresentation();
  return onUpdate === undefined || (!value.canAssign && !value.canUpdateSummary) ? null : (
    <section className="case-work">
      <button id="case-work-update" type="button" onClick={onUpdate}>
        {p.text("act.UPDATE_WORK.label")}
      </button>
    </section>
  );
};
