import type {
  ActorRef,
  Money,
  CaseFieldValue,
  ReminderDifference,
  TimelineChange,
  TimelineChanges as Changes,
} from "@contracts/client";
import type { ReactNode } from "react";
import { ActorIdentity } from "../../components/ActorIdentity";
import { CharacterWarning } from "../../components/BusinessValue";
import { usePresentation } from "../../presentation/context";

const Scalar = ({ value }: { value: CaseFieldValue | null }) => {
  const p = usePresentation();
  if (value === null) {
    return <>{p.text("ui.notRecorded")}</>;
  }
  switch (value.kind) {
    case "DATE":
      return <time dateTime={value.value}>{p.date(value.value)}</time>;
    case "AMOUNT":
      return <bdi>{p.amount(value.value)}</bdi>;
    case "STATUS":
      return <>{p.term("status", value.value)}</>;
    case "COUNTRY":
    case "CURRENCY":
    case "REFERENCE":
      return <bdi dir="ltr">{value.value}</bdi>;
    case "PARTY":
    case "TEXT":
      return (
        <>
          <bdi>{value.value}</bdi>
          <CharacterWarning value={value.value} />
        </>
      );
  }
};
const Change = ({
  label,
  before,
  after,
}: {
  label: string;
  before: ReactNode;
  after: ReactNode;
}) => {
  const p = usePresentation();
  return (
    <div className="timeline-change">
      <dt>{label}</dt>
      <dd>
        <span className="sr-only">{p.text("ui.timeline.before")}</span>
        <del>{before}</del>
        <span aria-hidden="true"> → </span>
        <span className="sr-only">{p.text("ui.timeline.after")}</span>
        <ins>{after}</ins>
      </dd>
    </div>
  );
};
const MoneyValue = ({ value }: { value: Money | null }) => {
  const p = usePresentation();
  return value === null ? (
    <>{p.text("ui.notRecorded")}</>
  ) : (
    <bdi>{p.money(value.amount, value.currency)}</bdi>
  );
};
const PersonValue = ({ value }: { value: ActorRef | null }) => {
  const p = usePresentation();
  return value === null ? <>{p.text("ui.notRecorded")}</> : <ActorIdentity actor={value} />;
};
const TextValue = ({ value }: { value: string | null }) => {
  const p = usePresentation();
  return value === null ? <>{p.text("ui.notRecorded")}</> : <bdi>{value}</bdi>;
};
const ReminderChanges = ({ change }: { change: ReminderDifference }) => {
  const p = usePresentation();
  const absent = p.text("ui.notRecorded");
  return (
    <>
      {change.action === null ? null : (
        <Change
          label={p.text("ui.reminderAction")}
          before={<TextValue value={change.action.before} />}
          after={<TextValue value={change.action.after} />}
        />
      )}
      {change.due === null ? null : (
        <Change
          label={p.text("ui.reminderDue")}
          before={change.due.before === null ? absent : p.date(change.due.before)}
          after={change.due.after === null ? absent : p.date(change.due.after)}
        />
      )}
      {change.state === null ? null : (
        <Change
          label={p.text("ui.reminderState")}
          before={
            change.state.before === null ? absent : p.term("reminderState", change.state.before)
          }
          after={change.state.after === null ? absent : p.term("reminderState", change.state.after)}
        />
      )}
    </>
  );
};
const changeKey = (item: TimelineChange): string => {
  switch (item.kind) {
    case "CASE_FIELD":
    case "CASE_MONEY":
      return `${item.kind}:${item.change.field}`;
    case "REMINDER":
      return `${item.kind}:${item.change.reminderId}`;
    case "LIFECYCLE":
    case "RESPONSIBILITY":
    case "HANDLING_SUMMARY":
      return item.kind;
  }
};
const ChangedValues = ({ item }: { item: TimelineChange }) => {
  const p = usePresentation();
  switch (item.kind) {
    case "CASE_FIELD":
      return (
        <Change
          label={p.fieldLabel(item.change.field)}
          before={<Scalar value={item.change.before} />}
          after={<Scalar value={item.change.after} />}
        />
      );
    case "CASE_MONEY":
      return (
        <Change
          label={p.fieldLabel(item.change.field)}
          before={<MoneyValue value={item.change.change.before} />}
          after={<MoneyValue value={item.change.change.after} />}
        />
      );
    case "LIFECYCLE":
      return (
        <Change
          label={p.text("input.lifecycle.label")}
          before={
            item.change.before === null
              ? p.text("ui.notRecorded")
              : p.term("lifecycle", item.change.before)
          }
          after={p.term("lifecycle", item.change.after)}
        />
      );
    case "RESPONSIBILITY":
      return (
        <Change
          label={p.fieldLabel("responsibleActor")}
          before={<PersonValue value={item.change.before} />}
          after={<PersonValue value={item.change.after} />}
        />
      );
    case "HANDLING_SUMMARY":
      return (
        <Change
          label={p.fieldLabel("handlingSummary")}
          before={<TextValue value={item.change.before} />}
          after={<TextValue value={item.change.after} />}
        />
      );
    case "REMINDER":
      return <ReminderChanges change={item.change} />;
  }
};
/** The core owns differences and disclosure. No comparisons or adjacent-page reconstruction occur here. */
export const TimelineChanges = ({ changes }: { changes: Changes }) => {
  const p = usePresentation();
  if (changes.kind === "WITHHELD") {
    return <p>{p.text("ui.timeline.withheld")}</p>;
  }
  if (changes.kind === "ERASED") {
    return <p>{p.text("lifecycle.ERASED.meaning")}</p>;
  }
  if (changes.items.length === 0) {
    return <p>{p.text("ui.historyNoChanges")}</p>;
  }
  return (
    <dl className="timeline-changes">
      {changes.items.map((item) => (
        <ChangedValues key={changeKey(item)} item={item} />
      ))}
    </dl>
  );
};
