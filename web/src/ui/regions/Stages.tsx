import "./stages.css";
import { MoneyMeter } from "./MoneyMeter";
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
          {step.meter === undefined ? (
            <meter
              className="progress-meter"
              min={0}
              max={1}
              value={step.reached ? 1 : 0}
              aria-label={p.term("caseStage", step.stage)}
            >
              {p.text(step.reached ? "ui.stage.recorded" : "label.notRecorded")}
            </meter>
          ) : (
            <MoneyMeter
              meter={step.meter}
              timeZone={timeZone}
              label={p.term("caseStage", step.stage)}
            />
          )}
          <strong>{p.term("caseStage", step.stage)}</strong>
          {step.meter === undefined ? (
            <span className="sr-only">
              {p.text(step.reached ? "ui.stage.recorded" : "label.notRecorded")}
            </span>
          ) : null}
          <span className="stage-date">
            {step.date === null ? (
              p.text(step.reached ? "ui.stage.noDate" : "label.notRecorded")
            ) : (
              <time dateTime={step.date}>{p.date(step.date)}</time>
            )}
          </span>
          {step.meter !== undefined || step.amount === null ? null : (
            <span className="stage-amount">
              <PresentedValue value={{ kind: "MONEY", ...step.amount }} timeZone={timeZone} />
            </span>
          )}
        </li>
      ))}
    </ol>
  );
};
