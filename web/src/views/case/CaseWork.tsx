import { RelativeTime } from "../../ui/RelativeTime";
import { ActorIdentity } from "../../components/ActorIdentity";
import type { CaseWorkView } from "@contracts/client";
import { CharacterWarning } from "../../components/BusinessValue";
import { usePresentation } from "../../presentation/context";
import { Icon } from "../../ui/icons";

const WorkAccount = ({ value }: { value: CaseWorkView["responsible"] }) => {
  const p = usePresentation();
  return value === null ? (
    <span>{p.term("assignment", "UNASSIGNED")}</span>
  ) : (
    <>
      <ActorIdentity actor={value} />
      <small
        className={
          value.availability === "AVAILABLE" ? "assignee-available" : "assignee-unavailable"
        }
      >
        {p.term("actorAvailability", value.availability)}
      </small>
    </>
  );
};
export const CaseWork = ({ value, onUpdate }: { value: CaseWorkView; onUpdate?: () => void }) => {
  const p = usePresentation();
  return (
    <>
      <section className="case-work case-responsible" aria-labelledby="case-responsible-title">
        <div className="section-heading">
          <h2 id="case-responsible-title">{p.fieldLabel("responsibleActor")}</h2>
          {onUpdate === undefined || !value.canAssign ? null : (
            <button
              id="case-work-update"
              className="case-edit-link"
              type="button"
              onClick={onUpdate}
              aria-label={p.text("ui.changeFact", { field: p.fieldLabel("responsibleActor") })}
              title={p.text("ui.changeFact", { field: p.fieldLabel("responsibleActor") })}
            >
              <Icon name="edit" />
            </button>
          )}
        </div>
        <WorkAccount value={value.responsible} />
      </section>
      <section className="case-work case-handling-summary" aria-labelledby="case-summary-title">
        <div className="section-heading">
          <h2 id="case-summary-title">{p.fieldLabel("handlingSummary")}</h2>
          {onUpdate === undefined || !value.canUpdateSummary ? null : (
            <button
              id="case-summary-update"
              className="case-edit-link"
              type="button"
              onClick={onUpdate}
              aria-label={p.text("ui.changeFact", { field: p.fieldLabel("handlingSummary") })}
              title={p.text("ui.changeFact", { field: p.fieldLabel("handlingSummary") })}
            >
              <Icon name="edit" />
            </button>
          )}
        </div>
        <p>
          <bdi dir="auto" className="plain-text-value">
            {value.summary ?? p.text("ui.notRecorded")}
          </bdi>
          <CharacterWarning value={value.summary} allowLineFeed />
        </p>
        {value.summary === null ||
        value.summaryChangedAt === null ||
        value.summaryRecordedBy === null ? null : (
          <p className="case-summary-attribution">
            <ActorIdentity actor={value.summaryRecordedBy} /> ·{" "}
            <RelativeTime value={value.summaryChangedAt} observedAt={value.basis.observedAt} />
          </p>
        )}
      </section>
    </>
  );
};
