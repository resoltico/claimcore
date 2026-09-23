import { NoticeView } from "../presentation/Message";
import { usePresentation } from "../presentation/context";
import type { Notice } from "../api/notices";
import { Button } from "react-aria-components/Button";
import { useCallback } from "react";
import type {
  CurrentCase,
  FieldDescriptor,
  Receipt,
  SemanticDefinition,
  WebV2Response,
} from "../api/v2";
import { v2 } from "../api/v2";
import { CaseFieldsView } from "../components/CaseFieldsView";
import { CopyValue } from "../components/CopyValue";
import { type CommandKind } from "../domain/metadata";
import { useRetryablePage, useV2Read } from "../hooks/useV2Read";

type CaseDetailProps = {
  token: string;
  caseReference: string;
  reloadSignal: number;
  definition: SemanticDefinition;
  onBack: () => void;
  onCommand: (current: CurrentCase, command: CommandKind) => void;
};

const fullHistory = (response: WebV2Response<"case.history">) => {
  const outcome = response.outcome;
  if (outcome.tag !== "SUCCEEDED") return null;
  if (outcome.data.tag === "NOT_FOUND") return { items: [], nextCursor: null };
  const entries = outcome.data.entries.flatMap((entry) =>
    entry.tag === "FULL" ? [entry.receipt] : [],
  );
  return { items: entries, nextCursor: outcome.data.nextCursor };
};

const caseLookup = (response: WebV2Response<"case.get">) =>
  response.outcome.tag === "SUCCEEDED" ? response.outcome.data : null;

const HistoryReceipt = ({
  receipt,
  fields,
}: {
  receipt: Receipt;
  fields: ReadonlyArray<FieldDescriptor>;
}) => {
  const p = usePresentation();
  return (
    <li>
      <details>
        <summary>
          {p.text("ui.historySummary", {
            command: p.commandLabel(receipt.command),
            revision: p.integer(receipt.snapshot.revision),
            timestamp: receipt.recordedAt,
          })}
        </summary>
        <p>
          {p.text("ui.receiptAttribution", {
            operationId: receipt.operationId,
            actor: receipt.recordedBy,
            state: receipt.replayed ? p.text("ui.exactReplay") : p.text("ui.accepted"),
          })}
        </p>
        <CaseFieldsView
          caseView={receipt.snapshot}
          fields={fields}
          context={p.text("ui.historyContext", { operationId: receipt.operationId })}
        />
      </details>
    </li>
  );
};

const AcceptedHistory = ({
  items,
  cursor,
  message,
  loading,
  load,
  fields,
}: ReturnType<typeof useRetryablePage<WebV2Response<"case.history">, Receipt>> & {
  fields: ReadonlyArray<FieldDescriptor>;
}) => {
  const p = usePresentation();
  return (
    <section aria-labelledby="history-title">
      <h2 id="history-title">{p.text("ui.acceptedHistory")}</h2>
      <p>{p.text("ui.historyHint")}</p>
      <ol className="history-list">
        {items.map((receipt) => (
          <HistoryReceipt key={receipt.operationId} receipt={receipt} fields={fields} />
        ))}
      </ol>
      {message === null ? null : (
        <p className="error" role="alert">
          <NoticeView value={message} />
        </p>
      )}
      {loading ? <p role="status">{p.text("ui.loadingHistory")}</p> : null}
      {cursor === null ? null : (
        <Button onPress={() => void load(cursor)} isDisabled={loading}>
          {p.text("ui.moreHistory")}
        </Button>
      )}
    </section>
  );
};

const AvailableCommands = ({
  current,
  onCommand,
}: {
  current: CurrentCase;
  definition: SemanticDefinition;
  onCommand: (command: CommandKind) => void;
}) => {
  const p = usePresentation();
  return (
    <section aria-labelledby="commands-title">
      <h2 id="commands-title">{p.text("ui.availableCommands")}</h2>
      <div className="actions">
        {current.availableCommands.map((command) => {
          return (
            <Button
              key={command}
              onPress={() => onCommand(command)}
              aria-label={p.text("ui.commandDescription", {
                label: p.commandLabel(command),
                meaning: p.commandMeaning(command),
              })}
            >
              {p.commandLabel(command)}
            </Button>
          );
        })}
      </div>
    </section>
  );
};

const CurrentFeedback = ({
  message,
  loading,
  missing,
}: {
  message: Notice | null;
  loading: boolean;
  missing: boolean;
}) => {
  const p = usePresentation();
  return (
    <>
      {message === null ? null : (
        <p className="error" role="alert">
          <NoticeView value={message} />
        </p>
      )}
      {loading ? <p role="status">{p.text("ui.loadingCurrentCase")}</p> : null}
      {missing ? <p role="status">{p.text("ui.caseNotFound")}</p> : null}
    </>
  );
};

const CurrentPresentation = ({
  current,
  definition,
  onCommand,
}: {
  current: CurrentCase;
  definition: SemanticDefinition;
  onCommand: (current: CurrentCase, command: CommandKind) => void;
}) => {
  const p = usePresentation();
  return (
    <>
      <CaseFieldsView
        caseView={current.case}
        fields={definition.fields}
        context={p.text("ui.currentCase")}
      />
      <AvailableCommands
        current={current}
        definition={definition}
        onCommand={(command) => onCommand(current, command)}
      />
    </>
  );
};

export const CaseDetail = ({
  token,
  caseReference,
  reloadSignal,
  definition,
  onBack,
  onCommand,
}: CaseDetailProps) => {
  const p = usePresentation();
  const get = useCallback(
    (signal: AbortSignal) => v2.get(caseReference, token, signal),
    [caseReference, token],
  );
  const current = useV2Read(get, caseLookup, `${caseReference}:${reloadSignal}`);
  const historyRequest = useCallback(
    (cursor: string | null, signal: AbortSignal) =>
      v2.history(caseReference, cursor, 50, token, signal),
    [caseReference, token],
  );
  const history = useRetryablePage<WebV2Response<"case.history">, Receipt>(
    historyRequest,
    fullHistory,
  );
  const lookup = current.value?.tag === "FOUND" ? current.value.current : null;
  return (
    <section aria-labelledby="case-detail-title">
      <div className="section-heading">
        <h2 id="case-detail-title">{p.text("ui.caseDetail")}</h2>
        <Button onPress={onBack}>{p.text("ui.backToCases")}</Button>
      </div>
      <p>
        {p.text("ui.exactReference", { reference: caseReference })}{" "}
        <CopyValue label={p.text("ui.caseReference")} value={caseReference} />
      </p>
      <CurrentFeedback
        message={current.message}
        loading={current.loading}
        missing={current.value?.tag === "NOT_FOUND"}
      />
      {lookup === null ? null : (
        <CurrentPresentation current={lookup} definition={definition} onCommand={onCommand} />
      )}
      <AcceptedHistory {...history} fields={definition.fields} />
    </section>
  );
};
