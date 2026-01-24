export type McpTool = {
  name: string;
  description: string;
  inputSchema?: any;
  outputSchema?: any;
  endpoint?: { contentType?: string };
  // UI flags (optional)
  isDisabled?: boolean;
  isAdded?: boolean;
  isOverridden?: boolean;
  isDescriptionOverridden?: boolean;
  isInputSchemaOverridden?: boolean;
};

export type UiTool = McpTool & {
  // local UI inputs
  uiJsonInput?: string;
  uiTextInputs?: Record<string, string>;
  uiFileInputs?: Record<string, File | null>;
};
