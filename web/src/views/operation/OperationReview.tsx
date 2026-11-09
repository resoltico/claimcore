import { Button } from "../../ui/Button";
import { useId, useState, type ReactNode } from "react";
import { ScreenView } from "../../ui/ScreenView";
import { ReviewPresentation, UnchangedReview } from "../../components/ReviewPresentation";
import { isReviewPresentationComplete } from "../../presentation/review";
import { taskForCommand } from "../../protocol/taskCatalog";
import type { HumanReview } from "@contracts/client";
import type { PreparedRequest } from "../../protocol/preparedRequest";
import { Dialog } from "../../ui/Dialog";
import { usePresentation } from "../../presentation/context";
import { NoticeView } from "../../presentation/Message";
import { renderCatalogKey } from "../../presentation/messages";

type ReviewModel = {
  prepared: PreparedRequest<"operation.execute"> | null;
  review: HumanReview | null;
  phase: string;
  busy: boolean;
  confirmed: boolean;
  confirm: (value: boolean) => void;
  execute: () => Promise<void>;
  returnToEdit: () => void;
};
const ReviewBody = ({ review }: { review: HumanReview }) => {
  const basis = review.document.basis.observation;
  const ordinary = review.document.screen.regions.filter((region) => region.role !== "DETAILS");
  return (
    <>
      <ReviewPresentation value={review.presentation} timeZone={basis.timeZone} />
      <UnchangedReview value={review.presentation} />
      <ScreenView view={{ regions: ordinary }} timeZone={basis.timeZone} />
    </>
  );
};
const useReviewChallenge = (model: ReviewModel) => {
  const operationId = model.prepared?.input.operationId ?? null;
  const { review } = model;
  const [challenge, setChallenge] = useState<{
    operationId: string | null;
    review: HumanReview | null;
    text: string;
  }>({ operationId, review, text: "" });
  const changed =
    challenge.operationId !== operationId || (review !== null && challenge.review !== review);
  if (changed) {
    setChallenge({ operationId, review, text: "" });
  }
  return { typed: changed ? "" : challenge.text, setChallenge, operationId, review };
};
const useReviewControls = (model: ReviewModel, paused: boolean) => {
  const p = usePresentation();
  const id = useId();
  const { typed, setChallenge, operationId, review } = useReviewChallenge(model);
  if (model.prepared === null || review === null) {
    return null;
  }
  const task = taskForCommand(model.prepared.input);
  const binding = review.document.context;
  const target = binding.confirmation;
  const expected = target.kind === "CASE_REFERENCE" ? target.reference : target.operationId;
  const irreversible = binding.consequence === "IRREVERSIBLE";
  const ready = !irreversible || typed === expected;
  const label =
    target.kind === "CASE_REFERENCE"
      ? p.text("ui.exactCaseReference", { label: p.fieldLabel("caseReference") })
      : p.text("ui.exactOperationId");
  const submitText =
    model.phase === "PREPARING"
      ? p.text("ui.preparingRecovery")
      : model.phase === "SUBMITTING"
        ? p.text("ui.submitting")
        : renderCatalogKey(p, task.labelId);
  return {
    body: !irreversible ? null : (
      <div className="text-field irreversible-confirmation">
        <p>
          <bdi dir="ltr">{expected}</bdi>
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
            model.confirm(event.target.value === expected);
          }}
        />
      </div>
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
        {submitText}
      </Button>
    ),
  };
};
const reviewOpen = (phase: string) =>
  phase === "REVIEWING" || phase === "PREPARING" || phase === "SUBMITTING";
const ReviewContents = ({
  review,
  complete,
  confirmation,
}: {
  review: HumanReview;
  complete: boolean;
  confirmation: ReactNode;
}) => {
  const p = usePresentation();
  const basis = review.document.basis.observation;
  return (
    <>
      {complete ? (
        <ReviewBody review={review} />
      ) : (
        <p role="alert" data-incomplete-review>
          <NoticeView value={{ kind: "local", reason: "incomplete" }} />
        </p>
      )}
      <p className="review-basis" title={basis.observedAt}>
        {p.text("ui.review.checked", { when: p.recordedTime(basis.observedAt, basis.timeZone) })}
      </p>
      <ScreenView view={review.document.screen} roles={["DETAILS"]} timeZone={basis.timeZone} />
      {complete &&
      review.presentation.changes.some(
        (entry) => entry.before.kind === "IDENTIFIER" || entry.after.kind === "IDENTIFIER",
      ) ? (
        <details className="review-request">
          <summary>{p.text("label.recordDetails")}</summary>
          <ReviewPresentation value={review.presentation} timeZone={basis.timeZone} technical />
        </details>
      ) : null}
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
  const complete = model.review === null || isReviewPresentationComplete(model.review.presentation);
  const controls = useReviewControls(model, paused || !complete);
  if (model.review === null || model.prepared === null || controls === null) {
    return children?.(null) ?? null;
  }
  const body = (
    <ReviewContents review={model.review} complete={complete} confirmation={controls.body} />
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
