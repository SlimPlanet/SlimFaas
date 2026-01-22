import { NavLink } from "react-router-dom";
import { useMemo, useState } from "react";
import "./Navbar.scss";

type NavItem = { to: string; label: string };

export default function Navbar() {
  const [open, setOpen] = useState(false);

  const items = useMemo<NavItem[]>(
    () => [
      { to: "/swagger-to-mcp", label: "Swagger to MCP" },
      { to: "/chat", label: "Chat" },
    ],
    [],
  );

  return (
    <header className="nav">
      <div className="sf-container nav__inner">
        <a className="nav__brand" href="./">
          <img className="nav__logo" src="./slimfaas.svg" alt="SlimFaas" />
          <div className="nav__brand-text">
            <div className="nav__title">SlimFaas</div>
            <div className="nav__subtitle">MCP Proxy UI</div>
          </div>
        </a>

        <button
          className="nav__burger"
          type="button"
          aria-label="Toggle menu"
          aria-expanded={open}
          onClick={() => setOpen((v) => !v)}
        >
          <span className="nav__burger-line" />
          <span className="nav__burger-line" />
          <span className="nav__burger-line" />
        </button>

        <nav className={`nav__menu ${open ? "nav__menu--open" : ""}`}>
          {items.map((it) => (
            <NavLink
              key={it.to}
              to={it.to}
              className={({ isActive }) =>
                `nav__link ${isActive ? "nav__link--active" : ""}`
              }
              onClick={() => setOpen(false)}
            >
              {it.label}
            </NavLink>
          ))}
          <a
            className="nav__link nav__link--ghost"
            href="https://slimfaas.dev/"
            target="_blank"
            rel="noreferrer"
            title="Open slimfaas.dev"
          >
            Docs
          </a>
        </nav>
      </div>
    </header>
  );
}
