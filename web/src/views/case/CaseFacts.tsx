import { CaseFactGroup } from "../../components/CaseFactGroup";
import type { CaseFields, CatalogDocument } from "../../api/service";
import type { ReactNode } from "react";

type Props = {
  values: CaseFields;
  fields: CatalogDocument["fields"];
  actions?: Partial<Record<"registration" | "decision" | "payment", ReactNode>>;
};
export const CaseFacts = ({ values, fields, actions }: Props) => (
  <section className="case-fields">
    <div className="case-fact-groups">
      {(["registration", "decision", "payment"] as const).map((group) => (
        <CaseFactGroup
          key={group}
          group={group}
          values={values}
          fields={fields}
          headingLevel={2}
          actions={actions?.[group]}
        />
      ))}
    </div>
  </section>
);
