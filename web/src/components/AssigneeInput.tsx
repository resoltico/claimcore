import { allActions } from "../operation/affordances";
import { FieldHelp } from "./FieldHelp";
import { PresentedValue } from "./PresentedValue";
import { useMemo, useState } from "react";
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
import { localNotice } from "../protocol/notices";
import type { CurrentCase } from "../protocol/types";
import type { TypedFieldProps } from "./CatalogDate";

type Props = TypedFieldProps & {
  current: CurrentCase;
  lookup: NonNullable<OperationTaskDefinition["inputs"][number]["choicesFrom"]>;
  seedInput: TaskInputId;
};
const AssigneeChoices = ({
  page,
  seedInput,
  ...props
}: TypedFieldProps & { page: HumanDocument; seedInput: TaskInputId }) => {
  const p = usePresentation();
  if (page.basis.kind !== "SNAPSHOT") {
    return (
      <p role="alert">
        <NoticeView value={localNotice("incomplete")} />
      </p>
    );
  }
  const zone = page.basis.observation.timeZone;
  const content = page.screen.regions.find((region) => region.role === "RESULTS")?.content;
  if (content?.kind === "TABLE" && content.rows.length === 0) {
    return <p>{p.text("label.noEligibleAccounts")}</p>;
  }
  if (content?.kind !== "TABLE") {
    return null;
  }
  return (
    <ul>
      {content.rows.map((row) => {
        const action = row.actions?.find(
          (candidate) => typeof candidate.seeds[seedInput] === "string",
        );
        const actorId = action?.seeds[seedInput];
        return typeof actorId === "string" ? (
          <li key={actorId}>
            <label>
              <input
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
const discoveredPage = (value: HumanReadReply | null) =>
  value?.outcome.kind === "SUCCEEDED" ? value.outcome.document : null;
/** Discovery is advisory; authoritative writes independently check the selected ActorId. */
export const AssigneeInput = ({ current, lookup, seedInput, ...props }: Props) => {
  const p = usePresentation();
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
    <fieldset id={`field-${props.inputName}`} className="assignee-input" disabled={props.disabled}>
      <legend>{p.fieldLabel(props.field.name)}</legend>
      <FieldHelp
        name={props.field.name}
        label={p.fieldLabel(props.field.name)}
        ruleId={props.field.ruleId}
      />
      <p>{p.hint(props.field)}</p>
      <SelectedAssignee current={current} value={props.value} seedInput={seedInput} />
      {result.loading ? <p role="status">{p.text("ui.loading")}</p> : null}
      {result.message === null ? null : (
        <p role="alert">
          <NoticeView value={result.message} />
        </p>
      )}
      {page === null ? null : (
        <>
          <AssigneeChoices {...props} page={page} seedInput={seedInput} />
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
      {props.error === undefined ? null : (
        <p role="alert">
          <NoticeView value={props.error} />
        </p>
      )}
    </fieldset>
  );
};
