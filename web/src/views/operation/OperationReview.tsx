import { usePresentation } from "../../presentation/context";
import { Button } from "react-aria-components/Button";
import { CheckboxButton, CheckboxField } from "react-aria-components/Checkbox";
import { BusinessValue } from "../../components/BusinessValue";
import { ReferenceSummary } from "../../components/ReferenceSummary";
import type { Review, CommandDraft, FieldDescriptor } from "../../api/service";
import { AccessibleModal } from "../../components/AccessibleModal";
import type { OperationEditorModel } from "./editorTypes";

const ReviewValues = ({
  review,
  fields,
}: {
  review: Review;
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

const ReviewIdentity = ({ draft, digest }: { draft: CommandDraft; digest: string }) => {
  const p = usePresentation();
  return (
    <>
      <p>
        <ReferenceSummary
          id="ui.reviewTarget"
          values={{ reference: draft.caseReference, revision: p.integer(draft.expectedRevision) }}
        />
      </p>
      <p>{p.text("ui.operationDigest", { operationId: draft.operationId, digest })}</p>
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
  if (
    model.state.review === null ||
    model.state.exposedRequest === null ||
    model.state.requestSha256 === null
  ) {
    return null;
  }
  return (
    <AccessibleModal
      closeLabel={p.text("ui.returnToEdit")}
      title={p.text("ui.reviewTitle")}
      description={p.text("ui.reviewDescription")}
      isOpen={model.state.delivery === "REVIEWING" || model.state.delivery === "SUBMITTING"}
      isDismissable={model.state.delivery === "REVIEWING"}
      onOpenChange={() => {
        model.dispatch({ type: "RETURN_TO_EDIT" });
      }}
    >
      <>
        <ReviewIdentity draft={model.state.exposedRequest} digest={model.state.requestSha256} />
        <ReviewValues review={model.state.review} fields={fields} />
        <ReviewConfirmation model={model} />
        <ReviewActions model={model} />
      </>
    </AccessibleModal>
  );
};
