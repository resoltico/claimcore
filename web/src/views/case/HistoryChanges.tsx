import type { HistoryDifference } from "@contracts/client";
import type { CaseReceipt, CatalogDocument } from "../../api/service";
import { BusinessValue } from "../../components/BusinessValue";
import { usePresentation } from "../../presentation/context";

type Props = {
  receipt: CaseReceipt;
  difference: HistoryDifference | null;
  fields: CatalogDocument["fields"];
};
const LifecycleChange = ({ change }: { change: NonNullable<HistoryDifference["lifecycle"]> }) => {
  const p = usePresentation();
  return (
    <dl>
      <dt>{p.text("input.lifecycle.label")}</dt>
      <dd>
        <span>
          {p.text("ui.before", {
            value:
              change.before === null
                ? p.text("ui.notRecorded")
                : p.term("lifecycle", change.before),
          })}
        </span>
        <span className="review-arrow" aria-hidden="true">
          {" → "}
        </span>
        <span>{p.text("ui.after", { value: p.term("lifecycle", change.after) })}</span>
      </dd>
    </dl>
  );
};
/** Core supplies both differences and disclosure; the view does no business comparison. */
export const HistoryChanges = ({ receipt, difference, fields }: Props) => {
  const p = usePresentation();
  if (receipt.subject.snapshot.kind === "WITHHELD") {
    return <p>{p.text("ui.snapshotWithheld")}</p>;
  }
  if (receipt.subject.snapshot.kind === "ERASED") {
    return <p>{p.text("lifecycle.ERASED.meaning")}</p>;
  }
  if (difference === null) {
    return <p>{p.text("ui.historyComparisonUnavailable")}</p>;
  }
  return (
    <section aria-label={p.text("ui.historyChanges")}>
      <h3>{p.text("ui.historyChanges")}</h3>
      {difference.lifecycle === null ? null : <LifecycleChange change={difference.lifecycle} />}
      {difference.fields.length === 0 && difference.lifecycle === null ? (
        <p>{p.text("ui.historyNoChanges")}</p>
      ) : null}
      <dl className="review-values history-changes">
        {difference.fields.map((change) => {
          const field = fields.find((descriptor) => descriptor.name === change.field)!;
          return (
            <div key={change.field}>
              <dt>{p.fieldLabel(change.field)}</dt>
              <dd>
                <BusinessValue value={change.before} field={field} context="ui.before" />
                <span className="review-arrow" aria-hidden="true">
                  {" → "}
                </span>
                <BusinessValue value={change.after} field={field} context="ui.after" />
              </dd>
            </div>
          );
        })}
      </dl>
    </section>
  );
};
