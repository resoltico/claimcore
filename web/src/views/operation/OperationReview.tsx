import { lifecycleMessageKey } from "../LifecycleStatus";
import { usePresentation } from "../../presentation/context";
import { Button } from "react-aria-components/Button";
import { CheckboxButton, CheckboxField } from "react-aria-components/Checkbox";
import { BusinessValue } from "../../components/BusinessValue";
import { ReferenceSummary } from "../../components/ReferenceSummary";
import type { Review, CommandDraft, FieldDescriptor } from "../../api/service";
import { AccessibleModal } from "../../components/AccessibleModal";
import type { OperationEditorModel } from "../../interaction/editorTypes";

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
        <div key={change.field}>
          <dt>{p.fieldLabel(change.field)}</dt>
          <dd>
            <BusinessValue
              value={change.before}
              field={fields.find((field) => field.name === change.field)!}
              context="ui.before"
            />
            <span className="review-arrow" aria-hidden="true">
              {" "}
              →{" "}
            </span>
            <BusinessValue
              value={change.after}
              field={fields.find((field) => field.name === change.field)!}
              context="ui.after"
            />
          </dd>
        </div>
      ))}
    </dl>
  );
};

const ReviewIdentity = ({ draft }: { draft: CommandDraft }) => {
  const p = usePresentation();
  return (
    <>
      <p>
        <ReferenceSummary
          id="ui.reviewTarget"
          values={{ reference: draft.caseReference, revision: p.integer(draft.expectedRevision) }}
        />
      </p>
      <p>
        <code>{draft.operationId}</code>
      </p>
    </>
  );
};

const authoredFields = (act: CommandDraft["act"]): Readonly<Record<string, string>> => {
  if ("values" in act) {
    return act.values;
  }
  if ("reason" in act) {
    return { reason: act.reason };
  }
  if ("groups" in act) {
    return Object.fromEntries(
      Object.values(act.groups).flatMap((group) =>
        group.mode === "REPLACE" ? Object.entries(group.values) : [],
      ),
    );
  }
  return {};
};
const AuthoredFields = ({ model }: { model: OperationEditorModel }) => {
  const p = usePresentation();
  const values = authoredFields(model.state.exposedRequest!.act);
  const fields = [
    ...model.fields.map(({ field }) => field),
    ...model.groups.flatMap((group) => group.fields),
  ];
  return (
    <dl className="review-values">
      {fields.map((field) => {
        const value = values[field.name];
        return value === undefined ? null : (
          <div key={field.name}>
            <dt>{p.fieldLabel(field.name)}</dt>
            <dd>
              <BusinessValue value={value} field={field} />
            </dd>
          </div>
        );
      })}
    </dl>
  );
};
const AuthoredCorrectionModes = ({ act }: { act: CommandDraft["act"] }) => {
  const p = usePresentation();
  if (!("groups" in act)) {
    return null;
  }
  return (
    <dl className="review-values">
      {Object.entries(act.groups).map(([name, group]) => (
        <div key={name}>
          <dt>{p.groupLabel(name)}</dt>
          <dd>{p.token(group.mode)}</dd>
        </div>
      ))}
    </dl>
  );
};

const ReviewOperation = ({ model }: { model: OperationEditorModel }) => {
  const p = usePresentation();
  const draft = model.state.exposedRequest!;
  const review = model.state.review!;
  return (
    <section aria-label={p.commandLabel(draft.act.kind)}>
      <h3>{p.commandLabel(draft.act.kind)}</h3>
      <p>{p.commandMeaning(draft.act.kind)}</p>
      <AuthoredFields model={model} />
      <AuthoredCorrectionModes act={draft.act} />
      {review.before === null ? null : (
        <p>{p.text("ui.before", { value: p.text(lifecycleMessageKey(review.before.state)) })}</p>
      )}
      <p>{p.text("ui.after", { value: p.text(lifecycleMessageKey(review.after.state)) })}</p>
    </section>
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
            {p.text(model.command.confirmationId)}
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
  if (model.state.review === null || model.state.exposedRequest === null) {
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
        <ReviewIdentity draft={model.state.exposedRequest} />
        <ReviewOperation model={model} />
        <ReviewValues review={model.state.review} fields={fields} />
        <ReviewConfirmation model={model} />
        <ReviewActions model={model} />
      </>
    </AccessibleModal>
  );
};
