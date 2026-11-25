import {
  useCallback,
  useLayoutEffect,
  useRef,
  useState,
  type KeyboardEvent,
  type RefObject,
} from "react";
import { attachmentTransport } from "@contracts/client";
import { readAttachmentPreviewResource } from "../../../api/attachments";
import { usePresentation } from "../../../presentation/context";
import { isPositiveInteger, messageRecord, previewChannel } from "./channel";

const pageDeadline = 20_000;
const suppressRepeatedEnter = (event: KeyboardEvent<HTMLButtonElement>) => {
  if (event.key === "Enter" && event.repeat) {
    event.preventDefault();
  }
};
type Position = { page: number; pageCount: number };
type Connection = {
  port: MessagePort;
  requests: AbortController;
  requestId: number;
  page: number;
  pageCount: number | null;
  awaiting: boolean;
};

type ResourceRequest = { id: number; type: "cmaps" | "wasm"; name: string };
const resourceRequest = (message: unknown): ResourceRequest | null => {
  if (
    !messageRecord(message, ["kind", "resourceId", "resourceType", "name"]) ||
    message["kind"] !== "resource" ||
    !isPositiveInteger(message["resourceId"]) ||
    (message["resourceType"] !== "cmaps" && message["resourceType"] !== "wasm") ||
    typeof message["name"] !== "string"
  ) {
    return null;
  }
  return { id: message["resourceId"], type: message["resourceType"], name: message["name"] };
};

const sendResource = async (connection: Connection, { id, type, name }: ResourceRequest) => {
  const { signal } = connection.requests;
  const bytes = await readAttachmentPreviewResource(type, name, signal);
  if (bytes !== null && !signal.aborted) {
    connection.port.postMessage({ resourceId: id, bytes }, [bytes]);
  }
};

const isFailedPage = (data: unknown, requestId: number) =>
  messageRecord(data, ["kind", "requestId"]) &&
  data["kind"] === "failed" &&
  data["requestId"] === requestId;

export const DocumentPreview = ({
  bytes,
  title,
  unavailable,
  closeButton,
}: {
  bytes: ArrayBuffer;
  title: string;
  unavailable: string;
  closeButton: RefObject<HTMLButtonElement | null>;
}) => {
  const p = usePresentation();
  const session = useRef<Connection | null>(null);
  const clock = useRef(0);
  const [position, setPosition] = useState<Position | null>(null);
  const [requested, setRequested] = useState(1);
  const [failed, setFailed] = useState(false);
  const previousButton = useRef<HTMLButtonElement>(null);
  const nextButton = useRef<HTMLButtonElement>(null);
  const pager = useRef<HTMLDivElement>(null);
  const moveFocus = useRef(false);

  useLayoutEffect(() => {
    if (moveFocus.current) {
      const enabled = requested === 1 ? nextButton : previousButton;
      enabled.current?.focus();
      moveFocus.current = false;
    }
  }, [requested]);

  const release = useCallback(() => {
    window.clearTimeout(clock.current);
    session.current?.requests.abort();
    session.current?.port.close();
    session.current = null;
  }, []);
  const fail = useCallback(() => {
    if (pager.current?.contains(document.activeElement) === true) {
      closeButton.current?.focus();
    }
    release();
    setFailed(true);
  }, [closeButton, release]);
  useLayoutEffect(() => {
    clock.current = window.setTimeout(fail, pageDeadline);
    return release;
  }, [fail, release]);

  const navigate = (offset: number) => {
    const connection = session.current;
    if (connection === null || connection.pageCount === null) {
      return;
    }
    const page = connection.page + offset;
    if (page < 1 || page > connection.pageCount) {
      return;
    }
    moveFocus.current =
      (page === 1 && document.activeElement === previousButton.current) ||
      (page === connection.pageCount && document.activeElement === nextButton.current);
    connection.page = page;
    connection.requestId++;
    connection.awaiting = true;
    window.clearTimeout(clock.current);
    clock.current = window.setTimeout(fail, pageDeadline);
    setRequested(page);
    connection.port.postMessage({ requestId: connection.requestId, page });
  };

  const start = (frame: HTMLIFrameElement) => {
    const viewer = frame.contentWindow;
    if (viewer === null || session.current !== null) {
      fail();
      return;
    }
    const channel = new MessageChannel();
    const connection: Connection = {
      port: channel.port1,
      requests: new AbortController(),
      requestId: 1,
      page: 1,
      pageCount: null,
      awaiting: true,
    };
    session.current = connection;
    connection.port.onmessage = ({ data }: MessageEvent<unknown>) => {
      if (session.current !== connection) {
        return;
      }
      const resource = resourceRequest(data);
      if (resource !== null) {
        void sendResource(connection, resource).catch(() => undefined);
        return;
      }
      if (!connection.awaiting) {
        return;
      }
      if (isFailedPage(data, connection.requestId)) {
        fail();
        return;
      }
      if (
        messageRecord(data, ["requestId", "page", "pageCount"]) &&
        data["requestId"] === connection.requestId &&
        data["page"] === connection.page &&
        isPositiveInteger(data["pageCount"]) &&
        data["pageCount"] >= connection.page &&
        (connection.pageCount === null || data["pageCount"] === connection.pageCount)
      ) {
        connection.awaiting = false;
        connection.pageCount = data["pageCount"];
        window.clearTimeout(clock.current);
        setPosition({ page: connection.page, pageCount: connection.pageCount });
      }
    };
    viewer.postMessage(previewChannel, "*", [channel.port2]);
    connection.port.postMessage(bytes, [bytes]);
    connection.port.postMessage({ requestId: connection.requestId, page: connection.page });
  };

  if (failed) {
    return <p role="alert">{unavailable}</p>;
  }
  return (
    <>
      {position === null ? null : (
        <div className="attachment-actions" ref={pager}>
          <button
            ref={previousButton}
            type="button"
            disabled={requested <= 1}
            onKeyDown={suppressRepeatedEnter}
            onClick={() => {
              navigate(-1);
            }}
          >
            {p.text("ui.previousPage")}
          </button>
          <button
            ref={nextButton}
            type="button"
            disabled={requested >= position.pageCount}
            onKeyDown={suppressRepeatedEnter}
            onClick={() => {
              navigate(1);
            }}
          >
            {p.text("ui.nextPage")}
          </button>
        </div>
      )}
      <p role="status" aria-live="polite">
        {position === null
          ? p.text("ui.loading")
          : p.text("ui.documentPage", {
              page: p.integer(position.page),
              count: p.integer(position.pageCount),
            })}
      </p>
      <iframe
        className="attachment-document"
        src={attachmentTransport.viewerPath}
        sandbox="allow-scripts"
        tabIndex={-1}
        title={title}
        onLoad={(event) => {
          start(event.currentTarget);
        }}
      />
    </>
  );
};
