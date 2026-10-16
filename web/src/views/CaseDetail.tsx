import { NoticeView } from "../presentation/Message";
import { usePresentation } from "../presentation/context";
import type { Notice } from "../api/notices";
import { Button } from "react-aria-components/Button";
import { useCallback, useState } from "react";
import type { CurrentCase, Descriptors, ServiceResponse } from "../api/service";
import { isCurrentCase, service } from "../api/service";
import { CaseFieldsView } from "../components/CaseFieldsView";
import { ReferenceSummary } from "../components/ReferenceSummary";
import { CopyValue } from "../components/CopyValue";
import { isCaseAct, type CommandKind } from "../interaction/metadata";
import { useRead } from "../hooks/useRead";
import { CaseHistory } from "./CaseHistory";
import { LifecycleStatus } from "./LifecycleStatus";

type CaseDetailProps = {
  token: string;
  caseReference: string;
  reloadSignal: number;
  definition: Descriptors;
  onBack: () => void;
  onCommand: (current: CurrentCase, command: CommandKind) => void;
};

const caseLookup = (response: ServiceResponse<"case.get">) =>
  response.outcome.tag === "SUCCEEDED" ? response.outcome.data : null;

const AvailableCommands = ({
  current,
  onCommand,
}: {
  current: CurrentCase;
  definition: Descriptors;
  onCommand: (command: CommandKind) => void;
}) => {
  const p = usePresentation();
  return (
    <section aria-labelledby="commands-title">
      <h2 id="commands-title">{p.text("ui.availableCommands")}</h2>
      <div className="actions">
        {current.offers
          .map((offer) => offer.definition)
          .filter(isCaseAct)
          .map((command) => (
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

const CurrentFeedback = ({ message, loading }: { message: Notice | null; loading: boolean }) => {
  const p = usePresentation();
  return (
    <>
      {message === null ? null : (
        <p className="error" role="alert">
          <NoticeView value={message} />
        </p>
      )}
      {loading ? <p role="status">{p.text("ui.loadingCurrentCase")}</p> : null}
    </>
  );
};

const CurrentPresentation = ({
  current,
  definition,
  onCommand,
}: {
  current: CurrentCase;
  definition: Descriptors;
  onCommand: (current: CurrentCase, command: CommandKind) => void;
}) => {
  const p = usePresentation();
  return (
    <>
      <CaseFieldsView
        values={current.state.fields}
        revision={current.revision}
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
  const [authorityRevision, setAuthorityRevision] = useState(0);
  const revalidateDisclosure = useCallback(() => {
    setAuthorityRevision((revision) => revision + 1);
  }, []);
  const get = useCallback(
    (signal: AbortSignal) => service.get(caseReference, token, signal),
    [caseReference, token],
  );
  const current = useRead(get, caseLookup, `${caseReference}:${reloadSignal}:${authorityRevision}`);
  const state = current.value;
  const lookup = state !== null && isCurrentCase(state) ? state : null;
  return (
    <section aria-labelledby="case-detail-title">
      <CaseHeader caseReference={caseReference} onBack={onBack} />
      <CurrentFeedback message={current.message} loading={current.loading} />
      {lookup === null ? null : (
        <CurrentPresentation current={lookup} definition={definition} onCommand={onCommand} />
      )}
      {state === null ? null : <LifecycleStatus value={state} />}
      {state?.state.tag === "ERASED" ? (
        <p role="status">
          {state.caseReference} · {state.revision}
        </p>
      ) : null}
      <CaseHistory
        key={`${caseReference}:${reloadSignal}`}
        token={token}
        caseReference={caseReference}
        fields={definition.fields}
        modes={state?.historyDetails ?? []}
        onDisclosureRefused={revalidateDisclosure}
      />
    </section>
  );
};
