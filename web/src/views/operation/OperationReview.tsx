import { Button } from "react-aria-components/Button";
import { Checkbox } from "react-aria-components/Checkbox";
import type { AdvisoryReview, PreparationDetails } from "../../api/v2";
import { AccessibleModal } from "../../components/AccessibleModal";
import type { OperationEditorModel } from "./editorTypes";

const ReviewValues = ({ review }: { review: AdvisoryReview }) => (
  <dl className="review-values">
    {review.changes.map((change) => (
      <div key={change.fieldName}>
        <dt>{change.fieldName}</dt>
        <dd>
          <bdi>{change.before ?? "Not recorded"}</bdi> → <bdi>{change.after ?? "Not recorded"}</bdi>
        </dd>
      </div>
    ))}
  </dl>
);

const PreparedIdentity = ({ preparation }: { preparation: PreparationDetails }) => (
  <>
    <p>
      Target <bdi>{preparation.summary.caseReference}</bdi> · expected revision{" "}
      {preparation.expectedRevision}
    </p>
    <p>
      Operation <bdi>{preparation.summary.operationId}</bdi> · digest{" "}
      <bdi>{preparation.summary.requestSha256 ?? "Unavailable"}</bdi>
    </p>
  </>
);

const ReviewActions = ({ model }: { model: OperationEditorModel }) => (
  <div className="dialog-actions">
    <Button
      className="secondary-button"
      onPress={() => model.dispatch({ type: "KEEP_FOR_RECOVERY" })}
      isDisabled={model.state.delivery === "SUBMITTING"}
    >
      Keep for Recovery
    </Button>
    <Button
      onPress={() => void model.submit()}
      isDisabled={!model.confirmed || model.state.delivery === "SUBMITTING"}
    >
      {model.state.delivery === "SUBMITTING" ? "Submitting…" : "Submit exact request"}
    </Button>
  </div>
);

export const OperationReview = ({ model }: { model: OperationEditorModel }) => {
  if (model.state.preparation === null) return null;
  return (
    <AccessibleModal
      title="Review prepared operation"
      description="The request is prepared but has not changed the accepted case. Submitting it starts a mutation that cannot be cancelled."
      isOpen={model.state.delivery === "REVIEWING" || model.state.delivery === "SUBMITTING"}
      isDismissable={model.state.delivery === "REVIEWING"}
      onOpenChange={() => model.dispatch({ type: "KEEP_FOR_RECOVERY" })}
    >
      <>
        <PreparedIdentity preparation={model.state.preparation} />
        <ReviewValues review={model.state.review!} />
        <Checkbox isSelected={model.confirmed} onChange={model.setConfirmed}>
          I will submit this exact prepared request.
        </Checkbox>
        <ReviewActions model={model} />
      </>
    </AccessibleModal>
  );
};
