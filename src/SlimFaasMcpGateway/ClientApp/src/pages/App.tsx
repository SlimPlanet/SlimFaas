import React from "react";
import { NavLink, Route, Routes } from "react-router-dom";
import ConfigurationsPage from "./ConfigurationsPage";
import ConfigurationEditorPage from "./ConfigurationEditorPage";
import DeploymentPage from "./DeploymentPage";
import TenantsPage from "./TenantsPage";

export default function App() {
  return (
    <div className="app">
      <header className="app__header">
        <div className="app__brand">
          <span className="app__logo" aria-hidden="true" />
          <span className="app__title">SlimFaas MCP Gateway</span>
        </div>
        <nav className="app__nav">
          <NavLink to="/" className={({ isActive }) => "app__nav-link" + (isActive ? " app__nav-link--active" : "")}>
            Configurations
          </NavLink>
          <NavLink to="/tenants" className={({ isActive }) => "app__nav-link" + (isActive ? " app__nav-link--active" : "")}>
            Tenants
          </NavLink>
        </nav>
        <div className="app__spacer" />
        <a className="app__github" href="https://github.com/" target="_blank" rel="noreferrer">
          GitHub
        </a>
      </header>

      <main className="app__main">
        <Routes>
          <Route path="/" element={<ConfigurationsPage />} />
          <Route path="/configurations/new" element={<ConfigurationEditorPage mode="create" />} />
          <Route path="/configurations/:id" element={<ConfigurationEditorPage mode="edit" />} />
          <Route path="/configurations/:id/deployments" element={<DeploymentPage />} />
          <Route path="/tenants" element={<TenantsPage />} />
        </Routes>
      </main>

      <footer className="app__footer">
        <span className="app__footer-text">Minimal MCP gateway UI — edit configs, deploy by environment, proxy upstream MCP servers.</span>
      </footer>
    </div>
  );
}
