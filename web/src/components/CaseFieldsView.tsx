import { useId } from "react";
import type { CaseFields, CaseView, FieldDescriptor } from "../api/v2";
import { hasSuspiciousCharacters, suspiciousCodePoints } from "../utils/text";
import { usePresentation } from "../presentation/context";
import type { Presentation } from "../presentation/context";
import { CopyValue } from "./CopyValue";

type CaseFieldsViewProps = {
  caseView: CaseView;
  fields: ReadonlyArray<FieldDescriptor>;
  context: string;
};
const valueFor = (fields: CaseFields, name: string): string | null => fields[name] ?? null;
const acceptedSummary = (
  p: Presentation,
  caseView: CaseView,
  fields: ReadonlyArray<FieldDescriptor>,
): string =>
  [
    `${p.text("ui.revisionLabel")}: ${caseView.revision}`,
    ...fields.map(
      (field) =>
        `${p.fieldLabel(field.name)}: ${valueFor(caseView.fields, field.name) ?? p.text("ui.notRecorded")}`,
    ),
  ].join("\n");
const RenderedValue = ({ value, field }: { value: string | null; field: FieldDescriptor }) => {
  const p = usePresentation();
  const warning =
    value === null || !hasSuspiciousCharacters(value)
      ? null
      : suspiciousCodePoints(value).join(", ");
  return (
    <span>
      <bdi>{p.fieldValue(value, field)}</bdi>
      {warning === null ? null : (
        <small className="character-warning">
          {p.text("ui.characterWarning", { characters: warning })}
        </small>
      )}
    </span>
  );
};
export const CaseFieldsView = ({ caseView, fields, context }: CaseFieldsViewProps) => {
  const p = usePresentation();
  const headingId = useId();
  return (
    <section aria-labelledby={headingId} className="case-fields">
      <div className="section-heading">
        <h2 id={headingId}>{p.text("ui.caseFieldsContext", { context })}</h2>
        <span>{p.text("ui.revision", { revision: p.integer(caseView.revision) })}</span>
        <CopyValue
          label={p.text("ui.acceptedSummary")}
          value={acceptedSummary(p, caseView, fields)}
        />
      </div>
      <dl>
        {fields.map((field) => {
          const value = valueFor(caseView.fields, field.name);
          return (
            <div key={field.name} className="field-row" data-field-name={field.name}>
              <dt>
                {p.fieldLabel(field.name)}
                <small>{p.fieldMeaning(field.name)}</small>
              </dt>
              <dd>
                <RenderedValue value={value} field={field} />
                {value === null ? null : (
                  <CopyValue label={p.fieldLabel(field.name)} value={value} />
                )}
              </dd>
            </div>
          );
        })}
      </dl>
    </section>
  );
};
