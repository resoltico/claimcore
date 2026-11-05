import { caseFieldValue } from "../protocol/caseFields";
import { useId, type ReactNode } from "react";
import type { CaseFields, CatalogDocument } from "../api/service";
import { CaseFactRows, CaseFactGroup } from "./CaseFactGroup";
import { usePresentation } from "../presentation/context";
import type { Presentation } from "../presentation/context";
import { CopyValue } from "./CopyValue";

type FieldCatalogEntry = CatalogDocument["fields"][number];
const groups = ["registration", "decision", "payment"] as const;

type CaseFieldsViewProps = {
  values: CaseFields;
  revision: number;
  fields: ReadonlyArray<FieldCatalogEntry>;
  context: ReactNode;
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
  const snapshotFields = fields.filter((field) => field.section !== "work");
  return (
    <section aria-labelledby={headingId} className="case-fields">
      <div className="section-heading">
        <h2 id={headingId}>{context}</h2>
        <span>{p.text("ui.revision", { revision: p.integer(revision) })}</span>
        <CopyValue
          label={p.text("ui.acceptedSummary")}
          value={acceptedSummary(p, values, revision, snapshotFields)}
        />
      </div>
      <CaseFactRows
        values={values}
        fields={snapshotFields.filter((field) => field.group === null)}
      />
      {groups.map((group) => (
        <CaseFactGroup key={group} group={group} values={values} fields={snapshotFields} />
      ))}
    </section>
  );
};
