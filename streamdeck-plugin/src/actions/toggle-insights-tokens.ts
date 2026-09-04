import { action } from "@elgato/streamdeck";
import { FieldToggleActionBase } from "./field-toggle-action-base.js";

@action({ UUID: "com.souz4rafael.onair.toggle-insights-tokens" })
export class ToggleInsightsTokensAction extends FieldToggleActionBase {
	protected readonly stateField = "showTokenUsageInInsights" as const;
	protected readonly field = "ShowTokenUsageInInsights";
}
