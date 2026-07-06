// State
let activeSaveId = null;
let activeSettlerId = null;
let settlersCache = [];
let autoRefreshInterval = null;
let isStreaming = false;
let streamInterval = null;

// DOM Elements
const dbStateEl = document.getElementById("db-state");
const dbPathEl = document.getElementById("db-path");
const p2StateEl = document.getElementById("p2-state");
const p2DetailEl = document.getElementById("p2-detail");
const refreshBtn = document.getElementById("refresh-btn");
const seedDemoBtn = document.getElementById("seed-demo-btn");
const savesStrip = document.getElementById("saves-strip");
const settlersBody = document.getElementById("settlers-body");
const settlerCount = document.getElementById("settler-count");

// Helper to escape HTML strings
const esc = (val) => String(val ?? "").replace(/[&<>"']/g, (ch) => ({
  "&": "&amp;",
  "<": "&lt;",
  ">": "&gt;",
  '"': "&quot;",
  "'": "&#39;",
}[ch]));

// Helper to format Date
const formatDate = (dateStr) => {
  if (!dateStr) return "";
  try {
    const d = typeof dateStr === "number" ? new Date(dateStr * 1000) : new Date(dateStr);
    return d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' }) + ' ' + d.toLocaleDateString([], { month: 'short', day: 'numeric' });
  } catch (e) {
    return dateStr;
  }
};

const memoryCategoryLabels = {
  conversations: "Conversations",
  secrets: "Secrets",
  events: "Events",
  promises: "Promises",
  betrayals: "Betrayals",
  favors: "Favors",
  deaths: "Deaths",
  relationship_milestones: "Relationships",
  decisions: "Decisions",
  health: "Health",
  mood: "Mood",
  danger: "Danger",
  colony: "Colony",
  system: "System",
};

function memoryTypeClass(category) {
  if (category === "danger" || category === "betrayals" || category === "deaths") return "incident-danger";
  if (category === "decisions") return "incident-decision";
  if (category === "health") return "incident-health";
  if (category === "mood" || category === "relationship_milestones") return "incident-mood";
  if (category === "secrets" || category === "promises") return "incident-secret";
  return "";
}

// Initial Load
document.addEventListener("DOMContentLoaded", () => {
  initTabs();
  initRangeSliders();
  fetchHealth();
  fetchSaves();
  
  // Refresh button
  refreshBtn.addEventListener("click", () => {
    fetchHealth();
    fetchSaves();
    if (activeSaveId) {
      loadSaveData(activeSaveId);
    }
  });

  // Seed Demo button
  seedDemoBtn.addEventListener("click", async () => {
    seedDemoBtn.disabled = true;
    seedDemoBtn.textContent = "Seeding...";
    try {
      const res = await fetch("/api/universe/seed", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ save_id: "demo_save" })
      });
      const data = await res.json();
      if (data.ok) {
        activeSaveId = "demo_save";
        await fetchSaves();
        loadSaveData("demo_save");
      }
    } catch (e) {
      alert("Error seeding database: " + e.message);
    } finally {
      seedDemoBtn.disabled = false;
      seedDemoBtn.textContent = "Seed Demo Save";
    }
  });

  // Sim Form Submit
  const simForm = document.getElementById("sim-form");
  simForm.addEventListener("submit", handleSimulateSubmit);

  // Initialize Game Control Bridge
  initGameBridge();

  // Setup periodic refresh
  autoRefreshInterval = setInterval(() => {
    fetchHealth();
    if (activeSaveId) {
      silentRefresh();
    }
  }, 5000);
});

// Setup tab switches
function initTabs() {
  const tabs = document.querySelectorAll(".tab-btn");
  tabs.forEach(tab => {
    tab.addEventListener("click", () => {
      // Deactivate all
      document.querySelectorAll(".tab-btn").forEach(t => t.classList.remove("active"));
      document.querySelectorAll(".tab-content").forEach(c => c.classList.remove("active"));
      
      // Activate selected
      tab.classList.add("active");
      const targetId = tab.dataset.tab;
      document.getElementById(targetId).classList.add("active");

      // Game stream management hook
      if (targetId === "game-control-tab") {
        refreshGameFrame();
      } else {
        stopGameStream();
      }
    });
  });
}

// Range slider dynamic display
function initRangeSliders() {
  const sliders = ["food", "rest", "mood"];
  sliders.forEach(s => {
    const slider = document.getElementById(`sim-${s}`);
    const display = document.getElementById(`val-${s}`);
    if (slider && display) {
      slider.addEventListener("input", () => {
        display.textContent = slider.value;
      });
    }
  });
}

// API: Fetch Health
async function fetchHealth() {
  try {
    const res = await fetch("/health");
    const health = await res.json();
    
    // DB badge
    if (health.database_exists) {
      dbStateEl.textContent = "Connected";
      dbStateEl.className = "status-badge ok";
    } else {
      dbStateEl.textContent = "Not Created";
      dbStateEl.className = "status-badge warn";
    }
    dbPathEl.textContent = health.database_path || "";
    
    // Player2 badge
    const p2 = health.player2 || {};
    if (p2.online) {
      p2StateEl.textContent = "Online";
      p2StateEl.className = "status-badge ok";
      p2DetailEl.textContent = `port ${p2.port} (${p2.version})`;
    } else {
      p2StateEl.textContent = "Offline";
      p2StateEl.className = "status-badge bad";
      p2DetailEl.textContent = `port ${p2.port || 4315}`;
    }
  } catch (e) {
    dbStateEl.textContent = "Offline";
    dbStateEl.className = "status-badge bad";
    p2StateEl.textContent = "Offline";
    p2StateEl.className = "status-badge bad";
  }
}

// API: Fetch Save Games list
async function fetchSaves() {
  try {
    const res = await fetch("/api/memory/saves");
    const data = await res.json();
    const saves = data.saves || [];
    
    if (saves.length === 0) {
      savesStrip.innerHTML = '<span class="dim">No active saves found in DB. Click Seed Demo Save to begin.</span>';
      return;
    }

    savesStrip.innerHTML = saves.map(s => `
      <span class="save-chip ${s === activeSaveId ? 'active' : ''}" data-save-id="${esc(s)}">
        📁 <strong>${esc(s)}</strong>
      </span>
    `).join("");

    // Bind clicks
    document.querySelectorAll(".save-chip").forEach(chip => {
      chip.addEventListener("click", () => {
        const saveId = chip.dataset.saveId;
        document.querySelectorAll(".save-chip").forEach(c => c.classList.remove("active"));
        chip.classList.add("active");
        activeSaveId = saveId;
        activeSettlerId = null;
        loadSaveData(saveId);
      });
    });

    // Auto-select first save if none active
    if (!activeSaveId && saves.length > 0) {
      activeSaveId = saves[0];
      const firstChip = document.querySelector(`.save-chip[data-save-id="${CSS.escape(saves[0])}"]`);
      if (firstChip) firstChip.classList.add("active");
      loadSaveData(saves[0]);
    }
  } catch (e) {
    savesStrip.innerHTML = `<span class="dim">Error fetching saves: ${esc(e.message)}</span>`;
  }
}

// Load save data
function loadSaveData(saveId) {
  fetchSettlers(saveId);
  fetchRelationships(saveId);
  fetchIncidents(saveId);
}

function renderKeyValueRows(rows, keyField, valueField, emptyText, valueFormatter = null) {
  if (!rows || rows.length === 0) return `<div class="dim">${esc(emptyText)}</div>`;
  return rows.map(row => {
    const value = valueFormatter ? valueFormatter(row) : row[valueField];
    return `
      <div class="sheet-row">
        <span>${esc(row[keyField])}</span>
        <strong>${esc(value)}</strong>
      </div>
    `;
  }).join("");
}

// API: Fetch Settlers for selected save
async function fetchSettlers(saveId) {
  try {
    const res = await fetch(`/api/memory/npcs?save_id=${encodeURIComponent(saveId)}`);
    const data = await res.json();
    const npcs = data.npcs || [];
    settlersCache = npcs;

    settlerCount.textContent = npcs.length;

    if (npcs.length === 0) {
      settlersBody.innerHTML = '<tr><td colspan="4" class="dim">No settlers bound to this save in database.</td></tr>';
      activeSettlerId = null;
      document.getElementById("npc-detail-title").textContent = "Select a settler to view memories";
      document.getElementById("npc-summary-section").innerHTML = "";
      document.getElementById("npc-personal-file").innerHTML = '<div class="dim">No personal file loaded.</div>';
      document.getElementById("memory-category-strip").innerHTML = "";
      document.getElementById("typed-memories-list").innerHTML = '<div class="dim">No typed memories.</div>';
      document.getElementById("dialogue-title").textContent = "Select a settler to view dialogue state";
      document.getElementById("dialogue-body").innerHTML = '<div class="dim">No dialogue state loaded.</div>';
      document.getElementById("character-sheet-title").textContent = "Select a settler to view character sheet";
      document.getElementById("character-sheet-body").innerHTML = '<div class="dim">No character sheet loaded.</div>';
      return;
    }

    if (!npcs.some(n => n.settler_id === activeSettlerId)) {
      activeSettlerId = null;
    }

    settlersBody.innerHTML = npcs.map(n => {
      const p = n.pressures || {};
      const isSelected = n.settler_id === activeSettlerId;
      
      const name = n.name || n.description || n.settler_id;
      const prof = n.role || "Worker";

      return `
        <tr class="interactive ${isSelected ? 'selected' : ''}" data-settler-id="${esc(n.settler_id)}">
          <td>
            <strong>${esc(name)}</strong>
            <div class="settler-subline">${Number(n.memories_count || 0)} memories · ${Number(n.secrets_count || 0)} secrets</div>
          </td>
          <td>${esc(prof)}</td>
          <td>
            <div class="pressure-progress-bar">
              <div class="bar-label"><span>Hunger</span><span>${Math.round(p.hunger_pressure * 100)}%</span></div>
              <div class="progress-track"><div class="progress-fill hunger" style="width: ${Math.min(100, p.hunger_pressure * 100)}%"></div></div>
            </div>
            <div class="pressure-progress-bar">
              <div class="bar-label"><span>Injury</span><span>${Math.round(p.injury_pressure * 100)}%</span></div>
              <div class="progress-track"><div class="progress-fill injury" style="width: ${Math.min(100, p.injury_pressure * 100)}%"></div></div>
            </div>
            <div class="pressure-progress-bar">
              <div class="bar-label"><span>Exhaustion</span><span>${Math.round(p.exhaustion_pressure * 100)}%</span></div>
              <div class="progress-track"><div class="progress-fill exhaustion" style="width: ${Math.min(100, p.exhaustion_pressure * 100)}%"></div></div>
            </div>
            <div class="pressure-progress-bar">
              <div class="bar-label"><span>Mood Stress</span><span>${Math.round(p.mood_pressure * 100)}%</span></div>
              <div class="progress-track"><div class="progress-fill mood" style="width: ${Math.min(100, p.mood_pressure * 100)}%"></div></div>
            </div>
          </td>
          <td class="mono">${esc(n.npc_id)}</td>
        </tr>
      `;
    }).join("");

    // Bind settler row clicks
    document.querySelectorAll("#settlers-body tr.interactive").forEach(row => {
      row.addEventListener("click", () => {
        const id = row.dataset.settlerId;
        document.querySelectorAll("#settlers-body tr").forEach(r => r.classList.remove("selected"));
        row.classList.add("selected");
        activeSettlerId = id;
        loadNpcDetails(id);
      });
    });

    // Auto-select first settler if none selected
    if (!activeSettlerId && npcs.length > 0) {
      activeSettlerId = npcs[0].settler_id;
      const firstRow = document.querySelector(`#settlers-body tr[data-settler-id="${CSS.escape(npcs[0].settler_id)}"]`);
      if (firstRow) firstRow.classList.add("selected");
      loadNpcDetails(npcs[0].settler_id);
    }
  } catch (e) {
    settlersBody.innerHTML = `<tr><td colspan="4" class="dim">Error fetching settlers: ${esc(e.message)}</td></tr>`;
  }
}

// API: Fetch NPC Details
async function loadNpcDetails(settlerId) {
  if (!activeSaveId) return;
  
  try {
    const res = await fetch(`/api/memory/npc?settler_id=${encodeURIComponent(settlerId)}&save_id=${encodeURIComponent(activeSaveId)}`);
    if (res.status === 404) {
      document.getElementById("npc-detail-title").textContent = "Select a settler to view memories";
      document.getElementById("npc-personal-file").innerHTML = '<div class="dim">No personal file found for this settler.</div>';
      document.getElementById("memory-category-strip").innerHTML = "";
      document.getElementById("typed-memories-list").innerHTML = '<div class="dim">No typed memories.</div>';
      document.getElementById("dialogue-title").textContent = "Select a settler to view dialogue state";
      document.getElementById("dialogue-body").innerHTML = '<div class="dim">No dialogue state loaded.</div>';
      document.getElementById("character-sheet-title").textContent = "Select a settler to view character sheet";
      document.getElementById("character-sheet-body").innerHTML = '<div class="dim">No character sheet loaded.</div>';
      return;
    }
    
    const data = await res.json();
    const npc = data.npc || {};
    const profile = data.profile || {};
    const pressures = data.pressures || {};
    const memories = data.memories || [];
    const permanent = data.permanent_memories || [];
    const typedMemories = data.typed_memories || [];
    const categories = data.memory_categories || {};
    
    // Update Title
    const nameMap = { "settler_1": "Arthur Pendelton", "settler_2": "Gwendolyn Stone", "settler_3": "Brother Luke" };
    const name = profile.display_name || npc.name || nameMap[settlerId] || npc.settler_id;
    const prof = profile.role || npc.role || "Worker";
    
    document.getElementById("npc-detail-title").innerHTML = `Memories Timeline &mdash; ${esc(name)} <span class="badge">${esc(prof)}</span>`;

    // Fill Summary Pills
    const summarySec = document.getElementById("npc-summary-section");
    summarySec.innerHTML = `
      <span class="meta-pill">ID: <strong>${esc(settlerId)}</strong></span>
      <span class="meta-pill">P2 binding: <strong>${esc(npc.npc_id)}</strong></span>
      ${(npc.traits || "").split(",").map(t => `<span class="meta-pill trait">${esc(t.trim())}</span>`).join("")}
      ${npc.stats ? `<span class="meta-pill">Skills: <strong>${esc(npc.stats)}</strong></span>` : ""}
    `;

    const personalFile = document.getElementById("npc-personal-file");
    personalFile.innerHTML = `
      <div class="personal-file-header">
        <div>
          <strong>${esc(name || settlerId)}</strong>
          <span class="badge">${esc(prof)}</span>
        </div>
        <div class="personal-file-counts">
          <span>${Number(profile.memories_count || typedMemories.length || 0)} memories</span>
          <span>${Number(profile.secrets_count || 0)} secrets</span>
        </div>
      </div>
      <div class="personal-file-body">
        ${profile.description ? `<p>${esc(profile.description)}</p>` : '<p class="dim">No evolving description recorded yet.</p>'}
        ${profile.evolving_summary ? `<p><strong>Summary:</strong> ${esc(profile.evolving_summary)}</p>` : ""}
      </div>
    `;

    const categoryStrip = document.getElementById("memory-category-strip");
    categoryStrip.innerHTML = Object.entries(categories).map(([key, info]) => `
      <span class="memory-category-pill ${Number(info.count || 0) > 0 ? 'active' : ''}">
        ${esc(memoryCategoryLabels[key] || key)} <strong>${Number(info.count || 0)}</strong>
      </span>
    `).join("");

    const typedList = document.getElementById("typed-memories-list");
    if (typedMemories.length === 0) {
      typedList.innerHTML = '<div class="dim">No typed personal memories recorded yet.</div>';
    } else {
      typedList.innerHTML = typedMemories.slice(0, 40).map(m => `
        <div class="timeline-item ${memoryTypeClass(m.category)}">
          <div class="timeline-header">
            <span class="timeline-type">${esc(memoryCategoryLabels[m.category] || m.category)} · ${esc(m.tier)}</span>
            <span class="timeline-time">${formatDate(m.created_at)}</span>
          </div>
          <div class="timeline-content">${esc(m.content)}</div>
          <div class="timeline-footer">event=${esc(m.event_type)} · importance=${Number(m.importance || 0)}</div>
        </div>
      `).join("");
    }

    // Fill Permanent Memories list
    const permList = document.getElementById("permanent-memories-list");
    if (permanent.length === 0) {
      permList.innerHTML = '<div class="dim">No permanent core memories recorded yet. (Needs to be of importance &ge; 9)</div>';
    } else {
      permList.innerHTML = permanent.map(m => `
        <div class="timeline-item incident-danger">
          <div class="timeline-header">
            <span class="timeline-type ok">${esc(m.event_type)}</span>
            <span class="timeline-time">${formatDate(m.timestamp)}</span>
          </div>
          <div class="timeline-content">${esc(m.content)}</div>
        </div>
      `).join("");
    }

    // Fill Recent memories list
    const recList = document.getElementById("recent-memories-list");
    if (memories.length === 0) {
      recList.innerHTML = '<div class="dim">No recent memory events.</div>';
    } else {
      recList.innerHTML = memories.map(m => {
        let typeClass = "";
        if (m.event_type === "decision") typeClass = "incident-decision";
        else if (m.event_type === "danger") typeClass = "incident-danger";
        else if (m.event_type === "health") typeClass = "incident-health";
        else if (m.event_type === "mood") typeClass = "incident-mood";

        return `
          <div class="timeline-item ${typeClass}">
            <div class="timeline-header">
              <span class="timeline-type">${esc(m.event_type)}</span>
              <span class="timeline-time">${formatDate(m.timestamp)}</span>
            </div>
            <div class="timeline-content">${esc(m.content)}</div>
          </div>
        `;
      }).join("");
    }

    // Pre-populate Simulation Form with selected settler's state
    document.getElementById("sim-settler-id").value = settlerId;
    document.getElementById("sim-name").value = name;
    document.getElementById("sim-background").value = prof;
    document.getElementById("sim-traits").value = npc.traits || "hardworking";
    fetchDialogueState(settlerId);
    fetchCharacterSheet(settlerId);

    // Sliders
    if (pressures) {
      const setSlider = (type, pressVal) => {
        const val = Math.round(100 - (pressVal * 100)); // pressure is 1.0 - level
        const slider = document.getElementById(`sim-${type}`);
        const display = document.getElementById(`val-${type}`);
        if (slider && display) {
          slider.value = val;
          display.textContent = val;
        }
      };
      setSlider("food", pressures.hunger_pressure ?? 0.5);
      setSlider("rest", pressures.exhaustion_pressure ?? 0.5);
      setSlider("mood", pressures.mood_pressure ?? 0.5);
      
      document.getElementById("sim-injured").checked = pressures.injury_pressure > 0;
      document.getElementById("sim-health-curr").value = pressures.injury_pressure ? Math.round((1.0 - pressures.injury_pressure) * 100) : 100;
    }

  } catch (e) {
    console.error("Error loading NPC details:", e);
  }
}

async function fetchDialogueState(settlerId) {
  const title = document.getElementById("dialogue-title");
  const body = document.getElementById("dialogue-body");
  if (!activeSaveId || !settlerId) return;

  try {
    const res = await fetch(`/api/dialogue/state?settler_id=${encodeURIComponent(settlerId)}&save_id=${encodeURIComponent(activeSaveId)}`);
    if (!res.ok) {
      title.textContent = "Dialogue State";
      body.innerHTML = '<div class="dim">No dialogue state recorded yet for this settler.</div>';
      return;
    }
    const data = await res.json();
    const state = data.state || {};
    title.textContent = `Dialogue State — ${settlerId}`;
    const trust = Number(state.trust ?? 0.5);
    const trustPct = Math.round(trust * 100);
    const claims = state.recent_claims || [];
    const contradictions = state.contradictions || [];
    const barter = state.barter_intents || [];

    body.innerHTML = `
      <section class="dialogue-card dialogue-wide">
        <h5>Trust Gate</h5>
        <div class="dialogue-meter">
          <div class="dialogue-meter-label">
            <span>Trust toward player</span>
            <strong>${trustPct}% · ${esc(state.disclosure_level || "normal")}</strong>
          </div>
          <div class="progress-track"><div class="progress-fill mood" style="width: ${Math.min(100, Math.max(0, trustPct))}%"></div></div>
        </div>
        <p>${esc(state.disclosure_level === "guarded" ? "Guarded: withholds secrets and sensitive colony weaknesses." : state.disclosure_level === "high" ? "High trust: may reveal sensitive memories and specific advice." : "Normal: shares practical context but withholds secrets.")}</p>
        ${state.voice_profile ? `<p><strong>Voice:</strong> ${esc(state.voice_profile)}</p>` : '<p class="dim">No voice profile recorded yet.</p>'}
      </section>
      <section class="dialogue-card">
        <h5>Recent Claims</h5>
        ${renderDialogueList(claims, item => `${item.speaker}: ${item.claim_text}`, item => item.status)}
      </section>
      <section class="dialogue-card">
        <h5>Contradictions</h5>
        ${renderDialogueList(contradictions, item => item.claim_text, item => item.contradiction_reason || "contradicted")}
      </section>
      <section class="dialogue-card">
        <h5>Barter / Request Intents</h5>
        ${renderDialogueList(barter, item => `${item.intent_type}: ${item.item || "unspecified"}`, item => `${item.status} · ${item.terms || "no terms"}`)}
      </section>
      <section class="dialogue-card dialogue-wide">
        <h5>Prompt Context Sent To NPC</h5>
        <pre class="prompt-context">${esc(state.prompt_context || "")}</pre>
      </section>
    `;
  } catch (e) {
    body.innerHTML = `<div class="dim">Error loading dialogue state: ${esc(e.message)}</div>`;
  }
}

function renderDialogueList(rows, titleFn, metaFn) {
  if (!rows || rows.length === 0) return '<div class="dim">No records yet.</div>';
  return rows.map(row => `
    <div class="dialogue-list-item">
      <strong>${esc(titleFn(row))}</strong>
      <span>${esc(metaFn(row))}</span>
    </div>
  `).join("");
}

async function fetchCharacterSheet(settlerId) {
  const title = document.getElementById("character-sheet-title");
  const body = document.getElementById("character-sheet-body");
  if (!activeSaveId || !settlerId) return;

  try {
    const res = await fetch(`/api/character-sheet?settler_id=${encodeURIComponent(settlerId)}&save_id=${encodeURIComponent(activeSaveId)}`);
    if (res.status === 404) {
      title.textContent = "Character Sheet";
      body.innerHTML = '<div class="dim">No character-sheet snapshot recorded yet for this settler.</div>';
      return;
    }
    const data = await res.json();
    const sheet = data.sheet || {};
    title.textContent = `Character Sheet — ${sheet.name || settlerId}`;

    body.innerHTML = `
      <section class="sheet-card sheet-wide">
        <h5>Identity & Status</h5>
        <div class="sheet-summary">
          <span><strong>${esc(sheet.name || settlerId)}</strong></span>
          <span>Role: <strong>${esc(sheet.role || "unknown")}</strong></span>
          <span>Background: <strong>${esc(sheet.background || "unknown")}</strong></span>
          <span>Pseudonym: <strong>${esc(sheet.pseudonym || "none")}</strong></span>
          <span>Age: <strong>${esc(sheet.age ?? "")}</strong></span>
          <span>Mood: <strong>${esc(sheet.mood || "unknown")} (${Math.round(sheet.mood_score || 0)})</strong></span>
          <span>Health: <strong>${Math.round(sheet.health_current || 0)}/${Math.round(sheet.health_max || 0)}</strong></span>
          <span>Activity: <strong>${esc(sheet.activity_description || sheet.activity_type || "unknown")}</strong></span>
          <span>Schedule: <strong>${esc(sheet.schedule_label || "unknown")}</strong></span>
        </div>
      </section>
      <section class="sheet-card">
        <h5>Skills</h5>
        ${renderKeyValueRows(data.skills, "skill_name", "level", "No skills captured.")}
      </section>
      <section class="sheet-card">
        <h5>Needs</h5>
        ${renderKeyValueRows(data.needs, "need_name", "value", "No needs captured.")}
      </section>
      <section class="sheet-card">
        <h5>Job Priorities</h5>
        ${renderKeyValueRows(data.work_priorities, "job_name", "priority", "No job priorities captured.")}
      </section>
      <section class="sheet-card">
        <h5>Equipment & Inventory</h5>
        ${renderKeyValueRows(data.equipment, "slot", "item", "No equipment captured.")}
      </section>
      <section class="sheet-card">
        <h5>Traits, Perks, States, Vitals</h5>
        ${renderKeyValueRows(data.traits, "value", "kind", "No traits captured.", row => row.detail ? `${row.kind}: ${row.detail}` : row.kind)}
      </section>
      <section class="sheet-card">
        <h5>Mood, Social, Religion Notes</h5>
        ${renderKeyValueRows(data.mood_modifiers, "label", "kind", "No modifiers captured.", row => row.value === null || row.value === undefined ? row.kind : `${row.kind}: ${row.value > 0 ? "+" : ""}${row.value}`)}
      </section>
      <section class="sheet-card">
        <h5>Schedule</h5>
        ${renderKeyValueRows(data.schedule, "hour", "activity", "No hourly schedule captured yet.")}
      </section>
      <section class="sheet-card">
        <h5>Manage Settings</h5>
        ${renderKeyValueRows(data.manage_settings, "setting_name", "setting_value", "No manage settings captured yet.")}
      </section>
    `;
  } catch (e) {
    body.innerHTML = `<div class="dim">Error loading character sheet: ${esc(e.message)}</div>`;
  }
}

// API: Fetch Relationships
async function fetchRelationships(saveId) {
  const body = document.getElementById("relationships-body");
  try {
    const res = await fetch(`/api/relationships?save_id=${encodeURIComponent(saveId)}`);
    const data = await res.json();
    const list = data.relationships || [];

    if (list.length === 0) {
      body.innerHTML = '<tr><td colspan="6" class="dim">No directed relationships recorded in this save.</td></tr>';
      return;
    }

    const nameMap = { "settler_1": "Arthur Pendelton", "settler_2": "Gwendolyn Stone", "settler_3": "Brother Luke" };
    
    body.innerHTML = list.map(r => {
      const subject = nameMap[r.npc_a_id] || r.npc_a_id;
      const object = nameMap[r.npc_b_id] || r.npc_b_id;
      
      const relColor = (v) => {
        if (v > 20) return "ok";
        if (v < -20) return "bad";
        return "";
      };

      return `
        <tr>
          <td><strong>${esc(subject)}</strong></td>
          <td>&rarr; ${esc(object)}</td>
          <td class="${relColor(r.standing)}">${r.standing > 0 ? '+' : ''}${Math.round(r.standing)}</td>
          <td class="${r.trust > 0.7 ? 'ok' : (r.trust < 0.3 ? 'bad' : '')}">${Math.round(r.trust * 100)}%</td>
          <td class="${r.fear > 0.4 ? 'warn' : ''}">${Math.round(r.fear * 100)}%</td>
          <td class="${r.resentment > 0.4 ? 'bad' : ''}">${Math.round(r.resentment * 100)}%</td>
        </tr>
      `;
    }).join("");
  } catch (e) {
    body.innerHTML = `<tr><td colspan="6" class="dim">Error fetching relationships: ${esc(e.message)}</td></tr>`;
  }
}

// API: Fetch Incidents
async function fetchIncidents(saveId) {
  const body = document.getElementById("incidents-body");
  try {
    const res = await fetch(`/api/incidents?save_id=${encodeURIComponent(saveId)}`);
    const data = await res.json();
    const list = data.incidents || [];

    if (list.length === 0) {
      body.innerHTML = '<tr><td colspan="5" class="dim">No incidents logged. Decisions will populate here.</td></tr>';
      return;
    }

    const nameMap = { "settler_1": "Arthur Pendelton", "settler_2": "Gwendolyn Stone", "settler_3": "Brother Luke" };

    body.innerHTML = list.map(i => {
      const name = nameMap[i.settler_id] || i.settler_id;
      return `
        <tr>
          <td class="mono">${formatDate(i.timestamp)}</td>
          <td><strong>${esc(name)}</strong></td>
          <td><span class="status-badge" style="background: var(--bg); border-color: var(--border); color: #fff;">${esc(i.action)}</span></td>
          <td><em>${esc(i.reasoning)}</em></td>
          <td>
            <span class="badge ${i.success ? 'success' : 'failed'}">
              ${i.success ? 'Validated' : 'Fallback'}
            </span>
          </td>
        </tr>
      `;
    }).join("");
  } catch (e) {
    body.innerHTML = `<tr><td colspan="5" class="dim">Error fetching incidents: ${esc(e.message)}</td></tr>`;
  }
}

// Handle simulation form run
async function handleSimulateSubmit(e) {
  e.preventDefault();
  
  const submitBtn = document.getElementById("run-sim-btn");
  const resultsBox = document.getElementById("sim-results-box");
  
  submitBtn.disabled = true;
  submitBtn.textContent = "Running Simulation (calling Player2)...";
  resultsBox.innerHTML = '<div class="dim">Processing needs math and querying Player2 API...</div>';
  
  const formData = new FormData(e.target);
  const payload = {};
  formData.forEach((val, key) => {
    if (key === "is_injured") {
      payload[key] = true;
    } else {
      payload[key] = val;
    }
  });
  if (!payload.is_injured) payload.is_injured = false;

  try {
    const res = await fetch("/api/simulate/decision", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(payload)
    });
    
    const sim = await res.json();
    if (!sim.ok) {
      resultsBox.innerHTML = `<div class="bad">Error running simulation: ${esc(sim.error)}</div>`;
      return;
    }

    // Render results
    let html = '';
    
    // Stage 1: Math Scoring
    html += `
      <div class="sim-block">
        <div class="sim-block-header">Stage 1: Deterministic Pressures & Mathematical Needs Scoring</div>
        <div class="sim-indicator-row">
          <span>Hunger pressure: <span class="sim-val">${Math.round(sim.pressures.hunger * 100)}%</span></span>
          <span>Exhaustion pressure: <span class="sim-val">${Math.round(sim.pressures.exhaustion * 100)}%</span></span>
          <span>Injury pressure: <span class="sim-val">${Math.round(sim.pressures.injury * 100)}%</span></span>
          <span>Mood pressure: <span class="sim-val">${Math.round(sim.pressures.mood * 100)}%</span></span>
        </div>
        <div class="sim-scores-list" style="margin-top: 10px;">
    `;
    
    (sim.math_options || []).slice(0, 5).forEach((opt, idx) => {
      const isTop = opt.name === sim.top_math_choice.name;
      html += `
        <div class="sim-score-row">
          <span class="sim-score-name">${idx + 1}. ${esc(opt.name)} ${isTop ? '★' : ''}</span>
          <span class="sim-score-val ${isTop ? 'top' : ''}">${opt.score.toFixed(2)}</span>
        </div>
      `;
    });
    
    html += `
        </div>
      </div>
    `;

    // Stage 2: Player2 bounding
    html += `
      <div class="sim-block">
        <div class="sim-block-header">Stage 2: Bounded LLM command selection (Player2 API)</div>
        <div class="sim-indicator-row">
          <span>Player2 online: <span class="sim-val ${sim.player2_online ? 'ok' : 'bad'}">${sim.player2_online ? 'YES' : 'NO'}</span></span>
          <span>Model selected: <span class="sim-val">${sim.was_llm ? 'Player2' : 'Offline Heuristic'}</span></span>
        </div>
        <div class="sim-monologue">
          <strong>Narrative thoughts:</strong><br>
          ${esc(sim.dialogue_complaint)}
        </div>
      </div>
    `;

    // Stage 3: Validation
    html += `
      <div class="sim-block">
        <div class="sim-block-header">Stage 3: Validation & Execution</div>
        <div class="sim-indicator-row">
          <span>Validation check: <span class="sim-val ${sim.validation_passed ? 'ok' : 'bad'}">${sim.validation_passed ? 'PASSED' : 'FAILED'}</span></span>
          <span>Final chosen action: <span class="status-badge" style="background: var(--bg); color: #fff;">${esc(sim.chosen_action)}</span></span>
        </div>
        <div style="font-size: 12px; margin-top: 6px; color: var(--text-muted);">
          Reasoning logged: ${esc(sim.reasoning)}
        </div>
      </div>
    `;

    resultsBox.innerHTML = html;

    // Reload lists to show new memories and incidents
    if (activeSaveId) {
      loadSaveData(activeSaveId);
    }

  } catch (e) {
    resultsBox.innerHTML = `<div class="bad">Connection error: ${esc(e.message)}</div>`;
  } finally {
    submitBtn.disabled = false;
    submitBtn.textContent = "Run Influence Engine Simulation";
  }
}

// Background refresh of database changes
async function silentRefresh() {
  if (!activeSaveId) return;
  try {
    // Refresh settlers
    const res = await fetch(`/api/memory/npcs?save_id=${encodeURIComponent(activeSaveId)}`);
    const data = await res.json();
    const npcs = data.npcs || [];
    settlersCache = npcs;
    settlerCount.textContent = npcs.length;
    
    // Re-render table rows values in-place without resetting click handlers
    npcs.forEach(n => {
      const p = n.pressures || {};
      const row = document.querySelector(`#settlers-body tr[data-settler-id="${CSS.escape(n.settler_id)}"]`);
      if (row) {
        // Update pressures values
        const updateBar = (cls, val) => {
          const bar = row.querySelector(`.progress-fill.${cls}`);
          const lbl = bar?.closest('.pressure-progress-bar')?.querySelector('.bar-label span:last-child');
          if (bar) bar.style.width = `${Math.min(100, val * 100)}%`;
          if (lbl) lbl.textContent = `${Math.round(val * 100)}%`;
        };
        updateBar("hunger", p.hunger_pressure);
        updateBar("injury", p.injury_pressure);
        updateBar("exhaustion", p.exhaustion_pressure);
        updateBar("mood", p.mood_pressure);
      }
    });

    // Refresh active details lists
    if (activeSettlerId) {
      const resNpc = await fetch(`/api/memory/npc?settler_id=${encodeURIComponent(activeSettlerId)}&save_id=${encodeURIComponent(activeSaveId)}`);
      if (resNpc.status === 200) {
        const dataNpc = await resNpc.json();
        
        // Re-render recent memories
        const recList = document.getElementById("recent-memories-list");
        const memories = dataNpc.memories || [];
        if (memories.length > 0) {
          recList.innerHTML = memories.map(m => {
            let typeClass = "";
            if (m.event_type === "decision") typeClass = "incident-decision";
            else if (m.event_type === "danger") typeClass = "incident-danger";
            else if (m.event_type === "health") typeClass = "incident-health";
            else if (m.event_type === "mood") typeClass = "incident-mood";

            return `
              <div class="timeline-item ${typeClass}">
                <div class="timeline-header">
                  <span class="timeline-type">${esc(m.event_type)}</span>
                  <span class="timeline-time">${formatDate(m.timestamp)}</span>
                </div>
                <div class="timeline-content">${esc(m.content)}</div>
              </div>
            `;
          }).join("");
        }

        // Re-render permanent
        const permList = document.getElementById("permanent-memories-list");
        const permanent = dataNpc.permanent_memories || [];
        if (permanent.length > 0) {
          permList.innerHTML = permanent.map(m => `
            <div class="timeline-item incident-danger">
              <div class="timeline-header">
                <span class="timeline-type ok">${esc(m.event_type)}</span>
                <span class="timeline-time">${formatDate(m.timestamp)}</span>
              </div>
              <div class="timeline-content">${esc(m.content)}</div>
            </div>
          `).join("");
        }
      }
    }

    // Refresh active tab lists
    const relActive = document.querySelector('.tab-btn[data-tab="relationships-tab"]').classList.contains('active');
    if (relActive) fetchRelationships(activeSaveId);

    const incActive = document.querySelector('.tab-btn[data-tab="incidents-tab"]').classList.contains('active');
    if (incActive) fetchIncidents(activeSaveId);

  } catch (e) {
    console.error("Silent background refresh failed:", e);
  }
}

// Game Control Bridge functions
function initGameBridge() {
  const gameScreenImg = document.getElementById("game-screen-img");
  const gameLoadingOverlay = document.getElementById("game-loading-overlay");
  const gameRefreshBtn = document.getElementById("game-refresh-btn");
  const gameStreamBtn = document.getElementById("game-stream-btn");
  const gameFocusBtn = document.getElementById("game-focus-btn");
  const gameBridgeStatus = document.getElementById("game-bridge-status");
  const gameTextInput = document.getElementById("game-text-input");
  const gameSendTextBtn = document.getElementById("game-send-text-btn");
  const gameClickIndicator = document.getElementById("game-click-indicator");

  if (!gameScreenImg) return;

  gameRefreshBtn.addEventListener("click", () => {
    refreshGameFrame();
  });

  gameStreamBtn.addEventListener("click", () => {
    if (isStreaming) {
      stopGameStream();
    } else {
      startGameStream();
    }
  });

  gameFocusBtn.addEventListener("click", async () => {
    gameBridgeStatus.textContent = "Focusing...";
    gameBridgeStatus.className = "status-val text-yellow";
    try {
      const res = await fetch("/api/game/screen?force_focus=true&t=" + Date.now());
      if (res.ok) {
        gameBridgeStatus.textContent = "Connected";
        gameBridgeStatus.className = "status-val text-green";
        refreshGameFrame();
      } else {
        gameBridgeStatus.textContent = "Focus Failed";
        gameBridgeStatus.className = "status-val text-red";
      }
    } catch (e) {
      gameBridgeStatus.textContent = "Offline";
      gameBridgeStatus.className = "status-val text-red";
    }
  });

  gameScreenImg.addEventListener("click", async (e) => {
    const rect = gameScreenImg.getBoundingClientRect();
    const x_rel = (e.clientX - rect.left) / rect.width;
    const y_rel = (e.clientY - rect.top) / rect.height;

    gameClickIndicator.style.left = `${(x_rel * 100).toFixed(2)}%`;
    gameClickIndicator.style.top = `${(y_rel * 100).toFixed(2)}%`;
    gameClickIndicator.style.display = "block";

    gameClickIndicator.style.animation = "none";
    gameClickIndicator.offsetHeight; // trigger reflow
    gameClickIndicator.style.animation = null;

    try {
      const res = await fetch("/api/game/input", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          action: "click",
          x: x_rel,
          y: y_rel
        })
      });
      const data = await res.json();
      if (data.ok) {
        setTimeout(refreshGameFrame, 300);
      }
    } catch (err) {
      console.error("Failed to inject click:", err);
    }
  });

  const injectText = async () => {
    const text = gameTextInput.value;
    if (!text) return;
    gameTextInput.value = "";
    try {
      await fetch("/api/game/input", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ action: "text", text })
      });
      setTimeout(refreshGameFrame, 400);
    } catch (err) {
      console.error("Failed to inject text:", err);
    }
  };

  gameSendTextBtn.addEventListener("click", injectText);
  gameTextInput.addEventListener("keydown", (e) => {
    if (e.key === "Enter") {
      injectText();
    }
  });

  document.querySelectorAll(".special-keys .btn-key").forEach(btn => {
    btn.addEventListener("click", async () => {
      const key = btn.dataset.key;
      try {
        await fetch("/api/game/input", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ action: "keypress", key })
        });
        setTimeout(refreshGameFrame, 300);
      } catch (err) {
        console.error("Failed to inject keypress:", err);
      }
    });
  });
}

async function refreshGameFrame() {
  const gameScreenImg = document.getElementById("game-screen-img");
  const gameLoadingOverlay = document.getElementById("game-loading-overlay");
  const gameBridgeStatus = document.getElementById("game-bridge-status");

  if (!gameScreenImg) return;

  try {
    const t = Date.now();
    const img = new Image();
    img.onload = () => {
      gameScreenImg.src = img.src;
      gameScreenImg.style.display = "block";
      if (gameLoadingOverlay) gameLoadingOverlay.style.display = "none";
      gameBridgeStatus.textContent = "Connected";
      gameBridgeStatus.className = "status-val text-green";
    };
    img.onerror = () => {
      gameBridgeStatus.textContent = "Capture Error";
      gameBridgeStatus.className = "status-val text-red";
    };
    img.src = `/api/game/screen?t=${t}`;
  } catch (e) {
    gameBridgeStatus.textContent = "Offline";
    gameBridgeStatus.className = "status-val text-red";
  }
}

function startGameStream() {
  const gameStreamBtn = document.getElementById("game-stream-btn");
  if (isStreaming) return;
  isStreaming = true;
  gameStreamBtn.textContent = "Stop Stream";
  gameStreamBtn.className = "btn btn-primary";
  streamInterval = setInterval(refreshGameFrame, 333);
}

function stopGameStream() {
  const gameStreamBtn = document.getElementById("game-stream-btn");
  if (!isStreaming) return;
  isStreaming = false;
  gameStreamBtn.textContent = "Start Stream (3 FPS)";
  gameStreamBtn.className = "btn btn-secondary";
  
  if (streamInterval) {
    clearInterval(streamInterval);
    streamInterval = null;
  }
}
