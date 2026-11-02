import type { OperationReceipt, ReminderVersion } from "@contracts/client";
import { usePresentation } from "../../presentation/context";
import { IdentifierValue } from "../../components/IdentifierValue";
import { CharacterWarning } from "../../components/BusinessValue";
const ReminderVersionView = ({ value }: { value: ReminderVersion }) => {
  const p = usePresentation();
  return (
    <section>
      <p>{p.text("ui.reminderRevision", { revision: p.integer(value.revision) })}</p>
      <IdentifierValue value={value.reminderId} />
      {value.snapshot.kind === "DISCLOSED" ? (
        <dl>
          <div>
            <dt>{p.text("ui.reminderAction")}</dt>
            <dd>
              <bdi dir="auto">{value.snapshot.view.action}</bdi>
              <CharacterWarning value={value.snapshot.view.action} />
            </dd>
          </div>
          <div>
            <dt>{p.text("ui.reminderDue")}</dt>
            <dd>
              <bdi dir="ltr">{p.date(value.snapshot.view.dueDate)}</bdi>
            </dd>
          </div>
          <div>
            <dt>{p.text("ui.reminderState")}</dt>
            <dd>{p.term("reminderState", value.snapshot.view.state)}</dd>
          </div>
        </dl>
      ) : (
        <p>
          {p.text(
            value.snapshot.kind === "ERASED" ? "lifecycle.ERASED.meaning" : "ui.snapshotWithheld",
          )}
        </p>
      )}
    </section>
  );
};
export const WorkReceiptView = ({ receipt }: { receipt: OperationReceipt }) => {
  const p = usePresentation();
  const { subject } = receipt;
  if (subject.kind === "WORK") {
    return (
      <section>
        <p>{p.text("ui.workRevision", { revision: p.integer(subject.revision) })}</p>
        {subject.snapshot.kind === "DISCLOSED" ? (
          <dl>
            <div>
              <dt>{p.fieldLabel("handlingSummary")}</dt>
              <dd>
                <bdi dir="auto" className="plain-text-value">
                  {subject.snapshot.view.work.handlingSummary ?? p.text("ui.notRecorded")}
                </bdi>
                <CharacterWarning
                  value={subject.snapshot.view.work.handlingSummary}
                  allowLineFeed
                />
              </dd>
            </div>
            <div>
              <dt>{p.fieldLabel("responsibleActor")}</dt>
              <dd>
                {subject.snapshot.view.work.responsibleActor === null ? (
                  p.text("ui.notRecorded")
                ) : (
                  <IdentifierValue value={subject.snapshot.view.work.responsibleActor} />
                )}
              </dd>
            </div>
          </dl>
        ) : (
          <p>
            {p.text(
              subject.snapshot.kind === "ERASED"
                ? "lifecycle.ERASED.meaning"
                : "ui.snapshotWithheld",
            )}
          </p>
        )}
      </section>
    );
  }
  if (subject.kind === "REMINDER") {
    return subject.effects.kind === "SINGLE" ? (
      <ReminderVersionView value={subject.effects.reminder} />
    ) : (
      <>
        <ReminderVersionView value={subject.effects.completed} />
        <ReminderVersionView value={subject.effects.next} />
      </>
    );
  }
  return null;
};
