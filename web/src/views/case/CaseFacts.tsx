import { useCompact } from "../../hooks/useCompact";
import type { CaseFields, CatalogDocument } from "../../api/service";
import { BusinessValue } from "../../components/BusinessValue";
import { CopyValue } from "../../components/CopyValue";
import { caseFieldValue } from "../../protocol/caseFields";
import { usePresentation } from "../../presentation/context";

type Props = { values: CaseFields; fields: CatalogDocument["fields"]; revision: number };
const FactRows = ({ values, fields }: Omit<Props, "revision">) => {
  const p = usePresentation();
  return (
    <dl>
      {fields.map((field) => {
        const value = caseFieldValue(values, field.name);
        return (
          <div className="field-row" key={field.name} data-field-name={field.name}>
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
};
export const CaseFacts = ({ values, fields, revision }: Props) => {
  const p = usePresentation();
  const compact = useCompact();
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
        <CopyValue label={p.text("ui.acceptedSummary")} value={accepted} />
      </div>
      <FactRows values={values} fields={fields.filter((field) => field.group === null)} />
      <div className="case-fact-groups">
        {(["registration", "decision", "payment"] as const).map((group) => {
          const title = p.text(`group.${group}.label`);
          const body =
            values[group] === null ? (
              <p>{p.text("ui.notRecorded")}</p>
            ) : (
              <FactRows values={values} fields={fields.filter((field) => field.group === group)} />
            );
          return compact ? (
            <details className="case-fact-card" key={group} open={group === "registration"}>
              <summary>{title}</summary>
              {body}
            </details>
          ) : (
            <section className="case-fact-card" key={group}>
              <h3>{title}</h3>
              {body}
            </section>
          );
        })}
      </div>
    </section>
  );
};
