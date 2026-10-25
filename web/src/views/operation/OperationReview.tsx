import { AccessibleModal } from "../../components/AccessibleModal";
import { ProtocolSummary } from "../../components/ProtocolSummary";
import { BusinessValue } from "../../components/BusinessValue";
import { usePresentation } from "../../presentation/context";
import { productMetadata } from "../../protocol/productMetadata";
import { operationDocument } from "../../interaction/operationForm";
import type { OperationModel } from "../../hooks/operation/useOperation";
import { resolveSchema, selectedSchema } from "../../protocol/forms";
import type { OperationReview as Review, CaseReview } from "../../api/types";
import { renderKey } from "../../presentation/messages";
import { lifecycleMessageKey } from "../LifecycleStatus";
type ReviewModel = Pick<
  OperationModel,
  "prepared" | "review" | "phase" | "busy" | "confirmed" | "confirm" | "execute" | "returnToEdit"
>;
const ReviewValues = ({ review }: { review: CaseReview }) => {
  const p = usePresentation();
  return (
    <>
      {review.before === null ? null : (
        <p>{p.text("ui.before", { value: p.text(lifecycleMessageKey(review.before.state)) })}</p>
      )}
      <p>{p.text("ui.after", { value: p.text(lifecycleMessageKey(review.after.state)) })}</p>
      <dl className="review-values">
        {review.changes.map((change) => (
          <div key={change.field}>
            <dt>{p.fieldLabel(change.field)}</dt>
            <dd>
              <BusinessValue
                value={change.before}
                field={productMetadata.catalog.fields.find((field) => field.name === change.field)!}
                context="ui.before"
              />
              <span aria-hidden="true"> → </span>
              <BusinessValue
                value={change.after}
                field={productMetadata.catalog.fields.find((field) => field.name === change.field)!}
                context="ui.after"
              />
            </dd>
          </div>
        ))}
      </dl>
    </>
  );
};
const authorityState = resolveSchema(operationDocument, {
  $ref: "#/components/schemas/AuthorityState",
});
const AuthorityValues = ({ review }: { review: Extract<Review, { scope: "AUTHORITY" }> }) => {
  const p = usePresentation();
  const states = [
    ["ui.before", review.before],
    ["ui.after", review.after],
  ] as const;
  return (
    <>
      {states.map(([label, state]) => (
        <section key={label}>
          <h4>{p.text(label, { value: p.text("ui.actor") })}</h4>
          <dl>
            <dt>{p.text("ui.enabled")}</dt>
            <dd>{p.text(state.enabled ? "ui.yes" : "ui.no")}</dd>
          </dl>
          <h5>{p.text("ui.actorGrants")}</h5>
          <ProtocolSummary
            value={state.grants}
            document={operationDocument}
            node={authorityState.properties!["grants"]!}
          />
        </section>
      ))}
    </>
  );
};
const ReviewConfirmation = ({
  model,
  confirmationId,
}: {
  model: ReviewModel;
  confirmationId: (typeof productMetadata.catalog.acts)[number]["confirmationId"];
}) => {
  const p = usePresentation();
  return (
    <>
      {" "}
      <label>
        <input
          type="checkbox"
          checked={model.confirmed}
          disabled={model.busy}
          onChange={(event) => {
            model.confirm(event.target.checked);
          }}
        />
        {renderKey(p, confirmationId)}
      </label>
      <button
        type="button"
        disabled={!model.confirmed || model.busy}
        onClick={() => void model.execute()}
      >
        {p.text(model.phase === "SUBMITTING" ? "ui.submitting" : "ui.submitExact")}
      </button>
    </>
  );
};
export const OperationReview = ({ model }: { model: ReviewModel }) => {
  const p = usePresentation();
  if (model.review === null || model.prepared === null) {
    return null;
  }
  const { input } = model.prepared;
  const schema = selectedSchema(operationDocument, operationDocument, "", { scope: input.scope });
  const display = Object.fromEntries(Object.entries(input).filter(([name]) => name !== "scope"));
  const act =
    input.scope === "CASE"
      ? productMetadata.catalog.acts.find((item) => item.kind === input.act.kind)!
      : productMetadata.catalog.authorityActs.find((item) => item.kind === input.act.kind)!;
  return (
    <AccessibleModal
      title={p.text("ui.reviewTitle")}
      closeLabel={p.text("ui.returnToEdit")}
      description={p.text("ui.reviewDescription")}
      isOpen={model.phase === "REVIEWING" || model.phase === "SUBMITTING"}
      isDismissable={!model.busy}
      onOpenChange={model.returnToEdit}
    >
      <>
        <h3>
          {input.scope === "CASE"
            ? p.commandLabel(input.act.kind)
            : p.authorityLabel(input.act.kind)}
        </h3>
        <p>
          {input.scope === "CASE"
            ? p.commandMeaning(input.act.kind)
            : p.authorityMeaning(input.act.kind)}
        </p>
        <ProtocolSummary value={display} document={operationDocument} node={schema} />
        {model.review.scope === "CASE" ? (
          <ReviewValues review={model.review} />
        ) : (
          <AuthorityValues review={model.review} />
        )}
        <ReviewConfirmation model={model} confirmationId={act.confirmationId} />
      </>
    </AccessibleModal>
  );
};
