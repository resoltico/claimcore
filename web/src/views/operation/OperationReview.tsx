import type { OperationInput } from "@contracts/client";
import { useRequesterContext } from "../../hooks/useRequesterContext";
import { IdentifierValue } from "../../components/IdentifierValue";
import { lifecycleMessageIds } from "../LifecycleStatus";
import { AccessibleModal } from "../../components/AccessibleModal";
import { ProtocolSummary } from "../../components/ProtocolSummary";
import { BusinessValue, CharacterWarning } from "../../components/BusinessValue";
import { usePresentation } from "../../presentation/context";
import { productMetadata } from "../../protocol/productMetadata";
import type { FieldCatalogEntry } from "../../api/service";
import type { OperationModel } from "../../hooks/operation/useOperation";
import type { OperationReview as Review, CaseReview } from "../../protocol/types";
import { renderCatalogKey } from "../../presentation/messages";
import { caseFieldValue } from "../../protocol/caseFields";
type ReviewModel = Pick<
  OperationModel,
  "prepared" | "review" | "phase" | "busy" | "confirmed" | "confirm" | "execute" | "returnToEdit"
>;
const ExactReviewValue = ({
  value,
  field,
  currency,
}: {
  value: string | null;
  field: FieldCatalogEntry;
  currency?: string;
}) => {
  const p = usePresentation();
  return value !== null &&
    ["AMOUNT", "CURRENCY", "CALENDAR_DATE", "PLANNED_DATE"].includes(field.scalar.kind) ? (
    <small className="exact-recorded-value">
      {p.text("ui.exactRecordedValue")}:{" "}
      <bdi dir="ltr">
        {value}
        {currency === undefined ? "" : ` ${currency}`}
      </bdi>
    </small>
  ) : null;
};
const reviewCurrency = (
  field: (typeof productMetadata.catalog.fields)[number],
  snapshot: CaseReview["before"],
) =>
  field.pairs === null || snapshot === null || snapshot.state.kind === "ERASED"
    ? undefined
    : (caseFieldValue(snapshot.state.fields, field.pairs) ?? undefined);
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
        {review.changes.map((change) => {
          const field = productMetadata.catalog.fields.find(
            (candidate) => candidate.name === change.field,
          )!;
          const beforeCurrency = reviewCurrency(field, review.before);
          const afterCurrency = reviewCurrency(field, review.after);
          return (
            <div key={change.field}>
              <dt>{p.fieldLabel(change.field)}</dt>
              <dd className="review-comparison">
                <div className="review-before">
                  <BusinessValue
                    value={change.before}
                    field={field}
                    context="ui.before"
                    {...(beforeCurrency === undefined ? {} : { currency: beforeCurrency })}
                  />
                  <ExactReviewValue
                    value={change.before}
                    field={field}
                    {...(beforeCurrency === undefined ? {} : { currency: beforeCurrency })}
                  />
                </div>
                <span className="review-arrow" aria-hidden="true">
                  {" → "}
                </span>
                <div className="review-after">
                  <BusinessValue
                    value={change.after}
                    field={field}
                    context="ui.after"
                    {...(afterCurrency === undefined ? {} : { currency: afterCurrency })}
                  />
                  <ExactReviewValue
                    value={change.after}
                    field={field}
                    {...(afterCurrency === undefined ? {} : { currency: afterCurrency })}
                  />
                </div>
              </dd>
            </div>
          );
        })}
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
      <p>
        {p.term("scope", review.accessScope.kind)}
        {review.accessScope.kind === "CASE" ? (
          <>
            {" "}
            <IdentifierValue value={review.accessScope.caseReference} />
          </>
        ) : null}
      </p>
      {states.map(([label, state]) => (
        <section key={label}>
          <h4>{p.text(label, { value: p.text("ui.actor") })}</h4>
          <dl>
            <dt>{p.text("label.enabled")}</dt>
            <dd>{p.text(state.enabled ? "ui.yes" : "ui.no")}</dd>
          </dl>
          <h5>{p.text("ui.actorGrants")}</h5>
          <ReviewGrants grants={state.grants} />
          <h5>{p.text("ui.effectiveAccess")}</h5>
          <dl>
            {state.access.map((access) => (
              <div key={access.capability}>
                <dt>{p.term("capability", access.capability)}</dt>
                <dd>
                  <p>{p.text(access.effective ? "ui.effective" : "ui.dormant")}</p>
                  <p>
                    {p.text("ui.directRoles")}:{" "}
                    {access.directRoles.map((role) => p.term("role", role)).join(", ") || "—"}
                  </p>
                  <p>
                    {p.text("ui.inheritedRoles")}:{" "}
                    {access.installationRoles.map((role) => p.term("role", role)).join(", ") || "—"}
                  </p>
                </dd>
              </div>
            ))}
          </dl>
        </section>
      ))}
    </>
  );
};
const ReviewIntent = ({ input, review }: { input: OperationInput; review: Review["subject"] }) => {
  const p = usePresentation();
  if (input.kind === "AUTHORITY") {
    return (
      <dl className="review-intent">
        <div>
          <dt>{p.text("ui.principal")}</dt>
          <dd>
            {p.term("principalKind", input.target.kind)} · <bdi>{input.target.subject}</bdi>
            <CharacterWarning value={input.target.subject} />
          </dd>
        </div>
        <div>
          <dt>{p.fieldLabel("principal.issuer")}</dt>
          <dd>
            <bdi>{input.target.issuer}</bdi>
            <CharacterWarning value={input.target.issuer} />
          </dd>
        </div>
        {input.act.kind === "GRANT" || input.act.kind === "REVOKE" ? (
          <div>
            <dt>{p.fieldLabel("role")}</dt>
            <dd>{p.term("role", input.act.role)}</dd>
          </div>
        ) : null}
      </dl>
    );
  }
  const reference = review.kind === "CASE" ? review.after.caseReference : "";
  return (
    <p className="review-intent">
      <bdi dir="ltr" className="identifier-value">
        {reference}
      </bdi>{" "}
      · {p.fieldLabel("expectedRevision")}: <bdi dir="ltr">{p.integer(input.expectedRevision)}</bdi>
    </p>
  );
};
const CorrectionIntent = ({
  input,
  review,
}: {
  input: OperationInput;
  review: Review["subject"];
}) => {
  const p = usePresentation();
  if (input.kind !== "CASE" || input.act.kind !== "CORRECT") {
    return null;
  }
  const { groups } = input.act;
  const prior =
    review.kind === "CASE" && review.before !== null && review.before.state.kind !== "ERASED"
      ? review.before.state.fields.payment
      : null;
  return (
    <dl className="review-groups">
      {(["registration", "decision", "payment"] as const).map((group) => (
        <div key={group}>
          <dt>{p.text(`group.${group}.label`)}</dt>
          <dd>
            {p.term("correction", groups[group].kind)}
            {group === "payment" && groups.payment.kind === "REAFFIRM" && prior !== null ? (
              <small className="exact-recorded-value">
                {p.fieldLabel("paymentDate")}: <bdi dir="ltr">{p.date(prior.paymentDate)}</bdi> ·{" "}
                <bdi dir="ltr">{prior.paymentDate}</bdi>
              </small>
            ) : null}
          </dd>
        </div>
      ))}
    </dl>
  );
};
const ReviewConfirmation = ({
  model,
  confirmationId,
  paused,
}: {
  model: ReviewModel;
  confirmationId: string;
  paused: boolean;
}) => {
  const p = usePresentation();

  const submitText = () => {
    if (model.phase === "PREPARING") {
      return p.text("ui.preparingRecovery");
    }
    if (model.phase === "SUBMITTING") {
      return p.text("ui.submitting");
    }
    return p.text("ui.submitExact");
  };
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
        disabled={!model.confirmed || model.busy || paused}
        onClick={() => void model.execute()}
      >
        {submitText()}
      </button>
    </>
  );
};
export const OperationReview = ({
  model,
  paused = false,
}: {
  model: ReviewModel;
  paused?: boolean;
}) => {
  const p = usePresentation();
  const requester = useRequesterContext();
  const stopped = paused || requester?.maintenance.active === true;
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
      isOpen={
        model.phase === "REVIEWING" || model.phase === "PREPARING" || model.phase === "SUBMITTING"
      }
      isDismissable={!model.busy}
      onOpenChange={model.returnToEdit}
    >
      <>
        <h3>
          {input.kind === "CASE" ? p.actLabel(input.act.kind) : p.authorityLabel(input.act.kind)}
        </h3>
        <p>
          {input.kind === "CASE"
            ? p.actMeaning(input.act.kind)
            : p.authorityMeaning(input.act.kind)}
        </p>
        <ReviewIntent input={input} review={model.review.subject} />
        {model.review.subject.kind === "CASE" ? (
          <ReviewValues review={model.review.subject} />
        ) : (
          <AuthorityValues review={model.review.subject} />
        )}
        <CorrectionIntent input={input} review={model.review.subject} />
        <details className="review-request">
          <summary>{p.text("ui.technicalDetails")}</summary>
          <ProtocolSummary value={display} entry={act} />
        </details>
        <p className="review-advisory">{p.text("ui.unchangedUntilSubmit")}</p>
        <ReviewConfirmation model={model} confirmationId={act.confirmationId!} paused={stopped} />
      </>
    </AccessibleModal>
  );
};
