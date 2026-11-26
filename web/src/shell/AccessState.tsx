import { ProductBrand } from "./ProductBrand";
import { useDocumentTitle } from "../hooks/useDocumentTitle";
import { Page } from "../ui/Page";
import { renderCatalogKey } from "../presentation/messages";
import type { Notice } from "../protocol/notices";
import { NoticeView } from "../presentation/Message";
import { guardedDeparture } from "../routing/navigation";
import { useEffect, useRef } from "react";
import type { RequesterDocument } from "../protocol/types";
import { ScreenView } from "../ui/ScreenView";
import { usePresentation } from "../presentation/context";
import { useSessionContext } from "../session/SessionContext";
import { Button } from "../ui/Button";
import { BrowserSessionCommands } from "../features/settings/BrowserSessions";
import {} from "./access-state.css";
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
        {feedback.loading ? <p role="status">{p.text("ui.loading")}</p> : null}
        <Button isDisabled={locked || feedback.loading} onPress={onCheck} variant="secondary">
          {p.text("ui.accessState.check")}
        </Button>
        <BrowserSessionCommands actions={requester.actions} disabled={locked || feedback.loading} />
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
