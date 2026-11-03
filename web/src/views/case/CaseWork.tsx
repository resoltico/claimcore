import { ActorIdentity, RecordedTime } from "../../components/ActorIdentity";
import type { CaseWorkView } from "@contracts/client";
import { IdentifierValue } from "../../components/IdentifierValue";
import { CharacterWarning } from "../../components/BusinessValue";
import { CopyValue } from "../../components/CopyValue";
import { usePresentation } from "../../presentation/context";
import { renderCatalogKey } from "../../presentation/messages";
import { operationEntry } from "../../protocol/operationCatalog";

const WorkAccount = ({ value }: { value: CaseWorkView["responsible"] }) => {
  const p = usePresentation();
  if (value === null) {
    return <span>{p.term("assignment", "UNASSIGNED")}</span>;
  }
  return (
    <>
      <bdi dir="auto">{value.account.subject}</bdi>
      <CharacterWarning value={value.account.subject} />
      <small className={value.available ? "assignee-available" : "assignee-unavailable"}>
        {p.text(value.available ? "label.assigneeAvailable" : "label.assigneeUnavailable")}
      </small>
      <details>
        <summary>{p.text("ui.accountDetails")}</summary>
        <IdentifierValue value={value.account.actorId} />{" "}
        <CopyValue label={p.fieldLabel("actorId")} value={value.account.actorId} />
      </details>
    </>
  );
};
export const CaseWork = ({
  value,
  onUpdate,
  onPlan,
}: {
  value: CaseWorkView;
  onUpdate?: () => void;
  onPlan?: () => void;
}) => {
  const p = usePresentation();
  return (
    <section className="case-work" aria-labelledby="case-work-title">
      <div className="section-heading">
        <h2 id="case-work-title">{p.term("section", "work")}</h2>
        {onUpdate === undefined ? null : (
          <button id="case-work-update" type="button" onClick={onUpdate}>
            {renderCatalogKey(p, operationEntry({ subject: "WORK", kind: "UPDATE_WORK" }).labelId)}
          </button>
        )}
      </div>
      {value.needsNextAction ? (
        <p className="work-attention" role="status">
          {p.text("ui.noPendingReminders")}
        </p>
      ) : null}
      {value.due === "INACTIVE" ? <p>{p.text("ui.caseWorkInactive")}</p> : null}
      <dl>
        <div>
          <dt>{p.fieldLabel("handlingSummary")}</dt>
          <dd>
            <bdi dir="auto" className="plain-text-value">
              {value.summary ?? p.text("ui.notRecorded")}
            </bdi>
            <CharacterWarning value={value.summary} allowLineFeed />
            {value.summary === null ? null : (
              <CopyValue label={p.fieldLabel("handlingSummary")} value={value.summary} />
            )}
          </dd>
        </div>
        <div>
          <dt>{p.fieldLabel("responsibleActor")}</dt>
          <dd>
            <WorkAccount value={value.responsible} />
          </dd>
        </div>
        <div>
          <dt>{p.text("ui.reminderAction")}</dt>
          <dd>
            <bdi dir="auto">{value.nextAction ?? p.text("ui.notRecorded")}</bdi>
            <CharacterWarning value={value.nextAction} />
          </dd>
        </div>
        <div>
          <dt>{p.text("ui.reminderDue")}</dt>
          <dd>
            {value.followUpDate === null ? (
              p.text("ui.notRecorded")
            ) : (
              <>
                <bdi dir="ltr">{p.date(value.followUpDate)}</bdi>{" "}
                <CopyValue label={p.text("ui.reminderDue")} value={value.followUpDate} />
              </>
            )}
            <span className={`work-due work-due-${value.due.toLowerCase()}`}>
              {p.term("due", value.due)}
            </span>
          </dd>
        </div>
        <div>
          <dt>{p.text("ui.pendingReminders")}</dt>
          <dd>{p.integer(value.pendingReminders)}</dd>
        </div>
        <div>
          <dt>{p.text("ui.overdueReminders")}</dt>
          <dd>{p.integer(value.overdueReminders)}</dd>
        </div>
      </dl>
      {onPlan === undefined ? null : (
        <button id="case-reminder-add" type="button" onClick={onPlan}>
          {p.text("ui.newReminder")}
        </button>
      )}
      <p className="observation-basis">
        {p.text("ui.workDateBasis", {
          date: p.date(value.basis.businessDate),
          zone: value.basis.timeZone,
        })}
      </p>
      {value.changedAt === null ? null : (
        <p>
          {p.text("ui.markedAt")}: <RecordedTime value={value.changedAt} />
        </p>
      )}
      {value.recordedBy === null ? null : (
        <p>
          {p.text("ui.markedBy")}: <ActorIdentity actorId={value.recordedBy} />
        </p>
      )}
      <details>
        <summary>{p.text("ui.technicalDetails")}</summary>
        <p>{p.text("ui.workRevision", { revision: p.integer(value.revision) })}</p>
      </details>
    </section>
  );
};
