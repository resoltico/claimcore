import { ObservedCaseFacts } from "./ObservedCaseFacts";
import type { OperationReview, Reminder } from "@contracts/client";
import { usePresentation } from "../../presentation/context";
import { CharacterWarning } from "../../components/BusinessValue";
import { IdentifierValue } from "../../components/IdentifierValue";

type WorkReview = Extract<OperationReview["subject"], { kind: "WORK" }>;
type ReminderReview = Extract<OperationReview["subject"], { kind: "REMINDER" }>;
export const WorkReviewValues = ({ review }: { review: WorkReview }) => {
  const p = usePresentation();
  return (
    <div className="review-values">
      <ObservedCaseFacts parent={review.parent} />
      {(
        [
          ["ui.before", review.before],
          ["ui.after", review.proposed],
        ] as const
      ).map(([label, snapshot]) => (
        <section key={label}>
          <h4>{p.text(label, { value: p.term("section", "work") })}</h4>
          <p>{p.text("ui.workRevision", { revision: p.integer(snapshot.revision) })}</p>
          <dl>
            <div>
              <dt>{p.fieldLabel("handlingSummary")}</dt>
              <dd>
                <bdi dir="auto" className="plain-text-value">
                  {snapshot.work.handlingSummary ?? p.text("ui.notRecorded")}
                </bdi>
                <CharacterWarning value={snapshot.work.handlingSummary} allowLineFeed />
              </dd>
            </div>
            <div>
              <dt>{p.fieldLabel("responsibleActor")}</dt>
              <dd>
                {snapshot.work.responsibleActor === null ? (
                  p.text("ui.notRecorded")
                ) : (
                  <IdentifierValue value={snapshot.work.responsibleActor} />
                )}
              </dd>
            </div>
          </dl>
        </section>
      ))}
    </div>
  );
};
const ReminderSnapshot = ({ value, before = false }: { value: Reminder; before?: boolean }) => {
  const p = usePresentation();
  return (
    <section>
      <h4>{p.text(before ? "ui.before" : "ui.after", { value: p.text("ui.reminderDetails") })}</h4>
      <p>{p.text("ui.reminderRevision", { revision: p.integer(value.revision) })}</p>
      <dl>
        <div>
          <dt>{p.text("ui.reminderAction")}</dt>
          <dd>
            <bdi dir="auto">{value.action}</bdi>
            <CharacterWarning value={value.action} />
          </dd>
        </div>
        <div>
          <dt>{p.text("ui.reminderDue")}</dt>
          <dd>
            <bdi dir="ltr">{p.date(value.dueDate)}</bdi> · <bdi dir="ltr">{value.dueDate}</bdi>
          </dd>
        </div>
        <div>
          <dt>{p.text("ui.reminderState")}</dt>
          <dd>{p.term("reminderState", value.state)}</dd>
        </div>
      </dl>
      <details>
        <summary>{p.text("ui.technicalDetails")}</summary>
        <IdentifierValue value={value.reminderId} />
      </details>
    </section>
  );
};
export const ReminderReviewValues = ({ review }: { review: ReminderReview }) => (
  <div className="review-values">
    <ObservedCaseFacts parent={review.parent} />
    {review.before === null ? null : <ReminderSnapshot value={review.before} before />}
    {review.effects.kind === "COMPLETED_AND_PLANNED" ? (
      <>
        <ReminderSnapshot value={review.effects.completed} />
        <ReminderSnapshot value={review.effects.next} />
      </>
    ) : (
      <ReminderSnapshot value={review.effects.reminder} />
    )}
  </div>
);
