import type { RegionInteraction } from "./regions/interaction";
import { TaskControls } from "./TaskControls";
import { coreRowLink } from "./regions/rowLink";
import { followLink } from "../routing/followLink";
import "./stepper.css";
import { MoneyMeter } from "./regions/MoneyMeter";
import type { RegionContent } from "@contracts/client";
import { usePresentation } from "../presentation/context";
import { PresentedValue } from "../components/PresentedValue";
type Step = Extract<RegionContent, { kind: "STAGES" }>["steps"][number];
const StageMetadata = ({ step, timeZone }: { step: Step; timeZone: string }) => {
  const p = usePresentation();
  if (!step.reached) {
    return null;
  }
  let value = null;
  if (step.meter !== undefined) {
    value = <MoneyMeter meter={step.meter} timeZone={timeZone} />;
  } else if (step.value !== null) {
    value = (
      <span className="stage-amount">
        <PresentedValue value={step.value} timeZone={timeZone} />
      </span>
    );
  }
  return (
    <>
      {step.date === null ? null : (
        <span className="stage-date">
          <time dateTime={step.date}>{p.date(step.date)}</time>
        </span>
      )}
      {value}
    </>
  );
};
export const Stepper = ({
  content,
  timeZone,
  onAction,
  actionsDisabled,
  actionPresentation,
  linkNavigation,
}: RegionInteraction & {
  content: Extract<RegionContent, { kind: "STAGES" }>;
  timeZone: string;
}) => {
  const p = usePresentation();
  return (
    <ol className="case-stage-list">
      {content.steps.map((step) => {
        const link = coreRowLink(step.link, linkNavigation);
        const label = p.term("caseStage", step.stage);
        return (
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
                aria-label={label}
              />
            ) : null}
            <strong>
              {link === undefined ? (
                label
              ) : (
                <a
                  href={link.href}
                  id={link.id}
                  onClick={(event) => {
                    followLink(event, link.follow);
                  }}
                >
                  {label}
                </a>
              )}
            </strong>
            <StageMetadata step={step} timeZone={timeZone} />
            {(step.actions ?? []).length === 0 ? null : (
              <TaskControls
                actions={step.actions ?? []}
                disabled={actionsDisabled ?? false}
                {...(actionPresentation === undefined ? {} : { actionPresentation })}
                {...(onAction === undefined
                  ? {}
                  : {
                      onAction: (action, originId) => {
                        onAction(action, undefined, originId);
                      },
                    })}
                compact
              />
            )}
          </li>
        );
      })}
    </ol>
  );
};
