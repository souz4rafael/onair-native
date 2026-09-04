import { action } from "@elgato/streamdeck";
import { ToggleActionBase } from "./toggle-action-base.js";
import type { OnAirAction } from "../onair-client.js";

@action({ UUID: "com.souz4rafael.onair.hide-insights-share" })
export class HideInsightsShareAction extends ToggleActionBase {
	protected readonly stateField = "insightsHiddenInShare" as const;
	protected readonly command: OnAirAction = "ToggleInsightsCaptureProtection";
}
