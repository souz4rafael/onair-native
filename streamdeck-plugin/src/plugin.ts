import streamDeck from "@elgato/streamdeck";
import { onairClient } from "./onair-client.js";

import { ToggleTpAction } from "./actions/toggle-tp.js";
import { LockTpAction } from "./actions/lock-tp.js";
import { HideTpShareAction } from "./actions/hide-tp-share.js";
import { HideControllerShareAction } from "./actions/hide-controller-share.js";
import { RecordingAction } from "./actions/recording.js";
import { StatusAction } from "./actions/status.js";
import { ReleaseStealthAction } from "./actions/release-stealth.js";
import { OpenFileAction } from "./actions/open-file.js";
import { ScrollUpAction } from "./actions/scroll-up.js";
import { ScrollDownAction } from "./actions/scroll-down.js";
import { OpacityDialAction } from "./actions/dial-opacity.js";
import { FontSizeDialAction } from "./actions/dial-font-size.js";
import { ScrollSpeedDialAction } from "./actions/dial-scroll-speed.js";
import { VoiceScrollSpeedDialAction } from "./actions/dial-voice-scroll-speed.js";
import { ScrollStepDialAction } from "./actions/dial-scroll-step.js";
import { VoiceThresholdDialAction } from "./actions/dial-voice-threshold.js";

streamDeck.logger.setLevel("info");

streamDeck.actions.registerAction(new ToggleTpAction());
streamDeck.actions.registerAction(new LockTpAction());
streamDeck.actions.registerAction(new HideTpShareAction());
streamDeck.actions.registerAction(new HideControllerShareAction());
streamDeck.actions.registerAction(new RecordingAction());
streamDeck.actions.registerAction(new StatusAction());
streamDeck.actions.registerAction(new ReleaseStealthAction());
streamDeck.actions.registerAction(new OpenFileAction());
streamDeck.actions.registerAction(new ScrollUpAction());
streamDeck.actions.registerAction(new ScrollDownAction());
streamDeck.actions.registerAction(new OpacityDialAction());
streamDeck.actions.registerAction(new FontSizeDialAction());
streamDeck.actions.registerAction(new ScrollSpeedDialAction());
streamDeck.actions.registerAction(new VoiceScrollSpeedDialAction());
streamDeck.actions.registerAction(new ScrollStepDialAction());
streamDeck.actions.registerAction(new VoiceThresholdDialAction());

streamDeck.connect();

// Connect to onAIr's local RemoteControlService (loopback WebSocket, 127.0.0.1:47823). It
// reconnects on its own with a fixed delay whenever onAIr isn't running or restarts, so this
// call doesn't need to be awaited or retried here.
onairClient.connect();
