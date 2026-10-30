import { caseProgress } from "../../presentation/caseProgress";
import type { CurrentCase, CatalogDocument } from "../../api/service";
import { BusinessValue } from "../../components/BusinessValue";
import { usePresentation } from "../../presentation/context";
const steps = ["UNDECIDED", "DECIDED", "PAID"] as const;
export const CaseProgress = ({
  current,
  definition,
}: {
  current: CurrentCase;
  definition: CatalogDocument;
}) => {
  const p = usePresentation();
  const { fields } = current.state;
  const progress = caseProgress(fields);
  const paidDate = definition.fields.find((field) => field.name === "paymentDate");
  return (
    <div className="case-progress">
      <div role="list" aria-label={p.text("input.progress.label")}>
        {steps.map((step) => (
          <span role="listitem" key={step} aria-current={step === progress ? "step" : undefined}>
            {p.term("progress", step)}
          </span>
        ))}
      </div>
      {fields.payment === null || paidDate === undefined ? null : (
        <p>
          {p.fieldLabel("paymentDate")}:{" "}
          <time dateTime={fields.payment.paymentDate}>
            <BusinessValue value={fields.payment.paymentDate} field={paidDate} />
          </time>
        </p>
      )}
    </div>
  );
};
