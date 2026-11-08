import "./stages.css";
import type { RegionContent } from "@contracts/client";
import { usePresentation } from "../../presentation/context";
import { PresentedValue } from "../../components/PresentedValue";
export const Stages = ({
  content,
  timeZone,
}: {
  content: Extract<RegionContent, { kind: "STAGES" }>;
  timeZone: string;
}) => {
  const p = usePresentation();
  return (
    <ol className="case-stage-list">
      {content.steps.map((step) => (
        <li
          key={step.stage}
          aria-current={step.stage === content.current ? "step" : undefined}
          data-complete={step.reached}
        >
          <meter
            min={0}
            max={1}
            value={step.reached ? 1 : 0}
            aria-label={p.term("caseStage", step.stage)}
          >
            {p.text(step.reached ? "ui.stage.recorded" : "label.notRecorded")}
          </meter>
          <strong>{p.term("caseStage", step.stage)}</strong>
          <span className="sr-only">
            {p.text(step.reached ? "ui.stage.recorded" : "label.notRecorded")}
          </span>
          <span>
            {step.date === null ? (
              p.text(step.reached ? "ui.stage.noDate" : "label.notRecorded")
            ) : (
              <time dateTime={step.date}>{p.date(step.date)}</time>
            )}
          </span>
          {step.amount === null ? null : (
            <PresentedValue value={{ kind: "MONEY", ...step.amount }} timeZone={timeZone} />
          )}
        </li>
      ))}
    </ol>
  );
};
