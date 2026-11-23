import type { Affordance, RegionContent } from "@contracts/client";
import { usePresentation } from "../../presentation/context";
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
export const PaymentsShown = ({ shown, total }: { shown: number; total: number }) => {
  const p = usePresentation();
  return <p>{p.text("ui.paymentsShown", { shown: p.integer(shown), total: p.integer(total) })}</p>;
};
