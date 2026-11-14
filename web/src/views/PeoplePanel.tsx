import type { OwnerScope } from "../protocol/ownerScope";
import type { PersonDocument } from "../protocol/types";
import type { MutationLockChange } from "../hooks/useWorkspaceMutationLock";
import { taskDefinitions, type Principal } from "@contracts/client";
import { isActiveRequester, useRequesterContext } from "../hooks/useRequesterContext";
import { ScreenView } from "../ui/ScreenView";
import { useOperationObservation } from "../hooks/useOperationObservation";
import { usePeopleTarget, type PeopleTarget } from "../hooks/operation/usePeopleTarget";
import { AccessRequests, type AccessRequestControl } from "./people/AccessRequests";
import { ActorOperation, ActorActions } from "./people/ActorOperation";
import { useOperationNavigation } from "../hooks/useOperationNavigation";
import { useEndpointCache } from "../hooks/EndpointProvider";
import { useContentRoute } from "../hooks/SettingsDetourContext";
import { navigate, replaceDisclosureRoute } from "../routing/navigation";
import { useCredential, useSessionContext } from "../session/SessionContext";
import {
  useCallback,
  useEffect,
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
  type ReactNode,
} from "react";
import { isDisclosureRefused } from "../api/outcomes";
import { resultNotice, type ServiceResponse, type OperationReceipt } from "../api/service";
import { PeopleGrants, type RevocationControl } from "./PeopleGrants";
import { PeopleListing } from "./PeopleListing";
import type { Notice } from "../protocol/notices";
import { NoticeView } from "../presentation/Message";
import { usePresentation } from "../presentation/context";
import { useEndpoint } from "../hooks/useEndpoint";
import { usePeopleDisclosure } from "../hooks/operation/usePeopleDisclosure";
import { useOperation, type OperationModel } from "../hooks/operation/useOperation";
import { OperationPanel, OperationDiscard } from "./operation/OperationPanel";
const contextData = (response: ServiceResponse<"context">) =>
  response.outcome.kind === "SUCCEEDED" && isActiveRequester(response.outcome.document)
    ? response.outcome.document
    : null;
type Disclosure = ReturnType<typeof usePeopleDisclosure>;
const usePeopleContext = (disclosure: Disclosure) => {
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
  onRevoke: ((grant: RevocationControl) => void) | undefined;
  open: boolean;
  actions: ReactNode;
  actorId: string | null;
  refused: (notice?: Notice) => void;
  onActor: (actor: PersonDocument) => void;
}) =>
  actorId === null ? null : (
    <PeopleGrants
      key={actorId}
      actorId={actorId}
      open={open}
      actions={actions}
      onRevoke={onRevoke}
      onBack={() => {
        const target =
          document.getElementById(`actor-grants-${actorId}`) ??
          document.getElementById("people-title");
        navigate("/people", false, target?.id);
      }}
      onDisclosureRefused={refused}
      onActor={onActor}
    />
  );
const PeopleDirectory = ({
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
  onRevoke: ((grant: RevocationControl) => void) | undefined;
  open: boolean;
  actions: ReactNode;
  selectedActorId: string | null;
  disclosure: Disclosure;
  available: boolean;
  onSelect: (id: string) => void;
  mayReadGrants: boolean;
  onActor: (actor: PersonDocument) => void;
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
      <PeopleListing
        titleId="people-title"
        mayReadGrants={mayReadGrants}
        onSelect={onSelect}
        onDisclosureRefused={disclosure.refused}
        onDisclosureRestored={disclosure.restored}
      />
    </>
  );
};
const usePeopleWorkspace = (disclosure: Disclosure, target: PeopleTarget) => {
  const route = useContentRoute();
  const selectedActorId = route.screen === "people" && route.actorId !== "" ? route.actorId : null;
  const setSelectedActorId = (actorId: string | null): void => {
    navigate(actorId === null ? "/people" : `/people/${encodeURIComponent(actorId)}`);
  };
  const context = usePeopleContext(disclosure);
  const offered = useMemo(
    () =>
      taskDefinitions.flatMap((task) =>
        task.kind === "OPERATION" &&
        task.subject === "PEOPLE" &&
        context.value?.context.tasks.includes(task.id) === true
          ? [task.act]
          : [],
      ),
    [context.value],
  );
  const { observe } = target;
  const onActor = useCallback(
    (actor: PersonDocument) => {
      observe(actor);
    },
    [observe],
  );
  return { selectedActorId, setSelectedActorId, context, offered, onActor };
};
const PendingPeopleRequests = ({
  model,
  context,
  selected,
  available,
  editor,
  refused,
}: {
  model: OperationModel;
  context: ReturnType<typeof contextData>;
  selected: boolean;
  available: boolean;
  editor: PeopleEditor;
  refused: Disclosure["refused"];
}) => (
  <AccessRequests
    available={
      !selected && available && context?.context.tasks.includes("people.requests") === true
    }
    busy={
      model.busy ||
      (model.phase !== "ACCEPTED" &&
        (model.dirty || model.prepared !== null || model.priorUncertain))
    }
    mayAdmit={context?.context.tasks.includes("people.admit") === true}
    onDisclosureRefused={refused}
    onAdmit={(request) => {
      editor.admit(request.principal.issuer, request);
      editor.open();
    }}
  />
);
const PeopleWorkspace = ({
  model,
  disclosure,
  disclosureAvailable,
  target,
  editor,
}: {
  editor: PeopleEditor;
  model: OperationModel;
  disclosure: Disclosure;
  disclosureAvailable: boolean;
  target: PeopleTarget;
}) => {
  const p = usePresentation();
  const { selectedActorId, setSelectedActorId, context, offered, onActor } = usePeopleWorkspace(
    disclosure,
    target,
  );
  return (
    <>
      <ContextStatus loading={context.loading} notice={context.message} reload={context.reload} />
      {selectedActorId === null &&
      context.value?.context.tasks.includes("people.admit") === true ? (
        <button
          type="button"
          disabled={model.busy}
          onClick={() => {
            if (
              model.phase === "ACCEPTED" ||
              (!model.dirty && model.prepared === null && !model.priorUncertain)
            ) {
              editor.admit(context.value!.context.peopleIssuerSeed ?? "");
            }
            editor.open();
          }}
        >
          {p.peopleLabel("people.admit")}
        </button>
      ) : null}
      <PendingPeopleRequests
        model={model}
        context={context.value}
        selected={selectedActorId !== null}
        available={disclosureAvailable}
        editor={editor}
        refused={disclosure.refused}
      />
      <div className="people-workspace">
        <div>
          <PeopleDirectory
            selectedActorId={selectedActorId}
            open={!editor.active && model.pendingDiscard === null}
            actions={<ActorActions target={target} busy={model.busy} onOpen={editor.open} />}
            onRevoke={
              target.offered.includes("people.revoke")
                ? (grant) => {
                    target.begin(
                      "people.revoke",
                      {},
                      {
                        role: grant.role,
                        scope:
                          grant.scope.kind === "INSTALLATION"
                            ? { kind: "INSTALLATION" }
                            : { kind: "CASE", caseId: grant.scope.caseId },
                      },
                    );
                    editor.open();
                  }
                : undefined
            }
            disclosure={disclosure}
            available={disclosureAvailable}
            onSelect={setSelectedActorId}
            mayReadGrants={context.value?.context.views.includes("PEOPLE") === true}
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
const PeopleReceipt = ({ operationId }: { operationId: string }) => {
  const p = usePresentation();
  const observation = useOperationObservation(operationId);
  const heading = useRef<HTMLHeadingElement>(null);
  useEffect(() => {
    if (observation.receipt !== null) {
      heading.current?.focus();
    }
  }, [observation.receipt]);
  return (
    <section className="receipt">
      <h2 ref={heading} tabIndex={-1}>
        {p.text("ui.acceptedOperation")}
      </h2>
      {observation.receipt === null ? null : (
        <ScreenView
          view={observation.receipt.screen}
          timeZone={observation.receipt.basis.observation.timeZone}
        />
      )}
      {observation.loading ? <p role="status">{p.text("ui.loading")}</p> : null}
      {observation.message === null ? null : (
        <p role="alert">
          <NoticeView value={observation.message} />
        </p>
      )}
    </section>
  );
};
type PeopleOwner = OwnerScope & { credential: string };
type RecordedPeople = (PeopleOwner & { operationId: string }) | null;
type PeopleActions = {
  invalidate: () => void;
  restore: () => void;
  reset: () => void;
  operationId: string | null;
} | null;
type PeopleEditor = {
  active: boolean;
  open: () => void;
  close: () => void;
  admit: (issuer: string, request?: AccessRequestControl) => void;
};
type PeopleLayoutProps = {
  editor: PeopleEditor;
  model: OperationModel;
  target: PeopleTarget;
  disclosure: Disclosure;
  recorded: RecordedPeople;
  disclosureAvailable: boolean;
  onClose: () => void;
};
const PeopleTitle = () => {
  const p = usePresentation();
  return <h1 id="people-title">{p.text("ui.shell.people")}</h1>;
};
const PeopleUnavailable = ({
  model,
  invalidate,
  available,
  editor,
}: {
  editor: PeopleEditor;
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
          {p.text("ui.continuePeopleRequest")}
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
const PeopleLayout = ({
  model,
  disclosure,
  recorded,
  onClose,
  disclosureAvailable,
  target,
  editor,
}: PeopleLayoutProps) => {
  const credential = useCredential();
  const { state: session } = useSessionContext();
  const requester = useRequesterContext();
  const visibleReceipt =
    recorded !== null &&
    requester !== null &&
    disclosureAvailable &&
    recorded.serviceOrigin === location.origin &&
    recorded.credential === credential &&
    recorded.sessionEpoch === session.epoch &&
    recorded.installationId === requester.context.installationId &&
    recorded.actorId === requester.context.actorId;
  const { invalidate } = disclosure;
  return (
    <section aria-labelledby="people-title" className="people-panel">
      <PeopleTitle />
      {visibleReceipt ? (
        <PeopleReceipt key={recorded.operationId} operationId={recorded.operationId} />
      ) : null}
      {disclosure.notice === null ? null : (
        <p role="alert">
          <NoticeView value={disclosure.notice} />
        </p>
      )}
      <OperationDiscard model={model} onClose={onClose} />
      {disclosure.active ? (
        <PeopleWorkspace
          key={`${credential}:${disclosure.revision}`}
          model={model}
          disclosure={disclosure}
          disclosureAvailable={disclosureAvailable}
          target={target}
          editor={editor}
        />
      ) : (
        <PeopleUnavailable
          model={model}
          invalidate={invalidate}
          available={disclosureAvailable}
          editor={editor}
        />
      )}
    </section>
  );
};
const initialPeopleAct = "people.admit";
type PeoplePanelProps = {
  onLockChange: MutationLockChange;
  disclosureAvailable?: boolean;
};
const usePeopleActions = (
  model: OperationModel,
  actionsRef: React.RefObject<PeopleActions>,
  ready: boolean,
) => {
  useLayoutEffect(() => {
    actionsRef.current = {
      invalidate: model.invalidateDisclosure,
      restore: model.restoreDisclosure,
      reset: model.discard,
      operationId: model.prepared?.input.operationId ?? null,
    };
  });
  useEffect(() => {
    if (ready) {
      actionsRef.current?.restore();
    }
  }, [ready, actionsRef]);
};
const usePeopleOperation = ({ onLockChange }: PeoplePanelProps) => {
  const credential = useCredential();
  const disclosure = usePeopleDisclosure();
  const requester = useRequesterContext();
  const { state: session } = useSessionContext();
  const [owner, setOwner] = useState<PeopleOwner | null>(null);
  if (owner === null && requester !== null) {
    setOwner({
      serviceOrigin: location.origin,
      installationId: requester.context.installationId,
      actorId: requester.context.actorId,
      sessionEpoch: session.epoch,
      credential,
    });
  }
  const cache = useEndpointCache();
  const { refused: refuseDisclosure, invalidate, ready } = disclosure;
  const [recorded, setRecorded] = useState<RecordedPeople>(null);
  const actions = useRef<PeopleActions>(null);
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
    replaceDisclosureRoute("/people");
    const operationId = actions.current?.operationId;
    if (
      receipt.context.target.kind === "PEOPLE" &&
      operationId !== null &&
      operationId !== undefined &&
      owner !== null
    ) {
      setRecorded({ ...owner, operationId });
    }
    cache?.invalidate();
    invalidate(true);
  };
  const model = useOperation({
    subject: "PEOPLE",
    kind: initialPeopleAct,
    current: null,
    credential,
    onLockChange,
    onRecorded,
    onDisclosureRefused: refused,
  });
  usePeopleActions(model, actions, ready);
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
const PeopleEditorPanel = (props: PeoplePanelProps) => {
  const state = usePeopleOperation(props);
  const [admissionPrincipal, setAdmissionPrincipal] = useState<Principal | null>(null);
  const target = usePeopleTarget(
    state.model,
    state.disclosure.revision,
    (props.disclosureAvailable ?? true) && state.disclosure.active,
    admissionPrincipal,
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
    <PeopleLayout
      {...state}
      model={target.model}
      target={target}
      editor={{
        active: editorOpen,
        open: () => {
          setEditor({ selection, active: true });
        },
        close: navigation.close,
        admit: (issuer, request) => {
          setAdmissionPrincipal(request?.principal ?? null);
          state.model.discard(
            false,
            "people.admit",
            {
              "input:principal.issuer": issuer,
              ...(request === undefined
                ? {}
                : {
                    "input:principal.kind": request.principal.kind,
                    "input:principal.issuer": request.principal.issuer,
                    "input:principal.subject": request.principal.subject,
                    "input:profile.displayName": request.profileSeed.displayName,
                    ...(typeof request.profileSeed.contact === "string"
                      ? { "input:profile.contact": request.profileSeed.contact }
                      : {}),
                  }),
            },
            {
              kind: "PEOPLE",
            },
          );
        },
      }}
      onClose={navigation.close}
      disclosureAvailable={props.disclosureAvailable ?? true}
    />
  );
};

export const PeoplePanel = (props: PeoplePanelProps) => {
  const { state } = useSessionContext();
  return state.kind === "authenticated" ? (
    <PeopleEditorPanel key={`${state.epoch}:${state.token}`} {...props} />
  ) : null;
};
