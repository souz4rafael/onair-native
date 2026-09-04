import { action } from "@elgato/streamdeck";
import { FieldToggleActionBase } from "./field-toggle-action-base.js";

@action({ UUID: "com.souz4rafael.onair.toggle-insights-external" })
export class ToggleInsightsExternalAction extends FieldToggleActionBase {
	protected readonly stateField = "showExternalInsightsInInsights" as const;
	protected readonly field = "ShowExternalInsightsInInsights";
}
