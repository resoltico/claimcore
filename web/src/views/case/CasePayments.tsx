import type { Region } from "@contracts/client";
import type { CurrentCase } from "../../protocol/types";
import { usePresentation } from "../../presentation/context";
import { renderCatalogKey } from "../../presentation/messages";
import { Entries } from "../../ui/regions/Entries";
import { Table } from "../../ui/regions/Table";
import { MoneyMeter } from "../../ui/regions/MoneyMeter";
import { Button } from "../../ui/Button";
import type { ActKind } from "../../operation/metadata";
import "./payments.css";
export const CasePayments = ({
  region,
  current,
  onAct,
}: {
  region: Region;
  current: CurrentCase;
  onAct: (act: ActKind, paymentId?: string) => void;
}) => {
  const p = usePresentation();
  if (region.content.kind !== "TABLE") {
    return null;
  }
  const { content } = region;
  const { meter, ...entriesTable } = content;
  const zone = current.work.basis.timeZone;
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
        {current.advice.acts.includes("DECIDE") ? (
          <Button
            variant="secondary"
            onPress={() => {
              onAct("DECIDE");
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
      {content.rows.length === 0 ? (
        <p className="payments-empty">{p.text("ui.noPayments")}</p>
      ) : (
        <Table
          view={{ ...entriesTable, sections: [] }}
          timeZone={zone}
          renderActions={(key) => {
            const entry = current.state.fields.payments.find(
              (candidate) => candidate.paymentId === key,
            );
            return entry === undefined || !current.advice.acts.includes("REMOVE_PAYMENT") ? null : (
              <Button
                variant="secondary"
                onPress={() => {
                  onAct("REMOVE_PAYMENT", entry.paymentId);
                }}
              >
                {p.text("ui.removePayment")}
              </Button>
            );
          }}
        />
      )}
    </section>
  );
};
