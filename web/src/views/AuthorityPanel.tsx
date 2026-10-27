import { useUnresolvedOperation } from "./operation/unresolvedOperation";
import { useCallback, useEffect, useLayoutEffect, useRef, useState } from "react";
import { productMetadata } from "../protocol/productMetadata";
import { isDisclosureRefused } from "../api/outcomes";
import { resultNotice, service, type ServiceResponse, type OperationReceipt } from "../api/service";
import { AuthorityGrants } from "./AuthorityGrants";
import { AuthorityListing } from "./AuthorityListing";
import type { Notice } from "../protocol/notices";
import { NoticeView } from "../presentation/Message";
import { usePresentation } from "../presentation/context";
import { useRead } from "../hooks/useRead";
import { useAuthorityDisclosure } from "../hooks/operation/useAuthorityDisclosure";
import { useOperation, type OperationModel } from "../hooks/operation/useOperation";
import { OperationPanel, OperationDiscard } from "./operation/OperationPanel";
const contextData = (response: ServiceResponse<"context">) =>
  response.outcome.kind === "SUCCEEDED" ? response.outcome.data : null;
type Disclosure = ReturnType<typeof useAuthorityDisclosure>;
const useAuthorityContext = (token: string, disclosure: Disclosure) => {
  const { refused, restored } = disclosure;
  return useRead(
    useCallback(
      async (signal: AbortSignal) => {
        const result = await service.context(token, signal);
        if (!signal.aborted) {
          if (isDisclosureRefused(result)) {
            refused(resultNotice(result));
          } else if (result.kind === "outcome" && result.value.outcome.kind === "SUCCEEDED") {
            restored("context");
          }
        }
        return result;
      },
      [token, refused, restored],
    ),
    contextData,
    token,
  );
};
const ContextStatus = ({
  loading,
  notice,
  reload,
}: {
  loading: boolean;
  notice: Notice | null;
  reload: () => void;
}) => {
  const p = usePresentation();
  return (
    <>
      {loading ? <p role="status">{p.text("ui.loading")}</p> : null}
      {notice === null ? null : (
        <>
          <p role="alert">
            <NoticeView value={notice} />
          </p>
          <button type="button" onClick={reload}>
            {p.text("ui.tryAgain")}
          </button>
        </>
      )}
    </>
  );
};
const AuthorityWorkspace = ({
  token,
  model,
  disclosure,
}: {
  token: string;
  model: OperationModel;
  disclosure: Disclosure;
}) => {
  const [selectedActorId, setSelectedActorId] = useState<string | null>(null);
  const context = useAuthorityContext(token, disclosure);
  const { refused } = disclosure;
  const offered = productMetadata.catalog.entries
    .filter((entry) => entry.subject === "AUTHORITY" && entry.endpoint === "operation.execute")
    .filter(
      (act) =>
        context.value?.offers.some(
          (offer) => offer.kind === "AUTHORITY_ACT" && offer.act === act.kind,
        ) === true,
    )
    .map((act) => act.kind);
  return (
    <>
      <OperationPanel model={model} current={null} offered={offered} />
      <ContextStatus loading={context.loading} notice={context.message} reload={context.reload} />
      <AuthorityListing
        token={token}
        mayReadGrants={context.value?.views.includes("AUTHORITY") === true}
        onSelect={setSelectedActorId}
        onDisclosureRefused={refused}
        onDisclosureRestored={disclosure.restored}
      />
      {selectedActorId === null ? null : (
        <AuthorityGrants
          key={selectedActorId}
          actorId={selectedActorId}
          token={token}
          onBack={() => {
            (
              document.getElementById(`actor-grants-${selectedActorId}`) ??
              document.getElementById("authority-actors-title")
            )?.focus();
            setSelectedActorId(null);
          }}
          onDisclosureRefused={refused}
        />
      )}
    </>
  );
};
const AuthorityReceipt = ({ identity }: { identity: string }) => {
  const p = usePresentation();
  const heading = useRef<HTMLHeadingElement>(null);
  useEffect(() => {
    heading.current?.focus();
  }, []);
  return (
    <>
      <h3 ref={heading} tabIndex={-1}>
        {p.text("ui.accepted")}
      </h3>
      <p role="status">
        <bdi>{identity}</bdi>
      </p>
    </>
  );
};
type RecordedAuthority = { token: string; id: string } | null;
type AuthorityActions = { invalidate: () => void; restore: () => void; reset: () => void } | null;
type AuthorityLayoutProps = {
  token: string;
  model: OperationModel;
  disclosure: Disclosure;
  recorded: RecordedAuthority;
};
const AuthorityLayout = ({ token, model, disclosure, recorded }: AuthorityLayoutProps) => {
  const p = usePresentation();
  const { invalidate } = disclosure;
  return (
    <section aria-labelledby="authority-title" className="authority-panel">
      <h2 id="authority-title">{p.text("subject.AUTHORITY.label")}</h2>
      {recorded?.token === token ? (
        <AuthorityReceipt key={recorded.id} identity={recorded.id} />
      ) : null}
      {disclosure.notice === null ? null : (
        <p role="alert">
          <NoticeView value={disclosure.notice} />
        </p>
      )}
      <OperationDiscard model={model} />
      {disclosure.active ? (
        <AuthorityWorkspace
          key={`${token}:${disclosure.revision}`}
          token={token}
          model={model}
          disclosure={disclosure}
        />
      ) : (
        <>
          <OperationPanel model={model} current={null} offered={[]} />
          <button
            type="button"
            onClick={() => {
              invalidate(true);
            }}
          >
            {p.text("ui.reloadActors")}
          </button>
        </>
      )}
    </section>
  );
};
const initialAuthorityAct = productMetadata.catalog.entries.find(
  (entry) => entry.subject === "AUTHORITY" && entry.endpoint === "operation.execute",
)!.kind;
type AuthorityPanelProps = {
  token: string;
  onLockChange: (locked: boolean) => void;
};
const useAuthorityOperation = ({ token, onLockChange }: AuthorityPanelProps) => {
  const disclosure = useAuthorityDisclosure();
  const { refused: refuseDisclosure, invalidate, ready } = disclosure;
  const [recorded, setRecorded] = useState<RecordedAuthority>(null);
  const actions = useRef<AuthorityActions>(null);
  const refused = useCallback(
    (notice?: Notice) => {
      actions.current?.invalidate();
      setRecorded(null);
      refuseDisclosure(notice);
    },
    [refuseDisclosure],
  );
  const onRecorded = (receipt: OperationReceipt) => {
    setRecorded({
      token,
      id: receipt.operationId,
    });
    invalidate(true);
  };
  const model = useOperation({
    subject: "AUTHORITY",
    kind: initialAuthorityAct,
    current: null,
    token,
    onLockChange,
    onRecorded,
    onDisclosureRefused: refused,
  });
  useUnresolvedOperation(model);
  useLayoutEffect(() => {
    actions.current = {
      invalidate: model.invalidateDisclosure,
      restore: model.restoreDisclosure,
      reset: model.discard,
    };
  });
  useEffect(() => {
    if (ready) {
      actions.current?.restore();
    }
  }, [ready]);
  return { model, disclosure: { ...disclosure, refused }, recorded };
};
export const AuthorityPanel = (props: AuthorityPanelProps) => {
  const state = useAuthorityOperation(props);
  return <AuthorityLayout token={props.token} {...state} />;
};
