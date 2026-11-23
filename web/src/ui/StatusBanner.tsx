import { useLayoutEffect, useRef, type ReactNode } from "react";
import { Icon } from "./icons";
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
  const banner = useRef<HTMLDivElement>(null);
  useLayoutEffect(() => {
    const element = banner.current;
    const escape = (event: KeyboardEvent) => {
      if (event.key === "Escape") {
        element?.closest(".page")?.querySelector("h1")?.focus();
      }
    };
    element?.addEventListener("keydown", escape);
    const focus = requestAnimationFrame(() => {
      if (shouldFocus?.() !== false) {
        banner.current?.focus();
      }
    });
    return () => {
      cancelAnimationFrame(focus);
      element?.removeEventListener("keydown", escape);
    };
  }, [shouldFocus]);
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
