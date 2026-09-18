# How alignment works

Subtitle Aligner has two workflows because a fixed delay and locally mistimed cues are different problems. Both compare subtitle text with speech detected in the media, but they collect and apply evidence differently.

## Shift entire track

**Shift entire track** is intended for a subtitle whose timing error is consistent from beginning to end.

The plugin starts with five cue-dense 30-second windows spread across the title. It can add up to four more windows when the first result is ambiguous. Each window produces two independent measurements:

1. a text-derived offset from matching subtitle words to the transcription; and
2. a speech-activity offset from comparing subtitle dialogue intervals with detected speech.

A correction is written only when at least five reliable windows support one high-confidence constant offset and both measurements agree. A small overall median by itself is not enough.

The sampled evidence is also tested for progressive drift and a mid-title timing break. Those findings are report-only because one global shift cannot fix them safely. The result instead recommends **Align individual cues**.

This workflow is used by the scheduled library task. Automatic runs inspect external subtitle sidecars only; embedded tracks are available through an explicit one-title action.

## Align individual cues

**Align individual cues** processes the complete audio in overlapping sections. The default sections are 120 seconds long with a 15-second overlap, which avoids losing dialogue near a section boundary.

Every eligible subtitle cue receives its own set of match candidates. The matcher chooses an ordered sequence across the title, so a repeated phrase cannot pull a later cue in front of an earlier one. It can skip a tempting match when doing so preserves a stronger sequence overall.

An accepted cue uses only timing evidence linked to that cue. The plugin does not average, smooth, or interpolate corrections between cues. A cue without a reliable individual match remains unchanged.

Individual-cue correction changes the cue's in-time and preserves its authored out-time. A later onset is rejected if that would leave less than one second of display time. Signs, typesetting, songs, karaoke, and other non-dialogue roles are excluded.

## Text matching

Exact normalized text is the strongest evidence. Normalization removes punctuation and harmless formatting differences while preserving word order.

When exact matching fails, the optional close-text matcher can compare:

- ordered normalized tokens;
- common English inflections;
- close spelling;
- low-weight phonetic similarity; and
- the score gap between the best and second-best candidate.

The close-text path must still match enough distinctive words and produce a unique nearby candidate. Authored timing helps rank otherwise credible candidates, but it is never copied into the correction.

The phrase score, spelling threshold, distinctive-token count, runner-up gap, search radius, stemming, and phonetic weight are available under **Advanced analysis settings**.

## Timing targets

For prerecorded dialogue, the target is the start of the spoken audio. Subtitle Aligner does not add an intentional lead or lag.

Timing evidence has a no-change band. A reliable same-language match within 100 ms is treated as confirmation that the cue is already aligned. The default cross-language band is 750 ms because translated timing is less precise. A match inside its applicable band is reported as verified, not rewritten.

If direct cue matches reveal one coherent whole-track bias, the individual-cue workflow does not recreate that shift as many local edits. When at least three quarters of the matches support the same meaningful offset, it recommends **Shift entire track** instead.

## Same-language audio

For subtitles in the same language as the dialogue, individual-cue alignment requires genuine word or token timing from the speech engine. Segment timing alone is not precise enough to retime individual cues.

The text match selects the spoken passage and its word timing supplies the onset. Semantic confidence and timing confidence are evaluated separately; the lower of the two controls whether the correction is eligible.

## Translated subtitles

Hand-authored translations rarely repeat the spoken language word for word. English subtitles over non-English speech therefore need more than an approximate translated timestamp.

A compatible speech service returns:

- paired source-language and English translation segments;
- an independently decoded English translation segment; and
- genuine voice-activity detection intervals.

The subtitle text identifies the translated passage. The source-linked segment provides the onset only when the independent decoder places the same passage within 750 ms and voice activity confirms speech in that region. Approximate translated-word timing is diagnostic information, not the timing authority.

A match can verify a cue without moving it. The plugin protects the authored timing when opening subtitle words are unmatched, when a plausible opening fragment overlaps an earlier dialogue cue, or when the timing channels disagree. Cross-language timing confidence is capped at medium.

Other language pairs are reported as inconclusive rather than guessed.

## Confidence and results

Results distinguish between:

- cues verified inside the no-change band;
- cues that received a correction;
- cues whose text matched but whose onset was protected by a safety rule; and
- cues with insufficient evidence.

The result card also shows scan time relative to the title runtime. **Stats for nerds** plots every subtitle cue across the title: accepted direct matches appear as evidence points, corrected cues show their movement, and unchanged cues remain on the original-timing line.

## Sidecar handling

When sidecar creation is enabled, the plugin writes a new filename containing `.subalign` beside the video. It never edits the source media or source subtitle.

An existing corrected sidecar is replaced only when its stored content hash proves that Subtitle Aligner created it. If the file was changed elsewhere, the plugin leaves it in place and reports the ownership conflict. Disabling corrected copies does not delete an existing `.subalign` file.

The media directory must allow Jellyfin to create and replace the plugin-owned sidecar. Permission and ownership errors are shown in full to administrators and users who can edit subtitles.
