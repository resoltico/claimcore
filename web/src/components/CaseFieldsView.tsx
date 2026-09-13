import { useId } from "react";
import type { CaseFields, CaseView, FieldDescriptor } from "../api/v2";
import { hasSuspiciousCharacters, suspiciousCodePoints } from "../utils/text";
import { CopyValue } from "./CopyValue";

type CaseFieldsViewProps = {
  caseView: CaseView;
  fields: ReadonlyArray<FieldDescriptor>;
  context: string;
};

const textOrNotRecorded = (value: string | null): string => value ?? "Not recorded";

const valueFor = (fields: CaseFields, name: string): string | null => fields[name] ?? null;

const acceptedSummary = (caseView: CaseView, fields: ReadonlyArray<FieldDescriptor>): string =>
  [
    `Revision: ${caseView.revision}`,
    ...fields.map(
      (field) => `${field.label}: ${textOrNotRecorded(valueFor(caseView.fields, field.name))}`,
    ),
  ].join("\n");

const RenderedValue = ({ value }: { value: string | null }) => {
  const displayed = textOrNotRecorded(value);
  const warning =
    value === null || !hasSuspiciousCharacters(value)
      ? null
      : suspiciousCodePoints(value).join(", ");
  return (
    <span>
      <bdi>{displayed}</bdi>
      {warning === null ? null : <small className="character-warning">Contains {warning}</small>}
    </span>
  );
};

export const CaseFieldsView = ({ caseView, fields, context }: CaseFieldsViewProps) => {
  const headingId = useId();
  return (
    <section aria-labelledby={headingId} className="case-fields">
      <div className="section-heading">
        <h2 id={headingId}>
          Case fields <span className="sr-only">for {context}</span>
        </h2>
        <span>Revision {caseView.revision}</span>
        <CopyValue label="accepted case summary" value={acceptedSummary(caseView, fields)} />
      </div>
      <dl>
        {fields.map((field) => {
          const value = valueFor(caseView.fields, field.name);
          return (
            <div key={field.name} className="field-row">
              <dt>
                {field.label}
                <small>{field.meaning}</small>
              </dt>
              <dd>
                <RenderedValue value={value} />
                <CopyValue label={field.label} value={textOrNotRecorded(value)} />
              </dd>
            </div>
          );
        })}
      </dl>
    </section>
  );
};
