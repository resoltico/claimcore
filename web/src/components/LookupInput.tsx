import { useCallback } from "react";
import { Button } from "../ui/Button";
import { Select } from "../ui/Select";
import { FieldGroup } from "../ui/Field";
import type { TypedFieldProps } from "../ui/DateField";
import { usePresentation } from "../presentation/context";
import { renderCatalogKey } from "../presentation/messages";
import { actorName } from "../presentation/actorName";
import { NoticeView } from "../presentation/Message";
import { useEndpointPage } from "../hooks/useEndpointPage";
import type { HumanReadReply, OperationTaskDefinition, PresentedValue } from "@contracts/client";

type Props = Omit<TypedFieldProps, "field"> & {
  metadata: { labelId: string; hintId: string | null };
  caseId: string;
  retainedChoice?: { value: string; labelId: string };
  lookup: NonNullable<OperationTaskDefinition["inputs"][number]["choicesFrom"]>;
};

const choiceArgument = (p: ReturnType<typeof usePresentation>, value: PresentedValue) => {
  switch (value.kind) {
    case "ACTOR":
      return actorName(p, value.value);
    case "PARTY":
    case "TEXT":
      return value.value;
    case "CODE":
      return p.term(value.vocabulary, value.token);
    default:
      throw new Error("Unsupported lookup label argument.");
  }
};

const choices = (reply: HumanReadReply) => {
  if (reply.outcome.kind !== "SUCCEEDED") {
    return null;
  }
  const { document } = reply.outcome;
  const region = document.screen.regions.find(({ role }) => role === "RESULTS");
  if (document.basis.kind !== "SNAPSHOT" || region?.content.kind !== "TABLE") {
    return null;
  }
  return {
    items: region.content.rows.flatMap(({ choice }) => (choice === undefined ? [] : [choice])),
    nextCursor: document.nextCursor,
    basis: document.basis.observation,
    screen: document.screen,
    disclosure: document.disclosure,
    emptyId: region.emptyId,
  };
};

type LookupPage = NonNullable<ReturnType<typeof choices>>;

type LookupResult = ReturnType<
  typeof useEndpointPage<Props["lookup"], LookupPage["items"][number], LookupPage>
>;
const LookupFeedback = ({ result }: { result: LookupResult }) => {
  const p = usePresentation();
  return (
    <>
      {result.loading ? <p role="status">{p.text("ui.loading")}</p> : null}
      {result.page?.emptyId === undefined || result.items.length > 0 ? null : (
        <p>{renderCatalogKey(p, result.page.emptyId)}</p>
      )}
      {result.message === null ? null : (
        <p role="alert">
          <NoticeView value={result.message} />
        </p>
      )}
      {result.cursor === null && result.message === null ? null : (
        <Button
          variant="secondary"
          isDisabled={result.loading}
          onPress={() => {
            void result.load(result.cursor);
          }}
        >
          {p.text(result.message === null ? "ui.nextPage" : "ui.tryAgain")}
        </Button>
      )}
    </>
  );
};

const LookupChoices = ({ caseId, lookup, ...props }: Props) => {
  const p = usePresentation();
  const input = useCallback(
    (cursor: string | null) => ({
      target: { kind: "ID" as const, caseId },
      limit: 50,
      ...(cursor === null ? {} : { cursor }),
    }),
    [caseId],
  );
  const result = useEndpointPage<Props["lookup"], LookupPage["items"][number], LookupPage>(
    lookup,
    input,
    choices,
  );
  const { metadata, inputName, disabled, error, optional, value, onChange, sharedErrorId } = props;
  const errorId = sharedErrorId ?? `field-${inputName}-error`;
  const options = result.items.map((item) => ({
    value: item.value,
    label: renderCatalogKey(
      p,
      item.label.messageId,
      Object.fromEntries(
        item.label.arguments.map(({ key, value: argument }) => [key, choiceArgument(p, argument)]),
      ),
    ),
  }));
  const retained = props.retainedChoice;
  if (
    retained !== undefined &&
    retained.value !== "" &&
    !options.some((option) => option.value === retained.value)
  ) {
    options.unshift({ value: retained.value, label: renderCatalogKey(p, retained.labelId) });
  }
  if (optional === true) {
    options.unshift({ value: "", label: p.text("label.notRecorded") });
  }
  return (
    <FieldGroup>
      <div className="text-field lookup-input">
        <Select
          id={`field-${inputName}`}
          label={renderCatalogKey(p, metadata.labelId)}
          value={value}
          fallbackLabel={
            value === "" || result.page === null ? "" : p.text("label.choiceNotListed")
          }
          invalid={error !== undefined}
          {...(error === undefined ? {} : { describedBy: errorId })}
          options={options}
          description={metadata.hintId === null ? undefined : renderCatalogKey(p, metadata.hintId)}
          disabled={disabled || options.length === 0}
          onChange={onChange}
        />
        <LookupFeedback result={result} />
        {error === undefined ? null : (
          <p id={errorId} role="alert">
            <NoticeView value={error} />
          </p>
        )}
      </div>
    </FieldGroup>
  );
};

export const LookupInput = (props: Props) => {
  const p = usePresentation();
  if (props.disabled) {
    return (
      <Select
        id={`field-${props.inputName}`}
        label={renderCatalogKey(p, props.metadata.labelId)}
        value=""
        options={[]}
        disabled
        onChange={props.onChange}
      />
    );
  }
  return <LookupChoices {...props} />;
};
