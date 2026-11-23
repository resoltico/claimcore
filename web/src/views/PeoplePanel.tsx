import { Page } from "../ui/Page";
import { PhoneAction } from "../ui/PhoneAction";
import { Icon } from "../ui/icons";
import { ActionMenu } from "../ui/ActionMenu";
import { principalCreationTask } from "../operation/metadata";
import { renderCatalogKey } from "../presentation/messages";
import { isOperationAffordance, initialAffordanceValues } from "../operation/affordances";
import {
  apiEndpoints,
  resultNotice,
  type ServiceResponse,
  type OperationReceipt,
} from "../api/service";
import type { PersonDocument } from "../protocol/types";
import type { MutationLockChange } from "../hooks/useWorkspaceMutationLock";

import { clientCompositions, taskDefinitions, type Principal } from "@contracts/client";
import { isActiveRequester } from "../hooks/useRequesterContext";
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

import { PeopleGrants, type RevocationControl } from "./PeopleGrants";
import { PeopleListing } from "./PeopleListing";
import type { Notice } from "../protocol/notices";
import { NoticeView } from "../presentation/Message";
import { usePresentation } from "../presentation/context";
import { useEndpoint } from "../hooks/useEndpoint";
import { usePeopleDisclosure } from "../hooks/operation/usePeopleDisclosure";
import { useOperation, type OperationModel } from "../hooks/operation/useOperation";
import { OperationPanel, OperationDiscard } from "./operation/OperationPanel";
const contextData = (response: ServiceResponse<typeof apiEndpoints.context>) =>
  response.outcome.kind === "SUCCEEDED" && isActiveRequester(response.outcome.document)
    ? response.outcome.document
    : null;
type Disclosure = ReturnType<typeof usePeopleDisclosure>;
const usePeopleContext = (disclosure: Disclosure) => {
  const query = useEndpoint(apiEndpoints.context, {});
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
        restored(apiEndpoints.context);
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
        context.value?.actions.some(
          (action) => action.task === task.id && action.blocked === null,
        ) === true
          ? [task.id]
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
      !selected &&
      available &&
      context?.links.some((named) => named.link.endpoint === apiEndpoints.peopleRequests) === true
    }
    busy={
      model.busy ||
      (model.phase !== "ACCEPTED" &&
        (model.dirty || model.prepared !== null || model.priorUncertain))
    }
    mayAdmit={
      context?.actions.some(
        (action) =>
          isOperationAffordance(action) &&
          action.task === clientCompositions.admission &&
          action.blocked === null,
      ) === true
    }
    onDisclosureRefused={refused}
    onAdmit={(request) => {
      editor.admit(request);
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
  before,
}: {
  editor: PeopleEditor;
  before: ReactNode;
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
  const additions =
    selectedActorId === null
      ? (context.value?.actions.filter(
          (action): action is AccessRequestControl =>
            isOperationAffordance(action) &&
            (action.task === clientCompositions.accountRegistration ||
              action.task === clientCompositions.admission) &&
            action.blocked === null,
        ) ?? [])
      : [];
  const blocked =
    model.busy ||
    (model.phase !== "ACCEPTED" &&
      (model.dirty || model.prepared !== null || model.priorUncertain));
  const items = additions.map((action) => ({
    id: action.task,
    label: renderCatalogKey(p, action.labelId),
    disabled: blocked,
    select: () => {
      editor.admit(action);
      editor.open();
    },
  }));
  const addPerson = p.text("label.addPerson");
  return (
    <Page
      id="people-title"
      as="section"
      labelledBy="people-title"
      className="people-panel"
      title={p.text("view.PEOPLE.label")}
      primaryAction={
        items.length === 0 ? null : (
          <PhoneAction
            renderCompact={() => (
              <ActionMenu
                label={addPerson}
                variant="primary"
                disabled={blocked}
                items={items}
                trigger={<Icon name="add" />}
              />
            )}
          >
            <ActionMenu
              label={addPerson}
              variant="primary"
              compact={false}
              disabled={blocked}
              items={items}
            />
          </PhoneAction>
        )
      }
    >
      {before}
      <ContextStatus loading={context.loading} notice={context.message} reload={context.reload} />
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
            actions={
              <ActorActions
                target={target}
                busy={model.busy}
                onOpen={editor.open}
                onDisclosureRefused={disclosure.refused}
              />
            }
            onRevoke={(action) => {
              target.begin(action);
              editor.open();
            }}
            disclosure={disclosure}
            available={disclosureAvailable}
            onSelect={setSelectedActorId}
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
    </Page>
  );
};
type PeopleActions = {
  invalidate: () => void;
  restore: () => void;
  reset: () => void;
} | null;
type PeopleEditor = {
  active: boolean;
  open: () => void;
  close: () => void;
  admit: (action: AccessRequestControl) => void;
};
type PeopleLayoutProps = {
  editor: PeopleEditor;
  model: OperationModel;
  target: PeopleTarget;
  disclosure: Disclosure;
  disclosureAvailable: boolean;
  onClose: () => void;
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
        {p.text("ui.tryAgain")}
      </button>
    </>
  );
};
const PeopleLayout = ({
  model,
  disclosure,
  onClose,
  disclosureAvailable,
  target,
  editor,
}: PeopleLayoutProps) => {
  const p = usePresentation();
  const credential = useCredential();
  const { invalidate } = disclosure;
  const before = (
    <>
      {disclosure.notice === null ? null : (
        <p role="alert">
          <NoticeView value={disclosure.notice} />
        </p>
      )}
      <OperationDiscard model={model} onClose={onClose} />
    </>
  );
  return disclosure.active ? (
    <PeopleWorkspace
      key={`${credential}:${disclosure.revision}`}
      model={model}
      disclosure={disclosure}
      disclosureAvailable={disclosureAvailable}
      target={target}
      editor={editor}
      before={before}
    />
  ) : (
    <Page
      id="people-title"
      as="section"
      labelledBy="people-title"
      className="people-panel"
      title={p.text("view.PEOPLE.label")}
    >
      {before}
      <PeopleUnavailable
        model={model}
        invalidate={invalidate}
        available={disclosureAvailable}
        editor={editor}
      />
    </Page>
  );
};

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
  const cache = useEndpointCache();
  const { refused: refuseDisclosure, invalidate, ready } = disclosure;
  const actions = useRef<PeopleActions>(null);
  const refused = useCallback(
    (notice?: Notice) => {
      actions.current?.invalidate();
      replaceDisclosureRoute("/people");
      cache?.invalidate();
      refuseDisclosure(notice);
    },
    [cache, refuseDisclosure],
  );
  const onRecorded = (_receipt: OperationReceipt) => {
    replaceDisclosureRoute("/people");
    cache?.invalidate();
    invalidate(true);
  };
  const model = useOperation({
    subject: "PEOPLE",
    kind: principalCreationTask().id,
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
  // Keep authoring through requester refresh; current disclosure separately masks the panel.
  const selection = `${actorSelection}:${state.disclosure.revision}`;
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
        admit: (action) => {
          const kind = action.seeds["input:principal.kind"];
          const issuer = action.seeds["input:principal.issuer"];
          const subject = action.seeds["input:principal.subject"];
          const fixed =
            !action.editable.some((input) => input === "input:principal.subject") &&
            (kind === "HUMAN" || kind === "SERVICE") &&
            typeof issuer === "string" &&
            typeof subject === "string";
          setAdmissionPrincipal(fixed ? { kind, issuer, subject } : null);
          state.model.discard(
            false,
            action.task,
            initialAffordanceValues(action),
            action.binding,
            action,
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
