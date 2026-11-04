import { Button } from "../../ui/Button";
import { useId, useState, type ReactNode } from "react";
import { AuthorityEffects } from "../../components/AuthorityEffects";
import { operationEntryForInput } from "../../protocol/operationCatalog";
import { WorkReviewValues, ReminderReviewValues } from "../work/WorkReviewValues";
import type { OperationInput } from "@contracts/client";
import { useRequesterContext } from "../../hooks/useRequesterContext";
import { IdentifierValue } from "../../components/IdentifierValue";
import { lifecycleMessageIds } from "../LifecycleStatus";
import { Dialog } from "../../ui/Dialog";
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
      <AuthorityEffects effects={review.effects} />
      <details className="authority-access-details">
        <summary>{p.text("ui.effectiveAccess")}</summary>
        {states.map(([label, state]) => (
          <section key={label}>
            <h4>{p.text(label, { value: p.text("ui.actor") })}</h4>
            <dl>
              <dt>{p.fieldLabel("profile.displayName")}</dt>
              <dd>
                {state.profile === null ? (
                  p.text("ui.notRecorded")
                ) : (
                  <bdi>{state.profile.displayName}</bdi>
                )}
              </dd>
              <dt>{p.fieldLabel("profile.contact")}</dt>
              <dd>
                {state.profile?.contact === null || state.profile === null ? (
                  p.text("ui.notRecorded")
                ) : (
                  <bdi>{state.profile.contact}</bdi>
                )}
              </dd>
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
                      {access.installationRoles.map((role) => p.term("role", role)).join(", ") ||
                        "—"}
                    </p>
                  </dd>
                </div>
              ))}
            </dl>
          </section>
        ))}
      </details>
    </>
  );
};
const reviewReference = (review: Review["subject"]): string => {
  if (review.kind === "CASE") {
    return review.after.caseReference;
  }
  if (review.kind === "WORK" || review.kind === "REMINDER") {
    return review.caseReference;
  }
  return "";
};
const ReviewBody = ({ review }: { review: Review["subject"] }) => {
  switch (review.kind) {
    case "CASE":
      return <ReviewValues review={review} />;
    case "WORK":
      return <WorkReviewValues review={review} />;
    case "REMINDER":
      return <ReminderReviewValues review={review} />;
    case "AUTHORITY":
      return <AuthorityValues review={review} />;
  }
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
  const reference = reviewReference(review);
  return (
    <p className="review-intent">
      <bdi dir="ltr" className="identifier-value">
        {reference}
      </bdi>{" "}
      {input.kind === "REMINDER" ? null : (
        <>
          · {p.fieldLabel("expectedRevision")}:{" "}
          <bdi dir="ltr">{p.integer(input.expectedRevision)}</bdi>
        </>
      )}
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
const ReviewBasis = ({ review }: { review: Review["subject"] }) => {
  const p = usePresentation();
  if (review.kind === "AUTHORITY" || review.before === null) {
    return null;
  }
  return (
    <p className="review-basis">
      {p.text("ui.review.basis", {
        revision: p.integer(review.before.revision),
        when: p.recordedTime(review.context.observedAt, review.context.timeZone),
      })}
    </p>
  );
};
const useReviewChallenge = (model: ReviewModel) => {
  const operationId = model.prepared?.input.operationId ?? null;
  const { review } = model;
  const [challenge, setChallenge] = useState<{
    operationId: string | null;
    review: Review | null;
    text: string;
  }>({ operationId, review, text: "" });
  const changed =
    challenge.operationId !== operationId || (review !== null && challenge.review !== review);
  if (changed) {
    setChallenge({ operationId, review, text: "" });
  }
  const typed = changed ? "" : challenge.text;
  return { typed, setChallenge, operationId, review };
};
const useReviewControls = (model: ReviewModel, paused: boolean) => {
  const p = usePresentation();
  const id = useId();
  const { typed, setChallenge, operationId, review } = useReviewChallenge(model);
  if (model.prepared === null || review === null) {
    return null;
  }
  const act = operationEntryForInput(model.prepared.input);
  const expected =
    model.prepared.input.kind === "CASE"
      ? { kind: "REFERENCE", value: reviewReference(review.subject) }
      : { kind: "OPERATION", value: model.prepared.input.operationId };
  const irreversible = act.consequence === "IRREVERSIBLE";
  const ready = !irreversible || typed === expected.value;
  const label =
    expected.kind === "REFERENCE"
      ? p.text("ui.exactCaseReference", { label: p.fieldLabel("caseReference") })
      : p.text("ui.exactOperationId");
  const submitText = () => {
    if (model.phase === "PREPARING") {
      return p.text("ui.preparingRecovery");
    }
    if (model.phase === "SUBMITTING") {
      return p.text("ui.submitting");
    }
    return renderCatalogKey(p, act.labelId);
  };
  return {
    body: (
      <>
        {act.consequenceId === null ? null : (
          <p className={`review-consequence review-consequence-${act.consequence.toLowerCase()}`}>
            {renderCatalogKey(p, act.consequenceId)}
          </p>
        )}
        {irreversible ? (
          <div className="text-field irreversible-confirmation">
            <p>
              <bdi dir="ltr">{expected.value}</bdi>
            </p>
            <label htmlFor={id}>{label}</label>
            <input
              id={id}
              type="text"
              autoComplete="off"
              spellCheck={false}
              dir="ltr"
              value={typed}
              disabled={model.busy}
              onChange={(event) => {
                setChallenge({ operationId, review, text: event.target.value });
                model.confirm(event.target.value === expected.value);
              }}
            />
          </div>
        ) : null}
      </>
    ),
    action: (
      <Button
        variant={irreversible ? "danger" : "primary"}
        data-confirm-operation
        isDisabled={!ready || model.busy || paused}
        onPress={() => {
          if (ready && !paused && !model.busy) {
            model.confirm(true);
            void model.execute();
          }
        }}
      >
        {submitText()}
      </Button>
    ),
  };
};
const reviewOpen = (phase: ReviewModel["phase"]) =>
  phase === "REVIEWING" || phase === "PREPARING" || phase === "SUBMITTING";
export const OperationReview = ({
  model,
  paused = false,
  children,
}: {
  model: ReviewModel;
  paused?: boolean;
  children?: (parts: { body: ReactNode; actions: ReactNode } | null) => ReactNode;
}) => {
  const p = usePresentation();
  const requester = useRequesterContext();
  const stopped = paused || requester?.maintenance.active === true;
  const controls = useReviewControls(model, stopped);
  if (model.review === null || model.prepared === null || controls === null) {
    return children?.(null) ?? null;
  }
  const { input } = model.prepared;
  const display = Object.fromEntries(Object.entries(input).filter(([name]) => name !== "kind"));
  const act = operationEntryForInput(input);
  const body = (
    <>
      {children === undefined ? (
        <>
          <h3>{renderCatalogKey(p, act.labelId)}</h3>
          <p>{renderCatalogKey(p, act.meaningId)}</p>
        </>
      ) : null}
      <ReviewIntent input={input} review={model.review.subject} />
      <ReviewBody review={model.review.subject} />
      <CorrectionIntent input={input} review={model.review.subject} />
      <ReviewBasis review={model.review.subject} />
      <details className="review-request">
        <summary>{p.text("ui.technicalDetails")}</summary>
        <ProtocolSummary value={display} entry={act} />
      </details>
      <p className="review-advisory">{p.text("ui.unchangedUntilSubmit")}</p>
      {controls.body}
    </>
  );
  const actions = (
    <>
      <Button variant="secondary" isDisabled={model.busy} onPress={model.returnToEdit}>
        {p.text("ui.returnToEdit")}
      </Button>
      {controls.action}
    </>
  );
  if (children !== undefined) {
    return children({ body, actions });
  }
  return (
    <Dialog
      title={p.text("label.reviewTitle")}
      placement="end"
      closeLabel={p.text("ui.returnToEdit")}
      description={p.text("ui.reviewDescription")}
      isOpen={reviewOpen(model.phase)}
      isDismissable={!model.busy}
      onOpenChange={model.returnToEdit}
      footer={actions}
    >
      {body}
    </Dialog>
  );
};
