import { createContext, useContext, useLayoutEffect, type ReactNode, type Ref } from "react";
import { useRoute } from "../routing/useRoute";
import "./page.css";

export const PhoneTitleTarget = createContext<((title: string) => void) | null>(null);
export const PageBanner = createContext<ReactNode>(null);

export const PageTitle = ({
  id,
  title,
  children,
  headingRef,
}: {
  id: string;
  title: string;
  children?: ReactNode;
  headingRef?: Ref<HTMLHeadingElement> | undefined;
}) => {
  const publishPhoneTitle = useContext(PhoneTitleTarget);
  const route = useRoute();
  useLayoutEffect(() => {
    const heading = document.getElementById(id);
    const active = heading !== null && heading.closest("[hidden]") === null;
    if (
      active &&
      (route.screen !== "settings" || heading.closest(".settings-workspace") !== null)
    ) {
      document.title = `${title} · ClaimCore`;
      publishPhoneTitle?.(title);
    }
  }, [id, publishPhoneTitle, route, title]);
  return (
    <h1 id={id} tabIndex={-1} className="page-title" ref={headingRef}>
      {children ?? title}
    </h1>
  );
};

type HeadingProps = {
  id: string;
  title: string;
  headingRef?: Ref<HTMLHeadingElement> | undefined;
  subtitle?: ReactNode;
  primaryAction?: ReactNode;
};
export const PageHeading = ({ id, title, headingRef, subtitle, primaryAction }: HeadingProps) => (
  <header className="page-heading">
    <div>
      <PageTitle id={id} title={title} headingRef={headingRef} />
      {subtitle === undefined ? null : <p>{subtitle}</p>}
    </div>
    {primaryAction}
  </header>
);

export const Page = ({
  className,
  busy,
  tabs,
  toolbar,
  children,
  ...head
}: ({ header: ReactNode } | HeadingProps) & {
  className?: string;
  busy?: boolean;
  tabs?: ReactNode;
  toolbar?: ReactNode;
  children: ReactNode;
}) => {
  const banner = useContext(PageBanner);
  return (
    <div className={["page", className].filter(Boolean).join(" ")} aria-busy={busy}>
      {"header" in head ? head.header : <PageHeading {...head} />}
      {banner}
      {tabs}
      {toolbar}
      {children}
    </div>
  );
};
