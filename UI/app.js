const host = window.chrome && window.chrome.webview ? window.chrome.webview : null;
const send = message => host ? host.postMessage(message) : preview(message);
const $ = id => document.getElementById(id);

const STAGES = [
	{ id: "export", name: "Rips the game", text: "AssetRipper exports Valheim into a Unity project" },
	{ id: "prepare", name: "Fixes shaders and DLLs", text: "Gets the game's shaders working in the editor and puts the game's own DLLs back" },
	{ id: "packages", name: "Swaps in Unity's packages", text: "Replaces the game's copies of Unity's DLLs with the real packages", switch: "packages", offText: "Only UI and TextMeshPro become Unity's package, the game's other DLLs stay as they are" },
	{ id: "husks", name: "Cleans up leftovers", text: "Removes broken objects, dev files and the game's bundle names" },
	{ id: "dedupe", name: "Merges duplicates", text: "Turns AssetRipper's _0, _1 copies back into one asset", option: "dedupe" },
	{ id: "import", name: "Imports into Unity", text: "Unity imports everything once" },
	{ id: "check", name: "Checks shaders", text: "Unity compiles every shader to make sure they work" },
	{ id: "template", name: "Makes a template", text: "Copies the project without the game's assets, for building mods", option: "template" },
];

const OPTIONS = [
	{ section: "The project" },
	{ id: "yaml", name: "YAML shaders", on: true, help: "On: AssetRipper exports the shaders as YAML assets. The rip then copies each pass's compiled program into the slots the editor reads, so the game's shaders render in the editor, game lighting included.\n\nOff: Dummy shaders. Materials won't look right and particles turn white. The rip is a little faster." },
	{ id: "dedupe", name: "Merge duplicate copies", on: true, help: "AssetRipper exports an asset once for every bundle it is in, as `name_0`, `name_1`...\n\nOn: keeps one copy of each asset (same internal name and content) and points all references to it. If a copy lost its texture (white material), the textured copy is kept. Real names that end in a number, like `TarPit1_1`, are not touched.\n\nAbout 2,000 copies on Valheim 1.0. They are moved to `removed_plugins\\duplicates`." },
	{ id: "packages", name: "Use Unity's own packages", on: true, help: "The game includes Unity packages as DLLs: TextMeshPro, UI, Input System, Timeline, Burst, Collections, Newtonsoft and more.\n\nOn: installs the real packages and points every component at the package scripts (about 15,000 references). You get the source and the editor tools: Timeline window, Input Debugger, FBX Exporter. Also adds the AssetBundle Browser (Window > AssetBundle Browser) when Git is installed.\n\nCollections is installed without its code generator. The old DLLs are moved to `removed_plugins\\package_dlls`, and the game's own DLLs get Auto Reference off so the packages don't clash with them.\n\nOff: the game's DLLs stay as they are, and Unity's packages with the same names are left out of the project, so the two never clash. UI and TextMeshPro still become Unity's package, since the editor's UI tools come with it." },
	{ id: "template", name: "Make a template project", on: false, help: "Also makes a clean project next to this one called `<name>Template`. Same settings, packages and DLLs, no game assets. The DLLs are in `Assets/_Template/Assemblies`, and `UILineRenderer` and `Line` in its `Scripts` folder.\n\nUse it for your mods and export what you need from the rip into it. Untick the game DLLs (in `Plugins`) when exporting, the template already has them.\n\nYou can turn this on later and press Carry on to make one for a finished rip." },
];

const THEMES = [
	["", "Default", "#E0A25A"], ["theme-meadows", "Meadows", "#7AA845"], ["theme-blackforest", "Black Forest", "#6E8C54"],
	["theme-swamp", "Swamp", "#8FA83D"], ["theme-mountains", "Mountains", "#80B5D4"], ["theme-plains", "Plains", "#E0B23D"],
	["theme-mistlands", "Mistlands", "#4FC4BF"], ["theme-ashlands", "Ashlands", "#E66129"], ["theme-deepnorth", "Deep North", "#619EE0"],
	null, ["theme-classic", "Classic", "#0088CC"], ["theme-sage", "Sage", "#5E7D46"], ["theme-cream", "Cream", "#BC703A"],
];

const state = { found: {}, options: {}, project: "", running: false, stages: {}, logOpen: false };
OPTIONS.filter(o => o.id).forEach(o => state.options[o.id] = o.on);

const icon = (name, extra = "") => `<svg class="icon ${extra}"><use href="#i-${name}"/></svg>`;
const escape = text => String(text ?? "").replace(/[&<>"]/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" })[c]);

function renderFound() {
	const marks = { ok: "check", warn: "alert", missing: "x", optional: "dash" };
	$("found").innerHTML = Object.values(state.found).map(f => `
		<div class="found is-${f.state}">
			<div class="found-mark">${icon(marks[f.state] || "dash")}</div>
			<div class="found-text">
				<div class="found-label"><span class="found-name">${escape(f.label)}</span><span class="found-tag">${escape(f.tag || "")}</span></div>
				<div class="found-value" title="${escape(f.value)}"><bdi>${escape(f.value)}</bdi></div>
			</div>
			${f.browse ? `<button class="found-pick" data-browse="${f.id}" title="Choose">${icon("folder")}</button>` : ""}
		</div>`).join("");
}

function optionLocked(o) {
	if (state.running || state.ripOptions && o.id !== "template") return true;
	return o.needs && !state.options[o.needs];
}

function renderOptions() {
	$("options").innerHTML = OPTIONS.map(o => {
		if (o.section) return `<div class="option-section">${escape(o.section)}</div>`;
		const locked = optionLocked(o);
		const on = state.options[o.id] && !locked || state.options[o.id] && (state.running || !!state.ripOptions);
		return `<div class="option ${on ? "" : "is-off"} ${locked ? "is-locked" : ""} ${o.nested ? "is-nested" : ""}" data-option="${o.id}">
			<div class="switch"></div>
			<div class="option-head"><span class="option-name">${escape(o.name)}</span><span class="help" data-help="${o.id}">?</span></div>
		</div>`;
	}).join("");
}

function effectiveOptions() {
	const out = {};
	OPTIONS.filter(o => o.id).forEach(o => out[o.id] = !!state.options[o.id] && !(o.needs && !state.options[o.needs]));
	return out;
}

function renderStages() {
	const options = effectiveOptions();
	$("stages").innerHTML = STAGES.map((s, i) => {
		const st = state.stages[s.id] || {};
		const skipped = s.option && !options[s.option];
		const status = st.state || (skipped ? "skipped" : "waiting");
		const label = { waiting: "Waiting", running: "Working", done: "Done", failed: "Failed", skipped: "Skipped" }[status];
		const mark = { waiting: "dash", running: "loader", done: "check", failed: "alert", skipped: "skip" }[status];
		const progress = st.progress;
		return `<div class="stage is-${status}" id="stage-${s.id}">
			<div class="stage-number">${i + 1}</div>
			<div>
				<div class="stage-name">${escape(s.name)}</div>
				<div class="stage-text">${escape(s.switch && !options[s.switch] ? s.offText : s.text)}</div>
				<div class="stage-detail">${escape(st.detail || "")}</div>
				<div class="stage-bar"><div class="stage-fill ${progress == null ? "is-endless" : ""}" style="width:${(progress || 0) * 100}%"></div></div>
			</div>
			<div class="stage-state">${st.time ? `<span>${escape(st.time)}</span>` : ""}${icon(mark)}<span>${label}</span></div>
		</div>`;
	}).join("");
	const active = STAGES.filter(s => !(s.option && !options[s.option]));
	const done = active.filter(s => (state.stages[s.id] || {}).state === "done").length;
	const running = active.find(s => (state.stages[s.id] || {}).state === "running");
	const part = running ? (state.stages[running.id].progress || 0) : 0;
	$("overall").style.width = `${(done + part) / active.length * 100}%`;
	$("overall-count").textContent = `${done} / ${active.length}`;
	$("overall-row").classList.toggle("is-shown", state.running || Object.values(state.stages).some(s => s.state === "done" || s.state === "failed"));
	if (running && running.id !== state.shown) {
		state.shown = running.id;
		$("stage-" + running.id).scrollIntoView({ block: "nearest", behavior: "smooth" });
	}
}

function renderDock() {
	const missing = Object.values(state.found).filter(f => f.state === "missing" && f.required);
	const go = $("go");
	go.classList.toggle("is-stop", state.running);
	const options = effectiveOptions();
	const active = STAGES.filter(s => !(s.option && !options[s.option]));
	const finished = !state.running && active.every(s => (state.stages[s.id] || {}).state === "done");
	state.finished = finished;
	go.disabled = !state.running && (missing.length > 0 || !state.project);
	$("go-text").textContent = state.running ? "Stop" : finished ? "Repair" : Object.values(state.stages).some(s => s.state === "done") ? "Carry on" : "Rip it";
	go.querySelector("use").setAttribute("href", state.running ? "#i-x" : finished ? "#i-wrench" : "#i-play");
	go.classList.toggle("is-repair", finished);
	if (!state.running && missing.length) {
		$("overall-text").textContent = "Missing: " + missing.map(m => m.label).join(", ");
		$("overall-row").classList.add("is-shown");
	}
	renderOptions();
}

function log(text, level) {
	const line = document.createElement("div");
	line.className = "log-line" + (level ? " is-" + level : "");
	line.textContent = text;
	const box = $("log");
	const stick = box.scrollTop + box.clientHeight >= box.scrollHeight - 4;
	box.appendChild(line);
	if (box.childElementCount > 4000) box.firstChild.remove();
	if (stick) box.scrollTop = box.scrollHeight;
}

function receive(m) {
	switch (m.type) {
		case "found":
			m.items.forEach(item => state.found[item.id] = item);
			renderFound(); renderStages(); renderDock();
			break;
		case "project":
			state.project = m.path;
			state.ripOptions = m.ripOptions || null;
			if (state.ripOptions) {
				if (!state.chosen) state.chosen = Object.assign({}, state.options);
				const template = state.options.template;
				Object.assign(state.options, state.ripOptions);
				state.options.template = template;
			} else if (state.chosen) {
				state.options = state.chosen;
				state.chosen = null;
			}
			renderStages();
			$("project-path").innerHTML = `<bdi>${escape(m.path || "Pick a folder")}</bdi>`;
			$("project-note").textContent = m.note || "";
			renderDock();
			break;
		case "stage": {
			const update = Object.assign({}, m);
			delete update.type;
			if (m.keep) delete update.progress;
			if (update.detail == null) delete update.detail;
			if (update.time == null) delete update.time;
			state.stages[m.id] = Object.assign(state.stages[m.id] || {}, update);
			renderStages();
			break;
		}
		case "demo":
			if (m.log) drawer(true);
			if (m.mode === "finished" || m.mode === "done") demoFinished(m.mode === "done");
			else fakeRun(effectiveOptions(), m.speed || 1);
			break;
		case "autostart":
			send({ type: "start", options: effectiveOptions() });
			break;
		case "saved":
			Object.keys(m.options || {}).forEach(k => { if (k in state.options) state.options[k] = m.options[k]; });
			renderStages(); renderDock();
			break;
		case "progress":
			state.stages = {};
			m.stages.forEach(id => state.stages[id] = { state: "done", detail: "Done in an earlier run" });
			renderStages(); renderDock();
			break;
		case "summary":
			$("overall-text").textContent = m.small ? `${m.big} · ${m.small}` : m.big;
			break;
		case "running":
			state.running = m.value;
			if (m.value) { $("result").classList.remove("is-open"); }
			renderStages(); renderDock();
			break;
		case "log":
			log(m.text, m.level);
			break;
		case "result":
			$("result-title").textContent = m.title;
			$("result-text").textContent = m.text;
			$("result-facts").innerHTML = (m.facts || []).map(([n, l]) => `<div><div class="fact-number">${escape(n)}</div><div class="fact-label">${escape(l)}</div></div>`).join("");
			$("result").classList.add("is-open");
			break;
		case "version":
			$("version").textContent = m.text;
			break;
	}
}
if (host) host.addEventListener("message", e => receive(e.data));

function setTheme(cls) {
	document.documentElement.className = cls;
	try { localStorage.setItem("theme", cls); } catch (e) { }
	renderThemes();
}
function renderThemes() {
	$("themes").innerHTML = THEMES.map(t => t ? `<button data-theme="${t[0]}" class="${document.documentElement.className === t[0] ? "is-active" : ""}"><span class="swatch" style="background:${t[2]}"></span>${t[1]}</button>` : `<div class="popover-section">More</div>`).join("");
}

document.addEventListener("click", e => {
	const t = e.target.closest("[data-link],[data-option],[data-browse],[data-window],[data-open],[data-theme],#theme-button,#go,#log-button,#project-browse,#drawer-close,#drawer-file");
	if (e.target.closest(".help")) return;
	if (!t) { $("themes").classList.remove("is-open"); return; }
	if (t.dataset.option !== undefined) {
		const o = OPTIONS.find(x => x.id === t.dataset.option);
		if (optionLocked(o)) return;
		state.options[o.id] = !state.options[o.id];
		renderStages(); renderDock();
		send({ type: "options", options: effectiveOptions() });
	} else if (t.dataset.link) send({ type: "link", id: t.dataset.link });
	else if (t.dataset.browse) send({ type: "browse", id: t.dataset.browse });
	else if (t.id === "project-browse") send({ type: "browse", id: "project" });
	else if (t.dataset.window) send({ type: "window", action: t.dataset.window });
	else if (t.dataset.open) send({ type: "open", what: t.dataset.open });
	else if (t.dataset.theme !== undefined) { setTheme(t.dataset.theme); $("themes").classList.remove("is-open"); }
	else if (t.id === "theme-button") { renderThemes(); $("themes").classList.toggle("is-open"); }
	else if (t.id === "log-button") drawer(!state.logOpen);
	else if (t.id === "drawer-close") drawer(false);
	else if (t.id === "drawer-file") send({ type: "open", what: "log" });
	else if (t.id === "go") send(state.running ? { type: "stop" } : state.finished ? { type: "repair" } : { type: "start", options: effectiveOptions() });
});
document.addEventListener("keydown", e => {
	if (e.key === "Escape" && state.logOpen) drawer(false);
});

function drawer(open) {
	state.logOpen = open;
	$("drawer").classList.toggle("is-open", open);
	$("log-button").classList.toggle("is-on", open);
	if (open) $("log").scrollTop = $("log").scrollHeight;
}

function richText(text) {
	return text.split("\n\n").map(p => "<p>" + escape(p).replace(/`([^`]+)`/g, "<code>$1</code>") + "</p>").join("");
}

document.addEventListener("mouseover", e => {
	const badge = e.target.closest(".help");
	const tip = $("tip");
	if (!badge) { tip.classList.remove("is-open"); return; }
	const o = OPTIONS.find(x => x.id === badge.dataset.help);
	$("tip-title").textContent = o.name;
	$("tip-text").innerHTML = richText(o.help);
	const r = badge.getBoundingClientRect();
	tip.classList.add("is-open");
	const top = Math.max(56, Math.min(r.top - 14, window.innerHeight - tip.offsetHeight - 44));
	tip.style.left = Math.max(r.right + 14, document.querySelector(".rail").getBoundingClientRect().right + 12) + "px";
	tip.style.top = top + "px";
});
$("drag").addEventListener("mousedown", e => { if (!e.target.closest("button") && e.button === 0) send({ type: "window", action: "drag" }); });
$("drag").addEventListener("dblclick", e => { if (!e.target.closest("button")) send({ type: "window", action: "max" }); });
$("result").addEventListener("click", e => { if (e.target.id === "result") $("result").classList.remove("is-open"); });

try { document.documentElement.className = localStorage.getItem("theme") || ""; } catch (e) { }
if (host) $("window").classList.add("framed");
renderFound(); renderOptions(); renderStages(); renderDock();
send({ type: "ready" });

const DEMO_LOG = [
	["== export"], ["Loading C:\\Program Files (x86)\\Steam\\steamapps\\common\\Valheim"], ["Export : Saving Unity.Collections"],
	["Exported to D:\\Tools\\AssetRipper\\ValheimEditor", "good"],
	["== prepare"], ["89 YAML shaders, 89 fixed", "good"], ["EyeHistogram replaced with Unity's post-processing v1 source"], ["44 game DLLs restored and enabled in the editor"],
	["== packages"], ["Collections 2.6.8 installed without its code generator"],
	["14,920 references in 315 files updated. 20 DLLs moved to removed_plugins\\package_dlls", "good"],
	["== husks"], ["c4210710: 7 husks, 7 preload entries, verified"], ["d59cfac: 6 husks, 6 preload entries, verified"], ["13 script-less objects removed from the bundles", "good"],
	["BonemawSerpent_breath_aoe.prefab: 1 empty component(s) removed"], ["fallenvalkyrie_poisonbreath_aoe.prefab: 1 empty component(s) removed"], ["Fenring_attack_flames_aoe.prefab: 1 empty component(s) removed"],
	["3 empty components removed. 46 unused dev files moved to removed_plugins\\husks", "good"],
	["Asset bundle names cleared from 37,269 assets, so building a mod's bundles builds only the mod's", "good"],
	["== dedupe"], ["References repointed in 1,293 files"], ["Round 1: 1,993 copies merged, 2 renamed back"], ["References repointed in 1 files"], ["Round 2: 7 copies merged, 0 renamed back"],
	["2,000 duplicates merged, 273 of them white materials", "good"],
	["== import"], ["Unity imported the project (38,192 assets), no compile errors", "good"],
	["== check"], ["All 88 shaders compile.", "good"],
];
const DEMO_STAGES = {
	export: ["6 min", "Exported to the project folder"], prepare: ["30 s", "44 game DLLs restored and enabled in the editor"],
	packages: ["2 min", "14,920 references in 315 files updated"], husks: ["2 min", "46 unused dev files moved to removed_plugins\\husks"],
	dedupe: ["6 min", "2,000 duplicates merged, 273 of them white materials"], import: ["20 min", "Unity imported the project, no compile errors"],
	check: ["1 min", "All 88 shaders compile."],
};
const DEMO_PROJECT = "D:\\Tools\\AssetRipper\\ValheimEditor";

function demoFinished(result) {
	DEMO_LOG.forEach(([text, level]) => log(text, level));
	Object.entries(DEMO_STAGES).forEach(([id, [time, detail]]) => state.stages[id] = { state: "done", progress: 1, time, detail });
	receive({ type: "project", path: DEMO_PROJECT, note: "This folder has a finished rip. Choose another folder for a new one." });
	renderStages(); renderDock();
	$("overall-text").textContent = "Done in 37 minutes";
	if (result) receive({ type: "result", title: "Done", text: "All stages finished. Unity is opening the project.", facts: [["88", "shaders compile"], ["14,920", "references kept"], ["2,000", "duplicates merged"]] });
}

function preview(m) {
	if (m.type === "ready") {
		const query = new URLSearchParams(location.search);
		setTimeout(() => {
			receive({ type: "found", items: [
				{ id: "git", label: "Git", tag: "for the AssetBundle Browser", value: "C:\\Program Files\\Git\\cmd\\git.exe", state: "ok" },
				{ id: "unity", label: "Unity 6000.0.75f1", tag: "same as the game", value: "C:\\Program Files\\Unity\\Hub\\Editor\\6000.0.75f1\\Editor\\Unity.exe", state: "ok", browse: true, required: true },
				{ id: "ripper", label: "AssetRipper", tag: "2.0.0", value: "D:\\Tools\\AssetRipper\\AssetRipper.GUI.Free.exe", state: "ok", browse: true, required: true },
				{ id: "game", label: "Valheim", tag: "Unity 6000.0.75f1", value: "C:\\Program Files (x86)\\Steam\\steamapps\\common\\Valheim", state: "ok", browse: true, required: true },
			] });
			receive({ type: "project", path: DEMO_PROJECT, note: "D: has 412 GB free. The project needs about 20 GB." });
			receive({ type: "version", text: query.has("demo") ? "ValheimRipper 1.0.0" : "ValheimRipper 1.0.0 (preview)" });
			if (query.has("framed")) $("window").classList.add("framed");
			if (query.has("zoom")) document.documentElement.style.zoom = query.get("zoom");
			if (query.has("log")) drawer(true);
			if (query.get("demo") === "finished" || query.get("demo") === "done") demoFinished(query.get("demo") === "done");
		}, 250);
	} else if (m.type === "start") fakeRun(m.options);
	else if (m.type === "stop") { stopFake = true; }
}

let stopFake = false;
async function fakeRun(options, slow = 1) {
	stopFake = false;
	receive({ type: "running", value: true });
	const wait = ms => new Promise(r => setTimeout(r, ms));
	const lines = {
		export: ["Loading the game files", "Exporting 12,410 of 38,097 assets", "Exporting 31,880 of 38,097 assets", "Exported to the project folder"],
		prepare: ["89 YAML shaders, 89 fixed", "44 game DLLs restored and enabled in the editor"],
		packages: ["Downloading the packages", "Installing Collections", "Updating references", "14,920 references in 315 files updated"],
		husks: ["Checking bundle 640 of 1,598", "13 script-less objects removed from the bundles", "46 unused dev files moved to removed_plugins\\husks"],
		dedupe: ["Finding duplicates", "Second pass", "2,000 duplicates merged, 273 of them white materials"],
		import: ["Imported 9,250 of about 41,500 assets", "Imported 30,750 of about 41,500 assets", "Unity imported the project, no compile errors"],
		check: ["Compiling every shader", "All 88 shaders compile."],
		template: ["Copying settings, packages and DLLs", "Template project made (8 MB)"],
	};
	const times = { export: "4 min", prepare: "30 s", packages: "21 s", husks: "2 min", dedupe: "6 min", import: "19 min", check: "1 min", template: "2 s" };
	for (const s of STAGES) {
		if (stopFake) break;
		if (s.option && !options[s.option]) continue;
		receive({ type: "stage", id: s.id, state: "running", progress: 0, detail: "" });
		let shown = "";
		for (let i = 1; i <= 10 && !stopFake; i++) {
			await wait(180 * slow);
			const line = lines[s.id][Math.floor((i - 1) / 10 * lines[s.id].length)];
			receive({ type: "stage", id: s.id, progress: i / 10, detail: line });
			receive({ type: "summary", big: s.name, small: line });
			if (line !== shown) { receive({ type: "log", text: `[${s.id}] ${line}` }); shown = line; }
		}
		receive({ type: "stage", id: s.id, state: stopFake ? "failed" : "done", progress: 1, time: times[s.id] });
	}
	receive({ type: "running", value: false });
	if (stopFake) { receive({ type: "summary", big: "Stopped", small: "Carry on continues from here." }); return; }
	receive({ type: "summary", big: "Done in 33 minutes", small: DEMO_PROJECT });
	receive({ type: "log", text: "All stages finished.", level: "good" });
	receive({ type: "result", title: "Done", text: "All stages finished. Unity is opening the project.", facts: [["88", "shaders compile"], ["14,920", "references kept"], ["2,000", "duplicates merged"]] });
}
