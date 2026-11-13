import { useId } from "react";
import { useCompact } from "../../hooks/useCompact";
import { PresentedValue } from "../../components/PresentedValue";
import { renderCatalogKey } from "../../presentation/messages";
import { RowDetails } from "../../ui/regions/RowDetails";
import type { HumanReadDocument_case_payments, RegionContent } from "@contracts/client";
import { usePresentation } from "../../presentation/context";
import { Table } from "../../ui/regions/Table";
import { Button } from "../../ui/Button";

type Handle = HumanReadDocument_case_payments["context"]["payments"][number];
export const PaymentRows = ({
  content,
  handles,
  zone,
  onRemove,
}: {
  content: Extract<RegionContent, { kind: "TABLE" }>;
  handles: ReadonlyArray<Handle>;
  zone: string;
  onRemove: (paymentId: string) => void;
}) => {
  const p = usePresentation();
  const compact = useCompact();
  const id = useId();
  const remove = (key: string) => {
    const handle = handles.find((candidate) => candidate.paymentId === key);
    return handle === undefined || !handle.canRemove ? null : (
      <Button
        variant="secondary"
        onPress={() => {
          onRemove(handle.paymentId);
        }}
      >
        {p.text("task.case.removePayment.label")}
      </Button>
    );
  };
  if (compact) {
    return (
      <ul className="payment-cards">
        {content.rows.map((row) => {
          const action = remove(row.key);
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
      {...(handles.some((handle) => handle.canRemove) ? { renderActions: remove } : {})}
    />
  );
};
export const PaymentsShown = ({ shown, total }: { shown: number; total: number }) => {
  const p = usePresentation();
  return <p>{p.text("ui.paymentsShown", { shown: p.integer(shown), total: p.integer(total) })}</p>;
};
