import type { Affordance, Region } from "@contracts/client";
import { useState } from "react";
import { CasePaymentPages } from "./CasePaymentPages";
import { PaymentRows, PaymentsShown } from "./PaymentRows";

import type { CurrentCase } from "../../protocol/types";
import { usePresentation } from "../../presentation/context";
import { renderCatalogKey } from "../../presentation/messages";
import { Entries } from "../../ui/regions/Entries";
import { MoneyMeter } from "../../ui/regions/MoneyMeter";
import { Button } from "../../ui/Button";
import { TaskControls } from "../../ui/TaskControls";
import {} from "./payments.css";
type PaymentTable = Extract<Region["content"], { kind: "TABLE" }>;
const PaymentPreview = ({
  content,
  total,
  zone,
  onAction,
  onExpand,
}: {
  content: PaymentTable;
  total: number | null;
  zone: string;
  onAction: (action: Affordance) => void;
  onExpand: () => void;
}) => {
  const p = usePresentation();
  return (
    <>
      {content.rows.length === 0 ? (
        <p className="payments-empty">{p.text("label.noPayments")}</p>
      ) : (
        <PaymentRows content={content} zone={zone} onAction={onAction} />
      )}
      {total === null ? null : <PaymentsShown shown={content.rows.length} total={total} />}
      {total !== null && total > content.rows.length ? (
        <Button variant="secondary" onPress={onExpand}>
          {p.text("ui.showAllPayments")}
        </Button>
      ) : null}
    </>
  );
};
export const CasePayments = ({
  region,
  current,
  onAct,
  onChanged,
}: {
  region: Region;
  current: CurrentCase;
  onAct: (action: Affordance) => void;
  onChanged: () => void;
}) => {
  const p = usePresentation();
  const owner = `${current.subject.caseId}:${current.subject.revision}`;
  const [expanded, setExpanded] = useState<string | null>(null);
  if (region.content.kind !== "TABLE") {
    return null;
  }
  const { content } = region;
  const { meter } = content;
  const count = (content.sections ?? [])
    .flatMap((section) => section.entries)
    .find((entry) => entry.key === "paymentCount")?.value;
  const total = count?.kind === "COUNT" ? count.value : null;
  const zone = current.basis.observation.timeZone;
  return (
    <section
      className="screen-region case-payments"
      data-region="PAYMENTS"
      aria-labelledby="case-payments-title"
    >
      <div className="region-heading">
        <h2 id="case-payments-title">
          {renderCatalogKey(p, region.titleId ?? "label.paymentDecisionAndPayments")}
        </h2>
        <TaskControls actions={region.actions ?? []} compact onAction={onAct} />
      </div>
      <Entries
        entries={(content.sections ?? []).flatMap((section) => section.entries)}
        timeZone={zone}
      />
      {meter === undefined ? null : (
        <MoneyMeter meter={meter} timeZone={zone} label={p.text("label.paid")} />
      )}
      {expanded === owner && total !== null ? (
        <CasePaymentPages current={current} total={total} onAction={onAct} onChanged={onChanged} />
      ) : (
        <PaymentPreview
          content={content}
          total={total}
          zone={zone}
          onAction={onAct}
          onExpand={() => {
            setExpanded(owner);
          }}
        />
      )}
    </section>
  );
};
