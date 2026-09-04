import { action } from "@elgato/streamdeck";
import { FieldToggleActionBase } from "./field-toggle-action-base.js";

@action({ UUID: "com.souz4rafael.onair.toggle-insights-pacing" })
export class ToggleInsightsPacingAction extends FieldToggleActionBase {
	protected readonly stateField = "showPacingInInsights" as const;
	protected readonly field = "ShowPacingInInsights";
}
