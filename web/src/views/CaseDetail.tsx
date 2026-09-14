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
import { commandFor, type CommandKind } from "../domain/metadata";
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

const acceptedHistory = ({
  items,
  cursor,
  message,
  loading,
  load,
  fields,
}: ReturnType<typeof useRetryablePage<WebV2Response<"case.history">, Receipt>> & {
  fields: ReadonlyArray<FieldDescriptor>;
}) => (
  <section aria-labelledby="history-title">
    <h2 id="history-title">Accepted history</h2>
    <p>
      Historical entries are read-only. Expand an entry to inspect its accepted snapshot and
      attribution.
    </p>
    <ol className="history-list">
      {items.map((receipt) => (
        <li key={receipt.operationId}>
          <details>
            <summary>
              <bdi>{receipt.command}</bdi> · revision {receipt.snapshot.revision} ·{" "}
              <bdi>{receipt.recordedAt}</bdi>
            </summary>
            <p>
              Operation <bdi>{receipt.operationId}</bdi> · recorded by{" "}
              <bdi>{receipt.recordedBy}</bdi> · {receipt.replayed ? "exact replay" : "accepted"}
            </p>
            <CaseFieldsView
              caseView={receipt.snapshot}
              fields={fields}
              context={`history operation ${receipt.operationId}`}
            />
          </details>
        </li>
      ))}
    </ol>
    {message === null ? null : (
      <p className="error" role="alert">
        {message}
      </p>
    )}
    {loading ? <p role="status">Loading accepted history…</p> : null}
    {cursor === null ? null : (
      <Button onPress={() => void load(cursor)} isDisabled={loading}>
        Load more history
      </Button>
    )}
  </section>
);

const AvailableCommands = ({
  current,
  definition,
  onCommand,
}: {
  current: CurrentCase;
  definition: SemanticDefinition;
  onCommand: (command: CommandKind) => void;
}) => (
  <section aria-labelledby="commands-title">
    <h2 id="commands-title">Available commands</h2>
    <div className="actions">
      {current.availableCommands.map((command) => {
        const descriptor = commandFor(definition, command);
        return (
          <Button
            key={command}
            onPress={() => onCommand(command)}
            aria-label={`${descriptor.label}: ${descriptor.meaning}`}
          >
            {descriptor.label}
          </Button>
        );
      })}
    </div>
  </section>
);

const CurrentFeedback = ({
  message,
  loading,
  missing,
}: {
  message: string | null;
  loading: boolean;
  missing: boolean;
}) => (
  <>
    {message === null ? null : (
      <p className="error" role="alert">
        {message}
      </p>
    )}
    {loading ? <p role="status">Loading current case…</p> : null}
    {missing ? <p role="status">Case was not found.</p> : null}
  </>
);

const CurrentPresentation = ({
  current,
  definition,
  onCommand,
}: {
  current: CurrentCase;
  definition: SemanticDefinition;
  onCommand: (current: CurrentCase, command: CommandKind) => void;
}) => (
  <>
    <CaseFieldsView caseView={current.case} fields={definition.fields} context="current case" />
    <AvailableCommands
      current={current}
      definition={definition}
      onCommand={(command) => onCommand(current, command)}
    />
  </>
);

export const CaseDetail = ({
  token,
  caseReference,
  reloadSignal,
  definition,
  onBack,
  onCommand,
}: CaseDetailProps) => {
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
        <h2 id="case-detail-title">Case detail</h2>
        <Button onPress={onBack}>Back to cases</Button>
      </div>
      <p>
        Exact reference: <bdi>{caseReference}</bdi>{" "}
        <CopyValue label="case reference" value={caseReference} />
      </p>
      <CurrentFeedback
        message={current.message}
        loading={current.loading}
        missing={current.value?.tag === "NOT_FOUND"}
      />
      {lookup === null ? null : (
        <CurrentPresentation current={lookup} definition={definition} onCommand={onCommand} />
      )}
      {acceptedHistory({ ...history, fields: definition.fields })}
    </section>
  );
};
