import type { Affordance, RegionContent } from "@contracts/client";
import { DataTable } from "../../ui/DataTable";

export const PaymentRows = ({
  content,
  zone,
  onAction,
}: {
  content: Extract<RegionContent, { kind: "TABLE" }>;
  zone: string;
  onAction: (action: Affordance) => void;
}) => (
  <DataTable
    view={{ kind: "TABLE", columns: content.columns, rows: content.rows }}
    emptyId="label.noPayments"
    timeZone={zone}
    onAction={(action) => {
      onAction(action);
    }}
  />
);
