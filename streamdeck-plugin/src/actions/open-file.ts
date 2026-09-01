import { action } from "@elgato/streamdeck";
import { MomentaryActionBase } from "./momentary-action-base.js";
import type { OnAirAction } from "../onair-client.js";

@action({ UUID: "com.souz4rafael.onair.open-file" })
export class OpenFileAction extends MomentaryActionBase {
	protected readonly command: OnAirAction = "OpenFile";
}
