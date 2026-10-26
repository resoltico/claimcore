import { caseFieldValue } from "../protocol/caseFields";
import { useId } from "react";
import type { CaseFields, CatalogDocument } from "../api/service";
import { BusinessValue } from "./BusinessValue";
import { usePresentation } from "../presentation/context";
import type { Presentation } from "../presentation/context";
import { CopyValue } from "./CopyValue";

type FieldCatalogEntry = CatalogDocument["fields"][number];
const groups = ["registration", "decision", "payment"] as const;

type CaseFieldsViewProps = {
  values: CaseFields;
  revision: number;
  fields: ReadonlyArray<FieldCatalogEntry>;
  context: string;
};
const acceptedSummary = (
  p: Presentation,
  values: CaseFields,
  revision: number,
  fields: ReadonlyArray<FieldCatalogEntry>,
): string =>
  [
    `${p.text("label.revisionLabel")}: ${revision}`,
    ...fields.map(
      (field) => `${p.fieldLabel(field.name)}: ${caseFieldValue(values, field.name) ?? "null"}`,
    ),
  ].join("\n");
export const CaseFieldsView = ({ values, revision, fields, context }: CaseFieldsViewProps) => {
  const p = usePresentation();
  const headingId = useId();
  const rows = (items: ReadonlyArray<FieldCatalogEntry>) => (
    <dl>
      {items.map((field) => {
        const value = caseFieldValue(values, field.name);
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
  );
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
      {rows(fields.filter((field) => field.group === null))}
      {groups.map((group) => (
        <section key={group}>
          <h3>{p.text(`group.${group}.label`)}</h3>
          {rows(fields.filter((field) => field.group === group))}
        </section>
      ))}
    </section>
  );
};
