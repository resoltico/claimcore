import type { CaseReceipt, CatalogDocument } from "../../api/service";
import { BusinessValue } from "../../components/BusinessValue";
import { caseFieldValue } from "../../protocol/caseFields";
import { usePresentation } from "../../presentation/context";
import { Button } from "react-aria-components/Button";
type Props = {
  receipt: CaseReceipt;
  previous: CaseReceipt | undefined;
  fields: CatalogDocument["fields"];
  loadPrevious?: (() => void) | undefined;
};
type LiveLifecycle = Extract<
  CaseReceipt["subject"]["snapshot"],
  { kind: "DISCLOSED" }
>["view"]["kind"];
const LifecycleChange = ({
  before,
  after,
}: {
  before: LiveLifecycle | undefined;
  after: LiveLifecycle;
}) => {
  const p = usePresentation();
  return (
    <dl>
      <dt>{p.text("input.lifecycle.label")}</dt>
      <dd>
        <span>
          {p.text("ui.before", {
            value: before === undefined ? p.text("ui.notRecorded") : p.term("lifecycle", before),
          })}
        </span>
        <span className="review-arrow" aria-hidden="true">
          {" → "}
        </span>
        <span>{p.text("ui.after", { value: p.term("lifecycle", after) })}</span>
      </dd>
    </dl>
  );
};
const DisclosedChanges = ({ receipt, previous, fields }: Omit<Props, "loadPrevious">) => {
  const p = usePresentation();
  const after = receipt.subject.snapshot;
  const before = previous?.subject.snapshot;
  if (after.kind !== "DISCLOSED") {
    return null;
  }
  const prior = before?.kind === "DISCLOSED" ? before.view : null;
  const changes = fields
    .map((field) => ({
      field,
      before: prior === null ? null : caseFieldValue(prior.fields, field.name),
      after: caseFieldValue(after.view.fields, field.name),
    }))
    .filter((change) => change.before !== change.after);
  const lifecycleChanged = prior?.kind !== after.view.kind;
  return (
    <>
      {lifecycleChanged ? <LifecycleChange before={prior?.kind} after={after.view.kind} /> : null}
      {changes.length === 0 && !lifecycleChanged ? <p>{p.text("ui.historyNoChanges")}</p> : null}
      <dl className="review-values history-changes">
        {changes.map((change) => (
          <div key={change.field.name}>
            <dt>{p.fieldLabel(change.field.name)}</dt>
            <dd>
              <BusinessValue value={change.before} field={change.field} context="ui.before" />
              <span className="review-arrow" aria-hidden="true">
                {" → "}
              </span>
              <BusinessValue value={change.after} field={change.field} context="ui.after" />
            </dd>
          </div>
        ))}
      </dl>
    </>
  );
};
export const HistoryChanges = ({ receipt, previous, fields, loadPrevious }: Props) => {
  const p = usePresentation();
  const after = receipt.subject.snapshot;
  if (after.kind === "WITHHELD") {
    return <p>{p.text("ui.snapshotWithheld")}</p>;
  }
  if (after.kind === "ERASED") {
    return <p>{p.text("lifecycle.ERASED.meaning")}</p>;
  }
  const opening = receipt.subject.revision === 1;
  const adjacent = previous?.subject.revision === receipt.subject.revision - 1;
  if (!opening && !adjacent) {
    return (
      <div>
        <p>{p.text("ui.historyPreviousNeeded")}</p>
        {loadPrevious === undefined ? null : (
          <Button onPress={loadPrevious}>{p.text("ui.moreHistory")}</Button>
        )}
      </div>
    );
  }
  if (!opening && previous?.subject.snapshot.kind !== "DISCLOSED") {
    return <p>{p.text("ui.historyComparisonUnavailable")}</p>;
  }
  return (
    <section aria-label={p.text("ui.historyChanges")}>
      <h3>{p.text("ui.historyChanges")}</h3>
      <DisclosedChanges
        receipt={receipt}
        previous={opening ? undefined : previous}
        fields={fields}
      />
    </section>
  );
};
