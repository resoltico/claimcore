import { usePresentation } from "../../presentation/context";
import { Button } from "react-aria-components/Button";
import { CheckboxButton, CheckboxField } from "react-aria-components/Checkbox";
import { BusinessValue } from "../../components/BusinessValue";
import { ReferenceSummary } from "../../components/ReferenceSummary";
import type { AdvisoryReview, PreparationDetails, FieldDescriptor } from "../../api/v3";
import { AccessibleModal } from "../../components/AccessibleModal";
import type { OperationEditorModel } from "./editorTypes";

const ReviewValues = ({
  review,
  fields,
}: {
  review: AdvisoryReview;
  fields: ReadonlyArray<FieldDescriptor>;
}) => {
  const p = usePresentation();
  return (
    <dl className="review-values">
      {review.changes.map((change) => (
        <div key={change.fieldName}>
          <dt>{p.fieldLabel(change.fieldName)}</dt>
          <dd>
            <BusinessValue
              value={change.before}
              field={fields.find((field) => field.name === change.fieldName)!}
              context="ui.before"
            />
            <span className="review-arrow" aria-hidden="true">
              {" "}
              →{" "}
            </span>
            <BusinessValue
              value={change.after}
              field={fields.find((field) => field.name === change.fieldName)!}
              context="ui.after"
            />
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
        <ReferenceSummary
          id="ui.reviewTarget"
          values={{
            reference: preparation.summary.caseReference,
            revision: p.integer(preparation.expectedRevision),
          }}
        />
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
    <CheckboxField
      isDisabled={model.state.delivery === "SUBMITTING"}
      isSelected={model.confirmed}
      onChange={model.setConfirmed}
    >
      <CheckboxButton className="review-confirmation">
        {({ isSelected }) => (
          <>
            <span aria-hidden="true" className="review-checkmark">
              {isSelected ? "✓" : ""}
            </span>
            {p.text("ui.reviewConfirmation")}
          </>
        )}
      </CheckboxButton>
    </CheckboxField>
  );
};

export const OperationReview = ({
  model,
  fields,
}: {
  model: OperationEditorModel;
  fields: ReadonlyArray<FieldDescriptor>;
}) => {
  const p = usePresentation();
  if (model.state.preparation === null) {
    return null;
  }
  return (
    <AccessibleModal
      closeLabel={p.text("ui.keepForRecovery")}
      title={p.text("ui.reviewTitle")}
      description={p.text("ui.reviewDescription")}
      isOpen={model.state.delivery === "REVIEWING" || model.state.delivery === "SUBMITTING"}
      isDismissable={model.state.delivery === "REVIEWING"}
      onOpenChange={() => {
        model.dispatch({ type: "KEEP_FOR_RECOVERY" });
      }}
    >
      <>
        <PreparedIdentity preparation={model.state.preparation} />
        <ReviewValues review={model.state.review!} fields={fields} />
        <ReviewConfirmation model={model} />
        <ReviewActions model={model} />
      </>
    </AccessibleModal>
  );
};
