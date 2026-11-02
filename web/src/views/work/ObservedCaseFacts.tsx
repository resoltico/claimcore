import type { CaseSnapshot } from "@contracts/client";
import { CaseFieldsView } from "../../components/CaseFieldsView";
import { usePresentation } from "../../presentation/context";
import { productMetadata } from "../../protocol/productMetadata";

const financialFields = productMetadata.catalog.fields.filter((field) =>
  [
    "caseReference",
    "status",
    "claimedAmount",
    "claimedCurrency",
    "paymentDecisionDate",
    "payableAmount",
    "payableCurrency",
    "paymentDate",
  ].includes(field.name),
);
/** This observation belongs to the preview, not the editor's earlier case read or a commit precondition. */
export const ObservedCaseFacts = ({ parent }: { parent: CaseSnapshot }) => {
  const p = usePresentation();
  if (parent.state.kind === "ERASED") {
    return <p>{p.text("lifecycle.ERASED.meaning")}</p>;
  }
  return (
    <section className="observed-case-facts">
      <p>{p.text("ui.financialEffectObservation")}</p>
      <CaseFieldsView
        values={parent.state.fields}
        revision={parent.revision}
        fields={financialFields}
        context={p.text("ui.observedFinancialFacts")}
      />
    </section>
  );
};
