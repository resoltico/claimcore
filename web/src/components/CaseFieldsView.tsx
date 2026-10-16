import { useId } from "react";
import type { CaseFields, FieldDescriptor } from "../api/service";
import { BusinessValue } from "./BusinessValue";
import { usePresentation } from "../presentation/context";
import type { Presentation } from "../presentation/context";
import { CopyValue } from "./CopyValue";

type CaseFieldsViewProps = {
  values: CaseFields;
  revision: number;
  fields: ReadonlyArray<FieldDescriptor>;
  context: string;
};
const valueFor = (fields: CaseFields, name: string): string | null =>
  Object.entries(fields).find(([key]) => key === name)?.[1] ?? null;
const acceptedSummary = (
  p: Presentation,
  values: CaseFields,
  revision: number,
  fields: ReadonlyArray<FieldDescriptor>,
): string =>
  [
    `${p.text("ui.revisionLabel")}: ${revision}`,
    ...fields.map(
      (field) => `${p.fieldLabel(field.name)}: ${valueFor(values, field.name) ?? "null"}`,
    ),
  ].join("\n");
export const CaseFieldsView = ({ values, revision, fields, context }: CaseFieldsViewProps) => {
  const p = usePresentation();
  const headingId = useId();
  return (
    <section aria-labelledby={headingId} className="case-fields">
      <div className="section-heading">
        <h2 id={headingId}>{context}</h2>
        <span>{p.text("ui.revision", { revision: p.integer(revision) })}</span>
        <CopyValue
          label={p.text("ui.acceptedSummary")}
          value={acceptedSummary(p, values, revision, fields)}
        />
      </div>
      <dl>
        {fields.map((field) => {
          const value = valueFor(values, field.name);
          return (
            <div key={field.name} className="field-row" data-field-name={field.name}>
              <dt>{p.fieldLabel(field.name)}</dt>
              <dd>
                <BusinessValue value={value} field={field} />
                <details>
                  <summary>{p.text("ui.fieldHelp")}</summary>
                  <p>{p.fieldMeaning(field.name)}</p>
                  {value === null ? null : (
                    <CopyValue label={p.fieldLabel(field.name)} value={value} />
                  )}
                </details>
              </dd>
            </div>
          );
        })}
      </dl>
    </section>
  );
};
