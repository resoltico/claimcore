import type { CaseStageOutline } from "@contracts/client";
import { termKeys } from "@contracts/messages";
import { isTerm } from "../presentation/terms";
import { usePresentation } from "../presentation/context";
import "../views/case/stage.css";
const stages = Object.keys(termKeys.caseStage).filter((value) => isTerm("caseStage", value));
export const CaseStageMini = ({ value }: { value: CaseStageOutline | null }) => {
  const p = usePresentation();
  return value === null ? null : (
    <ol className="case-stage-mini" aria-label={p.text("input.progress.label")}>
      {stages.map((stage) => (
        <li
          key={stage}
          data-complete={value.completed.includes(stage)}
          aria-current={stage === value.current ? "step" : undefined}
        >
          <span className="sr-only">
            {p.term("caseStage", stage)} ·{" "}
            {p.text(value.completed.includes(stage) ? "ui.stage.recorded" : "ui.notRecorded")}
          </span>
        </li>
      ))}
    </ol>
  );
};
