import type { RegionContent } from "@contracts/client";
import { usePresentation } from "../../presentation/context";
import { SectionsView } from "../SectionsView";
import { SuppliedMessage } from "../SuppliedMessage";
type Detail = NonNullable<Extract<RegionContent, { kind: "TABLE" }>["rows"][number]["detail"]>;
export const RowDetails = ({ value, zone }: { value: Detail; zone: string }) => {
  const p = usePresentation();
  return (
    <>
      {value.summary === null ? null : (
        <p className="row-summary">
          <SuppliedMessage message={value.summary} timeZone={zone} />
        </p>
      )}
      <SectionsView sections={value.highlights} timeZone={zone} />
      <details>
        <summary>{p.text("label.recordDetails")}</summary>
        <SectionsView sections={value.sections} timeZone={zone} />
      </details>
    </>
  );
};
