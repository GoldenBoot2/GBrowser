// GBrowser: runs INSIDE the DevTools window (its own page, devtools://devtools/...), never in a website.
// 1. Sites that block DevTools run `debugger;` in a loop (plain, through Function / eval), so the page freezes
//    "Paused in debugger" as soon as DevTools opens. When a pause comes from a `debugger` statement in the site's code
//    (reason "other", no breakpoint of yours hit, the paused position is the `debugger` keyword), DevTools resumes at
//    once instead of showing the pause: no frozen page, no jump to Sources, and the pause is too short to measure.
//    Your own breakpoints, "pause on exceptions" and the pause button still stop as usual.
// 2. Commands GBrowser asks for: "console" (Ctrl+Shift+J), "inspect" (Ctrl+Shift+C), "elements".
// Called as (skip: boolean, action: string) => Promise<string>. Tested on Edge DevTools 157 (21-11-2026).
(async (skip, action) => {
  const [S, U] = await Promise.all([import('./core/sdk/sdk.js'), import('./ui/legacy/legacy.js')]);
  const DM = S.DebuggerModel.DebuggerModel;
  globalThis.__gbSkipDebugger = !!skip;

  // is the paused position the `debugger` keyword? (location: scriptId, lineNumber, columnNumber; 0-based)
  // each script's text is fetched once (anti-debugging loops pause in the same few scripts again and again)
  const sources = globalThis.__gbSources || (globalThis.__gbSources = new Map());
  async function atDebuggerKeyword(model, loc) {
    try {
      if (!loc || !loc.scriptId) return false;
      const key = model.target().id() + '|' + loc.scriptId;
      let src = sources.get(key);
      if (src === undefined) {
        const r = await model.agent.invoke_getScriptSource({ scriptId: loc.scriptId });
        src = r && typeof r.scriptSource === 'string' ? r.scriptSource : null;
        if (sources.size > 500) sources.clear();
        sources.set(key, src);
      }
      if (typeof src !== 'string') return false;
      // positions count from the top of the document; a script inside an HTML page starts further down (its offset)
      const script = model.scriptForId(loc.scriptId);
      const lineOff = script ? script.lineOffset || 0 : 0, colOff = script ? script.columnOffset || 0 : 0;
      const line = (loc.lineNumber || 0) - lineOff;
      const col = (loc.columnNumber || 0) - (line === 0 ? colOff : 0);
      if (line < 0 || col < 0) return false;
      let i = 0;
      for (let k = 0; k < line; k++) { i = src.indexOf('\n', i) + 1; if (i === 0) return false; }
      return /^debugger\b/.test(src.substr(i + col, 12));
    } catch (e) { return false; }
  }

  if (!DM.prototype.__gbPatched) {
    const orig = DM.prototype.pausedScript;
    DM.prototype.pausedScript = async function (callFrames, reason, auxData, breakpointIds, ...rest) {
      if (globalThis.__gbSkipDebugger && reason === 'other' && !(breakpointIds && breakpointIds.length) && callFrames && callFrames[0]
          && await atDebuggerKeyword(this, callFrames[0].location)) {
        globalThis.__gbSkipped = (globalThis.__gbSkipped || 0) + 1;
        this.agent.invoke_resume({ terminateOnResume: false });
        return;
      }
      return orig.call(this, callFrames, reason, auxData, breakpointIds, ...rest);
    };
    DM.prototype.__gbPatched = true;
  }

  // DevTools may have opened on a page already stopped at such a statement: let it run on
  if (globalThis.__gbSkipDebugger)
    for (const m of S.TargetManager.TargetManager.instance().models(DM)) {
      const d = m.isPaused() && m.debuggerPausedDetails();
      if (d && d.reason === 'other' && !(d.breakpointIds && d.breakpointIds.length) && d.callFrames[0]
          && await atDebuggerKeyword(m, d.callFrames[0].location())) m.resume();
    }

  const views = U.ViewManager.ViewManager.instance();
  const actions = U.ActionRegistry.ActionRegistry.instance();
  if (action === 'console') await views.showView('console');
  else if (action === 'elements') await views.showView('elements');
  else if (action === 'inspect') {
    await views.showView('elements');
    if (actions.hasAction('elements.toggle-element-search')) await actions.getAction('elements.toggle-element-search').execute();
  }
  return 'ok ' + (globalThis.__gbSkipped || 0);
})
