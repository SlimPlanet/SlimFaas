import type { PropsWithChildren } from "react";
import Navbar from "../navbar/Navbar";
import "./Layout.scss";

export default function Layout({ children }: PropsWithChildren) {
  return (
    <div className="layout">
      <Navbar />
      <main className="layout__main">
        <div className="sf-container">{children}</div>
      </main>
      <footer className="layout__footer">
        <div className="sf-container layout__footer-inner">
          <span className="layout__footer-text sf-muted">
            SlimFaas MCP Proxy © 2026 SlimPlanet
          </span>
        </div>
      </footer>
    </div>
  );
}
