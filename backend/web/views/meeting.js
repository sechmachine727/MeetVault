import { el, clear, appendAll } from "../lib/dom.js";
import {
  formatDate,
  formatDuration,
  formatElapsed,
  statusKind,
  statusLabel,
  describeJobState,
  friendlyError,
  isProcessing,
  formatBytes,
  outOfDateKinds,
  outOfDateBundleKinds,
} from "../lib/format.js";

const TABS = ["overview", "transcript", "analysis", "qa", "speakers", "versions", "jobs"];

export async function renderMeeting(ctx, route) {
  const meetingId = route.id;
  const state = {
    tab: TABS.includes(route.params.tab) ? route.params.tab : "overview",
    meeting: null,
    status: null,
    disposed: false,
    pollTimer: null,
  };

  const head = el("div", {});
  const playerBar = el("div", { class: "card", "data-testid": "player-bar" });
  const body = el("div", { "data-testid": "tab-body" });

  // ONE logical player for the whole detail view: transcript rows, agenda items, sections and
  // citations all seek into it. A media element cannot send the Authorization header, so each
  // source is a blob fetched with the header (docs/API.md "Browser playback").
  //
  // A meeting has up to TWO tracks and they are not alternatives: the screen recording is
  // video-only (the desktop client records it without audio) and the sound lives in the separate
  // audio track. So both load by default — <video> for the screen track, <audio> for the meeting's
  // audio — and they are kept in step by paired playback below. Making them swap through one
  // element is what used to need "Load playback" and left the video silent.
  const playerHost = el("div", { class: "player-host", "data-testid": "player-host" });
  const playerCaption = el("div", { class: "meta", "data-testid": "player-caption" });

  // The timeline anchor: what the transcript seeks against, and what the existing data-testid
  // "player" keeps pointing at (the audio track when there is one, else the screen track).
  const anchor = el("div", { class: "player-slot player-slot-anchor", "data-testid": "player-anchor" });
  const companion = el("div", { class: "player-slot", "data-testid": "player-companion", hidden: true });

  // Named mediaElement, not media: renderTab already binds `const media` to the media payload, and
  // a function of the same name is shadowed inside it (TypeError: media is not a function).
  function mediaElement(attrs) {
    const { kind, muted, ...rest } = attrs;
    const node = el(kind === "video" ? "video" : "audio", {
      controls: true,
      preload: "metadata",
      ...rest,
    });
    // A video element carries the screen picture only. If such an MP4 ever has its own audio,
    // muting it keeps the meeting's audio track the single source of sound (no doubling).
    if (kind === "video" && muted) node.muted = true;
    return node;
  }

  // role "anchor" owns the transcript timeline; role "video" is the picture beside it.
  let anchorTrack = { role: "anchor", kind: "audio", node: mediaElement({ kind: "audio", "data-testid": "player" }), asset: null, url: null };
  let videoTrack = null;
  let pairSync = false;

  playerHost.append(companion);
  playerHost.append(anchor);
  anchor.append(anchorTrack.node);

  function slots() {
    return [anchorTrack, videoTrack].filter(Boolean);
  }

  function trackFor(asset) {
    if (asset.kind === "video") return videoTrack;
    return anchorTrack;
  }

  function setCaption() {
    const audio = slots().find((t) => t.asset && t.kind === "audio");
    const video = slots().find((t) => t.asset && t.kind === "video");
    if (!audio && !video) {
      playerCaption.textContent = "No recording on the server for this meeting.";
      return;
    }
    const parts = [];
    if (video) parts.push(`video: ${video.asset.original_name} (${formatDuration(video.asset.duration_ms)})`);
    if (audio) parts.push(`audio: ${audio.asset.original_name} (${formatDuration(audio.asset.duration_ms)})`);
    const synced = audio && video ? " — playing together" : "";
    playerCaption.textContent = `Playing ${parts.join(" · ")}${synced}`;
  }

  function isLoaded(track) {
    return Boolean(track && (track.url || track.node.getAttribute("src")));
  }

  // Loads one asset into its track (or clears it when asset is null), fetching the blob with the
  // auth header. Returns the track so callers can start/sync playback.
  async function showAsset(track, asset) {
    if (!track) return null;
    if (!asset) {
      if (track.url) URL.revokeObjectURL(track.url);
      track.url = null;
      track.asset = null;
      track.node.removeAttribute("src");
      track.node.load();
      setCaption();
      return track;
    }
    if (track.asset && track.asset.id === asset.id) {
      setCaption();
      return track;
    }
    const blob = await ctx.api.mediaBlob(asset.id);
    if (track.url) URL.revokeObjectURL(track.url);
    track.url = URL.createObjectURL(blob);
    track.asset = asset;
    track.node.src = track.url;
    setCaption();
    return track;
  }

  // Paired playback: these two elements are one recording, so starting either starts both and
  // pausing either pauses both. Without this, a native play on the busy <video> leaves the audio
  // silent (and vice versa), which reads as "no sound".
  function pairPlayback() {
    if (pairSync) return;
    const tracks = slots();
    if (tracks.length < 2) return;
    pairSync = true;
    for (const source of tracks) {
      source.node.addEventListener("play", () => {
        if (source.node.dataset.mirror === "1") return;
        for (const other of slots()) {
          if (other === source || !isLoaded(other)) continue;
          other.node.dataset.mirror = "1";
          const done = () => delete other.node.dataset.mirror;
          other.node.play().then(done, done);
        }
      });
      source.node.addEventListener("pause", () => {
        if (source.node.dataset.mirror === "1") return;
        for (const other of slots()) {
          if (other === source || other.node.paused || !isLoaded(other)) continue;
          other.node.dataset.mirror = "1";
          other.node.pause();
          delete other.node.dataset.mirror;
        }
      });
    }
  }

  function releaseBlobs() {
    for (const track of slots()) {
      if (track.url) URL.revokeObjectURL(track.url);
      track.url = null;
    }
  }

  async function downloadAsset(asset, fallbackName) {
    const blob = await ctx.api.mediaBlob(asset.id);
    const url = URL.createObjectURL(blob);
    const anchor = el("a", { href: url, download: (asset && asset.original_name) || fallbackName || "recording" });
    document.body.append(anchor);
    anchor.click();
    anchor.remove();
    setTimeout(() => URL.revokeObjectURL(url), 10_000);
  }

  // Seek one track and let paired playback bring the other along (the "play" event mirrors it).
  async function seekIn(track, seconds) {
    track.node.currentTime = Math.max(0, seconds);
    try {
      await track.node.play();
    } catch {
      /* an autoplay refusal is not an error */
    }
  }

  // The transcript timeline is the audio track's whenever there is one (it is the meeting's
  // timeline anchor); a video-only meeting seeks the screen track instead.
  async function seekTo(seconds) {
    if (!Number.isFinite(seconds)) return;
    const target = anchorTrack.asset ? anchorTrack : videoTrack ?? anchorTrack;
    await seekIn(target, seconds);
  }

  async function loadStatus() {
    state.status = await ctx.api.status(meetingId).catch(() => null);
  }

  async function loadHead() {
    state.meeting = await ctx.api.getMeeting(meetingId);
    await loadStatus();
    clear(head);
    const kind = statusKind(state.meeting);
    const status = state.status || {};
    const step = describeJobState(status);
    const elapsed = status.active_job_started_at ? formatElapsed(status.active_job_started_at, Date.now()) : "";
    appendAll(
      head,
      el("h1", { text: state.meeting.title }),
      el("p", {
        class: "sub",
        text: `${formatDate(state.meeting.created_at)} · ${formatDuration(state.meeting.duration_ms)} · ${state.meeting.language || "auto"} · ${state.meeting.media_count} recording(s), ${state.meeting.transcript_count} transcript version(s), ${state.meeting.speaker_count} speaker(s)`,
      }),
      el(
        "div",
        { class: "status" },
        el("span", { class: "pill", dataset: { kind }, "data-testid": "status", text: [statusLabel(kind), step, elapsed].filter(Boolean).join(" · ") }),
        state.meeting.has_analysis ? el("span", { class: "pill", "data-testid": "analyzed-badge", text: "Analyzed" }) : null,
        // The detail payload's is_mock describes the ACTIVE TRANSCRIPT (its provider), so the badge
        // says exactly that. The analysis' own mock state is on the Analysis tab.
        state.meeting.is_mock ? el("span", { class: "pill mock", "data-testid": "mock-badge", text: "Mock transcript" }) : null,
      ),
      el(
        "div",
        { class: "toolbar" },
        el("button", { type: "button", dataset: { action: "rerun" }, onclick: () => rerun() }, "Re-run analysis"),
        el("button", { type: "button", dataset: { action: "export" }, onclick: () => exportDocx() }, "Export to Word"),
        el("button", { class: "danger", type: "button", dataset: { action: "remove" }, onclick: () => removeMeeting() }, "Remove from cloud"),
      ),
      status.error ? el("div", { class: "banner error", "data-testid": "job-error", text: `Last job failed: ${status.error}` }) : null,
      // Only kinds with no FRESH document are genuinely out of date: a re-run marks its predecessor
      // stale while the new one is current, so an unfiltered banner cries stale over current data.
      outOfDateKinds(status).length
        ? el("div", { class: "banner", "data-testid": "stale-banner", text: `Out of date: ${outOfDateKinds(status).join(", ")} — re-run analysis to refresh.` })
        : null,
    );
  }

  async function loadPlayerBar() {
    clear(playerBar);
    const media = await ctx.api.listMedia(meetingId, { limit: 50 }).catch(() => ({ items: [] }));
    const videoAsset = media.items.find((item) => item.kind === "video") ?? null;
    const audioAsset = media.items.find((item) => item.kind === "audio") ?? null;

    // The screen track becomes the <video> that stays in the player (muted: the meeting's audio
    // track is the sound), and the audio track becomes the timeline anchor beside it. A
    // video-only meeting promotes its screen track to the anchor so the transcript still seeks;
    // an audio-only meeting hides the video slot.
    const anchorKind = audioAsset ? "audio" : videoAsset ? "video" : "audio";
    if (anchorTrack.kind !== anchorKind) {
      const replacement = mediaElement({
        kind: anchorKind,
        "data-testid": "player",
        muted: anchorKind === "video" && Boolean(audioAsset),
      });
      anchorTrack.node.replaceWith(replacement);
      anchorTrack = { ...anchorTrack, kind: anchorKind, node: replacement };
    }
    const videoAnchor = anchorKind === "video";
    if (videoAsset && !videoAnchor) {
      if (!videoTrack || videoTrack.node.tagName.toLowerCase() !== "video") {
        videoTrack = {
          role: "video",
          kind: "video",
          node: mediaElement({ kind: "video", "data-testid": "video-player", muted: true }),
          asset: null,
          url: null,
        };
        clear(companion);
        companion.append(videoTrack.node);
      }
      companion.hidden = false;
    } else if (!videoAnchor) {
      videoTrack = null;
      clear(companion);
      companion.hidden = true;
    }

    playerBar.append(playerCaption, playerHost);
    try {
      // Both by default — the user should never have to press "Load playback" to get the picture,
      // and the picture is silent on its own, so the audio must load and play with it.
      if (videoTrack) await showAsset(videoTrack, videoAsset);
      await showAsset(anchorTrack, videoAnchor ? videoAsset : audioAsset);
      pairPlayback();
    } catch (err) {
      ctx.toast(friendlyError(err));
    }
    if (!videoAsset && !audioAsset) releaseBlobs();
    setCaption();
  }

  async function rerun() {
    try {
      await ctx.api.process(meetingId, { steps: "analyze", force: true });
      ctx.toast("Analysis queued.");
      await loadHead();
      await renderTab();
      schedulePoll();
    } catch (err) {
      ctx.toast(friendlyError(err));
    }
  }

  async function exportDocx() {
    try {
      const blob = await ctx.api.exportDocx(meetingId);
      const url = URL.createObjectURL(blob);
      const slug = state.meeting.title.replace(/[^\w-]+/g, "-").replace(/^-|-$/g, "") || "meeting";
      const anchor = el("a", { href: url, download: `${slug}.docx` });
      document.body.append(anchor);
      anchor.click();
      anchor.remove();
      setTimeout(() => URL.revokeObjectURL(url), 10_000);
      ctx.toast("Word export downloaded.");
    } catch (err) {
      ctx.toast(friendlyError(err));
    }
  }

  // Deleting a version is not a local-only act: the server CASCADES every analysis document and
  // run derived from that transcript, so the meeting loses its summary/decisions/actions/answers.
  // Disclosing that is the difference between a destructive button and a trap (found by testing:
  // deleting the diarized version emptied analysis_runs and analysis_documents).
  function confirmDeleteVersion(version) {
    const dialog = el(
      "dialog",
      { "data-testid": "delete-version-dialog" },
      el("h3", {}, `Delete transcript version #${version.version}?`),
      el(
        "p",
        {},
        "The version is removed from the server — and so is every analysis derived from it (summary, agenda, decisions, action items and answers), because the server cascades them. Re-run analysis afterwards to rebuild them.",
      ),
      el(
        "div",
        { class: "dialog-actions" },
        el("button", { type: "button", class: "cancel", onclick: () => dialog.close() }, "Cancel"),
        el(
          "button",
          {
            type: "button",
            class: "primary confirm",
            onclick: async () => {
              try {
                await ctx.api.deleteTranscript(version.id);
                dialog.close();
                ctx.toast("Version deleted.");
                await renderTab();
                await loadHead();
              } catch (err) {
                dialog.close();
                ctx.toast(friendlyError(err));
              }
            },
          },
          "Delete version",
        ),
      ),
    );
    document.body.append(dialog);
    dialog.addEventListener("close", () => dialog.remove());
    dialog.showModal();
  }

  function removeMeeting() {
    const dialog = el(
      "dialog",
      { "data-testid": "remove-dialog" },
      el("h3", {}, "Remove from cloud?"),
      el("p", {}, "The meeting, its server recordings and its analysis are deleted from the server. The local recording is not touched."),
      el(
        "div",
        { class: "dialog-actions" },
        el("button", { type: "button", class: "cancel", onclick: () => dialog.close() }, "Cancel"),
        el(
          "button",
          {
            type: "button",
            class: "primary confirm",
            onclick: async () => {
              try {
                await ctx.api.deleteMeeting(meetingId);
                dialog.close();
                ctx.toast("Removed from cloud.");
                ctx.navigate("meetings");
              } catch (err) {
                dialog.close();
                ctx.toast(friendlyError(err));
              }
            },
          },
          "Remove",
        ),
      ),
    );
    document.body.append(dialog);
    dialog.addEventListener("close", () => dialog.remove());
    dialog.showModal();
  }

  function schedulePoll() {
    clearTimeout(state.pollTimer);
    if (state.disposed) return;
    if (!state.status || !isProcessing(state.meeting ? state.meeting.latest_job_state : null)) return;
    state.pollTimer = setTimeout(async () => {
      await loadHead();
      await renderTab();
      schedulePoll();
    }, 2000);
  }

  const TAB_LABELS = {
    overview: "Overview",
    transcript: "Transcript",
    analysis: "Analysis",
    qa: "Q&A",
    speakers: "Speakers",
    versions: "Versions",
    jobs: "Jobs",
  };

  function tabButton(name) {
    return el(
      "button",
      {
        type: "button",
        role: "tab",
        dataset: { tab: name },
        "aria-selected": state.tab === name ? "true" : "false",
        onclick: () => {
          state.tab = name;
          renderTab();
        },
      },
      TAB_LABELS[name],
    );
  }

  const tabs = el("div", { class: "tabs", role: "tablist" }, ...TABS.map(tabButton));

  async function renderTab() {
    clear(body);
    for (const button of tabs.querySelectorAll("button")) {
      button.setAttribute("aria-selected", button.dataset.tab === state.tab ? "true" : "false");
    }
    const panels = { overview, transcript, analysis, qa, speakers, versions, jobs };
    const panel = el("div", {});
    try {
      panel.append(await panels[state.tab]());
    } catch (err) {
      panel.append(el("div", { class: "banner error", text: friendlyError(err) }));
    }
    body.append(panel);
  }

  // ---------------------------------------------------------------- panels

  async function overview() {
    const [meeting, media] = await Promise.all([
      ctx.api.getMeeting(meetingId),
      ctx.api.listMedia(meetingId, { limit: 50 }),
    ]);
    const card = el("div", { class: "card", "data-testid": "overview" });
    const facts = el("dl", { class: "facts" });
    facts.append(
      el("dt", {}, "Status"),
      el("dd", { "data-testid": "status-text", text: meeting.status }),
      el("dt", {}, "Language"),
      el("dd", { text: meeting.language || "auto" }),
      el("dt", {}, "Duration"),
      el("dd", { text: formatDuration(meeting.duration_ms) }),
      el("dt", {}, "Created"),
      el("dd", { text: formatDate(meeting.created_at) }),
      el("dt", {}, "Recordings"),
      el("dd", { text: String(meeting.media_count) }),
      el("dt", {}, "Transcript versions"),
      el("dd", { text: String(meeting.transcript_count) }),
      el("dt", {}, "Speakers"),
      el("dd", { text: String(meeting.speaker_count) }),
      el("dt", {}, "Analysis"),
      el("dd", { text: meeting.has_analysis ? "current" : "none yet" }),
    );
    card.append(el("h2", {}, "Meeting"), facts);

    const mediaCard = el("div", { class: "card", "data-testid": "media-list" });
    mediaCard.append(el("h2", {}, "Recordings on the server"));
    if (!media.items.length) {
      mediaCard.append(el("div", { class: "empty", text: "No recordings on the server for this meeting." }));
    }
    for (const asset of media.items) {
      // Loaded by default now, so the row action starts this track instead of loading it. For a
      // video track that may start later than the meeting, playback begins at its offset.
      const track = trackFor(asset);
      const loaded = Boolean(track && isLoaded(track));
      mediaCard.append(
        el(
          "div",
          { class: "row", dataset: { asset: asset.id } },
          el(
            "div",
            {},
            el("div", { class: "title", text: asset.original_name || asset.storage_key }),
            el("div", {
              class: "meta",
              text: [
                `${asset.kind}${asset.role ? ` (${asset.role})` : ""}`,
                formatDuration(asset.duration_ms),
                formatBytes(asset.size_bytes),
                asset.start_offset_ms ? `starts at ${formatDuration(asset.start_offset_ms)}` : "",
              ]
                .filter(Boolean)
                .join(" · "),
            }),
          ),
          el(
            "div",
            { class: "actions" },
            el(
              "button",
              {
                type: "button",
                "data-testid": "load-playback",
                text: loaded ? "Play" : "Load playback",
                onclick: async () => {
                  try {
                    const target = trackFor(asset);
                    if (!target) return;
                    await showAsset(target, asset);
                    // A screen track may start later than the meeting: begin at its offset.
                    const from = asset.kind === "video" && asset.start_offset_ms ? asset.start_offset_ms / 1000 : 0;
                    await seekIn(target, from);
                  } catch (err) {
                    ctx.toast(friendlyError(err));
                  }
                },
              },
            ),
            el(
              "button",
              {
                type: "button",
                "data-testid": "download",
                onclick: async () => {
                  try {
                    await downloadAsset(asset, "recording");
                  } catch (err) {
                    ctx.toast(friendlyError(err));
                  }
                },
              },
              "Download",
            ),
            el(
              "button",
              {
                class: "danger",
                type: "button",
                "data-testid": "delete-media",
                onclick: async () => {
                  try {
                    await ctx.api.deleteMedia(asset.id);
                    ctx.toast("Recording deleted from the server.");
                    // Drop the deleted track's element source (its blob is revoked below), then
                    // rebuild the player so a remaining track becomes the anchor.
                    for (const track of slots()) {
                      if (track.asset?.id === asset.id) {
                        track.node.removeAttribute("src");
                        track.node.load();
                        track.asset = null;
                      }
                    }
                    await renderTab();
                    await loadHead();
                    await loadPlayerBar();
                  } catch (err) {
                    ctx.toast(friendlyError(err));
                  }
                },
              },
              "Delete",
            ),
          ),
        ),
      );
    }
    return el("div", {}, card, mediaCard);
  }

  async function transcript() {
    const [detail, text] = await Promise.all([
      ctx.api.transcript(meetingId),
      ctx.api.transcriptText(meetingId).catch(() => ""),
    ]);
    const segments = detail.segments || [];
    const card = el("div", { class: "card", "data-testid": "transcript" });
    card.append(
      el("h2", {}, `Transcript — ${segments.length} segment(s)`),
      el(
        "div",
        { class: "toolbar" },
        el(
          "button",
          {
            type: "button",
            "data-testid": "copy-text",
            onclick: async () => {
              try {
                await navigator.clipboard.writeText(
                  text || segments.map((s) => `${s.speaker_label || ""}: ${s.text}`).join("\n"),
                );
                ctx.toast("Transcript copied.");
              } catch (err) {
                ctx.toast(friendlyError(err));
              }
            },
          },
          "Copy transcript text",
        ),
      ),
    );
    if (!segments.length) {
      card.append(el("div", { class: "empty", text: "No active transcript for this meeting yet." }));
      return card;
    }
    const table = el(
      "table",
      { class: "segments" },
      el("thead", {}, el("tr", {}, el("th", {}, "Time"), el("th", {}, "Speaker"), el("th", {}, "Text"))),
    );
    const tbody = el("tbody", {});
    for (const segment of segments) {
      tbody.append(
        el(
          "tr",
          { dataset: { segment: segment.id } },
          el("td", { class: "time", "data-testid": "seek", onclick: () => seekTo((segment.start_ms || 0) / 1000) }, formatDuration(segment.start_ms)),
          el("td", { class: "speaker", text: segment.speaker_label || "—" }),
          el("td", { text: segment.text }),
        ),
      );
    }
    table.append(tbody);
    card.append(table);
    return card;
  }

  function citationButtons(item, testId, container) {
    const ids = item.segment_ids || [];
    const segmentsById = container.segmentsById || {};
    for (const [index, segmentId] of ids.slice(0, 4).entries()) {
      const segment = segmentsById[segmentId];
      const label = segment ? `${formatDuration(segment.start_ms)}` : `source ${index + 1}`;
      const button = el(
        "button",
        {
          type: "button",
          title: segment ? segment.text : segmentId,
          "data-testid": `${testId}-citation`,
          onclick: () => seekTo(((segment ? segment.start_ms : 0) || 0) / 1000),
        },
        label,
      );
      button.dataset.segment = segmentId;
      container.node.append(button);
    }
  }

  function citationsCard(title, document, testId, actorField, segmentsById) {
    const items = document && document.items ? document.items : [];
    const card = el("div", { class: "card", "data-testid": testId });
    card.append(el("h2", {}, title));
    if (!items.length) {
      card.append(el("div", { class: "empty", text: `No ${title.toLowerCase()} recorded.` }));
      return card;
    }
    for (const item of items) {
      const actors = item[actorField];
      const citation = el("div", { class: "citation" });
      const holder = { node: citation, segmentsById };
      citationButtons(item, testId, holder);
      card.append(
        el(
          "div",
          { class: "row" },
          el(
            "div",
            {},
            el("div", { class: "title", text: item.text }),
            el("div", {
              class: "meta",
              text: [
                Array.isArray(actors) ? actors.join(", ") : actors,
                item.due ? `due ${item.due}` : "",
                item.at_ms !== undefined ? formatDuration(item.at_ms) : "",
              ]
                .filter(Boolean)
                .join(" · "),
            }),
            citation,
          ),
        ),
      );
    }
    return card;
  }

  async function analysis() {
    const [bundle, detail] = await Promise.all([
      ctx.api.analysis(meetingId),
      ctx.api.transcript(meetingId).catch(() => ({ segments: [] })),
    ]);
    const segmentsById = Object.fromEntries((detail.segments || []).map((s) => [s.id, s]));
    const card = el("div", { class: "card", "data-testid": "analysis" });
    card.append(el("h2", {}, "Summary"));
    // The analysis' mock state comes from the analysis RUN (the bundle flag is the fallback) —
    // never from /status's aggregate, which is true whenever any step ran mocked.
    const analysisMock = bundle.run ? bundle.run.is_mock : bundle.mock;
    if (analysisMock) card.append(el("span", { class: "pill mock", "data-testid": "mock-analysis-badge", text: "Mock analysis" }));
    if (outOfDateBundleKinds(bundle).length) {
      card.append(el("div", { class: "banner", text: `Out of date: ${outOfDateBundleKinds(bundle).join(", ")}` }));
    }
    if (bundle.run) {
      card.append(
        el("p", {
          class: "citation",
          text: `Analyzed by ${bundle.run.provider}${bundle.run.model ? ` (${bundle.run.model})` : ""}${bundle.run.is_mock ? " — mock" : ""}`,
        }),
      );
    }
    const summary = bundle.summary || {};
    if (summary.text) card.append(el("p", { text: summary.text }));
    if (summary.bullets && summary.bullets.length) {
      card.append(el("ul", { class: "bullets", "data-testid": "summary-bullets" }, ...summary.bullets.map((b) => el("li", { text: b }))));
    }
    if (!summary.text && !(summary.bullets || []).length) {
      card.append(el("div", { class: "empty", "data-testid": "no-analysis", text: "No analysis is current for this meeting yet. Use Re-run analysis." }));
    }

    const agenda = bundle.agenda && bundle.agenda.items ? bundle.agenda.items : [];
    if (agenda.length) {
      const agendaCard = el("div", { class: "card", "data-testid": "agenda" }, el("h2", {}, "Agenda & discussion"));
      for (const item of agenda) {
        agendaCard.append(
          el(
            "div",
            { class: "row" },
            el("div", {}, el("div", { class: "title", text: item.title }), el("div", { class: "meta", text: item.topic || "" })),
            el("button", { type: "button", "data-testid": "seek-agenda", onclick: () => seekTo((item.start_ms || 0) / 1000) }, formatDuration(item.start_ms)),
          ),
        );
      }
      card.append(agendaCard);
    }

    const sections = bundle.sections || [];
    if (sections.length) {
      const sectionCard = el("div", { class: "card", "data-testid": "sections" }, el("h2", {}, "Sections"));
      const summaries = new Map((bundle.section_summaries || []).map((doc) => [doc.section_id, doc.payload || {}]));
      for (const section of sections) {
        const payload = summaries.get(section.id) || {};
        sectionCard.append(
          el(
            "div",
            { class: "row" },
            el(
              "div",
              {},
              el("div", { class: "title", text: `${section.ordinal}. ${section.title}` }),
              el("div", { class: "meta", text: payload.summary || section.summary || "" }),
            ),
            el("button", { type: "button", "data-testid": "seek-section", onclick: () => seekTo((section.start_ms || 0) / 1000) }, formatDuration(section.start_ms)),
          ),
        );
      }
      card.append(sectionCard);
    }

    card.append(citationsCard("Decisions", bundle.decisions, "decision", "decided_by", segmentsById));
    card.append(citationsCard("Action items", bundle.action_items, "action", "owner", segmentsById));
    return card;
  }

  async function qa() {
    const card = el("div", { class: "card", "data-testid": "qa" });
    const question = el("input", {
      type: "text",
      "aria-label": "Ask about this meeting",
      placeholder: "What did we decide about the release date?",
    });
    const askButton = el(
      "button",
      {
        class: "primary",
        type: "button",
        "data-testid": "ask",
        onclick: async () => {
          const value = question.value.trim();
          if (!value) return;
          askButton.disabled = true;
          try {
            await ctx.api.ask(meetingId, value);
            ctx.toast("Question queued — it appears when the analysis worker answers it.");
            question.value = "";
            await renderTab();
          } catch (err) {
            ctx.toast(friendlyError(err));
          } finally {
            askButton.disabled = false;
          }
        },
      },
      "Ask",
    );
    card.append(el("h2", {}, "Ask about this meeting"), el("div", { class: "toolbar" }, question, askButton));
    const page = await ctx.api.qa(meetingId);
    const host = el("div", { class: "rows", "data-testid": "qa-history" });
    if (!page.items.length) host.append(el("div", { class: "empty", text: "No questions asked yet." }));
    for (const doc of page.items) {
      const payload = doc.payload || {};
      const citation = el("div", { class: "citation" });
      for (const [index, cite] of (payload.citations || []).slice(0, 5).entries()) {
        const button = el(
          "button",
          { type: "button", title: cite.text || "", "data-testid": "qa-citation", onclick: () => seekTo((cite.start_ms || 0) / 1000) },
          `${index + 1}. ${formatDuration(cite.start_ms)}`,
        );
        button.dataset.segment = cite.segment_id;
        citation.append(button);
      }
      host.append(
        el(
          "div",
          { class: "row" },
          el(
            "div",
            {},
            el("div", { class: "title", text: payload.question || "(question)" }),
            el("div", { class: "meta", "data-testid": "qa-answer", text: payload.answer || "" }),
            citation,
          ),
          doc.stale ? el("span", { class: "pill stale", text: "Outdated" }) : null,
        ),
      );
    }
    card.append(host);
    return card;
  }

  async function speakers() {
    const list = await ctx.api.speakers(meetingId);
    const card = el("div", { class: "card", "data-testid": "speakers" }, el("h2", {}, "Speakers"));
    if (!list.length) card.append(el("div", { class: "empty", text: "No speakers on this transcript." }));
    for (const speaker of list) {
      const input = el("input", { type: "text", value: speaker.label, "aria-label": `Rename ${speaker.label}` });
      const others = list.filter((other) => other.id !== speaker.id);
      const mergeInto = el(
        "select",
        { "aria-label": `Merge ${speaker.label} into` },
        el("option", { value: "" }, "Merge into…"),
        ...others.map((other) => el("option", { value: other.id }, other.label)),
      );
      card.append(
        el(
          "div",
          { class: "row", dataset: { speaker: speaker.id } },
          el(
            "div",
            {},
            el("div", { class: "title", text: speaker.label }),
            el("div", { class: "meta", text: `${speaker.segment_count} segment(s) · ${formatDuration(speaker.speaking_ms)}` }),
          ),
          el(
            "div",
            { class: "actions" },
            input,
            el(
              "button",
              {
                type: "button",
                "data-testid": "rename-speaker",
                onclick: async () => {
                  try {
                    await ctx.api.renameSpeaker(speaker.id, input.value.trim());
                    ctx.toast("Speaker renamed.");
                    await renderTab();
                    await loadHead();
                  } catch (err) {
                    ctx.toast(friendlyError(err));
                  }
                },
              },
              "Rename",
            ),
            mergeInto,
            el(
              "button",
              {
                type: "button",
                "data-testid": "merge-speaker",
                onclick: async () => {
                  if (!mergeInto.value) return;
                  try {
                    await ctx.api.mergeSpeaker(speaker.id, mergeInto.value);
                    ctx.toast("Speakers merged.");
                    await renderTab();
                    await loadHead();
                  } catch (err) {
                    ctx.toast(friendlyError(err));
                  }
                },
              },
              "Merge",
            ),
            el(
              "button",
              {
                class: "danger",
                type: "button",
                "data-testid": "delete-speaker",
                onclick: async () => {
                  try {
                    await ctx.api.deleteSpeaker(speaker.id);
                    ctx.toast("Speaker removed.");
                    await renderTab();
                    await loadHead();
                  } catch (err) {
                    ctx.toast(friendlyError(err));
                  }
                },
              },
              "Delete",
            ),
          ),
        ),
      );
    }
    return card;
  }

  async function versions() {
    const page = await ctx.api.transcripts(meetingId, { limit: 50 });
    const card = el("div", { class: "card", "data-testid": "versions" }, el("h2", {}, "Transcript versions"));
    if (!page.items.length) card.append(el("div", { class: "empty", text: "No transcript versions yet." }));
    for (const version of page.items) {
      card.append(
        el(
          "div",
          { class: "row", dataset: { version: version.id } },
          el(
            "div",
            {},
            el("div", { class: "title", text: `#${version.version} · ${version.source} · ${version.segment_count} segment(s)` }),
            el("div", { class: "meta", text: [version.provider, version.language, formatDate(version.created_at)].filter(Boolean).join(" · ") }),
          ),
          el(
            "div",
            { class: "actions" },
            version.is_active ? el("span", { class: "pill", dataset: { kind: "ready" }, "data-testid": "active-version", text: "Active" }) : null,
            version.is_active
              ? null
              : el(
                  "button",
                  {
                    type: "button",
                    "data-testid": "activate-version",
                    onclick: async () => {
                      try {
                        await ctx.api.activateTranscript(version.id);
                        ctx.toast("Transcript activated.");
                        await renderTab();
                        await loadHead();
                      } catch (err) {
                        ctx.toast(friendlyError(err));
                      }
                    },
                  },
                  "Activate",
                ),
            el(
              "button",
              {
                class: "danger",
                type: "button",
                "data-testid": "delete-version",
                onclick: () => confirmDeleteVersion(version),
              },
              "Delete",
            ),
          ),
        ),
      );
    }
    return card;
  }

  async function jobs() {
    const page = await ctx.api.jobs(meetingId);
    const card = el("div", { class: "card", "data-testid": "jobs" }, el("h2", {}, "Jobs"));
    if (!page.items.length) card.append(el("div", { class: "empty", text: "No jobs for this meeting." }));
    const events = el("pre", { class: "meta", "data-testid": "job-events", text: "" });
    for (const job of page.items) {
      card.append(
        el(
          "div",
          { class: "row", dataset: { job: job.id } },
          el(
            "div",
            {},
            el("div", { class: "title", text: `${job.type} — ${job.state}${job.is_mock ? " (mock)" : ""}` }),
            el("div", { class: "meta", text: `${Math.round((job.progress || 0) * 100)}%${job.error ? ` · ${job.error}` : ""}` }),
          ),
          el(
            "div",
            { class: "actions" },
            isProcessing(job.state)
              ? el(
                  "button",
                  {
                    class: "danger",
                    type: "button",
                    "data-testid": "cancel-job",
                    onclick: async () => {
                      try {
                        await ctx.api.cancelJob(job.id);
                        ctx.toast("Cancellation requested.");
                        await renderTab();
                        await loadHead();
                      } catch (err) {
                        ctx.toast(friendlyError(err));
                      }
                    },
                  },
                  "Cancel",
                )
              : null,
            el("button", { type: "button", "data-testid": "watch-job", onclick: () => watchJob(job.id, events) }, "Watch"),
          ),
        ),
      );
    }
    card.append(el("h3", {}, "Live events"), events);
    return card;
  }

  // SSE needs the bearer header, which EventSource cannot send: read the stream with fetch().
  async function watchJob(jobId, output) {
    output.textContent = "";
    try {
      const res = await fetch(`/v1/jobs/${jobId}/events`, { headers: { Authorization: `Bearer ${ctx.api.getToken()}` } });
      if (!res.ok || !res.body) throw new Error(String(res.status));
      const reader = res.body.getReader();
      const decoder = new TextDecoder();
      for (;;) {
        const { value, done } = await reader.read();
        if (done) break;
        output.textContent += decoder.decode(value, { stream: true });
      }
    } catch {
      // Fall back to polling: the stream closes on a terminal state and may end on idle proxies.
      const job = await ctx.api.job(jobId).catch(() => null);
      output.textContent += `\n(poll) ${job ? `${job.type} ${job.state} ${Math.round((job.progress || 0) * 100)}%` : "unavailable"}\n`;
    }
  }

  // ---------------------------------------------------------------- mount

  const view = el("div", {}, head, playerBar, tabs, body);
  window.addEventListener(
    "hashchange",
    () => {
      state.disposed = true;
      clearTimeout(state.pollTimer);
      releaseBlobs();
    },
    { once: true },
  );

  await loadHead();
  await loadPlayerBar();
  await renderTab();
  schedulePoll();
  return view;
}
