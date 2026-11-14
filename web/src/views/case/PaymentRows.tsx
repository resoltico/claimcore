import type { Affordance, RegionContent } from "@contracts/client";
import { useId } from "react";
import { useCompact } from "../../hooks/useCompact";
import { PresentedValue } from "../../components/PresentedValue";
import { renderCatalogKey } from "../../presentation/messages";
import { RowDetails } from "../../ui/regions/RowDetails";

import { usePresentation } from "../../presentation/context";
import { Table } from "../../ui/regions/Table";
import { Actions } from "../../ui/regions/Actions";

export const PaymentRows = ({
  content,
  zone,
  onRemove,
}: {
  content: Extract<RegionContent, { kind: "TABLE" }>;
  zone: string;
  onRemove: (paymentId: string) => void;
}) => {
  const p = usePresentation();
  const compact = useCompact();
  const id = useId();
  const remove = (action: Affordance) => {
    if (
      action.task === "case.removePayment" &&
      action.binding.kind === "CASE" &&
      "paymentId" in action.binding
    ) {
      onRemove(action.binding.paymentId);
    }
  };
  if (compact) {
    return (
      <ul className="payment-cards">
        {content.rows.map((row) => {
          const action = <Actions actions={row.actions ?? []} compact onAction={remove} />;
          return (
            <li key={row.key}>
              <dl id={`${id}-${row.key}`}>
                {row.values.map((value, index) => {
                  const column = content.columns[index];
                  return column === undefined ? null : (
                    <div key={column.key}>
                      <dt>{renderCatalogKey(p, column.labelId)}</dt>
                      <dd>
                        <PresentedValue value={value} timeZone={zone} />
                      </dd>
                    </div>
                  );
                })}
              </dl>
              {row.detail === undefined ? null : <RowDetails value={row.detail} zone={zone} />}
              {action === null ? null : (
                <div
                  className="payment-card-actions"
                  role="group"
                  aria-labelledby={`${id}-${row.key}`}
                >
                  {action}
                </div>
              )}
            </li>
          );
        })}
      </ul>
    );
  }
  return (
    <Table
      view={{ kind: "TABLE", columns: content.columns, rows: content.rows }}
      timeZone={zone}
      onAction={remove}
    />
  );
};
export const PaymentsShown = ({ shown, total }: { shown: number; total: number }) => {
  const p = usePresentation();
  return <p>{p.text("ui.paymentsShown", { shown: p.integer(shown), total: p.integer(total) })}</p>;
};
