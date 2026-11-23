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
}: {
  tone: "success" | "info";
  children: ReactNode;
  action?: ReactNode;
  dismissLabel?: string;
  dismiss?: () => void;
}) => {
  const banner = useRef<HTMLDivElement>(null);
  useLayoutEffect(() => {
    const focus = requestAnimationFrame(() => {
      banner.current?.focus();
    });
    return () => {
      cancelAnimationFrame(focus);
    };
  }, []);
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
