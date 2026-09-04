import { action } from "@elgato/streamdeck";
import { ToggleActionBase } from "./toggle-action-base.js";
import type { OnAirAction } from "../onair-client.js";

@action({ UUID: "com.souz4rafael.onair.lock-insights" })
export class LockInsightsAction extends ToggleActionBase {
	protected readonly stateField = "insightsLocked" as const;
	protected readonly command: OnAirAction = "ToggleInsightsLock";
}
