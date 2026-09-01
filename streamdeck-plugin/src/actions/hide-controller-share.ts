import { action } from "@elgato/streamdeck";
import { ToggleActionBase } from "./toggle-action-base.js";
import type { OnAirAction } from "../onair-client.js";

@action({ UUID: "com.souz4rafael.onair.hide-controller-share" })
export class HideControllerShareAction extends ToggleActionBase {
	protected readonly stateField = "controllerHiddenInShare" as const;
	protected readonly command: OnAirAction = "ToggleControllerCaptureProtection";
}
