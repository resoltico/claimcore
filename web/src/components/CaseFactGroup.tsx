import "./CaseFactGroup.css";
import { FieldHelp } from "./FieldHelp";
import { CountryValue } from "./CountryValue";
import type { CaseFields, CatalogDocument } from "../api/service";
import { caseFieldValue } from "../protocol/caseFields";
import { BusinessValue } from "./BusinessValue";
import { usePresentation } from "../presentation/context";
import type { ReactNode } from "react";
type Props = { values: CaseFields; fields: CatalogDocument["fields"] };
const countryField = (field: Props["fields"][number]) =>
  field.scalar.kind === "TEXT" && typeof field.scalar.format?.unknownCode === "string";
export const CaseFactRows = ({ values, fields }: Props) => {
  const p = usePresentation();
  const pairedCurrencies = new Set(
    fields.filter((field) => field.scalar.kind === "AMOUNT").map((field) => field.pairs),
  );
  return (
    <dl>
      {fields
        .filter((field) => !pairedCurrencies.has(field.name))
        .map((field) => {
          const value = caseFieldValue(values, field.name);
          const currency =
            field.scalar.kind === "AMOUNT" && field.pairs !== null
              ? (caseFieldValue(values, field.pairs) ?? undefined)
              : undefined;
          return (
            <div className="field-row" data-field-name={field.name} key={field.name}>
              <dt>
                {p.fieldLabel(field.name)}{" "}
                <FieldHelp label={p.fieldLabel(field.name)} ruleId={field.ruleId} />
              </dt>
              <dd>
                {value !== null && countryField(field) ? (
                  <CountryValue value={value} field={field} />
                ) : (
                  <BusinessValue
                    value={value}
                    field={field}
                    {...(currency === undefined ? {} : { currency })}
                  />
                )}
                {currency === undefined ? null : <bdi dir="ltr">{currency}</bdi>}
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
  headingLevel = 3,
  actions,
}: Props & {
  group: "registration" | "decision" | "payment";
  headingLevel?: 2 | 3;
  actions?: ReactNode;
}) => {
  const p = usePresentation();
  const Heading = headingLevel === 2 ? "h2" : "h3";
  return (
    <section className="case-fact-card">
      <div className="section-heading">
        <Heading>{p.text(`group.${group}.label`)}</Heading>
        {actions}
      </div>
      {values[group] === null ? (
        <p>{p.text("ui.notRecorded")}</p>
      ) : (
        <CaseFactRows values={values} fields={fields.filter((field) => field.group === group)} />
      )}
    </section>
  );
};
