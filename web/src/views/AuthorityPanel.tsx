import { useOperationNavigation } from "../hooks/useOperationNavigation";
import { useEndpointCache } from "../hooks/EndpointProvider";
import { useRoute } from "../routing/useRoute";
import { navigate, setNavigationLock, replaceDisclosureRoute } from "../routing/navigation";
import { useCredential } from "../session/SessionContext";
import { useUnresolvedOperation } from "./operation/unresolvedOperation";
import { useCallback, useEffect, useLayoutEffect, useRef, useState } from "react";
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
  response.outcome.kind === "SUCCEEDED" ? response.outcome.data : null;
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
}: {
  actorId: string | null;
  refused: (notice?: Notice) => void;
}) =>
  actorId === null ? null : (
    <AuthorityGrants
      key={actorId}
      actorId={actorId}
      onBack={() => {
        const target =
          document.getElementById(`actor-grants-${actorId}`) ??
          document.getElementById("authority-actors-title");
        navigate("/authority", false, target?.id);
      }}
      onDisclosureRefused={refused}
    />
  );
const AuthorityDirectory = ({
  selectedActorId,
  disclosure,
  available,
  onSelect,
  mayReadGrants,
}: {
  selectedActorId: string | null;
  disclosure: Disclosure;
  available: boolean;
  onSelect: (id: string) => void;
  mayReadGrants: boolean;
}) => {
  if (!available) {
    return null;
  }
  return (
    <>
      <SelectedActorGrants actorId={selectedActorId} refused={disclosure.refused} />
      <AuthorityListing
        mayReadGrants={mayReadGrants}
        onSelect={onSelect}
        onDisclosureRefused={disclosure.refused}
        onDisclosureRestored={disclosure.restored}
      />
    </>
  );
};
const AuthorityWorkspace = ({
  model,
  disclosure,
  disclosureAvailable,
}: {
  model: OperationModel;
  disclosure: Disclosure;
  disclosureAvailable: boolean;
}) => {
  const route = useRoute();
  const selectedActorId =
    route.screen === "authority" && route.actorId !== "" ? route.actorId : null;
  const setSelectedActorId = (actorId: string | null): void => {
    navigate(actorId === null ? "/authority" : `/authority/${encodeURIComponent(actorId)}`);
  };
  const context = useAuthorityContext(disclosure);
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
      <OperationPanel
        model={model}
        current={null}
        offered={offered}
        disclosureAvailable={disclosureAvailable}
      />
      <ContextStatus loading={context.loading} notice={context.message} reload={context.reload} />
      <AuthorityDirectory
        selectedActorId={selectedActorId}
        disclosure={disclosure}
        available={disclosureAvailable}
        onSelect={setSelectedActorId}
        mayReadGrants={context.value?.views.includes("AUTHORITY") === true}
      />
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
type AuthorityLayoutProps = {
  model: OperationModel;
  disclosure: Disclosure;
  recorded: RecordedAuthority;
  onClose: () => void;
  disclosureAvailable: boolean;
};
const AuthorityTitle = ({ selected, available }: { selected: boolean; available: boolean }) => {
  const p = usePresentation();
  return selected && available ? (
    <p id="authority-title" className="eyebrow">
      {p.text("subject.AUTHORITY.label")}
    </p>
  ) : (
    <h1 id="authority-title">{p.text(selected ? "ui.actorGrants" : "subject.AUTHORITY.label")}</h1>
  );
};
const AuthorityUnavailable = ({
  model,
  invalidate,
  available,
}: {
  model: OperationModel;
  invalidate: Disclosure["invalidate"];
  available: boolean;
}) => {
  const p = usePresentation();
  return (
    <>
      <OperationPanel model={model} current={null} offered={[]} disclosureAvailable={available} />
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
}: AuthorityLayoutProps) => {
  const route = useRoute();
  const selected = route.screen === "authority" && route.actorId !== "";
  const credential = useCredential();
  const { invalidate } = disclosure;
  return (
    <section aria-labelledby="authority-title" className="authority-panel">
      <AuthorityTitle selected={selected} available={disclosureAvailable} />
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
        />
      ) : (
        <AuthorityUnavailable
          model={model}
          invalidate={invalidate}
          available={disclosureAvailable}
        />
      )}
    </section>
  );
};
const initialAuthorityAct = productMetadata.catalog.entries.find(
  (entry) => entry.subject === "AUTHORITY" && entry.endpoint === "operation.execute",
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
      replaceDisclosureRoute("/authority");
      cache?.invalidate();
      refuseDisclosure(notice);
    },
    [cache, refuseDisclosure],
  );
  const onRecorded = (receipt: OperationReceipt) => {
    setNavigationLock(false);
    replaceDisclosureRoute("/authority");
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
  useUnresolvedOperation(model);
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
export const AuthorityPanel = (props: AuthorityPanelProps) => {
  const state = useAuthorityOperation(props);
  const navigation = useOperationNavigation(state.model, undefined, "/authority");
  return (
    <AuthorityLayout
      {...state}
      disclosureAvailable={props.disclosureAvailable ?? true}
      onClose={navigation.close}
    />
  );
};
