import { useState } from "react";
import type { CaseFields, CatalogDocument } from "../api/service";
import { caseFieldValue } from "../protocol/caseFields";
import { BusinessValue } from "./BusinessValue";
import { CopyValue } from "./CopyValue";
import { usePresentation } from "../presentation/context";
type Props = { values: CaseFields; fields: CatalogDocument["fields"] };
const fieldCurrency = (values: CaseFields, name: string) => {
  if (name === "claimedAmount") {
    return values.registration.claimedCurrency;
  }
  if (name === "payableAmount") {
    return values.decision?.payableCurrency;
  }
  return undefined;
};
export const CaseFactRows = ({
  values,
  fields,
  explain = false,
}: Props & { explain?: boolean }) => {
  const p = usePresentation();
  return (
    <dl>
      {fields.map((field) => {
        const value = caseFieldValue(values, field.name);
        const currency = fieldCurrency(values, field.name);
        return (
          <div className="field-row" data-field-name={field.name} key={field.name}>
            <dt>{p.fieldLabel(field.name)}</dt>
            <dd>
              <BusinessValue
                value={value}
                field={field}
                {...(currency === undefined ? {} : { currency })}
              />
              {value === null ? null : (
                <CopyValue icon label={p.fieldLabel(field.name)} value={value} />
              )}{" "}
              {explain ? (
                <small className="field-description">{p.fieldMeaning(field.name)}</small>
              ) : null}
            </dd>
          </div>
        );
      })}
    </dl>
  );
};
export const CaseFactGroup = ({
  values,
  fields,
  group,
}: Props & { group: "registration" | "decision" | "payment" }) => {
  const p = usePresentation();
  const [explain, setExplain] = useState(false);
  return (
    <section className="case-fact-card">
      <div className="section-heading">
        <h3>{p.text(`group.${group}.label`)}</h3>
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
      </div>
      {values[group] === null && !explain ? (
        <p>{p.text("ui.notRecorded")}</p>
      ) : (
        <CaseFactRows
          values={values}
          fields={fields.filter((field) => field.group === group)}
          explain={explain}
        />
      )}
    </section>
  );
};
