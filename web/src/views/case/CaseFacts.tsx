import { CaseFactGroup } from "../../components/CaseFactGroup";
import type { CaseFields, CatalogDocument } from "../../api/service";

type Props = { values: CaseFields; fields: CatalogDocument["fields"] };
export const CaseFacts = ({ values, fields }: Props) => (
  <section className="case-fields">
    <div className="case-fact-groups">
      {(["registration", "decision", "payment"] as const).map((group) => (
        <CaseFactGroup key={group} group={group} values={values} fields={fields} headingLevel={2} />
      ))}
    </div>
  </section>
);
