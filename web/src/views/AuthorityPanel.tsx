import type { AuthorityGrant, ActorReviewItem } from "@contracts/client";
import { useAuthorityTarget, type AuthorityTarget } from "../hooks/operation/useAuthorityTarget";
import { ActorOperation, ActorActions } from "./authority/ActorOperation";
import { useOperationNavigation } from "../hooks/useOperationNavigation";
import { useEndpointCache } from "../hooks/EndpointProvider";
import { useContentRoute } from "../hooks/SettingsDetourContext";
import { navigate, setNavigationLock, replaceDisclosureRoute } from "../routing/navigation";
import { useCredential } from "../session/SessionContext";
import {
  useCallback,
  useEffect,
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
  type ReactNode,
} from "react";
import { productMetadata } from "../protocol/productMetadata";
import { isDisclosureRefused } from "../api/outcomes";
import { resultNotice, type ServiceResponse, type OperationReceipt } from "../api/service";
import { AuthorityGrants } from "./AuthorityGrants";
import { AuthorityListing } from "./AuthorityListing";
import type { Notice } from "../protocol/notices";
import { NoticeView } from "../presentation/Message";
import { usePresentation } from "../presentation/context";
import { useEndpoint } from "../hooks/useEndpoint";
import { useAuthorityDisclosure } from "../hooks/operation/useAuthorityDisclosure";
import { useOperation, type OperationModel } from "../hooks/operation/useOperation";
import { OperationPanel, OperationDiscard } from "./operation/OperationPanel";
const contextData = (response: ServiceResponse<"context">) =>
  response.outcome.kind === "SUCCEEDED" && response.outcome.data.requester.kind === "ACTIVE"
    ? response.outcome.data.requester
    : null;
type Disclosure = ReturnType<typeof useAuthorityDisclosure>;
const useAuthorityContext = (disclosure: Disclosure) => {
  const query = useEndpoint("context", {});
  const { refused, restored } = disclosure;
  useEffect(() => {
    let active = true;
    void Promise.resolve().then(() => {
      if (!active || query.result === null) {
        return;
      }
      if (isDisclosureRefused(query.result)) {
        refused(resultNotice(query.result));
      } else if (
        query.result.kind === "outcome" &&
        query.result.value.outcome.kind === "SUCCEEDED"
      ) {
        restored("context");
      }
    });
    return () => {
      active = false;
    };
  }, [query.result, refused, restored]);
  return { ...query, value: query.value === null ? null : contextData(query.value) };
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
const SelectedActorGrants = ({
  actorId,
  refused,
  onActor,
  open,
  actions,
  onRevoke,
}: {
  onRevoke: ((grant: AuthorityGrant) => void) | undefined;
  open: boolean;
  actions: ReactNode;
  actorId: string | null;
  refused: (notice?: Notice) => void;
  onActor: (actor: ActorReviewItem) => void;
}) =>
  actorId === null ? null : (
    <AuthorityGrants
      key={actorId}
      actorId={actorId}
      open={open}
      actions={actions}
      onRevoke={onRevoke}
      onBack={() => {
        const target =
          document.getElementById(`actor-grants-${actorId}`) ??
          document.getElementById("authority-title");
        navigate("/people", false, target?.id);
      }}
      onDisclosureRefused={refused}
      onActor={onActor}
    />
  );
const AuthorityDirectory = ({
  selectedActorId,
  disclosure,
  available,
  onSelect,
  mayReadGrants,
  onActor,
  open,
  actions,
  onRevoke,
}: {
  onRevoke: ((grant: AuthorityGrant) => void) | undefined;
  open: boolean;
  actions: ReactNode;
  selectedActorId: string | null;
  disclosure: Disclosure;
  available: boolean;
  onSelect: (id: string) => void;
  mayReadGrants: boolean;
  onActor: (actor: ActorReviewItem) => void;
}) => {
  if (!available) {
    return null;
  }
  return (
    <>
      <SelectedActorGrants
        actorId={selectedActorId}
        refused={disclosure.refused}
        onActor={onActor}
        open={open}
        actions={actions}
        onRevoke={onRevoke}
      />
      <AuthorityListing
        titleId="authority-title"
        mayReadGrants={mayReadGrants}
        onSelect={onSelect}
        onDisclosureRefused={disclosure.refused}
        onDisclosureRestored={disclosure.restored}
      />
    </>
  );
};
const useAuthorityWorkspace = (disclosure: Disclosure, target: AuthorityTarget) => {
  const route = useContentRoute();
  const selectedActorId = route.screen === "people" && route.actorId !== "" ? route.actorId : null;
  const setSelectedActorId = (actorId: string | null): void => {
    navigate(actorId === null ? "/people" : `/people/${encodeURIComponent(actorId)}`);
  };
  const context = useAuthorityContext(disclosure);
  const offered = useMemo(
    () =>
      productMetadata.catalog.entries
        .filter((entry) => entry.subject === "AUTHORITY" && entry.endpoint === "operation.execute")
        .filter(
          (act) =>
            context.value?.offers.some(
              (offer) => offer.kind === "AUTHORITY_ACT" && offer.act === act.kind,
            ) === true,
        )
        .map((act) => act.kind),
    [context.value],
  );
  const { observe } = target;
  const onActor = useCallback(
    (actor: ActorReviewItem) => {
      observe(actor, offered);
    },
    [observe, offered],
  );
  return { selectedActorId, setSelectedActorId, context, offered, onActor };
};
const AuthorityWorkspace = ({
  model,
  disclosure,
  disclosureAvailable,
  target,
  editor,
}: {
  editor: AuthorityEditor;
  model: OperationModel;
  disclosure: Disclosure;
  disclosureAvailable: boolean;
  target: AuthorityTarget;
}) => {
  const p = usePresentation();
  const { selectedActorId, setSelectedActorId, context, offered, onActor } = useAuthorityWorkspace(
    disclosure,
    target,
  );
  return (
    <>
      <ContextStatus loading={context.loading} notice={context.message} reload={context.reload} />
      {selectedActorId === null &&
      context.value?.offers.some(
        (offer) => offer.kind === "AUTHORITY_ACT" && offer.act === "ADMIT",
      ) === true ? (
        <button
          type="button"
          disabled={model.busy}
          onClick={() => {
            if (
              model.phase === "ACCEPTED" ||
              (!model.dirty && model.prepared === null && !model.priorUncertain)
            ) {
              editor.admit(context.value!.issuer);
            }
            editor.open();
          }}
        >
          {p.authorityLabel("ADMIT")}
        </button>
      ) : null}
      <div className="authority-workspace">
        <div>
          <AuthorityDirectory
            selectedActorId={selectedActorId}
            open={!editor.active && model.pendingDiscard === null}
            actions={<ActorActions target={target} busy={model.busy} onOpen={editor.open} />}
            onRevoke={
              target.offered.includes("REVOKE")
                ? (grant) => {
                    target.begin("REVOKE", {
                      "act.role": grant.role,
                      "act.scope.kind": grant.scope.kind,
                      ...(grant.scope.kind === "CASE"
                        ? { "act.scope.target.caseReference": grant.scope.caseReference }
                        : {}),
                    });
                    editor.open();
                  }
                : undefined
            }
            disclosure={disclosure}
            available={disclosureAvailable}
            onSelect={setSelectedActorId}
            mayReadGrants={context.value?.views.includes("AUTHORITY") === true}
            onActor={onActor}
          />
        </div>
        <ActorOperation
          model={model}
          target={target}
          offered={offered}
          available={disclosureAvailable}
          open={editor.active}
          onOpen={editor.open}
          onClose={editor.close}
        />
      </div>
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
      <h2 ref={heading} tabIndex={-1}>
        {p.text("ui.accepted")}
      </h2>
      <p role="status">
        <bdi>{identity}</bdi>
      </p>
    </>
  );
};
type RecordedAuthority = { credential: string; id: string } | null;
type AuthorityActions = { invalidate: () => void; restore: () => void; reset: () => void } | null;
type AuthorityEditor = {
  active: boolean;
  open: () => void;
  close: () => void;
  admit: (issuer: string) => void;
};
type AuthorityLayoutProps = {
  editor: AuthorityEditor;
  model: OperationModel;
  target: AuthorityTarget;
  disclosure: Disclosure;
  recorded: RecordedAuthority;
  disclosureAvailable: boolean;
  onClose: () => void;
};
const AuthorityTitle = () => {
  const p = usePresentation();
  return <h1 id="authority-title">{p.text("ui.shell.people")}</h1>;
};
const AuthorityUnavailable = ({
  model,
  invalidate,
  available,
  editor,
}: {
  editor: AuthorityEditor;
  model: OperationModel;
  invalidate: Disclosure["invalidate"];
  available: boolean;
}) => {
  const p = usePresentation();
  return (
    <>
      <OperationPanel
        model={model}
        current={null}
        offered={[]}
        disclosureAvailable={available}
        open={editor.active}
        onClose={editor.close}
      />
      {model.priorUncertain ? (
        <button type="button" onClick={editor.open}>
          {p.text("ui.continueAuthorityRequest")}
        </button>
      ) : null}
      <button
        type="button"
        onClick={() => {
          invalidate(true);
        }}
      >
        {p.text("ui.reloadActors")}
      </button>
    </>
  );
};
const AuthorityLayout = ({
  model,
  disclosure,
  recorded,
  onClose,
  disclosureAvailable,
  target,
  editor,
}: AuthorityLayoutProps) => {
  const credential = useCredential();
  const { invalidate } = disclosure;
  return (
    <section aria-labelledby="authority-title" className="authority-panel">
      <AuthorityTitle />
      {recorded?.credential === credential ? (
        <AuthorityReceipt key={recorded.id} identity={recorded.id} />
      ) : null}
      {disclosure.notice === null ? null : (
        <p role="alert">
          <NoticeView value={disclosure.notice} />
        </p>
      )}
      <OperationDiscard model={model} onClose={onClose} />
      {disclosure.active ? (
        <AuthorityWorkspace
          key={`${credential}:${disclosure.revision}`}
          model={model}
          disclosure={disclosure}
          disclosureAvailable={disclosureAvailable}
          target={target}
          editor={editor}
        />
      ) : (
        <AuthorityUnavailable
          model={model}
          invalidate={invalidate}
          available={disclosureAvailable}
          editor={editor}
        />
      )}
    </section>
  );
};
const initialAuthorityAct = productMetadata.catalog.entries.find(
  (entry) => entry.subject === "AUTHORITY" && entry.kind === "ADMIT",
)!.kind;
type AuthorityPanelProps = {
  onLockChange: (locked: boolean) => void;
  disclosureAvailable?: boolean;
};
const useAuthorityActions = (
  model: OperationModel,
  actionsRef: React.RefObject<AuthorityActions>,
  ready: boolean,
) => {
  useLayoutEffect(() => {
    actionsRef.current = {
      invalidate: model.invalidateDisclosure,
      restore: model.restoreDisclosure,
      reset: model.discard,
    };
  });
  useEffect(() => {
    if (ready) {
      actionsRef.current?.restore();
    }
  }, [ready, actionsRef]);
};
const useAuthorityOperation = ({ onLockChange }: AuthorityPanelProps) => {
  const credential = useCredential();
  const disclosure = useAuthorityDisclosure();
  const cache = useEndpointCache();
  const { refused: refuseDisclosure, invalidate, ready } = disclosure;
  const [recorded, setRecorded] = useState<RecordedAuthority>(null);
  const actions = useRef<AuthorityActions>(null);
  const refused = useCallback(
    (notice?: Notice) => {
      actions.current?.invalidate();
      setRecorded(null);
      replaceDisclosureRoute("/people");
      cache?.invalidate();
      refuseDisclosure(notice);
    },
    [cache, refuseDisclosure],
  );
  const onRecorded = (receipt: OperationReceipt) => {
    setNavigationLock(false);
    replaceDisclosureRoute("/people");
    setRecorded({ credential, id: receipt.operationId });
    cache?.invalidate();
    invalidate(true);
  };
  const model = useOperation({
    subject: "AUTHORITY",
    kind: initialAuthorityAct,
    current: null,
    credential,
    onLockChange,
    onRecorded,
    onDisclosureRefused: refused,
  });
  useAuthorityActions(model, actions, ready);
  return {
    model,
    disclosure: {
      ...disclosure,
      refused,
      invalidate: (force = false) => {
        cache?.invalidate();
        invalidate(force);
      },
    },
    recorded,
  };
};
const AuthorityEditorPanel = (props: AuthorityPanelProps) => {
  const state = useAuthorityOperation(props);
  const target = useAuthorityTarget(
    state.model,
    state.disclosure.revision,
    (props.disclosureAvailable ?? true) && state.disclosure.active,
  );
  const route = useContentRoute();
  const actorSelection = route.screen === "people" ? route.actorId : "";
  const selection = `${actorSelection}:${state.disclosure.revision}:${props.disclosureAvailable ?? true}`;
  const [editor, setEditor] = useState({ selection, active: false });
  if (editor.selection !== selection) {
    setEditor({ selection, active: false });
  }
  const editorOpen = editor.selection === selection && editor.active;
  const closeEditor = useCallback(() => {
    setEditor((prior) => ({ ...prior, active: false }));
  }, []);
  const navigation = useOperationNavigation(target.model, closeEditor, "/people");
  return (
    <AuthorityLayout
      {...state}
      model={target.model}
      target={target}
      editor={{
        active: editorOpen,
        open: () => {
          setEditor({ selection, active: true });
        },
        close: navigation.close,
        admit: (issuer) => {
          state.model.discard(false, "ADMIT", { "target.issuer": issuer });
        },
      }}
      onClose={navigation.close}
      disclosureAvailable={props.disclosureAvailable ?? true}
    />
  );
};

export const AuthorityPanel = (props: AuthorityPanelProps) => <AuthorityEditorPanel {...props} />;
