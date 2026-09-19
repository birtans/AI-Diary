import { useEffect, useState } from "react";

const BREAKPOINT = 768;

/** Tracks the same breakpoint the mobile CSS uses, so components can conditionally mount instead of just hide. */
export function useIsMobile(): boolean {
  const [isMobile, setIsMobile] = useState(() => window.matchMedia(`(max-width: ${BREAKPOINT}px)`).matches);

  useEffect(() => {
    const mql = window.matchMedia(`(max-width: ${BREAKPOINT}px)`);
    const onChange = () => setIsMobile(mql.matches);
    mql.addEventListener("change", onChange);
    return () => mql.removeEventListener("change", onChange);
  }, []);

  return isMobile;
}
