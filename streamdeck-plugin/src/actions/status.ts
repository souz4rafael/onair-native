import { action, SingletonAction, type KeyDownEvent, type KeyAction, type WillAppearEvent } from "@elgato/streamdeck";
import { onairClient, type RemoteState } from "../onair-client.js";

/** Shows which AI provider is active (cloud vs local Whisper) and whether onAIr is currently
 * recording. Pressing it forces onAIr to re-check whether the local Whisper model is actually
 * loaded or it's fallen back to the cloud API (RecheckWhisperModel) — useful right after
 * downloading/pointing at a model file, without needing to retype the path in the Controller.
 * The tile's own title updates naturally once the resulting state push arrives. */
@action({ UUID: "com.souz4rafael.onair.status" })
export class StatusAction extends SingletonAction {
	private subscribed = false;

	override onWillAppear(ev: WillAppearEvent): void | Promise<void> {
		if (!this.subscribed) {
			this.subscribed = true;
			onairClient.onState((state) => this.syncAll(state));
			onairClient.onConnectionChange((connected) => {
				if (!connected) this.setDisconnectedTitle();
			});
		}
		const state = onairClient.currentState;
		if (state && ev.action.isKey()) return this.applyState(ev.action, state);
	}

	override onKeyDown(ev: KeyDownEvent): void {
		onairClient.send("RecheckWhisperModel");
		if (ev.action.isKey()) void ev.action.showOk();
	}

	private syncAll(state: RemoteState): void {
		for (const a of this.actions) {
			if (a.isKey()) void this.applyState(a, state);
		}
	}

	private setDisconnectedTitle(): void {
		for (const a of this.actions) {
			if (a.isKey()) void a.setTitle("onAIr\noffline");
		}
	}

	private applyState(a: KeyAction, state: RemoteState): void {
		const line1 = state.whisperLocalLoaded ? "Whisper" : state.chatProvider;
		const line2 = state.whisperLocalLoaded ? "(local)" : "(cloud)";
		const rec = state.recording ? "\n● REC" : "";
		void a.setTitle(`${line1}\n${line2}${rec}`);
	}
}
