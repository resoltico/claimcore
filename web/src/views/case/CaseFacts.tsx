import { useState } from "react";
import { CaseFactRows, CaseFactGroup } from "../../components/CaseFactGroup";
import type { CaseFields, CatalogDocument } from "../../api/service";
import { CopyValue } from "../../components/CopyValue";
import { caseFieldValue } from "../../protocol/caseFields";
import { usePresentation } from "../../presentation/context";

type Props = { values: CaseFields; fields: CatalogDocument["fields"]; revision: number };
export const CaseFacts = ({ values, fields, revision }: Props) => {
  const p = usePresentation();
  const [explain, setExplain] = useState(false);
  const accepted = [
    `${p.text("label.revisionLabel")}: ${revision}`,
    ...fields.map(
      (field) => `${p.fieldLabel(field.name)}: ${caseFieldValue(values, field.name) ?? "null"}`,
    ),
  ].join("\n");
  return (
    <section className="case-fields" aria-label={p.text("ui.currentCase")}>
      <div className="section-heading">
        <h2>{p.text("ui.currentCase")}</h2>
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
        <CopyValue label={p.text("ui.acceptedSummary")} value={accepted} />
      </div>
      <CaseFactRows
        values={values}
        fields={fields.filter((field) => field.group === null)}
        explain={explain}
      />
      <div className="case-fact-groups">
        {(["registration", "decision", "payment"] as const).map((group) => (
          <CaseFactGroup key={group} group={group} values={values} fields={fields} />
        ))}
      </div>
    </section>
  );
};
