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
              <img className="nav__logo" src="/slimfaas.svg" alt="SlimFaas" />
              <div className="nav__brand-text">
                  <div className="nav__title">SlimFaas</div>
                  <div className="nav__subtitle">MCP Proxy</div>
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
                  className="nav__link nav__link--ghost nav__link--icon"
                  href="https://github.com/SlimPlanet/SlimFaas"
                  target="_blank"
                  rel="noreferrer"
                  aria-label="Open GitHub"
                  title="GitHub"
                  onClick={() => setOpen(false)}
              >
                  <svg viewBox="0 0 24 24" aria-hidden="true" focusable="false">
                      <path d="M12 .5C5.73.5.75 5.62.75 12c0 5.1 3.29 9.41 7.86 10.94.58.11.8-.26.8-.58v-2.1c-3.2.71-3.88-1.58-3.88-1.58-.53-1.36-1.29-1.72-1.29-1.72-1.05-.73.08-.72.08-.72 1.16.08 1.78 1.22 1.78 1.22 1.03 1.8 2.7 1.28 3.36.98.1-.76.4-1.28.72-1.57-2.55-.3-5.23-1.3-5.23-5.77 0-1.27.44-2.3 1.17-3.11-.12-.3-.51-1.5.11-3.12 0 0 .96-.32 3.14 1.19.91-.26 1.88-.39 2.85-.39.97 0 1.95.13 2.85.39 2.18-1.51 3.14-1.19 3.14-1.19.62 1.62.23 2.82.11 3.12.73.81 1.17 1.84 1.17 3.11 0 4.48-2.69 5.46-5.25 5.76.41.36.78 1.08.78 2.18v3.23c0 .32.21.69.81.58 4.56-1.54 7.84-5.84 7.84-10.94C23.25 5.62 18.27.5 12 .5Z"/>
                  </svg>
              </a>
          </nav>
      </div>
    </header>
  );
}
