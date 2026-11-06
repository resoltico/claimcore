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

const AbsentValue = () => {
  const p = usePresentation();
  return <span className="timeline-absent-value">{p.text("ui.notRecorded")}</span>;
};

const Scalar = ({ value }: { value: CaseFieldValue | null }) => {
  const p = usePresentation();
  if (value === null) {
    return <AbsentValue />;
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
          <bdi className={value.kind === "TEXT" ? "plain-text-value" : undefined}>
            {value.value}
          </bdi>
          <CharacterWarning value={value.value} allowLineFeed={value.kind === "TEXT"} />
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
        <span className="timeline-change-arrow" aria-hidden="true">
          {" "}
          →{" "}
        </span>
        <span className="sr-only">{p.text("ui.timeline.after")}</span>
        <ins>{after}</ins>
      </dd>
    </div>
  );
};
const MoneyValue = ({ value }: { value: Money | null }) => {
  const p = usePresentation();
  return value === null ? <AbsentValue /> : <bdi>{p.money(value.amount, value.currency)}</bdi>;
};
const PersonValue = ({ value }: { value: ActorRef | null }) =>
  value === null ? <AbsentValue /> : <ActorIdentity actor={value} />;
const TextValue = ({ value }: { value: string | null }) =>
  value === null ? <AbsentValue /> : <bdi className="plain-text-value">{value}</bdi>;
const ReminderChanges = ({ change }: { change: ReminderDifference }) => {
  const p = usePresentation();
  const absent = <AbsentValue />;
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
            item.change.before === null ? <AbsentValue /> : p.term("lifecycle", item.change.before)
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
export const TimelineChangeList = ({
  items,
  compact = false,
}: {
  items: ReadonlyArray<TimelineChange>;
  compact?: boolean;
}) =>
  items.length === 0 ? null : (
    <dl className={compact ? "timeline-changes timeline-changes-compact" : "timeline-changes"}>
      {items.map((item) => (
        <ChangedValues key={changeKey(item)} item={item} />
      ))}
    </dl>
  );

/** The core owns differences and disclosure. No comparisons or adjacent-page reconstruction occur here. */
export const TimelineChanges = ({
  changes,
  compact = false,
}: {
  changes: Changes;
  compact?: boolean;
}) => {
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
  return <TimelineChangeList items={changes.items} compact={compact} />;
};
