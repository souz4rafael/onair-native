import { action } from "@elgato/streamdeck";
import { ToggleActionBase } from "./toggle-action-base.js";
import type { OnAirAction } from "../onair-client.js";

@action({ UUID: "com.souz4rafael.onair.hide-tp-share" })
export class HideTpShareAction extends ToggleActionBase {
	protected readonly stateField = "tpHiddenInShare" as const;
	protected readonly command: OnAirAction = "ToggleOverlayCaptureProtection";
}
