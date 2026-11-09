import { FieldHelp } from "./FieldHelp";
import { PresentedValue } from "./PresentedValue";
import { useMemo, useState } from "react";
import { useEndpoint } from "../hooks/useEndpoint";
import { useRequesterContext } from "../hooks/useRequesterContext";
import { useCredential, useSessionContext } from "../session/SessionContext";
import { usePresentation } from "../presentation/context";
import { NoticeView } from "../presentation/Message";
import type { HumanReadDocument_caseAssignees, HumanResponseByEndpoint } from "@contracts/client";
import { localNotice } from "../protocol/notices";
import type { CurrentCase } from "../protocol/types";
import type { TypedFieldProps } from "./CatalogDate";

type Props = TypedFieldProps & { current: CurrentCase };
const AssigneeChoices = ({
  page,
  ...props
}: TypedFieldProps & { page: HumanReadDocument_caseAssignees }) => {
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
  return page.context.actorIds.length === 0 ? (
    <p>{p.text("ui.noEligibleAccounts")}</p>
  ) : content?.kind !== "TABLE" ? null : (
    <ul>
      {page.context.actorIds.map((actorId) => {
        const row = content.rows.find((row) => row.key === actorId);
        return row === undefined ? null : (
          <li key={actorId}>
            <label>
              <input
                type="radio"
                name={props.inputName}
                value={actorId}
                checked={props.value === actorId}
                onChange={() => props.onChange(actorId)}
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
        );
      })}
    </ul>
  );
};
const SelectedAssignee = ({ current, value }: Pick<Props, "current" | "value">) => {
  const p = usePresentation();
  if (value === "" || current.context.seeds.work?.responsible !== value) {
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
const discoveredPage = (value: HumanResponseByEndpoint["case.assignees"] | null) =>
  value?.outcome.kind === "SUCCEEDED" ? value.outcome.document : null;
/** Discovery is advisory; authoritative writes independently check the selected ActorId. */
export const AssigneeInput = ({ current, ...props }: Props) => {
  const p = usePresentation();
  const credential = useCredential();
  const { epoch } = useSessionContext().state;
  const requester = useRequesterContext();
  const { caseId } = current.context;
  const actorId = requester?.context.actorId;
  const installationId = requester?.context.installationId;
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
      <FieldHelp
        name={props.field.name}
        label={p.fieldLabel(props.field.name)}
        ruleId={props.field.ruleId}
      />
      <p>{p.hint(props.field)}</p>
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
              disabled={result.loading || page.context.nextCursor === null}
              onClick={() => {
                if (page.context.nextCursor !== null) {
                  setPaging({ owner, cursors: [...cursors, page.context.nextCursor] });
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
