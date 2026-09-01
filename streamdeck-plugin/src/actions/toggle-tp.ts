import { action } from "@elgato/streamdeck";
import { ToggleActionBase } from "./toggle-action-base.js";
import type { OnAirAction } from "../onair-client.js";

@action({ UUID: "com.souz4rafael.onair.toggle-tp" })
export class ToggleTpAction extends ToggleActionBase {
	protected readonly stateField = "tpOpen" as const;
	protected readonly command: OnAirAction = "ToggleOverlayVisibility";
}
