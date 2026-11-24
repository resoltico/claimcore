import { ComboBox, type PickerItem } from "../ui/ComboBox";
import { apiEndpoints } from "../api/service";
import { actorName } from "../presentation/actorName";
import { personLinkActorId } from "../routing/coreLinks";
import { isCanonicalUuid } from "../protocol/uuid";
import { ChoiceControl } from "../ui/TextField";
import { allActions } from "../operation/affordances";
import { FieldGroup, FieldRule } from "../ui/Field";
import { PresentedValue } from "./PresentedValue";
import { useEffect, useId, useMemo, useState } from "react";
import { useEndpoint } from "../hooks/useEndpoint";
import { useRequesterContext } from "../hooks/useRequesterContext";
import { useCredential, useSessionContext } from "../session/SessionContext";
import { usePresentation } from "../presentation/context";
import { NoticeView } from "../presentation/Message";
import type {
  HumanDocument,
  HumanReadReply,
  OperationTaskDefinition,
  TaskInputId,
} from "@contracts/client";
import { renderCatalogKey } from "../presentation/messages";
import { localNotice } from "../protocol/notices";
import type { CurrentCase } from "../protocol/types";
import type { TypedFieldProps } from "../ui/DateField";

type Props = TypedFieldProps & {
  current: CurrentCase;
  lookup: NonNullable<OperationTaskDefinition["inputs"][number]["choicesFrom"]>;
  seedInput: TaskInputId;
};
const AssigneeChoices = ({ page, ...props }: TypedFieldProps & { page: HumanDocument }) => {
  const p = usePresentation();
  if (page.basis.kind !== "SNAPSHOT") {
    return (
      <p role="alert">
        <NoticeView value={localNotice("incomplete")} />
      </p>
    );
  }
  const zone = page.basis.observation.timeZone;
  const region = page.screen.regions.find((candidate) => candidate.role === "RESULTS");
  const content = region?.content;
  if (content?.kind === "TABLE" && content.rows.length === 0) {
    const emptyId = region?.emptyId;
    return emptyId === undefined ? null : <p>{renderCatalogKey(p, emptyId)}</p>;
  }
  if (content?.kind !== "TABLE") {
    return null;
  }
  return (
    <ul>
      {content.rows.map((row) => {
        const actorId = row.choice;
        return typeof actorId === "string" ? (
          <li key={actorId}>
            <label>
              <ChoiceControl
                type="radio"
                name={props.inputName}
                value={actorId}
                checked={props.value === actorId}
                onChange={() => {
                  props.onChange(actorId);
                }}
              />
              {row.values.map((value, index) =>
                content.columns[index]?.prominence === "DETAIL" ? null : (
                  <span key={content.columns[index]?.key}>
                    <PresentedValue value={value} timeZone={zone} />
                  </span>
                ),
              )}
            </label>
          </li>
        ) : null;
      })}
    </ul>
  );
};
const SelectedAssignee = ({
  current,
  value,
  seedInput,
}: Pick<Props, "current" | "value" | "seedInput">) => {
  const p = usePresentation();
  if (
    value === "" ||
    allActions(current).find((action) => action.seeds[seedInput] === value)?.seeds[seedInput] !==
      value
  ) {
    return null;
  }
  const content = current.screen.regions.find((region) => region.role === "WORK")?.content;
  const caption =
    content?.kind === "ENTRIES"
      ? content.entries.find((entry) => entry.key === "responsible")?.value
      : undefined;
  return caption === undefined ? null : (
    <p>
      {p.text("ui.selectedAccount")}:{" "}
      <PresentedValue value={caption} timeZone={current.basis.observation.timeZone} />
    </p>
  );
};
const EmptyLookupChoice = ({ optional, inputName, value, onChange }: TypedFieldProps) => {
  const p = usePresentation();
  return optional === true ? (
    <label>
      <ChoiceControl
        type="radio"
        name={inputName}
        value=""
        checked={value === ""}
        onChange={() => {
          onChange("");
        }}
      />
      {p.text("label.notRecorded")}
    </label>
  ) : null;
};
const discoveredPage = (value: HumanReadReply | null) =>
  value?.outcome.kind === "SUCCEEDED" ? value.outcome.document : null;
/** Discovery is advisory; authoritative writes independently check the selected ActorId. */
export const AssigneeInput = ({ current, lookup, seedInput, ...props }: Props) => {
  const p = usePresentation();
  const hintId = useId();
  const ruleId = `${hintId}-rule`;
  const credential = useCredential();
  const { epoch } = useSessionContext().state;
  const requester = useRequesterContext();
  const { caseId } = current.subject;
  const actorId = requester?.subject.actorId;
  const installationId = requester?.subject.installationId;
  // Cursor ownership includes the authenticated disclosure scope, even for the same case.
  const owner = useMemo(
    () => ({ caseId, credential, epoch, actorId, installationId }),
    [caseId, credential, epoch, actorId, installationId],
  );
  const [paging, setPaging] = useState<{ owner: typeof owner; cursors: ReadonlyArray<string> }>({
    owner,
    cursors: [],
  });
  const cursors = paging.owner === owner ? paging.cursors : [];
  const cursor = cursors.at(-1);
  const result = useEndpoint(
    lookup,
    {
      target: { kind: "ID", caseId },
      limit: 50,
      ...(cursor === undefined ? {} : { cursor }),
    },
    !props.disabled,
  );
  const page = discoveredPage(result.value);
  return (
    <FieldGroup>
      <fieldset
        id={`field-${props.inputName}`}
        className="assignee-input"
        disabled={props.disabled}
        aria-describedby={props.field.ruleId === null ? hintId : `${hintId} ${ruleId}`}
      >
        <legend>{p.fieldLabel(props.field.name)}</legend>
        <p id={hintId}>{p.hint(props.field)}</p>
        <SelectedAssignee current={current} value={props.value} seedInput={seedInput} />
        <EmptyLookupChoice {...props} />
        {result.loading ? <p role="status">{p.text("ui.loading")}</p> : null}
        {result.message === null ? null : (
          <p role="alert">
            <NoticeView value={result.message} />
          </p>
        )}
        {page === null ? null : (
          <>
            <AssigneeChoices {...props} page={page} />
            <div className="actions">
              <button
                type="button"
                disabled={result.loading || cursors.length === 0}
                onClick={() => {
                  setPaging({ owner, cursors: cursors.slice(0, -1) });
                }}
              >
                {p.text("ui.previousAccounts")}
              </button>
              <button
                type="button"
                disabled={result.loading || page.nextCursor === null}
                onClick={() => {
                  if (page.nextCursor !== null) {
                    setPaging({ owner, cursors: [...cursors, page.nextCursor] });
                  }
                }}
              >
                {p.text("ui.nextAccounts")}
              </button>
            </div>
          </>
        )}
        <button type="button" disabled={result.loading} onClick={result.reload}>
          {p.text("ui.refreshAccounts")}
        </button>
        <FieldRule
          id={ruleId}
          name={props.field.name}
          ruleId={props.field.ruleId}
          invalid={props.error !== undefined}
        />
        {props.error === undefined ? null : (
          <p role="alert">
            <NoticeView value={props.error} />
          </p>
        )}
      </fieldset>
    </FieldGroup>
  );
};

const directoryChoices = (
  document: HumanDocument,
  p: ReturnType<typeof usePresentation>,
): PickerItem[] =>
  document.screen.regions.flatMap(({ content }) => {
    if (content.kind !== "TABLE") {
      return [];
    }
    return content.rows.flatMap((row) => {
      const actor = row.values.find((value) => value.kind === "ACTOR");
      const id = row.link === undefined ? undefined : personLinkActorId(row.link);
      return actor?.kind === "ACTOR" && id !== undefined
        ? [{ id, label: actorName(p, actor.value) }]
        : [];
    });
  });

export const DirectoryPersonPicker = ({
  selected,
  change,
}: {
  selected: string;
  change: (id: string) => void;
}) => {
  const p = usePresentation();
  const [draft, setDraft] = useState(selected);
  const [query, setQuery] = useState("");
  const exact = draft.trim().toLowerCase();
  const isExact = isCanonicalUuid(exact);
  useEffect(() => {
    if (draft.trim().length < 2 || isExact) {
      return;
    }
    const delay = window.setTimeout(() => {
      setQuery(draft.trim());
    }, 250);
    return () => {
      window.clearTimeout(delay);
    };
  }, [draft, isExact]);
  const searching = query === draft.trim() && query !== "" && !isExact;
  const list = useEndpoint(apiEndpoints.peopleList, { text: query, limit: 8 }, searching);
  const document = list.value?.outcome.kind === "SUCCEEDED" ? list.value.outcome.document : null;
  const choices: PickerItem[] = searching && document !== null ? directoryChoices(document, p) : [];
  if (isExact) {
    choices.push({ id: exact, label: p.text("label.useAccountId", { id: exact }) });
  }
  const clearOrUseId = () => {
    change(isExact ? exact : "");
  };
  return (
    <ComboBox
      id="history-actor"
      label={p.text("label.person")}
      value={draft}
      items={choices}
      onChange={setDraft}
      onSelect={change}
      onSubmit={isExact || draft.trim() === "" ? clearOrUseId : undefined}
    />
  );
};
