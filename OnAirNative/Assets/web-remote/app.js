// onAIr Web Remote — vanilla JS, no build step, no dependencies. Mirrors the exact WebSocket
// protocol used by the Stream Deck plugin / MCP server (see RemoteControlService.cs /
// WebRemoteService.cs's doc comments): newline-delimited JSON, ops command/adjust/getState/set/
// loadScript/getScriptText/listFonts/showInsight/clearInsight/listStealthWindows/
// embedStealthWindow, state pushed as {"op":"state","data":{...RemoteState fields...}}. Any
// request that includes an "id" gets a correlated
// {"op":"result","id":...,"success":...,"error"?:...,"data"?:...} reply — see
// sendRequest()/pendingRequests below, used for listFonts and custom-hex color validation.

(function () {
  "use strict";

  const $ = (id) => document.getElementById(id);

  const state = {
    pin: localStorage.getItem("onairPin") || "",
    ws: null,
    connected: false,
    reconnectTimer: null,
    reconnectDelay: 1000,
    watchdog: null,
  };

  let lastState = null;

  // ── Request/response correlation ────────────────────────────────────────
  // Every op the server understands echoes back the caller's "id" in a
  // {"op":"result","id":...,"success":...,"error"?:...,"data"?:...} reply (see
  // WebRemoteService.HandleIncomingMessage). Fire-and-forget ops (buttons/steppers) just use
  // send(); anything that needs to show a validation error or a data payload (font list, custom
  // hex color) awaits sendRequest() instead.
  let reqId = 0;
  const pendingRequests = new Map();

  function sendRequest(obj) {
    return new Promise((resolve) => {
      const id = String(++reqId);
      pendingRequests.set(id, resolve);
      send({ ...obj, id });
    });
  }

  // ── PIN gate / connection lifecycle ───────────────────────────────────

  function showGate(errorMsg) {
    $("app").classList.add("hidden");
    $("pinGate").classList.remove("hidden");
    $("pinError").textContent = errorMsg || "";
    $("pinInput").focus();
  }

  function showApp() {
    $("pinGate").classList.add("hidden");
    $("app").classList.remove("hidden");
  }

  function wsUrl(pin) {
    const proto = location.protocol === "https:" ? "wss:" : "ws:";
    return `${proto}//${location.host}/ws?pin=${encodeURIComponent(pin)}`;
  }

  function setConnDot(ok) {
    const dot = $("connDot");
    if (dot) dot.className = "dot " + (ok ? "on" : "off");
  }

  function connect(pin) {
    if (state.ws) {
      // Detach handlers from the old socket first so its eventual (delayed) close event can
      // never act on state that now belongs to the new connection attempt below.
      state.ws.onopen = null;
      state.ws.onmessage = null;
      state.ws.onclose = null;
      state.ws.onerror = null;
      try { state.ws.close(); } catch { /* ignore */ }
    }

    const ws = new WebSocket(wsUrl(pin));
    state.ws = ws;

    ws.onopen = () => {
      state.connected = true;
      state.reconnectDelay = 1000;
      state.pin = pin;
      localStorage.setItem("onairPin", pin);
      showApp();
      setConnDot(true);
      resetWatchdog();
      populateFontFamilies();
      populateStealthWindows();
    };

    ws.onmessage = (ev) => {
      resetWatchdog();
      let msg;
      try { msg = JSON.parse(ev.data); } catch { return; }
      if (msg.op === "state") { render(msg.data); return; }
      if (msg.op === "result" && msg.id && pendingRequests.has(msg.id)) {
        const resolve = pendingRequests.get(msg.id);
        pendingRequests.delete(msg.id);
        resolve(msg);
      }
    };

    ws.onclose = () => {
      if (state.ws !== ws) return; // superseded by a newer connect() call
      setConnDot(false);
      if (!state.connected) {
        showGate("Wrong PIN or can't reach onAIr — check the PIN and try again.");
        return;
      }
      state.connected = false;
      scheduleReconnect();
    };

    ws.onerror = () => { /* onclose always follows — nothing extra to do here */ };
  }

  function scheduleReconnect() {
    clearTimeout(state.reconnectTimer);
    state.reconnectTimer = setTimeout(() => connect(state.pin), state.reconnectDelay);
    state.reconnectDelay = Math.min(state.reconnectDelay * 1.5, 8000);
  }

  // Mobile browsers suspend/close background sockets — reconnect whenever the page becomes
  // visible again if we don't currently have a live connection.
  function resetWatchdog() {
    clearTimeout(state.watchdog);
    state.watchdog = setTimeout(() => {
      if (state.ws) try { state.ws.close(); } catch { /* ignore */ }
    }, 10000);
  }

  document.addEventListener("visibilitychange", () => {
    if (document.visibilityState === "visible" && state.pin &&
        (!state.ws || state.ws.readyState !== WebSocket.OPEN)) {
      connect(state.pin);
    }
  });

  function send(obj) {
    if (state.ws && state.ws.readyState === WebSocket.OPEN) state.ws.send(JSON.stringify(obj));
  }

  // ── Rendering ──────────────────────────────────────────────────────────

  const QA_SESSION_COLORS = { true: "#16a34a", false: "#78808a" };

  function setToggle(id, active) {
    const el = $(id);
    if (el) el.classList.toggle("active", !!active);
  }

  function render(s) {
    lastState = s;

    setToggle("btnTpOpen", s.tpOpen);
    setToggle("btnTpLock", s.tpLocked);
    setToggle("btnTpHide", s.tpHiddenInShare);
    setToggle("btnInsOpen", s.insightsOpen);
    setToggle("btnInsLock", s.insightsLocked);
    setToggle("btnInsHide", s.insightsHiddenInShare);
    setToggle("btnRecording", s.recording);
    setToggle("btnCtrlHide", s.controllerHiddenInShare);
    setToggle("btnShowFollowUps", s.showFollowUpSuggestions);
    setToggle("btnShowPacing", s.showPacingInInsights);
    setToggle("btnShowTokenUsage", s.showTokenUsageInInsights);
    setToggle("btnShowFollowUpsInInsights", s.showFollowUpsInInsights);
    setToggle("btnShowExternalInsights", s.showExternalInsightsInInsights);

    $("valFontSize").textContent = s.fontSize;
    $("valOpacity").textContent = Math.round(s.opacity) + "%";
    $("valScrollStep").textContent = s.scrollStep;
    $("valScrollSpeed").textContent = s.scrollSpeed;
    $("valVoiceSpeed").textContent = s.voiceScrollSpeed;
    $("valVoiceThreshold").textContent = Math.round(s.voiceThreshold);
    $("valScriptName").textContent = s.loadedScriptName || "(none)";
    $("valChatProvider").textContent =
      `${s.chatProvider || "?"} · Whisper: ${s.whisperModelStatus || (s.whisperLocalLoaded ? "local" : "cloud")}`;
    $("valInsFontSize").textContent = s.insightFontSize;
    $("valInsOpacity").textContent = Math.round(s.insightOpacity) + "%";
    $("valInsightText").textContent = s.insightText || "(none)";
    $("valConvTurns").textContent = s.conversationTurnCount ?? "--";
    // Conversation modal (#convModal) reads lastState.conversationHistory on demand (see
    // wireConversationModal below) rather than re-rendering here — it's only visible when the
    // presenter explicitly taps "View Conversation".

    const qaBadge = $("qaSessionBadge");
    qaBadge.textContent = s.qaSessionActive ? "Active" : "Inactive";
    qaBadge.style.backgroundColor = QA_SESSION_COLORS[!!s.qaSessionActive];

    document.querySelectorAll(".mode-btn").forEach((btn) => {
      btn.classList.toggle("active", btn.dataset.scrollmode === s.scrollMode);
    });
    $("scrollStepRow").style.display = s.scrollMode === "Manual" ? "" : "none";
    $("scrollSpeedRow").style.display = s.scrollMode === "Auto" ? "" : "none";
    $("voiceSpeedRow").style.display = s.scrollMode === "Voice" ? "" : "none";
    $("voiceThresholdRow").style.display = s.scrollMode === "Voice" ? "" : "none";

    // Font-family selects only get their <option> list once (populateFontFamilies, on connect)
    // but the selected value needs to track every state push in case another client changes it.
    if ($("fontFamilyTp").options.length) $("fontFamilyTp").value = s.fontFamily;
    if ($("fontFamilyIns").options.length) $("fontFamilyIns").value = s.insightFontFamily;

    $("valStealthStatus").textContent = s.stealthEmbedded
      ? `🔒 Embedded — ${s.stealthEmbedTitle || "window"}`
      : "Not embedded";
  }

  // ── Pill tab bar ─────────────────────────────────────────────────────

  function selectTab(tab) {
    document.querySelectorAll(".tabbar .pill").forEach((btn) => {
      const isActive = btn.dataset.tab === tab;
      btn.classList.toggle("active", isActive);
      btn.setAttribute("aria-selected", isActive ? "true" : "false");
    });
    document.querySelectorAll(".tab-panel").forEach((panel) => {
      panel.classList.toggle("hidden", panel.dataset.panel !== tab);
    });
    localStorage.setItem("onairTab", tab);
    // Windows open/close on the PC independently of onAIr, so refresh the picker every time the
    // presenter switches into this tab rather than relying solely on the once-per-connection
    // fetch in ws.onopen.
    if (tab === "stealth") populateStealthWindows();
  }

  function wireTabs() {
    document.querySelectorAll(".tabbar .pill").forEach((btn) => {
      btn.addEventListener("click", () => selectTab(btn.dataset.tab));
    });
    selectTab(localStorage.getItem("onairTab") || "tp");
  }

  // ── Button wiring ──────────────────────────────────────────────────────

  function wireButtons() {
    document.querySelectorAll("[data-cmd]").forEach((btn) => {
      btn.addEventListener("click", () => send({ op: "command", action: btn.dataset.cmd }));
    });
    document.querySelectorAll("[data-adjust]").forEach((btn) => {
      btn.addEventListener("click", () => send({ op: "adjust", action: btn.dataset.adjust }));
    });
    document.querySelectorAll("[data-scrollmode]").forEach((btn) => {
      btn.addEventListener("click", () => send({ op: "set", field: "ScrollMode", value: btn.dataset.scrollmode }));
    });
    // No dedicated increment/decrement HotkeyActions exist for the AI Insights window's font
    // size/opacity (unlike the TP's own IncreaseFontSize/DecreaseOpacity etc.), so these compute
    // the next absolute value client-side from the last known state and use the "set" op instead.
    document.querySelectorAll("[data-setstep]").forEach((btn) => {
      btn.addEventListener("click", () => {
        if (!lastState) return;
        const [field, stepStr] = btn.dataset.setstep.split(":");
        const step = parseFloat(stepStr);
        const current = field === "InsightFontSize" ? lastState.insightFontSize : lastState.insightOpacity;
        const next = Math.max(0, (current ?? 0) + step);
        send({ op: "set", field, value: field === "InsightFontSize" ? Math.round(next) : next });
      });
    });

    $("btnInsightSend").addEventListener("click", () => {
      const text = $("insightInput").value.trim();
      if (!text) return;
      send({ op: "showInsight", text });
      $("insightInput").value = "";
    });
    $("btnInsightClear").addEventListener("click", () => send({ op: "clearInsight" }));

    $("btnForget").addEventListener("click", () => {
      localStorage.removeItem("onairPin");
      state.pin = "";
      if (state.ws) try { state.ws.close(); } catch { /* ignore */ }
      showGate("");
    });

    $("pinSubmit").addEventListener("click", submitPin);
    $("pinInput").addEventListener("keydown", (e) => { if (e.key === "Enter") submitPin(); });
  }

  // Generic boolean-field toggle: reads the field's current value off the last known state
  // (converting the PascalCase "set" field name to the camelCase state key the same way the
  // server's JSON serializer does — e.g. ShowFollowUpSuggestions -> showFollowUpSuggestions) and
  // sends the negation. Reusable for any future on/off field without new bespoke wiring.
  function toCamel(pascal) {
    return pascal.charAt(0).toLowerCase() + pascal.slice(1);
  }

  function wireToggleFields() {
    document.querySelectorAll("[data-toggle-field]").forEach((btn) => {
      btn.addEventListener("click", () => {
        if (!lastState) return;
        const field = btn.dataset.toggleField;
        const current = !!lastState[toCamel(field)];
        send({ op: "set", field, value: !current });
      });
    });
  }

  function showFieldError(inputEl, errorEl, message) {
    inputEl.classList.toggle("invalid", !!message);
    if (errorEl) errorEl.textContent = message || "";
  }

  // Color swatches always send a known-good hardcoded hex, so these are fire-and-forget.
  function wireColorSwatches() {
    document.querySelectorAll(".swatch-row").forEach((row) => {
      const field = row.dataset.colorField;
      row.querySelectorAll(".swatch").forEach((btn) => {
        btn.addEventListener("click", () => send({ op: "set", field, value: btn.dataset.color }));
      });
    });
  }

  // Custom hex inputs can be typed wrong, so these await the server's validation result (see
  // ControllerWindow.SetRemoteField's HexColorPattern check) and surface any error inline instead
  // of failing silently.
  function wireColorApply() {
    document.querySelectorAll("[data-apply-color-field]").forEach((btn) => {
      btn.addEventListener("click", async () => {
        const field = btn.dataset.applyColorField;
        const input = $(btn.dataset.applyColorInput);
        const errorEl = input.closest(".card").querySelector(".field-error");
        const hex = input.value.trim();
        if (!/^#[0-9A-Fa-f]{6}$/.test(hex)) {
          showFieldError(input, errorEl, "Use format #RRGGBB");
          return;
        }
        const res = await sendRequest({ op: "set", field, value: hex });
        showFieldError(input, errorEl, res.success ? "" : (res.error || "Failed to apply color"));
      });
    });
  }

  function wireFontFamilySelects() {
    document.querySelectorAll("[data-fontfamily-field]").forEach((sel) => {
      sel.addEventListener("change", () => send({ op: "set", field: sel.dataset.fontfamilyField, value: sel.value }));
    });
  }

  // Installed fonts don't change while onAIr is running, so one fetch per connection is enough —
  // both the Teleprompter and AI Insights font-family <select>s share the same installed list.
  async function populateFontFamilies() {
    const res = await sendRequest({ op: "listFonts" });
    if (!res.success) return;
    const fonts = res.data || [];
    [$("fontFamilyTp"), $("fontFamilyIns")].forEach((sel) => {
      const previous = sel.value;
      sel.innerHTML = "";
      fonts.forEach((f) => {
        const opt = document.createElement("option");
        opt.value = f;
        opt.textContent = f;
        sel.appendChild(opt);
      });
      if (previous && fonts.includes(previous)) sel.value = previous;
    });
    if (lastState) {
      $("fontFamilyTp").value = lastState.fontFamily;
      $("fontFamilyIns").value = lastState.insightFontFamily;
    }
  }

  // App Stealth's window list DOES change while onAIr is running (the presenter opens/closes
  // other apps), so unlike populateFontFamilies() this is re-fetched on every visit to the tab
  // (see selectTab) in addition to once on connect.
  async function populateStealthWindows() {
    const sel = $("stealthWindowSelect");
    const previous = sel.value;
    const res = await sendRequest({ op: "listStealthWindows" });
    const windows = res.success ? (res.data || []) : [];

    sel.innerHTML = "";
    const placeholder = document.createElement("option");
    placeholder.value = "";
    placeholder.textContent = windows.length ? "Select a window…" : "No windows found";
    sel.appendChild(placeholder);
    windows.forEach((w) => {
      const opt = document.createElement("option");
      opt.value = w.id;
      opt.textContent = w.display;
      sel.appendChild(opt);
    });
    if (previous && windows.some((w) => w.id === previous)) sel.value = previous;
    $("btnEmbedWindow").disabled = !sel.value;
  }

  function wireStealthTab() {
    const sel = $("stealthWindowSelect");
    sel.addEventListener("change", () => { $("btnEmbedWindow").disabled = !sel.value; });

    $("btnRefreshWindows").addEventListener("click", populateStealthWindows);

    $("btnEmbedWindow").addEventListener("click", async () => {
      const windowId = sel.value;
      if (!windowId) return;
      const res = await sendRequest({ op: "embedStealthWindow", windowId });
      if (!res.success) $("valStealthStatus").textContent = res.error || "Failed to embed";
    });
  }

  function submitPin() {
    const pin = $("pinInput").value.trim();
    if (!pin) return;
    connect(pin);
  }

  // ── Conversation memory modal (Q&A tab's "View Conversation" button) ───
  // Full remembered Q&A history (RemoteState.ConversationHistory) is only fetched/rendered on
  // demand — the compact turn COUNT (#valConvTurns) already renders on every state push, but the
  // actual question/answer text only matters once the presenter explicitly asks to see it.

  function renderConversationModal() {
    const body = $("convModalBody");
    body.innerHTML = "";
    const turns = (lastState && lastState.conversationHistory) || [];

    if (turns.length === 0) {
      const p = document.createElement("p");
      p.className = "hint";
      p.textContent = "No conversation turns remembered yet.";
      body.appendChild(p);
      return;
    }

    turns.forEach((turn) => {
      const wrap = document.createElement("div");
      wrap.className = "conv-turn";
      const q = document.createElement("p");
      q.className = "q";
      q.textContent = `Q: ${turn.question}`;
      const a = document.createElement("p");
      a.className = "a";
      a.textContent = `A: ${turn.answer}`;
      wrap.appendChild(q);
      wrap.appendChild(a);
      body.appendChild(wrap);
    });
  }

  function wireConversationModal() {
    $("btnViewConversation").addEventListener("click", () => {
      renderConversationModal();
      $("convModal").classList.remove("hidden");
    });
    $("btnCloseConvModal").addEventListener("click", () => $("convModal").classList.add("hidden"));
    // Click on the dimmed backdrop (not the card itself) also dismisses it.
    $("convModal").addEventListener("click", (e) => {
      if (e.target === $("convModal")) $("convModal").classList.add("hidden");
    });
  }

  // ── Bootstrap ──────────────────────────────────────────────────────────

  wireTabs();
  wireButtons();
  wireToggleFields();
  wireColorSwatches();
  wireColorApply();
  wireFontFamilySelects();
  wireStealthTab();
  wireConversationModal();

  // A "Copy Link" URL from Settings carries ?pin=... — auto-connect and strip it from the
  // visible address bar (so it doesn't linger in browser history / on-screen during a share).
  const urlPin = new URLSearchParams(location.search).get("pin");
  if (urlPin) {
    history.replaceState({}, "", location.pathname);
    connect(urlPin);
  } else if (state.pin) {
    connect(state.pin);
  } else {
    showGate("");
  }
})();
