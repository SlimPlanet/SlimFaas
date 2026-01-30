export type TenantListItemDto = {
  id: string;
  name: string;
  description?: string | null;
};

export type ConfigurationListItemDto = {
  id: string;
  tenantName: string;
  name: string;
  gatewayUrl: string;
  createdAtUtc: string;
  defaultDeploymentEnvironment: string;
};

export type ConfigurationDto = {
  id: string;
  tenantId?: string | null;
  tenantName: string;
  name: string;
  upstreamMcpUrl: string;
  description?: string | null;
  hasDiscoveryJwtToken: boolean;
  catalogOverrideYaml?: string | null;
  enforceAuthEnabled: boolean;
  authPolicyYaml?: string | null;
  rateLimitEnabled: boolean;
  rateLimitPolicyYaml?: string | null;
  catalogCacheTtlMinutes: number;
  version: number;
  createdAtUtc: string;
  updatedAtUtc: string;
};

export type AuditHistoryItemDto = { index: number; modifiedAtUtc: number; author: string };

export type DeploymentStateDto = { environmentName: string; deployedAuditIndex?: number | null };

export type DeploymentOverviewDto = {
  configurationId: string;
  tenantName: string;
  configurationName: string;
  environments: DeploymentStateDto[];
  history: AuditHistoryItemDto[];
};

export type AuditDiffDto = {
  from: { index: number; modifiedAtUtc: number; author: string };
  to: { index: number; modifiedAtUtc: number; author: string };
  patch: { path: string; value?: any; remove: boolean }[];
};

export type DiffLineType = "Unchanged" | "Inserted" | "Deleted" | "Modified" | "Imaginary";

export type DiffLine = {
  type: DiffLineType;
  text: string;
  position: number | null;
};

export type UnifiedDiff = {
  lines: DiffLine[];
};

export type AuditTextDiffDto = {
  from: { index: number; modifiedAtUtc: number; author: string };
  to: { index: number; modifiedAtUtc: number; author: string };
  unifiedDiff: UnifiedDiff;
};

export type EnvironmentListDto = { environments: string[] };

export type LoadCatalogResponseDto = { catalogYaml: string };
