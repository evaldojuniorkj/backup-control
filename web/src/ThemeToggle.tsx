import { useState } from "react";

type Theme = "light" | "dark";
function initialTheme(): Theme {
  try {
    const saved=localStorage.getItem("backup-control-theme");
    if(saved==="light"||saved==="dark") return saved;
  } catch { /* Browsers may disable local storage. */ }
  return window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light";
}
const startingTheme=initialTheme();
document.documentElement.dataset.theme=startingTheme;

export function ThemeToggle() {
  const [theme,setTheme]=useState<Theme>(()=>document.documentElement.dataset.theme==="dark"?"dark":"light");
  function toggle() {
    const next=theme==="dark"?"light":"dark";
    document.documentElement.dataset.theme=next;
    setTheme(next);
    try {localStorage.setItem("backup-control-theme",next);} catch { /* Theme still works without persistence. */ }
  }
  return <button type="button" className="theme-toggle" onClick={toggle} aria-label={theme==="dark"?"Ativar modo claro":"Ativar modo escuro"} title={theme==="dark"?"Ativar modo claro":"Ativar modo escuro"}>
    <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" aria-hidden="true">{theme==="dark"?<><circle cx="12" cy="12" r="4"/><path d="M12 2v2m0 16v2M2 12h2m16 0h2M5 5l1.5 1.5m11 11L19 19M5 19l1.5-1.5m11-11L19 5"/></>:<path d="M20.5 14A8.5 8.5 0 0 1 10 3.5 8.5 8.5 0 1 0 20.5 14Z"/>}</svg>
    <span>{theme==="dark"?"Modo claro":"Modo escuro"}</span>
  </button>;
}
