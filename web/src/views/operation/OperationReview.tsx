import { Button } from "../../ui/Button";
import { useId, useState, type ReactNode } from "react";
import { ObservedCaseFacts } from "../work/ObservedCaseFacts";
import { AuthorityEffects } from "../../components/AuthorityEffects";
import { ReviewPresentation, UnchangedReview } from "../../components/ReviewPresentation";
import { CharacterWarning } from "../../components/BusinessValue";
import { isReviewPresentationComplete } from "../../presentation/review";
import { operationEntryForInput } from "../../protocol/operationCatalog";
import type { OperationInput, OperationReview as Review } from "@contracts/client";
import { useRequesterContext } from "../../hooks/useRequesterContext";
import { Dialog } from "../../ui/Dialog";
import { ProtocolSummary } from "../../components/ProtocolSummary";
import { usePresentation } from "../../presentation/context";
import { NoticeView } from "../../presentation/Message";
import type { OperationModel } from "../../hooks/operation/useOperation";
import { renderCatalogKey } from "../../presentation/messages";

type ReviewModel = Pick<
  OperationModel,
  "prepared" | "review" | "phase" | "busy" | "confirmed" | "confirm" | "execute" | "returnToEdit"
>;
const reviewReference = (review: Review["subject"]): string => {
  if (review.kind === "CASE") {
    return review.after.caseReference;
  }
  return review.kind === "AUTHORITY" ? "" : review.caseReference;
};
const reviewBasis = (review: Review["subject"]) =>
  review.kind === "AUTHORITY" ? review.basis : review.context;
const ReviewBasis = ({ review }: { review: Review["subject"] }) => {
  const p = usePresentation();
  const basis = reviewBasis(review);
  const when = p.recordedTime(basis.observedAt, basis.timeZone);
  const before = review.kind === "AUTHORITY" ? null : review.before;
  return (
    <p className="review-basis" title={basis.observedAt}>
      {before === null
        ? p.text("ui.review.checked", { when })
        : p.text("ui.review.basis", { revision: p.integer(before.revision), when })}
    </p>
  );
};

/** Exact authored intent remains distinct from the core-supplied value delta. */
const AuthoredIntent = ({
  input,
  review,
}: {
  input: OperationInput;
  review: Review["subject"];
}) => {
  const p = usePresentation();
  if (input.kind !== "CASE") {
    return null;
  }
  if (input.act.kind === "VOID" || input.act.kind === "REINSTATE") {
    return (
      <dl className="review-authored-intent">
        <dt>{p.fieldLabel("reason")}</dt>
        <dd>
          <bdi dir="auto">{input.act.reason}</bdi>
          <CharacterWarning value={input.act.reason} />
        </dd>
      </dl>
    );
  }
  if (input.act.kind !== "CORRECT" || input.act.groups.payment.kind !== "REAFFIRM") {
    return null;
  }
  const payment =
    review.kind === "CASE" && review.before !== null && review.before.state.kind !== "ERASED"
      ? review.before.state.fields.payment
      : null;
  return (
    <dl className="review-authored-intent">
      <dt>{p.text("group.payment.label")}</dt>
      <dd>
        {p.term("correction", "REAFFIRM")}
        {payment === null ? null : (
          <p>
            <time dateTime={payment.paymentDate}>{p.date(payment.paymentDate)}</time> ·{" "}
            <bdi dir="ltr">{payment.paymentDate}</bdi>
          </p>
        )}
      </dd>
    </dl>
  );
};
const ReviewBody = ({ review, input }: { review: Review; input: OperationInput }) => {
  const p = usePresentation();
  const basis = reviewBasis(review.subject);
  return (
    <>
      <ReviewPresentation value={review.presentation} timeZone={basis.timeZone} />
      <AuthoredIntent input={input} review={review.subject} />
      <UnchangedReview value={review.presentation} />
      {review.subject.kind === "AUTHORITY" && review.subject.effects.length > 0 ? (
        <details className="authority-review-effects">
          <summary>{p.text("label.effect")}</summary>
          <AuthorityEffects effects={review.subject.effects} />
        </details>
      ) : null}
      {review.subject.kind === "WORK" || review.subject.kind === "REMINDER" ? (
        <ObservedCaseFacts parent={review.subject.parent} />
      ) : null}
      <ReviewBasis review={review.subject} />
    </>
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
const ReviewContents = ({
  review,
  input,
  complete,
  standalone,
  confirmation,
}: {
  review: Review;
  input: OperationInput;
  complete: boolean;
  standalone: boolean;
  confirmation: ReactNode;
}) => {
  const p = usePresentation();
  const display = Object.fromEntries(Object.entries(input).filter(([name]) => name !== "kind"));
  const act = operationEntryForInput(input);
  return (
    <>
      {standalone ? (
        <>
          <h3>{renderCatalogKey(p, act.labelId)}</h3>
          <p>{renderCatalogKey(p, act.meaningId)}</p>
        </>
      ) : null}
      {complete ? (
        <ReviewBody review={review} input={input} />
      ) : (
        <p role="alert" data-incomplete-review>
          <NoticeView value={{ kind: "local", reason: "incomplete" }} />
        </p>
      )}
      <details className="review-request">
        <summary>{p.text("label.recordDetails")}</summary>
        {complete ? (
          <ReviewPresentation
            value={review.presentation}
            timeZone={reviewBasis(review.subject).timeZone}
            technical
          />
        ) : null}
        <ProtocolSummary value={display} entry={act} />
      </details>
      <p className="review-advisory">{p.text("ui.unchangedUntilSubmit")}</p>
      {confirmation}
    </>
  );
};
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
  const complete = model.review === null || isReviewPresentationComplete(model.review.presentation);
  const controls = useReviewControls(model, stopped || !complete);
  if (model.review === null || model.prepared === null || controls === null) {
    return children?.(null) ?? null;
  }
  const { input } = model.prepared;
  const body = (
    <ReviewContents
      review={model.review}
      input={input}
      complete={complete}
      standalone={children === undefined}
      confirmation={controls.body}
    />
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
