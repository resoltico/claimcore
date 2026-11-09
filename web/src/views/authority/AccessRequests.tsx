import { PageTables, pageItemIndex } from "../../ui/PageTables";
import { PageRow } from "../../ui/PageRow";
import { useCallback, useEffect, useRef, useState } from "react";
import type { HumanReadDocument_authorityRequests } from "@contracts/client";
export type AccessRequestControl =
  HumanReadDocument_authorityRequests["context"]["requests"][number];
import { requestEndpoint, resultNotice, type ServiceResponse } from "../../api/service";
import { isDisclosureRefused } from "../../api/outcomes";
import { useEndpointPage } from "../../hooks/useEndpointPage";
import { useCredential, useSessionContext } from "../../session/SessionContext";
import { usePresentation } from "../../presentation/context";
import { NoticeView } from "../../presentation/Message";
import type { Notice } from "../../protocol/notices";
import { Button } from "../../ui/Button";
import { Dialog } from "../../ui/Dialog";
import "./access-requests.css";

type Props = {
  available: boolean;
  busy: boolean;
  mayAdmit?: boolean;
  onAdmit: (request: AccessRequestControl) => void;
  onDisclosureRefused: (notice?: Notice) => void;
};
const input = (cursor: string | null) => ({ limit: 50, ...(cursor === null ? {} : { cursor }) });
const select = (reply: ServiceResponse<"authority.requests">) => {
  if (reply.outcome.kind !== "SUCCEEDED") {
    return null;
  }
  const doc = reply.outcome.document;
  if (doc.basis.kind !== "SNAPSHOT") {
    return null;
  }
  return {
    items: doc.context.requests,
    nextCursor: doc.context.nextCursor,
    basis: doc.basis.observation,
    screen: doc.screen,
  };
};
type RequestPage = NonNullable<ReturnType<typeof select>>;
const RequestFeedback = ({
  loading,
  dismissed,
  message,
  notice,
  empty,
}: {
  loading: boolean;
  dismissed: boolean;
  message: Notice | null;
  notice: Notice | null;
  empty: boolean;
}) => {
  const p = usePresentation();
  return (
    <>
      {loading ? <p role="status">{p.text("ui.loading")}</p> : null}
      {dismissed ? <p role="status">{p.text("endpoint.authority.requests.dismiss.done")}</p> : null}
      {message === null ? null : (
        <p role="alert">
          <NoticeView value={message} />
        </p>
      )}
      {notice === null ? null : (
        <p role="alert">
          <NoticeView value={notice} />
        </p>
      )}
      {!loading && message === null && empty ? <p>{p.text("ui.requests.empty")}</p> : null}
    </>
  );
};
const AvailableRequests = ({
  busy,
  mayAdmit = true,
  onAdmit,
  onDisclosureRefused,
}: Omit<Props, "available">) => {
  const p = usePresentation();
  const credential = useCredential();
  const [selected, setSelected] = useState<AccessRequestControl | null>(null);
  const [notice, setNotice] = useState<Notice | null>(null);
  const [dismissed, setDismissed] = useState(false);
  const [pending, setPending] = useState(false);
  const [refused, setRefused] = useState(false);
  const controller = useRef<AbortController | null>(null);
  const refuseDisclosure = useCallback(
    (message?: Notice) => {
      controller.current?.abort();
      controller.current = null;
      setPending(false);
      setSelected(null);
      setRefused(true);
      onDisclosureRefused(message);
    },
    [onDisclosureRefused],
  );
  const page = useEndpointPage<"authority.requests", AccessRequestControl, RequestPage>(
    "authority.requests",
    input,
    select,
    refuseDisclosure,
  );
  useEffect(() => () => controller.current?.abort(), []);
  const dismiss = async () => {
    if (busy || selected === null || controller.current !== null) {
      return;
    }
    const current = new AbortController();
    controller.current = current;
    setPending(true);
    setNotice(null);
    const result = await requestEndpoint(
      "authority.requests.dismiss",
      credential,
      selected.principal,
      current.signal,
    );
    if (current.signal.aborted) {
      return;
    }
    controller.current = null;
    setPending(false);
    if (isDisclosureRefused(result)) {
      refuseDisclosure(resultNotice(result));
    } else if (result.kind === "outcome" && result.value.outcome.kind === "SUCCEEDED") {
      setSelected(null);
      setDismissed(true);
      await page.load(null);
    } else {
      setNotice(resultNotice(result));
      // A delivery failure may have removed the request. Refresh is explicit; never repeat the write automatically.
      setSelected(null);
    }
  };
  if (refused) {
    return null;
  }
  const disabled = busy || pending;
  return (
    <section className="access-requests" aria-labelledby="access-requests-title">
      <h2 id="access-requests-title">{p.text("endpoint.authority.requests.label")}</h2>
      <RequestFeedback
        loading={page.loading}
        dismissed={dismissed}
        message={page.message}
        notice={notice}
        empty={page.items.length === 0}
      />
      <PageTables
        views={page.views}
        renderActions={(key) => {
          const request = page.items.find((request) => request.key === key);
          return request === undefined ? null : (
            <div className="access-request-actions">
              <Button
                isDisabled={disabled || !mayAdmit}
                onPress={() => {
                  onAdmit(request);
                }}
              >
                {p.text("act.ADMIT.label")}
              </Button>
              <Button
                variant="secondary"
                isDisabled={disabled}
                onPress={() => {
                  setDismissed(false);
                  setSelected(request);
                }}
              >
                {p.text("endpoint.authority.requests.dismiss.label")}
              </Button>
            </div>
          );
        }}
      />

      <Button
        variant="secondary"
        isDisabled={disabled || page.loading}
        onPress={() => {
          void page.load(null);
        }}
      >
        {p.text("ui.requests.refresh")}
      </Button>
      {page.cursor === null ? null : (
        <Button
          variant="secondary"
          isDisabled={disabled || page.loading}
          onPress={() => {
            void page.load(page.cursor);
          }}
        >
          {p.text("ui.requests.more")}
        </Button>
      )}
      {page.page === null ? null : (
        <p className="review-basis" title={page.page.basis.observedAt}>
          {p.text("ui.review.checked", {
            when: p.recordedTime(page.page.basis.observedAt, page.page.basis.timeZone),
          })}
        </p>
      )}
      <Dialog
        title={p.text("endpoint.authority.requests.dismiss.label")}
        description={p.text("endpoint.authority.requests.dismiss.meaning")}
        closeLabel={p.text("ui.close")}
        isOpen={selected !== null}
        isDismissable={!pending}
        onOpenChange={(open) => {
          if (!open && !pending) {
            setSelected(null);
          }
        }}
        footer={
          <>
            <Button
              variant="secondary"
              isDisabled={pending}
              onPress={() => {
                setSelected(null);
              }}
            >
              {p.text("ui.close")}
            </Button>
            <Button
              variant="danger"
              isDisabled={disabled}
              onPress={() => {
                void dismiss();
              }}
            >
              {p.text("endpoint.authority.requests.dismiss.label")}
            </Button>
          </>
        }
      >
        {selected === null ? null : (
          <PageRow
            views={page.views}
            index={pageItemIndex(page.views, selected.key)}
            titleId="label.accessRequest"
          />
        )}
      </Dialog>
    </section>
  );
};
/** Availability and the session epoch bound all disclosed request data and in-flight mutations. */
export const AccessRequests = ({ available, ...props }: Props) => {
  const { state } = useSessionContext();
  return available && state.kind === "authenticated" ? (
    <AvailableRequests key={`${state.epoch}:${state.token}`} {...props} />
  ) : null;
};
