import { useLayoutEffect, useRef, useState, type ReactNode } from "react";

export const CaseTitleStrip = ({ children }: { children: ReactNode }) => {
  const title = useRef<HTMLDivElement>(null);
  const [stuck, setStuck] = useState(false);
  useLayoutEffect(() => {
    const element = title.current;
    const card = element?.parentElement;
    if (element === null || card === null || card === undefined) {
      return;
    }
    const observe = () => {
      const edge = card.getBoundingClientRect().top + card.clientTop;
      setStuck(element.getBoundingClientRect().top > edge + 1);
    };
    observe();
    window.addEventListener("scroll", observe, { passive: true });
    window.addEventListener("resize", observe);
    return () => {
      window.removeEventListener("scroll", observe);
      window.removeEventListener("resize", observe);
    };
  }, []);
  return (
    <div
      ref={title}
      className="case-title-sticky"
      data-region="IDENTITY"
      data-stuck={stuck || undefined}
    >
      {children}
    </div>
  );
};
