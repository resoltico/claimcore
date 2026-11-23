import type { ReactNode } from "react";
import "./page.css";

export const PageHeading = ({
  id,
  title,
  subtitle,
  primaryAction,
}: {
  id: string;
  title: ReactNode;
  subtitle?: ReactNode;
  primaryAction?: ReactNode;
}) => (
  <header className="page-heading">
    <div>
      <h1 id={id} tabIndex={-1}>
        {title}
      </h1>
      {subtitle === undefined ? null : <p>{subtitle}</p>}
    </div>
    {primaryAction}
  </header>
);

export const Page = ({
  id,
  title,
  subtitle,
  primaryAction,
  tabs,
  toolbar,
  children,
}: {
  id: string;
  title: ReactNode;
  subtitle?: ReactNode;
  primaryAction?: ReactNode;
  tabs?: ReactNode;
  toolbar?: ReactNode;
  children: ReactNode;
}) => (
  <div className="page">
    <PageHeading id={id} title={title} subtitle={subtitle} primaryAction={primaryAction} />
    {tabs}
    {toolbar}
    {children}
  </div>
);
