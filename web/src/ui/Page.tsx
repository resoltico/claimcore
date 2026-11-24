import {
  createContext,
  useCallback,
  useContext,
  useMemo,
  useRef,
  type ReactNode,
  type RefObject,
} from "react";
import "./page.css";

type HeadingHandle = {
  attach: (node: HTMLHeadingElement | null) => void;
  focus: () => void;
};
export const PageHeadingTarget = createContext<HeadingHandle | null>(null);
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
  headingRef?: RefObject<HTMLHeadingElement | null> | undefined;
}) => {
  const pageHeading = useContext(PageHeadingTarget);
  const attach = useCallback(
    (node: HTMLHeadingElement | null) => {
      pageHeading?.attach(node);
      if (headingRef !== undefined) {
        headingRef.current = node;
      }
    },
    [headingRef, pageHeading],
  );
  return (
    <h1 id={id} tabIndex={-1} className="page-title" ref={attach}>
      {children ?? title}
    </h1>
  );
};

type HeadingProps = {
  id: string;
  title: string;
  headingRef?: RefObject<HTMLHeadingElement | null> | undefined;
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
  as: Element = "div",
  labelledBy,
  banner,
  tabs,
  toolbar,
  children,
  ...head
}: ({ header: ReactNode } | HeadingProps) & {
  className?: string;
  busy?: boolean;
  as?: "div" | "section";
  labelledBy?: string;
  banner?: ReactNode;
  tabs?: ReactNode;
  toolbar?: ReactNode;
  children: ReactNode;
}) => {
  const feedback = useContext(PageBanner);
  const heading = useRef<HTMLHeadingElement>(null);
  const handle = useMemo<HeadingHandle>(
    () => ({
      attach: (node) => {
        heading.current = node;
      },
      focus: () => {
        heading.current?.focus();
      },
    }),
    [],
  );
  return (
    <Element
      className={["page", className].filter(Boolean).join(" ")}
      aria-busy={busy}
      aria-labelledby={labelledBy}
    >
      <PageHeadingTarget value={handle}>
        {"header" in head ? head.header : <PageHeading {...head} />}
        {feedback}
        {banner}
        {tabs}
        {toolbar}
        {children}
      </PageHeadingTarget>
    </Element>
  );
};
