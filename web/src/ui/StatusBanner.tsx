import { useContext, useLayoutEffect, useRef, type ReactNode } from "react";
import { Icon } from "./icons";
import { PageHeadingTarget } from "./Page";
import { Button } from "./Button";
import "./status-banner.css";

export const StatusBanner = ({
  tone,
  children,
  action,
  dismissLabel,
  dismiss,
  shouldFocus,
}: {
  tone: "success" | "info";
  children: ReactNode;
  action?: ReactNode;
  dismissLabel?: string;
  dismiss?: () => void;
  shouldFocus?: (() => boolean) | undefined;
}) => {
  const heading = useContext(PageHeadingTarget);
  const banner = useRef<HTMLDivElement>(null);
  useLayoutEffect(() => {
    const element = banner.current;
    const escape = (event: KeyboardEvent) => {
      if (event.key === "Escape") {
        heading?.focus();
      }
    };
    const reveal = () => {
      if (element?.contains(element.ownerDocument.activeElement) === true) {
        element.scrollIntoView?.({ block: "nearest" });
      }
    };
    element?.addEventListener("keydown", escape);
    window.addEventListener("resize", reveal);
    const focus = requestAnimationFrame(() => {
      if (shouldFocus?.() !== false) {
        element?.scrollIntoView?.({ block: "nearest" });
        element?.focus({ preventScroll: true });
      }
    });
    return () => {
      cancelAnimationFrame(focus);
      element?.removeEventListener("keydown", escape);
      window.removeEventListener("resize", reveal);
    };
  }, [heading, shouldFocus]);
  return (
    <div className="status-banner" data-tone={tone} role="status" tabIndex={-1} ref={banner}>
      <Icon name={tone === "success" ? "check" : "info"} />
      <span>{children}</span>
      {action}
      {dismiss === undefined ? null : (
        <Button
          variant="quiet"
          compact
          {...(dismissLabel === undefined ? {} : { "aria-label": dismissLabel })}
          onPress={dismiss}
        >
          <Icon name="close" />
        </Button>
      )}
    </div>
  );
};
