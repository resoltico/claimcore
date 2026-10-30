import { IdentifierValue } from "../../components/IdentifierValue";
import { lifecycleMessageIds } from "../LifecycleStatus";
import { AccessibleModal } from "../../components/AccessibleModal";
import { ProtocolSummary } from "../../components/ProtocolSummary";
import { BusinessValue } from "../../components/BusinessValue";
import { usePresentation } from "../../presentation/context";
import { productMetadata } from "../../protocol/productMetadata";
import type { OperationModel } from "../../hooks/operation/useOperation";
import type { OperationReview as Review, CaseReview } from "../../protocol/types";
import { renderCatalogKey } from "../../presentation/messages";
type ReviewModel = Pick<
  OperationModel,
  "prepared" | "review" | "phase" | "busy" | "confirmed" | "confirm" | "execute" | "returnToEdit"
>;
const ReviewValues = ({ review }: { review: CaseReview }) => {
  const p = usePresentation();
  return (
    <>
      {review.before === null ? null : (
        <p>
          {p.text("ui.before", { value: p.text(lifecycleMessageIds[review.before.state.kind]) })}
        </p>
      )}
      <p>{p.text("ui.after", { value: p.text(lifecycleMessageIds[review.after.state.kind]) })}</p>
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
              <span className="review-arrow" aria-hidden="true">
                {" → "}
              </span>
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
type AuthorityReview = Extract<Review["subject"], { kind: "AUTHORITY" }>;
const ReviewGrants = ({ grants }: { grants: AuthorityReview["after"]["grants"] }) => {
  const p = usePresentation();
  return (
    <ul>
      {grants.map((grant) => (
        <li key={JSON.stringify(grant)}>
          <dl>
            <dt>{p.text("field.role.label")}</dt>
            <dd>
              <span>{p.term("role", grant.role)}</span>
            </dd>
            <dt>{p.text("input.scope.label")}</dt>
            <dd>
              <span>{p.term("scope", grant.scope.kind)}</span>{" "}
              {grant.scope.kind === "CASE" ? (
                <IdentifierValue value={grant.scope.caseReference} />
              ) : null}
            </dd>
          </dl>
        </li>
      ))}
    </ul>
  );
};
const AuthorityValues = ({ review }: { review: AuthorityReview }) => {
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
            <dt>{p.text("label.enabled")}</dt>
            <dd>{p.text(state.enabled ? "ui.yes" : "ui.no")}</dd>
          </dl>
          <h5>{p.text("ui.actorGrants")}</h5>
          <ReviewGrants grants={state.grants} />
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
  confirmationId: string;
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
        {renderCatalogKey(p, confirmationId)}
      </label>
      <button
        className="primary"
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
  const display = Object.fromEntries(Object.entries(input).filter(([name]) => name !== "kind"));
  const act = productMetadata.catalog.entries.find(
    (item) => item.subject === input.kind && item.kind === input.act.kind,
  )!;
  return (
    <AccessibleModal
      title={p.text("label.reviewTitle")}
      closeLabel={p.text("ui.returnToEdit")}
      description={p.text("ui.reviewDescription")}
      isOpen={model.phase === "REVIEWING" || model.phase === "SUBMITTING"}
      isDismissable={!model.busy}
      onOpenChange={model.returnToEdit}
    >
      <>
        <p>{p.text("ui.unchangedUntilSubmit")}</p>
        <h3>
          {input.kind === "CASE" ? p.actLabel(input.act.kind) : p.authorityLabel(input.act.kind)}
        </h3>
        <p>
          {input.kind === "CASE"
            ? p.actMeaning(input.act.kind)
            : p.authorityMeaning(input.act.kind)}
        </p>
        <ProtocolSummary value={display} entry={act} />
        {model.review.subject.kind === "CASE" ? (
          <ReviewValues review={model.review.subject} />
        ) : (
          <AuthorityValues review={model.review.subject} />
        )}
        <ReviewConfirmation model={model} confirmationId={act.confirmationId!} />
      </>
    </AccessibleModal>
  );
};
