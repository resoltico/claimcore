import { caseFieldValue } from "../protocol/caseFields";
import { useId, useState, type ReactNode } from "react";
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
  const [explain, setExplain] = useState(false);
  return (
    <section aria-labelledby={headingId} className="case-fields">
      <div className="section-heading">
        <h2 id={headingId}>{context}</h2>
        <button
          type="button"
          className="explain-fields"
          aria-pressed={explain}
          onClick={() => {
            setExplain(!explain);
          }}
        >
          {p.text("ui.explainFields")}
        </button>
        <span>{p.text("ui.revision", { revision: p.integer(revision) })}</span>
        <CopyValue
          label={p.text("ui.acceptedSummary")}
          value={acceptedSummary(p, values, revision, fields)}
        />
      </div>
      <CaseFactRows
        values={values}
        fields={fields.filter((field) => field.group === null)}
        explain={explain}
      />
      {groups.map((group) => (
        <CaseFactGroup key={group} group={group} values={values} fields={fields} />
      ))}
    </section>
  );
};
