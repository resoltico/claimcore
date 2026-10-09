import { NoticeView } from "../presentation/Message";
import { usePresentation } from "../presentation/context";
import type { Notice } from "../api/notices";
import { Button } from "react-aria-components/Button";
import { useCallback } from "react";
import type { CurrentCase, SemanticDefinition, WebV3Response } from "../api/v3";
import { v3 } from "../api/v3";
import { CaseFieldsView } from "../components/CaseFieldsView";
import { ReferenceSummary } from "../components/ReferenceSummary";
import { CopyValue } from "../components/CopyValue";
import { type CommandKind } from "../domain/metadata";
import { useRead } from "../hooks/useRead";
import { CaseHistory } from "./CaseHistory";
import { LifecycleStatus } from "./LifecycleStatus";

type CaseDetailProps = {
  token: string;
  caseReference: string;
  reloadSignal: number;
  definition: SemanticDefinition;
  onBack: () => void;
  onCommand: (current: CurrentCase, command: CommandKind) => void;
};

const caseLookup = (response: WebV3Response<"case.get">) =>
  response.outcome.tag === "SUCCEEDED" ? response.outcome.data : null;

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
        {current.availableCommands.map((command) => (
          <Button
            key={command}
            onPress={() => {
              onCommand(command);
            }}
            aria-label={p.text("ui.commandDescription", {
              label: p.commandLabel(command),
              meaning: p.commandMeaning(command),
            })}
          >
            {p.commandLabel(command)}
          </Button>
        ))}
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
        onCommand={(command) => {
          onCommand(current, command);
        }}
      />
    </>
  );
};

const CaseHeader = ({
  caseReference,
  onBack,
}: Pick<CaseDetailProps, "caseReference" | "onBack">) => {
  const p = usePresentation();
  const label = p.fieldLabel("caseReference");
  return (
    <>
      <div className="section-heading">
        <h2 id="case-detail-title">{p.text("ui.caseDetail")}</h2>
        <Button onPress={onBack}>{p.text("ui.backToCases")}</Button>
      </div>
      <p>
        <ReferenceSummary id="ui.exactReference" values={{ label, reference: caseReference }} />{" "}
        <CopyValue label={label} value={caseReference} />
      </p>
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
  const get = useCallback(
    (signal: AbortSignal) => v3.get(caseReference, token, signal),
    [caseReference, token],
  );
  const current = useRead(get, caseLookup, `${caseReference}:${reloadSignal}`);
  const lookup = current.value?.tag === "FOUND" ? current.value.current : null;
  return (
    <section aria-labelledby="case-detail-title">
      <CaseHeader caseReference={caseReference} onBack={onBack} />
      <CurrentFeedback
        message={current.message}
        loading={current.loading}
        missing={current.value?.tag === "NOT_FOUND"}
      />
      {lookup === null ? null : (
        <CurrentPresentation current={lookup} definition={definition} onCommand={onCommand} />
      )}
      {current.loading ? null : (
        <LifecycleStatus caseReference={caseReference} token={token} reloadSignal={reloadSignal} />
      )}
      <CaseHistory
        key={`${caseReference}:${reloadSignal}`}
        token={token}
        caseReference={caseReference}
        fields={definition.fields}
      />
    </section>
  );
};
