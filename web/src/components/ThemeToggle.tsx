import { useEffect, useState } from "react";
import { applyTheme, getEffectiveTheme, getStoredTheme, type Theme } from "../lib/theme";

interface Props {
  className?: string;
}

export function ThemeToggle({ className = "" }: Props) {
  const [theme, setTheme] = useState<Theme>(getEffectiveTheme);

  useEffect(() => {
    const media = window.matchMedia("(prefers-color-scheme: dark)");
    const onChange = () => {
      if (!getStoredTheme()) setTheme(media.matches ? "dark" : "light");
    };
    media.addEventListener("change", onChange);
    return () => media.removeEventListener("change", onChange);
  }, []);

  const toggle = () => {
    const next: Theme = theme === "dark" ? "light" : "dark";
    applyTheme(next);
    setTheme(next);
  };

  return (
    <button
      type="button"
      className={`icon-button theme-toggle ${className}`}
      title={theme === "dark" ? "Switch to light mode" : "Switch to dark mode"}
      onClick={toggle}
    >
      {theme === "dark" ? "☀️" : "🌙"}
    </button>
  );
}
