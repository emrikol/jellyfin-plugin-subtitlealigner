# Changelog

Notable changes are documented here. Versions use Jellyfin's four-part plugin version convention.

## Unreleased

## 0.4.0.11 — 2026-09-17

- Cached complete, validated speech-service responses by exact audio content, language, timing requirements, selected model, backend, and server build so deterministic reruns do not repeat inference.
- Added seven-day sliding retention, a 2 GB least-recently-used size ceiling, and a daily Jellyfin maintenance task; both limits are configurable.
- Added cache usage, clear-cache controls, and a per-run **Retranscribe audio** option to the administrator settings page.
- Kept cache failures non-fatal, discarded corrupt or incompatible entries, and stored only timestamped response data—not extracted audio.
- Added a concise public guide with synthetic interface examples and moved detailed matching behavior into a focused alignment document.
- Separated timeline direction labels from timing-scale values for easier reading.

## 0.4.0.10 — 2026-09-17

- Bound direct cue corrections to their original subtitle timing rows so excluded sound-effect, lyric, or otherwise non-matchable rows cannot shift later corrections onto the wrong cues.

## 0.4.0.9 — 2026-09-17

- Sent required timing granularities and dual-output needs during route capability discovery, allowing `model=auto` speech services to select a compatible backend before media upload.

## 0.4.0.8 — 2026-09-17

- Reworded legacy blocked-sidecar records as neutral ownership warnings so an earlier plugin failure is not presented as a definite outside edit.

## 0.4.0.7 — 2026-09-17

- Recognized an original-timing ASS sidecar after a harmless one-centisecond muxer round trip only when every non-timing field remains byte-for-byte identical.

## 0.4.0.6 — 2026-09-17

- Recovered interrupted plugin-owned sidecar resets only when the on-disk file exactly matches the original subtitle, preserving any genuinely edited file.
- Presented sidecar ownership conflicts as amber cleanup warnings without marking a successfully completed alignment as failed.
- Counted only analysis and correction errors as run failures; sidecar cleanup warnings remain visible under needs attention.

## 0.4.0.5 — 2026-09-17

- Retried transient external speech-service failures twice with short bounded backoff before using the slower NAS engine.
- Reported the current remote attempt in live per-title progress so recovery is visible rather than appearing stalled.

## 0.4.0.4 — 2026-09-17

- Stopped waiting for the full request timeout when a speech service reports inference complete but does not promptly deliver the response; the current section now falls back to the NAS after a bounded grace period.
- Made live status distinguish result delivery, NAS fallback, and cue matching, and replaced misleading zero-second ETAs with an explicit wait state when a section outlives its estimate.
- Closed restored sidecar files before hashing them, preventing a successful reset from being reported as a file-in-use error.

## 0.4.0.3 — 2026-09-17

- Added conservative default cross-language fuzzy matching for hand-authored translations, using ordered token, inflection, spelling, and low-weight phonetic evidence instead of requiring near-verbatim wording.
- Used reliable source-linked segment onset for cross-language timing while treating genuine VAD as independent speech confirmation and approximate translated-word timing as diagnostic only.
- Evaluated each individual cue independently, rejected source onsets that fall inside an earlier authored dialogue cue, and limited local candidates to two seconds from the authored onset by default.
- Lowered the translated cue-coverage default to 35% while requiring distinctive words, unique nearby evidence, monotonic ordering, and a conservative 750 ms no-change band.
- Classified direct-cue confidence from accepted independent matches rather than whole-title coverage, since unmatched cues are never moved or interpolated.
- Refused individual corrections when a strong majority of direct matches reveal one coherent whole-track bias, with guidance to use **Shift entire track** instead.
- Required source-linked translation onset to agree with the independently decoded translation segment before moving a cross-language cue; VAD remains separate speech-presence evidence.
- Preserved hand-authored timing when the match omits opening subtitle words, when a plausible opening fragment overlaps the preceding cue, or when moving the start would leave less than one second before the unchanged out-time.
- Reported safety-protected text matches separately from cues verified inside the no-change band and cues that actually received a correction.

## 0.4.0.2 — 2026-09-17

- Used request-status polling for inference progress so every advertised backend can report progress without requiring streaming `verbose_json` support.
- Restored external English transcription and cross-language dual-output alignment by keeping inference requests non-streaming while preserving live progress, ETA, and remote cancellation.
- Updated the shared speech-service contract and conformance checker to distinguish portable progress polling from optional route-specific event streaming.

## 0.4.0.1 — 2026-09-17

- Negotiated route-specific segment or word timing with compatible external speech servers before uploading audio.
- Preserved selected-model, language, timing-provenance, speech-activity, confidence, cache, performance, warning, and stable error metadata from extended speech responses.
- Refused individual-cue corrections based on approximate translated word timestamps; translated text now identifies the utterance region while VAD independently supplies the cross-language cue onset.
- Preserved non-dialogue roles during parsing and left signs, typesetting, songs, karaoke, opening/ending lyrics, and overlapping dialogue unchanged.
- Reported semantic and timing confidence separately, with correction eligibility determined by the lower rating.
- Added a versioned backend-neutral speech-service extension contract, strict capability validation, and one live conformance checker for external and NAS implementations. The checker now exercises route resolution, every advertised timing channel, translation, optional dual output, explicit unavailable-feature errors, provenance, performance/cache metadata, and stable error codes.
- Implemented that contract in the bundled NAS-local whisper.cpp service, including genuine word/VAD timing, optional paired translation output, route and performance provenance, streamed progress, active idempotent cancellation, and explicit schema/error handling.
- Made NAS VAD intervals strictly request-scoped so a no-speech request cannot inherit speech boundaries from an earlier inference, with a same-context speech-to-silence regression test.
- Kept fuzzy matching enabled for same-language transcription while requiring strict normalized text matches for translated subtitle alignment by default; higher-risk cross-language fuzzy matching is now a separate advanced opt-in.
- Required translated-text matches to cover at least 75% of a subtitle cue by default, preventing a short shared phrase from moving a longer cue to the wrong nearby utterance.
- Rejected subtitle matches that begin partway through a translated segment, because that segment's VAD onset locates only its first utterance and cannot time a later combined cue.
- Required paired source/translation output for cross-language individual-cue runs, rejected mismatched segment IDs, and combined translation text with source-language timing instead of approximate translated timing.
- Assigned each translated segment to at most one source segment in the NAS service, preventing one English phrase from becoming duplicate timing evidence at several source-language onsets.
- Added separate measured no-change bands for genuine word timing (100 ms) and coarser translated-text + VAD evidence (750 ms), with explicit advanced settings and conservative legacy defaults.
- Added a pinned, checksum-verified Silero VAD model to managed model installation so the local backend can provide real speech intervals rather than relabeling decoder segment boundaries.
- Made the Linux x86-64 engine build apply one reviewed patch to the exact whisper.cpp v1.9.4 source and produce the same checksum from different absolute build paths.
- Streamed validated inference progress from compatible speech services into Jellyfin's live progress bar and ETA, propagated user cancellation to the active remote request, and rejected mismatched, incomplete, or regressing progress streams.
- Retried the current inference section once on the NAS after a transient external transport, overload, or backend failure without masking contract errors or cancellation.
- Required independent speech-activity correlation across at least five sampled windows to corroborate a text-derived whole-track shift, while excluding lyrics and signs from the activity signal.
- Reported the whole-track timing evidence, activity offset, window count, and correlation without exposing subtitle text.
- Corrected CRLF handling so SRT, WebVTT, ASS, and SSA timing lines retain their original line endings when a sidecar is written.
- Ranked credible duplicate text candidates with a timing prior without using authored timing as the correction value.
- Replaced greedy crossed-match cleanup with deterministic global monotonic selection and explicit cue skips.
- Added synthetic regression coverage for constant shifts, progressive drift, mid-title timing jumps, nearby paraphrases, contractions, and globally consistent cue sequences.
- Fixed timing-break detection when a constant model's median residual hid a badly aligned minority region; model comparison now uses p90 residuals.
- Advanced the analysis fingerprint so saved results are recomputed under the new timing-evidence and matcher rules.

## 0.4.0.0 — 2026-09-16

- Replaced sparse timing-map interpolation with direct, independent matching for every subtitle cue in an individual-cue run.
- Applied only the offset supported by each accepted cue's own speech-and-text match; unmatched cues remain unchanged and never inherit a nearby correction.
- Displayed every direct cue match on the evidence timeline without a connecting line that implies smoothing or interpolation.
- Reported exact, close, and unmatched cue counts in plain language and removed obsolete timing-map controls from the settings screen.
- Targeted prerecorded speech timing without adding an intentional caption lead or lag, while preserving each cue's authored out-time.

## 0.3.3.3 — 2026-09-16

- Consolidated pre-0.3.3.1 embedded-stream result identities with their stable subtitle ordinals so one physical subtitle no longer appears as two alignment results.
- Preserved verified plugin-owned legacy sidecars during later corrections instead of creating a second stable-name sidecar beside them.
- Drew every adjusted cue above the correction line with a visible movement mark, kept unchanged cues visible on the original-timing line, and added exact cue counts to the legend.
- Labeled the smaller timestamp ledger as evidence checkpoints so it is no longer mistaken for the complete cue set.

## 0.3.3.2 — 2026-09-16

- Renamed the title actions to **Shift entire track** and **Align individual cues**, with copy that distinguishes sampled windows from complete-audio processing.
- Limited whole-track sidecar creation to high-confidence constant offsets; detected drift and timing breaks now remain report-only and direct users to individual-cue alignment.
- Added every subtitle cue to the evidence timeline so adjusted and unchanged cues appear across the complete title alongside the larger speech-and-text checkpoints.
- Distinguished matched speech passages, smoothed correction checkpoints, and cues adjusted from the timing map throughout the result UI.
- Reconstructed cue-level visualization data for older corrected results without rerunning inference.

## 0.3.3.1 — 2026-09-16

- Identified embedded subtitle tracks by their stable subtitle ordinal instead of Jellyfin's mutable combined media-stream index.
- Named generated embedded-track sidecars with the stable subtitle ordinal so adding a sidecar cannot create another apparent source track.
- Recovered title duration from Jellyfin when an older saved result predates runtime tracking, allowing its existing timing checkpoints to render immediately.
- Applied the historical 90-second support default when an older Full alignment did not persist that setting.

## 0.3.3.0 — 2026-09-16

- Added an optional evidence-checkpoint timeline to completed Full alignment results.
- Plotted the saved local timing correction across the complete title, including adjusted regions, unsupported gaps, and every reliable checkpoint.
- Added a responsive checkpoint ledger with the timestamp and applied offset for each evidence point.
- Kept timing evidence behind the existing authorized result endpoint without exposing subtitle text or media paths.

## 0.3.2.1 — 2026-09-16

- Replaced embedded stream identifiers and language codes in subtitle-editor results with human-readable subtitle labels.
- Reorganized saved findings around the outcome, confidence, safely aligned cue coverage, timing adjustment, evidence, and scan cost.
- Recorded analysis time and compared it with the title runtime, including the runtime percentage and real-time speed.
- Added determinate live progress with elapsed time and an estimated time remaining for one-title runs.
- Added scan cost to recent-result evidence in the administrator dashboard.

## 0.3.2.0 — 2026-09-16

- Added a conservative fuzzy recovery pass for Full alignment sections that strict word matching could not place.
- Restricted fuzzy candidates to a window predicted by strict timing anchors and rejected candidates that disagree with that timing model.
- Added tunable phrase, spelling, stemming, phonetic, uniqueness, timing-agreement, search-radius, and cue-support controls under Advanced analysis settings.
- Reported strict and guided timing anchors separately in the dashboard and subtitle editor.

## 0.3.1.5 — 2026-09-16

- Resolved the item Jellyfin loads for its subtitle dialog without scanning the entire library.
- Validated the candidate item's media filename on the server before returning results or queueing work.
- Retained the exact-filename lookup as a safe fallback when Jellyfin does not expose the loaded item.

## 0.3.1.4 — 2026-09-16

- Bound saved results and alignment actions to the file displayed by Jellyfin's subtitle dialog instead of the background page URL.
- Reloaded the integration when Jellyfin reuses one subtitle-dialog element for another title.
- Prevented a grid or modal workflow from displaying or queueing work for the previously open title.

## 0.3.1.3 — 2026-09-16

- Labeled every saved result as a completed Quick check or Full alignment.
- Clarified full-alignment evidence, including analyzed sections, timing anchors, cue-map coverage, median timing offset, and local-map residuals.
- Removed the duplicated correction-status prefix from sidecar result messages.
- Mapped embedded subtitles by their FFmpeg subtitle ordinal instead of Jellyfin's incompatible stream index, preventing attached fonts from being sent to the ASS muxer.
- Preserved the requested alignment mode on failed results so full-pass errors are identified correctly.

## 0.3.1.2 — 2026-09-16

- Sent the virtual `auto` model ID to OpenAI-compatible speech servers so they can route transcription and translation independently.
- Preserved the known source-language hint, including `ja` for Japanese audio translated to English subtitles.

## 0.3.1.1 — 2026-09-16

- Sent the virtual `default` model ID to OpenAI-compatible speech servers so they can choose an appropriate model for transcription or translation.
- Kept the NAS-native engine's explicit administrator-selected model unchanged.

## 0.3.1.0 — 2026-09-16

- Improved settings guidance, dependent-control states, validation feedback, and long-content handling.
- Prevented overlapping dashboard status polls on slow or unavailable servers.
- Added public installation, compatibility, privacy, troubleshooting, contribution, and security documentation.
- Added build validation, CodeQL scanning, dependency updates, and structured issue templates.
- Updated Microsoft.Extensions.Http to 10.0.12 and made disposable singleton resources explicit.

## 0.3.0.3 — 2026-09-16

- Added full local subtitle alignment alongside the fast whole-track check.
- Added trusted external Whisper servers using native whisper.cpp or OpenAI-compatible transcription APIs.
- Added local fallback when an external server is unavailable or lacks speech-to-English translation.
- Allowed corrected sidecar creation without delete-child permission.
- Showed full operational errors to administrators and users allowed to manage subtitles.

## 0.2.0.6 — 2026-09-16

- Added resumable library scans, one-title runs, host-aware model selection, model benchmarking, and managed model verification.
- Added native Jellyfin subtitle-editor actions with live progress and saved findings.
- Added conservative fixed-offset, drift, and timing-break classification.
- Renamed generated subtitle copies to `.subalign` sidecars.
