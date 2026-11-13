import { useState } from "react";
import { CasePaymentPages } from "./CasePaymentPages";
import { PaymentRows, PaymentsShown } from "./PaymentRows";
import type { Region } from "@contracts/client";
import type { CurrentCase } from "../../protocol/types";
import { usePresentation } from "../../presentation/context";
import { renderCatalogKey } from "../../presentation/messages";
import { Entries } from "../../ui/regions/Entries";
import { MoneyMeter } from "../../ui/regions/MoneyMeter";
import { Button } from "../../ui/Button";
import type { ActKind } from "../../operation/metadata";
import "./payments.css";
type PaymentTable = Extract<Region["content"], { kind: "TABLE" }>;
const PaymentPreview = ({
  content,
  handles,
  total,
  zone,
  onRemove,
  onExpand,
}: {
  content: PaymentTable;
  handles: CurrentCase["context"]["payments"];
  total: number | null;
  zone: string;
  onRemove: (id: string) => void;
  onExpand: () => void;
}) => {
  const p = usePresentation();
  return (
    <>
      {content.rows.length === 0 ? (
        <p className="payments-empty">{p.text("ui.noPayments")}</p>
      ) : (
        <PaymentRows content={content} handles={handles} zone={zone} onRemove={onRemove} />
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
  onAct: (act: ActKind, paymentId?: string) => void;
  onChanged: () => void;
}) => {
  const p = usePresentation();
  const owner = `${current.context.caseId}:${current.context.revision}`;
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
        {current.context.tasks.includes("case.decide") ? (
          <Button
            variant="secondary"
            onPress={() => {
              onAct("case.decide");
            }}
          >
            {p.text("ui.changeDecision")}
          </Button>
        ) : null}
      </div>
      <Entries
        entries={(content.sections ?? []).flatMap((section) => section.entries)}
        timeZone={zone}
      />
      {meter === undefined ? null : (
        <MoneyMeter meter={meter} timeZone={zone} label={p.text("label.paid")} />
      )}
      {expanded === owner && total !== null ? (
        <CasePaymentPages
          current={current}
          total={total}
          onRemove={(id) => {
            onAct("case.removePayment", id);
          }}
          onChanged={onChanged}
        />
      ) : (
        <PaymentPreview
          content={content}
          handles={current.context.payments}
          total={total}
          zone={zone}
          onRemove={(id) => {
            onAct("case.removePayment", id);
          }}
          onExpand={() => {
            setExpanded(owner);
          }}
        />
      )}
    </section>
  );
};
