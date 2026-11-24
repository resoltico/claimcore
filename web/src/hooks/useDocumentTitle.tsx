import { createContext, useContext, useLayoutEffect, type ReactNode } from "react";

export const PhoneTitleTarget = createContext<((title: string) => void) | null>(null);
const TitleScope = createContext(true);

export const DocumentTitleScope = ({
  active,
  children,
}: {
  active: boolean;
  children: ReactNode;
}) => {
  const available = useContext(TitleScope);
  return <TitleScope value={available && active}>{children}</TitleScope>;
};

export const useDocumentTitle = (title: string | null) => {
  const active = useContext(TitleScope);
  const publish = useContext(PhoneTitleTarget);
  useLayoutEffect(() => {
    if (active && title !== null) {
      document.title = `${title} · ClaimCore`;
      publish?.(title);
    }
  }, [active, publish, title]);
};
