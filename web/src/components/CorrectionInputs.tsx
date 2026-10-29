import { BusinessValue } from "./BusinessValue";
import { caseFieldValue } from "../protocol/caseFields";
import { productMetadata } from "../protocol/productMetadata";
import { usePresentation } from "../presentation/context";
import type { CurrentCase } from "../api/service";
import type { ReactNode } from "react";
export const CorrectionInputs = ({
  group,
  current,
  children,
}: {
  group: string | null;
  current: CurrentCase | null;
  children: ReactNode;
}) => {
  const p = usePresentation();
  if (group === null || current === null) {
    return children;
  }
  const fields = productMetadata.catalog.fields.filter((field) => field.group === group);
  return (
    <div className="correction-replacement">
      <aside className="correction-current">
        <h3>{p.text("ui.before", { value: p.groupLabel(group) })}</h3>
        <dl>
          {fields.map((field) => (
            <div key={field.name}>
              <dt>{p.fieldLabel(field.name)}</dt>
              <dd>
                <BusinessValue
                  field={field}
                  value={caseFieldValue(current.state.fields, field.name)}
                />
              </dd>
            </div>
          ))}
        </dl>
      </aside>
      <div>
        <h3>{p.text("ui.after", { value: p.groupLabel(group) })}</h3>
        {children}
      </div>
    </div>
  );
};
