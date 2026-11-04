import type { CaseStageDisplay, CaseStageEntry } from "@contracts/client";
import { usePresentation } from "../../presentation/context";
import { LifecycleStatus } from "../LifecycleStatus";
import { Icon } from "../../ui/icons";
import "./stage.css";
const Stage = ({
  entry,
  current,
}: {
  entry: CaseStageEntry;
  current: Extract<CaseStageDisplay, { kind: "TRACKER" }>["current"];
}) => {
  const p = usePresentation();
  return (
    <li
      className="case-stage-step"
      aria-current={entry.stage === current ? "step" : undefined}
      data-complete={entry.completed}
    >
      <span className="case-stage-marker" aria-hidden="true">
        {entry.completed ? <Icon name="check" /> : null}
      </span>
      <strong>{p.term("caseStage", entry.stage)}</strong>
      <span className="sr-only">
        {p.text(entry.completed ? "ui.stage.recorded" : "ui.notRecorded")}
      </span>
      {entry.completed ? (
        <small>
          {entry.date === null ? (
            p.text("ui.stage.noDate")
          ) : (
            <time dateTime={entry.date}>{p.date(entry.date)}</time>
          )}
        </small>
      ) : (
        <small>{p.text("ui.notRecorded")}</small>
      )}
      {entry.amount === null ? null : (
        <bdi>{p.money(entry.amount.amount, entry.amount.currency)}</bdi>
      )}
    </li>
  );
};
/** Stage order, completion, dates and amounts are supplied by the core. */
export const CaseProgress = ({ value }: { value: CaseStageDisplay }) => {
  const p = usePresentation();
  if (value.kind !== "TRACKER") {
    return <LifecycleStatus value={value} />;
  }
  return (
    <section className="case-progress" aria-label={p.text("input.progress.label")}>
      <ol className="case-stage-list">
        {value.steps.map((entry) => (
          <Stage key={entry.stage} entry={entry} current={value.current} />
        ))}
      </ol>
    </section>
  );
};
