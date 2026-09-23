import { usePresentation } from "../../presentation/context";
import { Button } from "react-aria-components/Button";
import { Checkbox } from "react-aria-components/Checkbox";
import type { AdvisoryReview, PreparationDetails } from "../../api/v2";
import { AccessibleModal } from "../../components/AccessibleModal";
import type { OperationEditorModel } from "./editorTypes";

const ReviewValues = ({ review }: { review: AdvisoryReview }) => {
  const p = usePresentation();
  return (
    <dl className="review-values">
      {review.changes.map((change) => (
        <div key={change.fieldName}>
          <dt>{p.fieldLabel(change.fieldName)}</dt>
          <dd>
            <span>{p.text("ui.before", { value: change.before ?? p.text("ui.notRecorded") })}</span>
            <span className="review-arrow" aria-hidden="true">
              {" "}
              →{" "}
            </span>
            <span>{p.text("ui.after", { value: change.after ?? p.text("ui.notRecorded") })}</span>
          </dd>
        </div>
      ))}
    </dl>
  );
};

const PreparedIdentity = ({ preparation }: { preparation: PreparationDetails }) => {
  const p = usePresentation();
  return (
    <>
      <p>
        {p.text("ui.reviewTarget", {
          reference: preparation.summary.caseReference,
          revision: p.integer(preparation.expectedRevision),
        })}
      </p>
      <p>
        {p.text("ui.operationDigest", {
          operationId: preparation.summary.operationId,
          digest: preparation.summary.requestSha256 ?? p.text("ui.unavailable"),
        })}
      </p>
    </>
  );
};

const ReviewActions = ({ model }: { model: OperationEditorModel }) => {
  const p = usePresentation();
  return (
    <div className="dialog-actions">
      <Button
        className="secondary-button"
        onPress={() => model.dispatch({ type: "KEEP_FOR_RECOVERY" })}
        isDisabled={model.state.delivery === "SUBMITTING"}
      >
        {p.text("ui.keepForRecovery")}
      </Button>
      <Button
        onPress={() => void model.submit()}
        isDisabled={!model.confirmed || model.state.delivery === "SUBMITTING"}
      >
        {model.state.delivery === "SUBMITTING" ? p.text("ui.submitting") : p.text("ui.submitExact")}
      </Button>
    </div>
  );
};

const ReviewConfirmation = ({ model }: { model: OperationEditorModel }) => {
  const p = usePresentation();
  return (
    <Checkbox
      className="review-confirmation"
      isDisabled={model.state.delivery === "SUBMITTING"}
      isSelected={model.confirmed}
      onChange={model.setConfirmed}
    >
      {({ isSelected }) => (
        <>
          <span aria-hidden="true" className="review-checkmark">
            {isSelected ? "✓" : ""}
          </span>
          {p.text("ui.reviewConfirmation")}
        </>
      )}
    </Checkbox>
  );
};

export const OperationReview = ({ model }: { model: OperationEditorModel }) => {
  const p = usePresentation();
  if (model.state.preparation === null) return null;
  return (
    <AccessibleModal
      title={p.text("ui.reviewTitle")}
      description={p.text("ui.reviewDescription")}
      isOpen={model.state.delivery === "REVIEWING" || model.state.delivery === "SUBMITTING"}
      isDismissable={model.state.delivery === "REVIEWING"}
      onOpenChange={() => model.dispatch({ type: "KEEP_FOR_RECOVERY" })}
    >
      <>
        <PreparedIdentity preparation={model.state.preparation} />
        <ReviewValues review={model.state.review!} />
        <ReviewConfirmation model={model} />
        <ReviewActions model={model} />
      </>
    </AccessibleModal>
  );
};
