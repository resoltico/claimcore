import { ProductBrand } from "./ProductBrand";
import { useDocumentTitle } from "../hooks/useDocumentTitle";
import { Page } from "../ui/Page";
import { renderCatalogKey } from "../presentation/messages";
import type { Notice } from "../protocol/notices";
import { NoticeView } from "../presentation/Message";
import { guardedDeparture } from "../routing/navigation";
import { useEffect, useRef, useState } from "react";
import type { RequesterDocument } from "../protocol/types";
import { ScreenView } from "../ui/ScreenView";
import { usePresentation } from "../presentation/context";
import { useSessionContext } from "../session/SessionContext";
import { apiEndpoints, requestEndpoint, resultNotice } from "../api/service";
import { Button } from "../ui/Button";
import { BrowserSessionCommands } from "../features/settings/BrowserSessions";
import {} from "./access-state.css";
const RequestAccessButton = ({
  credential,
  disabled,
  labelId,
  onCheck,
}: {
  credential: string;
  disabled: boolean;
  labelId: string;
  onCheck: () => void;
}) => {
  const p = usePresentation();
  const [submitting, setSubmitting] = useState(false);
  const [requestFailure, setRequestFailure] = useState<Notice | null>(null);
  const submission = useRef<AbortController | null>(null);
  useEffect(() => () => submission.current?.abort(), []);
  const submit = async () => {
    if (credential === "" || submission.current !== null) {
      return;
    }
    const controller = new AbortController();
    submission.current = controller;
    setSubmitting(true);
    setRequestFailure(null);
    const result = await requestEndpoint(
      apiEndpoints.peopleRequestsSubmit,
      credential,
      {},
      controller.signal,
    );
    submission.current = null;
    if (controller.signal.aborted) {
      return;
    }
    setSubmitting(false);
    if (result.kind === "outcome" && result.value.outcome.kind === "SUCCEEDED") {
      onCheck();
    } else {
      setRequestFailure(resultNotice(result));
    }
  };
  return (
    <>
      {requestFailure === null ? null : (
        <p role="alert">
          <NoticeView value={requestFailure} />
        </p>
      )}
      {submitting ? <p role="status">{p.text("ui.loading")}</p> : null}
      <Button isDisabled={disabled || submitting} onPress={() => void submit()}>
        {renderCatalogKey(p, labelId)}
      </Button>
    </>
  );
};
const RequestAccess = ({
  requester,
  disabled,
  onCheck,
}: {
  requester: RequesterDocument;
  disabled: boolean;
  onCheck: () => void;
}) => {
  const { state } = useSessionContext();
  const action = requester.actions.find(
    (candidate) => candidate.task === apiEndpoints.peopleRequestsSubmit,
  );
  if (action === undefined || state.kind !== "authenticated") {
    return null;
  }
  return (
    <RequestAccessButton
      key={state.epoch}
      credential={state.token}
      disabled={disabled || action.blocked !== null}
      labelId={action.labelId}
      onCheck={onCheck}
    />
  );
};
export const AccessState = ({
  requester,
  locked,
  onCheck,
  feedback,
}: {
  requester: RequesterDocument;
  locked: boolean;
  onCheck: () => void;
  feedback: { message: Notice | null; loading: boolean };
}) => {
  const p = usePresentation();
  const heading = useRef<HTMLHeadingElement>(null);
  useEffect(() => {
    heading.current?.focus({ preventScroll: false });
  }, []);
  const { logout } = useSessionContext();
  const identity = requester.screen.regions.find((region) => region.role === "IDENTITY");
  const titleId = identity?.titleId ?? "label.account";
  useDocumentTitle(requester.subject.state === "ACTIVE" ? null : renderCatalogKey(p, titleId));
  if (requester.subject.state === "ACTIVE") {
    return null;
  }
  const view = {
    ...requester.screen,
    regions: requester.screen.regions.map((region) => {
      if (region !== identity) {
        return region;
      }
      const { titleId: _title, ...content } = region;
      return content;
    }),
  };
  return (
    <main className="access-state">
      <span className="product-name">
        <ProductBrand />
      </span>
      <Page id="access-state-title" title={renderCatalogKey(p, titleId)} headingRef={heading}>
        <ScreenView
          view={view}
          timeZone={
            requester.basis.kind === "SNAPSHOT" ? requester.basis.observation.timeZone : "UTC"
          }
        />
        {feedback.message === null ? null : (
          <p role="alert">
            <NoticeView value={feedback.message} />
          </p>
        )}
        <RequestAccess
          requester={requester}
          disabled={locked || feedback.loading}
          onCheck={onCheck}
        />
        {feedback.loading ? <p role="status">{p.text("ui.loading")}</p> : null}
        <Button isDisabled={locked || feedback.loading} onPress={onCheck} variant="secondary">
          {p.text("ui.accessState.check")}
        </Button>
        <BrowserSessionCommands
          actions={requester.actions}
          scope={requester.subject.terminationScope}
          disabled={locked || feedback.loading}
        />
        <Button
          isDisabled={locked}
          onPress={() => {
            guardedDeparture(() => {
              void logout();
            });
          }}
          variant="secondary"
        >
          {p.text("ui.signOut")}
        </Button>
      </Page>
    </main>
  );
};
