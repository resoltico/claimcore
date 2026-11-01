import { useMemo, useState } from "react";
import { useEndpoint } from "../hooks/useEndpoint";
import { useRequesterContext } from "../hooks/useRequesterContext";
import { useCredential, useSessionContext } from "../session/SessionContext";
import { usePresentation } from "../presentation/context";
import { NoticeView } from "../presentation/Message";
import { IdentifierValue } from "./IdentifierValue";
import { CharacterWarning } from "./BusinessValue";
import type { AssigneePage, AssigneePageReply } from "@contracts/client";
import type { CurrentCase } from "../protocol/types";
import type { TypedFieldProps } from "./CatalogDate";

type Props = TypedFieldProps & { current: CurrentCase };
const AssigneeChoices = ({ page, ...props }: TypedFieldProps & { page: AssigneePage }) => {
  const p = usePresentation();
  return page.items.length === 0 ? (
    <p>{p.text("ui.noEligibleAccounts")}</p>
  ) : (
    <ul>
      {page.items.map((account) => (
        <li key={account.actorId}>
          <label>
            <input
              type="radio"
              name={props.inputName}
              value={account.actorId}
              checked={props.value === account.actorId}
              onChange={() => {
                props.onChange(account.actorId);
              }}
            />
            <bdi dir="auto">{account.subject}</bdi> <IdentifierValue value={account.actorId} />
            <CharacterWarning value={account.subject} />
          </label>
        </li>
      ))}
    </ul>
  );
};
const SelectedAssignee = ({ current, value }: Pick<Props, "current" | "value">) => {
  const p = usePresentation();
  const selected = current.work.responsible?.account;
  return value === "" ? null : (
    <p>
      {p.text("ui.selectedAccount")}:{" "}
      {selected?.actorId === value ? <bdi>{selected.subject}</bdi> : null}{" "}
      <IdentifierValue value={value} />
    </p>
  );
};
const discoveredPage = (value: AssigneePageReply | null) =>
  value?.outcome.kind === "SUCCEEDED" ? value.outcome.data : null;
/** Discovery is advisory; authoritative writes independently check the selected ActorId. */
export const AssigneeInput = ({ current, ...props }: Props) => {
  const p = usePresentation();
  const credential = useCredential();
  const { epoch } = useSessionContext().state;
  const requester = useRequesterContext();
  const { caseId } = current;
  const actorId = requester?.actorId;
  const installationId = requester?.installationId;
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
    "case.assignees",
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
      <p>{p.fieldMeaning(props.field.name)}</p>
      <SelectedAssignee current={current} value={props.value} />
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
      {props.error === undefined ? null : (
        <p role="alert">
          <NoticeView value={props.error} />
        </p>
      )}
    </fieldset>
  );
};
