import { action } from "@elgato/streamdeck";
import { ToggleActionBase } from "./toggle-action-base.js";
import type { OnAirAction } from "../onair-client.js";

@action({ UUID: "com.souz4rafael.onair.toggle-insights" })
export class ToggleInsightsAction extends ToggleActionBase {
	protected readonly stateField = "insightsOpen" as const;
	protected readonly command: OnAirAction = "ToggleInsightsVisibility";
}
