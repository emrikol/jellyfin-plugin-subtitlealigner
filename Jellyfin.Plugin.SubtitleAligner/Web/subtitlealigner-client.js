(() => {
    'use strict';

    const dialogMarker = 'subtitleAlignerReady';
    const clientMarker = '__subtitleAlignerWrapped';
    const recentItemsByFileName = new Map();
    const etaByRun = new Map();
    let timelineSequence = 0;
    document.documentElement.dataset.subtitleAlignerClient = 'loaded';

    function ensureStyles() {
        if (document.querySelector('#subtitleAlignerEditorStyles')) return;

        const style = document.createElement('style');
        style.id = 'subtitleAlignerEditorStyles';
        style.textContent = `
            .subtitleAlignerEditorAction { margin-bottom: 2em; }
            .subtitleAlignerEditorActions { display: flex; flex-wrap: wrap; gap: .75em; margin-top: .75em; }
            .subtitleAlignerEditorLive { max-width: 52em; margin-top: 1.25em; }
            .subtitleAlignerEditorLiveHeader { display: flex; justify-content: space-between; gap: 1em; align-items: baseline; }
            .subtitleAlignerEditorPhase { font-weight: 600; }
            .subtitleAlignerEditorElapsed { opacity: .72; font-variant-numeric: tabular-nums; }
            .subtitleAlignerEditorMessage { margin: .3em 0 .65em; }
            .subtitleAlignerEditorProgress { display: block; width: 100%; height: .6em; accent-color: #00a4dc; }
            .subtitleAlignerEditorProgress::-moz-progress-bar { background: #00a4dc; }
            .subtitleAlignerEditorProgress::-webkit-progress-value { background: #00a4dc; }
            .subtitleAlignerEditorProgress[hidden] { display: none; }
            .subtitleAlignerEditorProgressMeta { display: flex; justify-content: space-between; gap: 1em; margin-top: .35em; font-size: .9em; opacity: .8; font-variant-numeric: tabular-nums; }
            .subtitleAlignerEditorProgressMeta[hidden] { display: none; }
            .subtitleAlignerEditorResults { display: grid; gap: .75em; margin-top: 1em; }
            .subtitleAlignerEditorResult { min-width: 0; padding: 1.1em 1.25em; border: 1px solid rgba(255, 255, 255, .18); border-radius: .25em; background: rgba(255, 255, 255, .055); }
            .subtitleAlignerEditorResultHeader { display: flex; align-items: flex-start; justify-content: space-between; gap: 1em; }
            .subtitleAlignerEditorResult h3 { margin: 0; font-size: 1.05em; overflow-wrap: anywhere; }
            .subtitleAlignerEditorResult p { margin: .4em 0 0; }
            .subtitleAlignerEditorTrack { margin-top: .2em !important; opacity: .76; }
            .subtitleAlignerEditorConfidence { flex: none; padding: .25em .65em; border: 1px solid currentColor; border-radius: 999px; color: #a6e3a1; font-size: .82em; font-weight: 600; white-space: nowrap; }
            .subtitleAlignerEditorSummary { max-width: 72ch; margin-top: .8em !important; }
            .subtitleAlignerCoverage { margin-top: 1em; }
            .subtitleAlignerCoverageHeader { display: flex; justify-content: space-between; gap: 1em; margin-bottom: .35em; font-size: .92em; }
            .subtitleAlignerCoverageHeader strong { font-variant-numeric: tabular-nums; }
            .subtitleAlignerCoverage progress { display: block; width: 100%; height: .45em; accent-color: #52b954; }
            .subtitleAlignerCoverage progress::-moz-progress-bar { background: #52b954; }
            .subtitleAlignerCoverage progress::-webkit-progress-value { background: #52b954; }
            .subtitleAlignerCoverageNote { margin-top: .35em !important; font-size: .9em; opacity: .76; }
            .subtitleAlignerMetricGrid { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); margin: 1em -1.25em -.1em; border-top: 1px solid rgba(255, 255, 255, .12); }
            .subtitleAlignerMetric { min-width: 0; margin: 0; padding: .85em 1.25em; border-right: 1px solid rgba(255, 255, 255, .12); }
            .subtitleAlignerMetric:last-child { border-right: 0; }
            .subtitleAlignerMetric dt { margin: 0 0 .3em; opacity: .66; font-size: .76em; font-weight: 600; letter-spacing: .04em; text-transform: uppercase; }
            .subtitleAlignerMetric dd { margin: 0; }
            .subtitleAlignerMetricValue { display: block; font-size: 1.08em; font-weight: 600; font-variant-numeric: tabular-nums; }
            .subtitleAlignerMetricDetail { display: block; margin-top: .18em; opacity: .72; font-size: .84em; line-height: 1.35; }
            .subtitleAlignerEditorResultMeta { opacity: .76; }
            .subtitleAlignerEditorWarning { margin: 1em -1.25em -.1em; padding: .85em 1.25em 0; border-top: 1px solid rgba(232, 163, 23, .48); color: #f2c36b; }
            .subtitleAlignerEditorWarning strong { display: block; }
            .subtitleAlignerEditorWarning p { max-width: 72ch; color: #ead9b8; overflow-wrap: anywhere; }
            .subtitleAlignerEditorTechnical { margin-top: .8em; }
            .subtitleAlignerEditorTechnical summary { cursor: pointer; color: inherit; opacity: .78; }
            .subtitleAlignerEditorTechnical p { overflow-wrap: anywhere; }
            .subtitleAlignerTimeline { margin: 1em -1.25em -1.1em; border-top: 1px solid rgba(255, 255, 255, .12); }
            .subtitleAlignerTimeline > summary { display: flex; align-items: center; justify-content: space-between; gap: 1em; padding: 1em 1.25em; cursor: pointer; list-style: none; font-weight: 600; }
            .subtitleAlignerTimeline > summary::-webkit-details-marker { display: none; }
            .subtitleAlignerTimelineSummary { display: flex; align-items: baseline; gap: .75em; }
            .subtitleAlignerTimelineHint { opacity: .62; font-size: .85em; font-weight: 400; }
            .subtitleAlignerTimelineChevron { width: .62em; height: .62em; border-right: 2px solid currentColor; border-bottom: 2px solid currentColor; opacity: .68; transform: rotate(45deg); transition: transform 180ms ease-out; }
            .subtitleAlignerTimeline[open] .subtitleAlignerTimelineChevron { transform: rotate(225deg); }
            .subtitleAlignerTimelinePanel { padding: .1em 1.25em 1.25em; }
            .subtitleAlignerTimelineHeading { display: flex; justify-content: space-between; gap: 1em; align-items: end; margin-bottom: .75em; }
            .subtitleAlignerTimelineHeading h4 { margin: 0; font-size: 1em; }
            .subtitleAlignerTimelineHeading p { max-width: 70ch; margin: .25em 0 0; opacity: .72; font-size: .88em; line-height: 1.45; }
            .subtitleAlignerTimelineLegend { display: flex; flex-wrap: wrap; gap: .75em 1em; opacity: .72; font-size: .8em; }
            .subtitleAlignerTimelineLegend span { display: inline-flex; align-items: center; gap: .38em; }
            .subtitleAlignerTimelineSwatch { width: .5em; height: .5em; border-radius: 50%; background: #52b954; }
            .subtitleAlignerTimelineSwatchUnchanged { border: 1px solid rgba(255, 255, 255, .38); background: rgba(255, 255, 255, .18); }
            .subtitleAlignerTimelineSwatchEvidence { width: .5em; height: .5em; border: 2px solid currentColor; border-radius: 50%; background: #a6e3a1; color: #a6e3a1; outline: 2px solid rgba(0, 0, 0, .65); }
            .subtitleAlignerTimelineChart { border-top: 1px solid rgba(255, 255, 255, .11); border-bottom: 1px solid rgba(255, 255, 255, .11); background: rgba(0, 0, 0, .12); }
            .subtitleAlignerTimelineChart svg { display: block; width: 100%; height: auto; }
            .subtitleAlignerTimelineGrid { stroke: rgba(255, 255, 255, .11); stroke-width: 1; }
            .subtitleAlignerTimelineZero { stroke: rgba(255, 255, 255, .42); stroke-width: 1.25; }
            .subtitleAlignerTimelineSupport { fill: rgba(82, 185, 84, .07); }
            .subtitleAlignerTimelineGap { fill: rgba(0, 0, 0, .09); }
            .subtitleAlignerTimelineLine { fill: none; stroke: #7ed87f; stroke-width: 2.5; stroke-linejoin: round; stroke-linecap: round; }
            .subtitleAlignerTimelineStem { stroke: rgba(255, 255, 255, .4); stroke-width: 1.5; }
            .subtitleAlignerTimelineDot { fill: #b4efb0; stroke: rgba(0, 0, 0, .8); stroke-width: 3; }
            .subtitleAlignerTimelineCueMovement { stroke: #52b954; stroke-width: 1.25; opacity: .42; }
            .subtitleAlignerTimelineCueAdjusted { fill: #b4efb0; stroke: rgba(0, 0, 0, .75); stroke-width: .8; }
            .subtitleAlignerTimelineCueUnchanged { stroke: rgba(255, 255, 255, .52); stroke-width: 1.4; stroke-linecap: round; }
            .subtitleAlignerTimelineAxis { fill: currentColor; opacity: .62; font-size: 12px; }
            .subtitleAlignerTimelineValue { fill: currentColor; font-size: 11px; font-weight: 600; font-variant-numeric: tabular-nums; }
            .subtitleAlignerCheckpointHeading { display: flex; justify-content: space-between; gap: 1em; align-items: baseline; padding: .8em .55em .55em; }
            .subtitleAlignerCheckpointHeading h5 { margin: 0; font-size: .9em; }
            .subtitleAlignerCheckpointHeading p { margin: 0; opacity: .62; font-size: .78em; }
            .subtitleAlignerCheckpointGrid { display: grid; grid-template-columns: repeat(6, minmax(0, 1fr)); border-bottom: 1px solid rgba(255, 255, 255, .11); }
            .subtitleAlignerCheckpoint { min-width: 0; padding: .72em .55em; border-right: 1px solid rgba(255, 255, 255, .11); text-align: center; }
            .subtitleAlignerCheckpoint:last-child { border-right: 0; }
            .subtitleAlignerCheckpoint time { display: block; opacity: .62; font-size: .74em; }
            .subtitleAlignerCheckpoint strong { display: block; margin-top: .18em; font-size: .92em; font-variant-numeric: tabular-nums; }
            .subtitleAlignerTimelineCaption { display: flex; justify-content: space-between; gap: 1em; margin-top: .7em; opacity: .72; font-size: .84em; line-height: 1.45; }
            .subtitleAlignerTimelineCaption strong { opacity: 1; font-weight: 600; }
            .subtitleAlignerEditorResult[data-outcome="good"] { border-color: #52b54b; }
            .subtitleAlignerEditorResult[data-outcome="attention"] { border-color: #e8a317; }
            .subtitleAlignerEditorResult[data-outcome="error"] { border-color: #d9534f; }
            @media (max-width: 42em) {
                .subtitleAlignerEditorResultHeader { align-items: flex-start; }
                .subtitleAlignerMetricGrid { grid-template-columns: repeat(2, minmax(0, 1fr)); }
                .subtitleAlignerMetric:nth-child(2n) { border-right: 0; }
                .subtitleAlignerMetric:nth-child(n+3) { border-top: 1px solid rgba(255, 255, 255, .12); }
                .subtitleAlignerMetric:last-child:nth-child(odd) { grid-column: 1 / -1; border-right: 0; }
                .subtitleAlignerTimelineHeading { display: block; }
                .subtitleAlignerTimelineLegend { margin-top: .65em; }
                .subtitleAlignerCheckpointHeading { display: block; }
                .subtitleAlignerCheckpointHeading p { margin-top: .2em; }
                .subtitleAlignerCheckpointGrid { grid-template-columns: repeat(3, minmax(0, 1fr)); }
                .subtitleAlignerCheckpoint:nth-child(3n) { border-right: 0; }
                .subtitleAlignerCheckpoint:nth-child(n+4) { border-top: 1px solid rgba(255, 255, 255, .11); }
                .subtitleAlignerTimelineCaption { display: block; }
                .subtitleAlignerTimelineCaption span { display: block; margin-top: .25em; }
            }
            @media (max-width: 28em) {
                .subtitleAlignerEditorResultHeader { display: block; }
                .subtitleAlignerEditorConfidence { display: inline-block; margin-top: .65em; }
                .subtitleAlignerEditorProgressMeta { display: block; }
                .subtitleAlignerEditorProgressEta { display: block; margin-top: .15em; }
                .subtitleAlignerTimelineHint { display: none; }
            }
        `;
        document.head.append(style);
    }

    function value(object, camelName, pascalName) {
        return object?.[camelName] ?? object?.[pascalName];
    }

    function displayNumber(number, digits = 2) {
        if (number === null || number === undefined || number === '') return '';
        const parsed = Number(number);
        return Number.isFinite(parsed) ? parsed.toFixed(digits) : '';
    }

    function displaySignedNumber(number, digits = 2) {
        if (number === null || number === undefined || number === '') return '';
        const parsed = Number(number);
        if (!Number.isFinite(parsed)) return '';
        const magnitude = Math.abs(parsed).toFixed(digits);
        if (Number(magnitude) === 0) return magnitude;
        return parsed > 0 ? `+${magnitude}` : `−${magnitude}`;
    }

    function displayTimestamp(seconds) {
        const parsed = Number(seconds);
        if (!Number.isFinite(parsed) || parsed < 0) return '';
        const rounded = Math.round(parsed);
        const hours = Math.floor(rounded / 3600);
        const minutes = Math.floor((rounded % 3600) / 60);
        const remainingSeconds = rounded % 60;
        return [hours, minutes, remainingSeconds]
            .map(part => String(part).padStart(2, '0'))
            .join(':');
    }

    function displayDuration(seconds) {
        const parsed = Number(seconds);
        if (!Number.isFinite(parsed) || parsed < 0) return '';
        const rounded = Math.round(parsed);
        const hours = Math.floor(rounded / 3600);
        const minutes = Math.floor((rounded % 3600) / 60);
        const remainingSeconds = rounded % 60;
        if (hours) return `${hours}h ${minutes}m`;
        if (minutes) return `${minutes}m ${remainingSeconds}s`;
        return `${remainingSeconds}s`;
    }

    function languageName(code) {
        const normalized = String(code || '').trim().toLowerCase();
        const aliases = {
            eng: 'en', spa: 'es', fre: 'fr', fra: 'fr', ger: 'de', deu: 'de',
            ita: 'it', por: 'pt', jpn: 'ja', chi: 'zh', zho: 'zh', kor: 'ko',
            ara: 'ar', rus: 'ru'
        };
        const languageCode = aliases[normalized] || normalized;
        if (!languageCode) return '';
        try {
            return new Intl.DisplayNames([navigator.language], { type: 'language' }).of(languageCode) || '';
        } catch {
            return languageCode.toUpperCase();
        }
    }

    function friendlySubtitleLabel(result) {
        const language = languageName(value(result, 'language', 'Language'));
        return language ? `${language} subtitles` : 'Subtitles';
    }

    function appendMetric(parent, label, metricValue, detail) {
        if (!metricValue) return;
        const metric = document.createElement('div');
        metric.className = 'subtitleAlignerMetric';
        const term = document.createElement('dt');
        term.textContent = label;
        const description = document.createElement('dd');
        const primary = document.createElement('span');
        primary.className = 'subtitleAlignerMetricValue';
        primary.textContent = metricValue;
        description.append(primary);
        if (detail) {
            const secondary = document.createElement('span');
            secondary.className = 'subtitleAlignerMetricDetail';
            secondary.textContent = detail;
            description.append(secondary);
        }
        metric.append(term, description);
        parent.append(metric);
    }

    function appendTechnicalDetails(parent, text) {
        if (!text) return;
        const details = document.createElement('details');
        details.className = 'subtitleAlignerEditorTechnical';
        const summary = document.createElement('summary');
        summary.textContent = 'Technical details';
        const paragraph = document.createElement('p');
        paragraph.className = 'fieldDescription';
        paragraph.textContent = text;
        details.append(summary, paragraph);
        parent.append(details);
    }

    function displayTimelineTime(seconds) {
        const parsed = Math.max(0, Number(seconds) || 0);
        const rounded = Math.round(parsed);
        const hours = Math.floor(rounded / 3600);
        const minutes = Math.floor((rounded % 3600) / 60);
        const remainingSeconds = rounded % 60;
        return hours
            ? `${hours}:${String(minutes).padStart(2, '0')}:${String(remainingSeconds).padStart(2, '0')}`
            : `${minutes}:${String(remainingSeconds).padStart(2, '0')}`;
    }

    function timelineElement(name, attributes = {}) {
        const element = document.createElementNS('http://www.w3.org/2000/svg', name);
        for (const [key, attributeValue] of Object.entries(attributes)) {
            element.setAttribute(key, String(attributeValue));
        }
        return element;
    }

    function appendTimelineText(parent, textValue, attributes) {
        const element = timelineElement('text', attributes);
        element.textContent = textValue;
        parent.append(element);
        return element;
    }

    function timelineSupportIntervals(points, supportSeconds, durationSeconds) {
        const intervals = [];
        for (const point of points) {
            const start = Math.max(0, point.time - supportSeconds);
            const end = Math.min(durationSeconds, point.time + supportSeconds);
            const previous = intervals[intervals.length - 1];
            if (previous && start <= previous[1]) previous[1] = Math.max(previous[1], end);
            else intervals.push([start, end]);
        }
        return intervals;
    }

    function timelineOffsetAt(points, supportSeconds, subtitleSeconds) {
        const upperIndex = points.findIndex(point => point.time >= subtitleSeconds);
        if (upperIndex <= 0) {
            const nearest = upperIndex === 0 ? points[0] : points[points.length - 1];
            return Math.abs(nearest.time - subtitleSeconds) <= supportSeconds ? nearest.offset : null;
        }

        const upper = points[upperIndex];
        const lower = points[upperIndex - 1];
        const lowerDistance = subtitleSeconds - lower.time;
        const upperDistance = upper.time - subtitleSeconds;
        if (Math.min(lowerDistance, upperDistance) > supportSeconds) return null;
        const span = upper.time - lower.time;
        const fraction = span <= Number.EPSILON ? 0 : lowerDistance / span;
        return lower.offset + ((upper.offset - lower.offset) * fraction);
    }

    function niceTimelineStep(rawStep) {
        if (!Number.isFinite(rawStep) || rawStep <= 0) return .1;
        const magnitude = 10 ** Math.floor(Math.log10(rawStep));
        const normalized = rawStep / magnitude;
        const multiplier = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10;
        return multiplier * magnitude;
    }

    function timelineScale(points) {
        const offsets = points.map(point => point.offset);
        const rawMinimum = Math.min(0, ...offsets);
        const rawMaximum = Math.max(0, ...offsets);
        const rawRange = Math.max(.1, rawMaximum - rawMinimum);
        const padding = Math.max(.05, rawRange * .15);
        const step = niceTimelineStep((rawRange + (padding * 2)) / 4);
        let minimum = Math.floor((rawMinimum - padding) / step) * step;
        let maximum = Math.ceil((rawMaximum + padding) / step) * step;
        if (maximum - minimum < step * 2) {
            minimum -= step;
            maximum += step;
        }
        const ticks = [];
        for (let tick = minimum; tick <= maximum + (step / 2); tick += step) {
            ticks.push(Math.abs(tick) < step / 100 ? 0 : tick);
        }
        return { minimum, maximum, ticks };
    }

    function appendAlignmentTimeline(parent, result) {
        const directCueMatching = value(result, 'cueAdjustmentStrategy', 'CueAdjustmentStrategy') === 'Direct';
        const rawPoints = value(result, 'timingPoints', 'TimingPoints');
        const duration = Number(value(result, 'mediaDurationSeconds', 'MediaDurationSeconds') ?? 0);
        if (!Array.isArray(rawPoints) || rawPoints.length < 2 || !Number.isFinite(duration) || duration <= 0) return;

        const points = rawPoints
            .map(point => ({
                time: Number(value(point, 'subtitleSeconds', 'SubtitleSeconds')),
                offset: Number(value(point, 'offsetSeconds', 'OffsetSeconds'))
            }))
            .filter(point => Number.isFinite(point.time) && Number.isFinite(point.offset))
            .sort((left, right) => left.time - right.time);
        if (points.length < 2) return;

        const rawCuePoints = value(result, 'cueTimingPoints', 'CueTimingPoints');
        const cuePoints = Array.isArray(rawCuePoints)
            ? rawCuePoints
                .map(point => ({
                    time: Number(value(point, 'subtitleSeconds', 'SubtitleSeconds')),
                    offset: Number(value(point, 'offsetSeconds', 'OffsetSeconds')),
                    adjusted: Boolean(value(point, 'adjusted', 'Adjusted')),
                    matchKind: value(point, 'matchKind', 'MatchKind') || '',
                    matchedWords: Number(value(point, 'matchedWords', 'MatchedWords') ?? 0),
                    matchScore: Number(value(point, 'matchScore', 'MatchScore') ?? 0)
                }))
                .filter(point => Number.isFinite(point.time) && point.time >= 0 && point.time <= duration)
                .sort((left, right) => left.time - right.time)
            : [];

        const configuredSupport = Number(value(result, 'localAlignmentSupportSeconds', 'LocalAlignmentSupportSeconds') ?? 90);
        const support = Math.max(15, Math.min(300, Number.isFinite(configuredSupport) && configuredSupport > 0 ? configuredSupport : 90));
        const coverage = Number(value(result, 'alignmentCoveragePercent', 'AlignmentCoveragePercent') ?? 0);
        const alignedCueCount = Number(value(result, 'alignedCueCount', 'AlignedCueCount') ?? 0);
        const totalCueCount = Number(value(result, 'totalCueCount', 'TotalCueCount') ?? 0);
        const anchors = Number(value(result, 'anchors', 'Anchors') ?? 0);
        const adjustedCuePointCount = cuePoints.filter(point => point.adjusted).length;
        const unchangedCuePointCount = cuePoints.length - adjustedCuePointCount;
        const compact = window.matchMedia('(max-width: 42em)').matches;
        const width = compact ? 360 : 1200;
        const height = compact ? 210 : 250;
        const margin = compact
            ? { left: 42, right: 8, top: 24, bottom: 38 }
            : { left: 74, right: 22, top: 28, bottom: 48 };
        const plotWidth = width - margin.left - margin.right;
        const plotHeight = height - margin.top - margin.bottom;
        const scale = timelineScale(points);
        const x = seconds => margin.left + ((seconds / duration) * plotWidth);
        const y = offset => margin.top + (((scale.maximum - offset) / (scale.maximum - scale.minimum)) * plotHeight);
        const formatOffset = offset => `${displaySignedNumber(offset, 2) || '0.00'}s`;
        const intervals = directCueMatching ? [] : timelineSupportIntervals(points, support, duration);
        const chartId = `subtitleAlignerTimeline${++timelineSequence}`;

        const details = document.createElement('details');
        details.className = 'subtitleAlignerTimeline';
        const summary = document.createElement('summary');
        const summaryLabel = document.createElement('span');
        summaryLabel.className = 'subtitleAlignerTimelineSummary';
        summaryLabel.append(document.createTextNode('Stats for nerds'));
        const summaryHint = document.createElement('span');
        summaryHint.className = 'subtitleAlignerTimelineHint';
        summaryHint.textContent = 'See the evidence behind the correction';
        summaryLabel.append(summaryHint);
        const chevron = document.createElement('span');
        chevron.className = 'subtitleAlignerTimelineChevron';
        chevron.setAttribute('aria-hidden', 'true');
        summary.append(summaryLabel, chevron);

        const panel = document.createElement('section');
        panel.className = 'subtitleAlignerTimelinePanel';
        const heading = document.createElement('div');
        heading.className = 'subtitleAlignerTimelineHeading';
        const headingCopy = document.createElement('div');
        const title = document.createElement('h4');
        title.id = `${chartId}Title`;
        title.textContent = directCueMatching ? 'Direct cue matches across the title' : 'Cue adjustments and timing evidence';
        const description = document.createElement('p');
        description.id = `${chartId}Description`;
        description.textContent = directCueMatching
            ? 'Every mark is one subtitle cue. Green dots have their own direct speech-and-text match; gray marks had no reliable match and were left unchanged. No offsets are averaged or interpolated between cues.'
            : cuePoints.length
            ? 'Every short mark is one subtitle cue. Green movement lines show each cue from its original timing to its adjusted timing; gray marks stayed unchanged. Larger dots are direct speech-and-text checkpoints.'
            : 'Each large dot is a reliable point where speech and subtitle text matched. The connecting line shows the correction calculated between nearby points.';
        headingCopy.append(title, description);

        const legend = document.createElement('div');
        legend.className = 'subtitleAlignerTimelineLegend';
        legend.setAttribute('aria-hidden', 'true');
        for (const [label, className] of [
            [directCueMatching ? `${adjustedCuePointCount} directly matched cues` : cuePoints.length ? `${adjustedCuePointCount} adjusted cues` : 'Adjusted region', ''],
            [cuePoints.length ? `${unchangedCuePointCount} unchanged cues` : 'Unchanged region', ' subtitleAlignerTimelineSwatchUnchanged'],
            [directCueMatching ? 'Each green dot is direct evidence' : `${points.length} evidence ${points.length === 1 ? 'checkpoint' : 'checkpoints'}`, ' subtitleAlignerTimelineSwatchEvidence']
        ]) {
            const item = document.createElement('span');
            const swatch = document.createElement('i');
            swatch.className = `subtitleAlignerTimelineSwatch${className}`;
            item.append(swatch, document.createTextNode(label));
            legend.append(item);
        }
        heading.append(headingCopy, legend);

        const chart = document.createElement('div');
        chart.className = 'subtitleAlignerTimelineChart';
        const svg = timelineElement('svg', {
            viewBox: `0 0 ${width} ${height}`,
            role: 'img',
            'aria-labelledby': `${chartId}Title ${chartId}Description`
        });

        let cursor = 0;
        for (const [start, end] of intervals) {
            if (start > cursor) {
                svg.append(timelineElement('rect', {
                    x: x(cursor), y: margin.top, width: x(start) - x(cursor), height: plotHeight,
                    class: 'subtitleAlignerTimelineGap'
                }));
            }
            svg.append(timelineElement('rect', {
                x: x(start), y: margin.top, width: x(end) - x(start), height: plotHeight,
                class: 'subtitleAlignerTimelineSupport'
            }));
            cursor = end;
        }
        if (!directCueMatching && cursor < duration) {
            svg.append(timelineElement('rect', {
                x: x(cursor), y: margin.top, width: x(duration) - x(cursor), height: plotHeight,
                class: 'subtitleAlignerTimelineGap'
            }));
        }

        for (const tick of scale.ticks) {
            svg.append(timelineElement('line', {
                x1: margin.left, y1: y(tick), x2: width - margin.right, y2: y(tick),
                class: tick === 0 ? 'subtitleAlignerTimelineZero' : 'subtitleAlignerTimelineGrid'
            }));
            appendTimelineText(svg, tick === 0 ? 'Original' : formatOffset(tick), {
                x: margin.left - 10, y: y(tick) + 4, 'text-anchor': 'end', class: 'subtitleAlignerTimelineAxis'
            });
        }
        appendTimelineText(svg, 'Later', {
            x: margin.left,
            y: 14,
            'text-anchor': 'start',
            class: 'subtitleAlignerTimelineAxis'
        });
        appendTimelineText(svg, 'Earlier', {
            x: margin.left,
            y: height - 4,
            'text-anchor': 'start',
            class: 'subtitleAlignerTimelineAxis'
        });

        const horizontalTicks = compact
            ? [0, duration / 2, duration]
            : [0, duration / 4, duration / 2, (duration * 3) / 4, duration];
        horizontalTicks.forEach((tick, index) => {
            svg.append(timelineElement('line', {
                x1: x(tick), y1: margin.top, x2: x(tick), y2: margin.top + plotHeight,
                class: 'subtitleAlignerTimelineGrid'
            }));
            appendTimelineText(svg, displayTimelineTime(tick), {
                x: x(tick),
                y: margin.top + plotHeight + 24,
                'text-anchor': index === 0 ? 'start' : index === horizontalTicks.length - 1 ? 'end' : 'middle',
                class: 'subtitleAlignerTimelineAxis'
            });
        });

        for (const cue of cuePoints.filter(point => !point.adjusted)) {
            svg.append(timelineElement('line', {
                x1: x(cue.time), y1: y(0) - (compact ? 2 : 3),
                x2: x(cue.time), y2: y(0) + (compact ? 2 : 3),
                class: 'subtitleAlignerTimelineCueUnchanged'
            }));
        }
        for (const cue of cuePoints.filter(point => point.adjusted && Number.isFinite(point.offset))) {
            svg.append(timelineElement('line', {
                x1: x(cue.time), y1: y(0), x2: x(cue.time), y2: y(cue.offset),
                class: 'subtitleAlignerTimelineCueMovement'
            }));
        }

        if (!directCueMatching) {
            for (const [start, end] of intervals) {
                const sampleCount = Math.max(12, Math.ceil((end - start) / 10));
                const samples = [];
                for (let index = 0; index <= sampleCount; index++) {
                    const time = start + (((end - start) * index) / sampleCount);
                    const offset = timelineOffsetAt(points, support, time);
                    if (offset !== null) samples.push({ time, offset });
                }
                const path = samples
                    .map((point, index) => `${index ? 'L' : 'M'}${x(point.time).toFixed(1)},${y(point.offset).toFixed(1)}`)
                    .join(' ');
                if (path) svg.append(timelineElement('path', { d: path, class: 'subtitleAlignerTimelineLine' }));
            }
        }

        for (const cue of cuePoints.filter(point => point.adjusted && Number.isFinite(point.offset))) {
            svg.append(timelineElement('circle', {
                cx: x(cue.time), cy: y(cue.offset), r: compact ? 1.8 : 2.2,
                class: 'subtitleAlignerTimelineCueAdjusted'
            }));
        }

        if (!directCueMatching) {
            const showPointLabels = points.length <= (compact ? 6 : 12);
            for (const point of points) {
                svg.append(timelineElement('line', {
                    x1: x(point.time), y1: y(0), x2: x(point.time), y2: y(point.offset),
                    class: 'subtitleAlignerTimelineStem'
                }));
                svg.append(timelineElement('circle', {
                    cx: x(point.time), cy: y(point.offset), r: compact ? 5 : 6,
                    class: 'subtitleAlignerTimelineDot'
                }));
                if (showPointLabels) {
                    appendTimelineText(svg, formatOffset(point.offset), {
                        x: x(point.time),
                        y: y(point.offset) + (point.offset > 0 ? -11 : 19),
                        'text-anchor': 'middle',
                        class: 'subtitleAlignerTimelineValue'
                    });
                }
            }
        }
        chart.append(svg);

        const checkpointHeading = document.createElement('div');
        checkpointHeading.className = 'subtitleAlignerCheckpointHeading';
        const checkpointTitle = document.createElement('h5');
        checkpointTitle.textContent = 'Evidence checkpoints';
        const checkpointDescription = document.createElement('p');
        checkpointDescription.textContent = `${points.length} direct speech-and-text ${points.length === 1 ? 'match anchors' : 'matches anchor'} the cue corrections shown above.`;
        checkpointHeading.append(checkpointTitle, checkpointDescription);

        const checkpointGrid = document.createElement('div');
        checkpointGrid.className = 'subtitleAlignerCheckpointGrid';
        checkpointGrid.setAttribute('aria-label', 'Timing evidence checkpoints');
        for (const point of points) {
            const checkpoint = document.createElement('div');
            checkpoint.className = 'subtitleAlignerCheckpoint';
            const time = document.createElement('time');
            time.textContent = displayTimelineTime(point.time);
            const offset = document.createElement('strong');
            offset.textContent = formatOffset(point.offset);
            checkpoint.append(time, offset);
            checkpointGrid.append(checkpoint);
        }

        const caption = document.createElement('div');
        caption.className = 'subtitleAlignerTimelineCaption';
        const captionLead = document.createElement('strong');
        const captionMeta = document.createElement('span');
        if (directCueMatching && cuePoints.length && totalCueCount) {
            const exact = cuePoints.filter(point => point.adjusted && point.matchKind === 'Exact').length;
            const fuzzy = cuePoints.filter(point => point.adjusted && point.matchKind === 'Fuzzy').length;
            captionLead.textContent = `${alignedCueCount} of ${totalCueCount} cues have their own audio match.`;
            captionMeta.textContent = `${exact} exact text matches${fuzzy ? ` · ${fuzzy} close text matches` : ''} · ${unchangedCuePointCount} unmatched and unchanged · ${coverage.toFixed(1)}% direct coverage`;
        } else if (cuePoints.length && totalCueCount) {
            captionLead.textContent = `${alignedCueCount} cues adjusted across ${totalCueCount} total.`;
            captionMeta.textContent = `${anchors} matched speech ${anchors === 1 ? 'passage' : 'passages'} · ${points.length} correction ${points.length === 1 ? 'checkpoint' : 'checkpoints'} · ${support}-second evidence radius · ${coverage.toFixed(1)}% cue coverage`;
        } else {
            captionLead.textContent = `${points.length} correction ${points.length === 1 ? 'checkpoint shaped' : 'checkpoints shaped'} the local timing map.`;
            captionMeta.textContent = `${support}-second evidence radius · ${coverage.toFixed(1)}% cue coverage`;
        }
        caption.append(captionLead, captionMeta);

        panel.append(heading, chart);
        if (!directCueMatching) panel.append(checkpointHeading, checkpointGrid);
        panel.append(caption);
        details.append(summary, panel);
        parent.append(details);
    }

    function displayDate(dateText) {
        if (!dateText) return '';
        const date = new Date(dateText);
        return Number.isNaN(date.getTime()) ? '' : date.toLocaleString();
    }

    function appendText(parent, className, text) {
        if (!text) return;
        const paragraph = document.createElement('p');
        paragraph.className = className;
        paragraph.textContent = text;
        parent.append(paragraph);
    }

    function resultOutcome(status, correctionStatus) {
        if (status === 'Error' || correctionStatus === 'Error') return 'error';
        if (correctionStatus === 'Blocked') return 'attention';
        if (status === 'In sync' || status === 'Minor timing variation' || status === 'Locally aligned' || correctionStatus === 'Created') return 'good';
        return 'attention';
    }

    function appendCorrectionWarning(parent, message) {
        const warning = document.createElement('div');
        warning.className = 'subtitleAlignerEditorWarning';
        warning.setAttribute('role', 'status');
        const title = document.createElement('strong');
        title.textContent = 'Sidecar cleanup needs attention';
        const detail = document.createElement('p');
        detail.textContent = message || 'An existing subtitle sidecar was left untouched.';
        warning.append(title, detail);
        parent.append(warning);
    }

    function correctionWarningMessage(message) {
        const legacyMatch = /^The old (.+) was changed outside the plugin and was left in place\.$/.exec(message || '');
        return legacyMatch
            ? `The older ${legacyMatch[1]} could not be verified as plugin-owned, so it was left untouched. Review or remove it manually if it is no longer needed.`
            : message;
    }

    function renderResults(container, results, complete) {
        const signature = JSON.stringify([complete, results]);
        if (container.dataset.subtitleAlignerSignature === signature) return;

        container.dataset.subtitleAlignerSignature = signature;
        container.replaceChildren();
        if (!results.length) {
            if (complete) appendText(container, 'fieldDescription', 'No subtitle results were produced for this title.');
            return;
        }

        for (const result of results) {
            const status = value(result, 'status', 'Status') || 'Result';
            const confidence = value(result, 'confidence', 'Confidence') || '';
            const correctionStatus = value(result, 'correctionStatus', 'CorrectionStatus') || '';
            const alignmentMode = value(result, 'alignmentMode', 'AlignmentMode') || 'Quick';
            const fullAlignment = alignmentMode === 'Full';
            const directCueMatching = value(result, 'cueAdjustmentStrategy', 'CueAdjustmentStrategy') === 'Direct';
            const wholeTrackShiftRecommended = Boolean(value(
                result,
                'wholeTrackShiftRecommended',
                'WholeTrackShiftRecommended'));
            const failed = status === 'Error' || correctionStatus === 'Error';
            const minorVariation = status === 'Minor timing variation';
            const card = document.createElement('article');
            card.className = 'subtitleAlignerEditorResult';
            card.dataset.outcome = resultOutcome(status, correctionStatus);

            const header = document.createElement('header');
            header.className = 'subtitleAlignerEditorResultHeader';
            const headingGroup = document.createElement('div');
            const heading = document.createElement('h3');
            const rawOffset = value(result, 'offsetSeconds', 'OffsetSeconds');
            const titles = {
                'In sync': 'Subtitles are in sync',
                'Minor timing variation': 'Subtitles are in sync',
                'Constant offset': 'A consistent timing adjustment was found',
                'Progressive drift': 'Subtitle timing gradually drifts',
                'Timing break': 'A timing change was found',
                'Inconsistent': 'Timing changes across the subtitle',
                'Insufficient evidence': 'More evidence is needed',
                'Locally aligned': 'Individual-cue alignment completed'
            };
            heading.textContent = wholeTrackShiftRecommended
                ? 'Use Shift entire track'
                : failed
                ? `${fullAlignment ? 'Individual-cue alignment' : 'Whole-track shift'} failed`
                : titles[status] || `${fullAlignment ? 'Individual-cue alignment' : 'Whole-track shift'} completed`;
            const track = document.createElement('p');
            track.className = 'subtitleAlignerEditorTrack';
            track.textContent = friendlySubtitleLabel(result);
            headingGroup.append(heading, track);
            header.append(headingGroup);
            if (confidence && confidence !== 'Inconclusive' && !failed) {
                const badge = document.createElement('span');
                badge.className = 'subtitleAlignerEditorConfidence';
                badge.textContent = `${confidence} confidence`;
                header.append(badge);
            }
            card.append(header);

            const alignedCues = Number(value(result, 'alignedCueCount', 'AlignedCueCount') ?? 0);
            let adjustedCues = Number(value(result, 'adjustedCueCount', 'AdjustedCueCount') ?? alignedCues);
            let verifiedCues = Number(value(result, 'verifiedCueCount', 'VerifiedCueCount') ?? 0);
            const protectedCues = Number(value(result, 'protectedCueCount', 'ProtectedCueCount') ?? 0);
            const cueDeadBand = Number(value(result, 'cueAlignmentDeadBandSeconds', 'CueAlignmentDeadBandSeconds') ?? 0);
            const timingEvidence = value(result, 'timingEvidence', 'TimingEvidence') || '';
            const semanticConfidence = value(result, 'semanticConfidence', 'SemanticConfidence') || '';
            const timingConfidence = value(result, 'timingConfidence', 'TimingConfidence') || '';
            const individualCueAlignmentRecommended = Boolean(value(
                result,
                'individualCueAlignmentRecommended',
                'IndividualCueAlignmentRecommended'));
            if (directCueMatching && alignedCues > 0 && adjustedCues === 0 && verifiedCues === 0) {
                adjustedCues = alignedCues;
            }
            const totalCues = Number(value(result, 'totalCueCount', 'TotalCueCount') ?? 0);
            const coverage = Number(value(result, 'alignmentCoveragePercent', 'AlignmentCoveragePercent') ?? 0);
            const resultMessage = value(result, 'message', 'Message') || '';
            let summary = resultMessage;
            if (minorVariation) {
                const preciseOffset = displaySignedNumber(rawOffset, 3);
                summary = preciseOffset
                    ? `The median timing is essentially perfect at ${preciseOffset}s. No correction is needed.`
                    : 'The timing is within the in-sync range. No correction is needed.';
            } else if (status === 'Locally aligned' && totalCues) {
                const remaining = Math.max(0, totalCues - alignedCues);
                const deadBandDescription = cueDeadBand > 0
                    ? ` within the ±${Math.round(cueDeadBand * 1000)} ms no-change band`
                    : '';
                summary = directCueMatching
                    ? `Directly matched ${alignedCues} of ${totalCues} subtitle cues to their spoken audio. ${verifiedCues} were already aligned${deadBandDescription}; ${protectedCues} were protected because their exact onset could not be changed safely; ${adjustedCues} received an onset correction.`
                    : `Adjusted ${alignedCues} of ${totalCues} subtitle cues using local timing evidence.`;
                if (remaining) summary += directCueMatching
                    ? ` The remaining ${remaining} cues had no reliable individual match and were left unchanged.`
                    : ` The remaining ${remaining} cues had no nearby evidence and were left unchanged.`;
            } else if (!fullAlignment
                && individualCueAlignmentRecommended
                && ['Progressive drift', 'Timing break', 'Inconsistent'].includes(status)) {
                summary = `${resultMessage} Use Align individual cues to process the complete audio and correct local timing changes.`;
            } else if (failed) {
                summary = 'Subtitle Aligner could not finish this track. Technical details are available below.';
            }
            appendText(card, 'subtitleAlignerEditorSummary', summary);

            if (totalCues) {
                const coverageBlock = document.createElement('div');
                coverageBlock.className = 'subtitleAlignerCoverage';
                const coverageHeader = document.createElement('div');
                coverageHeader.className = 'subtitleAlignerCoverageHeader';
                const label = document.createElement('span');
                label.textContent = wholeTrackShiftRecommended
                    ? 'Cues supporting the shared shift'
                    : directCueMatching ? 'Subtitle cues with reliable timing evidence' : 'Subtitle cues adjusted from the timing map';
                const amount = document.createElement('strong');
                amount.textContent = `${coverage.toFixed(1)}%`;
                coverageHeader.append(label, amount);
                const coverageBar = document.createElement('progress');
                coverageBar.max = totalCues;
                coverageBar.value = alignedCues;
                coverageBar.setAttribute('aria-label', directCueMatching
                    ? `${alignedCues} of ${totalCues} subtitle cues directly matched to audio`
                    : `${alignedCues} of ${totalCues} subtitle cues adjusted from the timing map`);
                const note = document.createElement('p');
                note.className = 'subtitleAlignerCoverageNote';
                note.textContent = wholeTrackShiftRecommended
                    ? `${alignedCues} directly matched cues agree on one whole-track correction`
                    : directCueMatching
                    ? `${verifiedCues} verified aligned · ${protectedCues} protected · ${adjustedCues} adjusted · ${Math.max(0, totalCues - alignedCues)} insufficient evidence`
                    : `${alignedCues} of ${totalCues} cues`;
                coverageBlock.append(coverageHeader, coverageBar, note);
                card.append(coverageBlock);
            }

            const metrics = document.createElement('dl');
            metrics.className = 'subtitleAlignerMetricGrid';
            const offset = displaySignedNumber(rawOffset);
            const drift = displaySignedNumber(value(result, 'driftSecondsPerHour', 'DriftSecondsPerHour'));
            const timingBreak = displayTimestamp(value(result, 'breakSeconds', 'BreakSeconds'));
            const residual = displayNumber(value(result, 'p90ResidualSeconds', 'P90ResidualSeconds'));
            if (offset) {
                appendMetric(
                    metrics,
                    fullAlignment ? 'Median adjustment' : 'Timing adjustment',
                    `${offset}s`,
                    residual ? `90% of timing matches are within ${residual}s` : 'Relative to the audio');
            } else if (drift) {
                appendMetric(metrics, 'Timing drift', `${drift}s/hour`, 'The offset changes gradually');
            } else if (timingBreak) {
                appendMetric(metrics, 'Timing change', timingBreak, 'Approximate point where the offset changes');
            }

            if (timingEvidence) {
                appendMetric(
                    metrics,
                    'Timing evidence',
                    timingEvidence,
                    directCueMatching && cueDeadBand > 0
                        ? `Offsets within ±${Math.round(cueDeadBand * 1000)} ms are not changed`
                        : 'Speech activity and subtitle text are measured independently');
            }

            const speechActivitySamples = Number(value(result, 'speechActivitySamples', 'SpeechActivitySamples') ?? 0);
            const speechActivityOffset = displaySignedNumber(value(result, 'speechActivityOffsetSeconds', 'SpeechActivityOffsetSeconds'));
            const speechActivityCorrelation = displayNumber(value(result, 'speechActivityCorrelation', 'SpeechActivityCorrelation'));
            if (!directCueMatching && speechActivitySamples > 0 && speechActivityOffset) {
                appendMetric(
                    metrics,
                    'Speech activity',
                    `${speechActivityOffset}s across ${speechActivitySamples} windows`,
                    speechActivityCorrelation ? `Median normalized correlation ${speechActivityCorrelation}` : 'Independent timing corroboration');
            }

            if (directCueMatching && semanticConfidence && timingConfidence) {
                appendMetric(
                    metrics,
                    'Confidence evidence',
                    `${semanticConfidence} semantic`,
                    `${timingConfidence} timing · overall confidence uses the lower of the two`);
            }

            const anchors = Number(value(result, 'anchors', 'Anchors') ?? 0);
            const strictAnchors = Number(value(result, 'strictAnchors', 'StrictAnchors') ?? 0);
            const guidedFuzzyAnchors = Number(value(result, 'guidedFuzzyAnchors', 'GuidedFuzzyAnchors') ?? 0);
            const samples = Number(value(result, 'samples', 'Samples') ?? 0);
            const matchedWords = Number(value(result, 'matchedWords', 'MatchedWords') ?? 0);
            if (anchors || samples || matchedWords) {
                const evidenceDetail = [];
                const timingPoints = value(result, 'timingPoints', 'TimingPoints');
                const timingPointCount = Array.isArray(timingPoints) ? timingPoints.length : 0;
                if (guidedFuzzyAnchors > 0) evidenceDetail.push(`${strictAnchors} exact + ${guidedFuzzyAnchors} close ${directCueMatching ? 'cue matches' : 'passages'}`);
                if (fullAlignment && timingPointCount && !directCueMatching) evidenceDetail.push(`${timingPointCount} correction ${timingPointCount === 1 ? 'checkpoint' : 'checkpoints'}`);
                if (samples) evidenceDetail.push(fullAlignment
                    ? `${samples} overlapping audio ${samples === 1 ? 'section' : 'sections'} covering the full title`
                    : `${samples} sampled ${samples === 1 ? 'window' : 'windows'}`);
                if (matchedWords) evidenceDetail.push(`${matchedWords} matched words`);
                appendMetric(
                    metrics,
                    'Evidence',
                    anchors
                        ? directCueMatching
                            ? `${anchors} directly matched ${anchors === 1 ? 'cue' : 'cues'}`
                            : `${anchors} matched speech ${anchors === 1 ? 'passage' : 'passages'}`
                        : `${samples} ${samples === 1 ? 'window' : 'windows'} checked`,
                    evidenceDetail.join(' · '));
            }

            const elapsed = Number(value(result, 'analysisElapsedSeconds', 'AnalysisElapsedSeconds') ?? 0);
            const mediaDuration = Number(value(result, 'mediaDurationSeconds', 'MediaDurationSeconds') ?? 0);
            if (elapsed > 0) {
                const costDetail = [];
                if (mediaDuration > 0) {
                    const percentage = (elapsed / mediaDuration) * 100;
                    const speed = mediaDuration / elapsed;
                    costDetail.push(`${displayDuration(mediaDuration)} title`);
                    costDetail.push(`${percentage.toFixed(1)}% of runtime`);
                    costDetail.push(`${speed.toFixed(1)}× faster than real time`);
                }
                appendMetric(metrics, 'Scan time', displayDuration(elapsed), costDetail.join(' · '));
            }
            if (metrics.childElementCount) card.append(metrics);

            const correctionMessage = value(result, 'correctionMessage', 'CorrectionMessage') || '';
            if (correctionStatus === 'Created') {
                appendText(
                    card,
                    'fieldDescription subtitleAlignerEditorResultMeta',
                    'A corrected subtitle was saved beside the video. The original subtitle was not changed.');
            } else if (correctionStatus === 'Blocked') {
                appendCorrectionWarning(card, correctionWarningMessage(correctionMessage));
            } else if (correctionStatus && !['Not needed', 'Not eligible'].includes(correctionStatus) && !failed) {
                appendText(card, 'fieldDescription subtitleAlignerEditorResultMeta', correctionMessage || correctionStatus);
            }
            const resultTimingPoints = value(result, 'timingPoints', 'TimingPoints');
            if (fullAlignment
                && !failed
                && (status === 'Locally aligned'
                    || (directCueMatching && Array.isArray(resultTimingPoints) && resultTimingPoints.length > 1))) {
                appendAlignmentTimeline(card, result);
            }
            if (failed) {
                appendTechnicalDetails(card, [resultMessage, correctionMessage].filter(Boolean).join(' '));
            }

            container.append(card);
        }
    }

    function setButton(button, label, disabled) {
        button.disabled = disabled;
        button.querySelector('span').textContent = label;
    }

    function setRunButtons(section, mode, running) {
        const quick = section.querySelector('.subtitleAlignerQuickButton');
        const full = section.querySelector('.subtitleAlignerFullButton');
        if (running) {
            setButton(quick, mode === 'Full' ? 'Shift entire track' : 'Checking whole-track shift…', true);
            setButton(full, mode === 'Full' ? 'Aligning individual cues…' : 'Align individual cues', true);
        } else {
            setButton(quick, 'Shift entire track', false);
            setButton(full, 'Align individual cues', false);
        }
    }

    function liveRunTiming(runId, run, progress) {
        const now = Date.now();
        const started = new Date(value(run, 'startedUtc', 'StartedUtc') || '').getTime();
        const analysisStarted = new Date(value(run, 'analysisStartedUtc', 'AnalysisStartedUtc') || '').getTime();
        const elapsed = Number.isFinite(started) ? Math.max(0, (now - started) / 1000) : 0;
        let remaining = null;
        let estimateExpired = false;

        if (Number.isFinite(analysisStarted) && progress > 15 && progress < 95) {
            const analysisElapsed = Math.max(1, (now - analysisStarted) / 1000);
            const workProgress = Math.max(.01, Math.min(.99, (progress - 15) / 80));
            const observedRemaining = analysisElapsed * (1 - workProgress) / workProgress;
            const previous = etaByRun.get(runId);
            if (!previous || previous.progress !== progress) {
                const smoothed = previous && Number.isFinite(previous.remaining)
                    ? (previous.remaining * .65) + (observedRemaining * .35)
                    : observedRemaining;
                etaByRun.set(runId, { progress, remaining: smoothed, measuredAt: now });
                remaining = smoothed;
            } else if (Number.isFinite(previous.remaining)) {
                const countdown = previous.remaining - ((now - previous.measuredAt) / 1000);
                if (countdown > 1) {
                    remaining = countdown;
                } else {
                    etaByRun.set(runId, { progress, remaining: null, measuredAt: now });
                    estimateExpired = true;
                }
            } else {
                estimateExpired = true;
            }
        }

        return { elapsed, remaining, estimateExpired };
    }

    function renderRun(section, run) {
        const complete = Boolean(value(run, 'complete', 'Complete'));
        const state = value(run, 'state', 'State') || (complete ? 'Complete' : 'Running');
        const progress = Math.max(0, Math.min(100, Number(value(run, 'progressPercent', 'ProgressPercent') ?? 0)));
        const phase = section.querySelector('.subtitleAlignerEditorPhase');
        const message = section.querySelector('.subtitleAlignerEditorMessage');
        const progressBar = section.querySelector('.subtitleAlignerEditorProgress');
        const progressMeta = section.querySelector('.subtitleAlignerEditorProgressMeta');
        const progressAmount = section.querySelector('.subtitleAlignerEditorProgressAmount');
        const progressEta = section.querySelector('.subtitleAlignerEditorProgressEta');
        const elapsedLabel = section.querySelector('.subtitleAlignerEditorElapsed');
        const results = value(run, 'results', 'Results') || [];
        const mode = value(run, 'alignmentMode', 'AlignmentMode') || 'Quick';
        const runId = String(value(run, 'runId', 'RunId') || section.dataset.subtitleAlignerRunId || '');
        const timing = liveRunTiming(runId, run, progress);

        phase.textContent = complete
            ? 'Complete'
            : state === 'Queued'
                ? 'Waiting to start'
                : mode === 'Full'
                    ? 'Individual-cue alignment in progress'
                    : 'Whole-track shift in progress';
        message.textContent = value(run, 'message', 'Message') || '';
        elapsedLabel.textContent = !complete && timing.elapsed > 0
            ? `${displayDuration(timing.elapsed)} elapsed`
            : '';
        progressBar.hidden = complete;
        progressMeta.hidden = complete;
        if (!complete && state === 'Queued') {
            progressBar.removeAttribute('value');
            progressAmount.textContent = 'Waiting for Jellyfin';
            progressEta.textContent = '';
        } else {
            progressBar.value = progress;
            progressBar.setAttribute('aria-valuetext', `${Math.round(progress)}% complete`);
            progressAmount.textContent = `${Math.round(progress)}% complete`;
            progressEta.textContent = timing.remaining === null
                ? timing.estimateExpired
                    ? 'Waiting for the current section…'
                    : progress < 95 ? 'Estimating time remaining…' : 'Finishing…'
                : `About ${displayDuration(timing.remaining)} remaining`;
        }
        renderResults(section.querySelector('.subtitleAlignerEditorResults'), results, complete);
        setRunButtons(section, mode, !complete);
        if (complete) etaByRun.delete(runId);
    }

    async function pollRun(section, client, runId, failures = 0) {
        if (!section.isConnected || section.dataset.subtitleAlignerRunId !== runId) return;

        try {
            const response = await client.ajax({
                type: 'GET',
                url: client.getUrl(`SubtitleAligner/SyncStatus/${encodeURIComponent(runId)}`)
            });
            const run = await response.json();
            if (!section.isConnected || section.dataset.subtitleAlignerRunId !== runId) return;
            renderRun(section, run);
            if (!Boolean(value(run, 'complete', 'Complete'))) {
                window.setTimeout(() => pollRun(section, client, runId), 1000);
            }
        } catch (error) {
            if (!section.isConnected || section.dataset.subtitleAlignerRunId !== runId) return;
            const nextFailures = failures + 1;
            const message = section.querySelector('.subtitleAlignerEditorMessage');
            message.textContent = nextFailures < 10
                ? 'Connection interrupted. Reconnecting…'
                : 'Live updates stopped. Check again to start a new title check.';
            if (nextFailures < 10) {
                window.setTimeout(() => pollRun(section, client, runId, nextFailures), 2000);
            } else {
                setRunButtons(section, 'Quick', false);
                section.querySelector('.subtitleAlignerEditorProgress').hidden = true;
                section.querySelector('.subtitleAlignerEditorProgressMeta').hidden = true;
            }
            console.error('Subtitle Aligner could not refresh the selected title.', error);
        }
    }

    function renderSnapshot(section, snapshot) {
        const activeRunId = value(snapshot, 'activeRunId', 'ActiveRunId') || '';
        if (activeRunId) {
            section.dataset.subtitleAlignerRunId = activeRunId;
            pollRun(section, window.ApiClient, activeRunId);
            return;
        }

        delete section.dataset.subtitleAlignerRunId;
        const results = value(snapshot, 'results', 'Results') || [];
        const lastAnalyzed = displayDate(value(snapshot, 'lastAnalyzedUtc', 'LastAnalyzedUtc'));
        section.querySelector('.subtitleAlignerEditorProgress').hidden = true;
        section.querySelector('.subtitleAlignerEditorProgressMeta').hidden = true;
        section.querySelector('.subtitleAlignerEditorElapsed').textContent = '';
        section.querySelector('.subtitleAlignerEditorPhase').textContent = results.length
            ? 'Previous result'
            : 'Not checked yet';
        section.querySelector('.subtitleAlignerEditorMessage').textContent = results.length
            ? `Last checked ${lastAnalyzed || 'previously'}.`
            : 'No saved Subtitle Aligner result exists for this title.';
        renderResults(section.querySelector('.subtitleAlignerEditorResults'), results, false);
        setRunButtons(section, 'Quick', false);
    }

    async function loadPreviousResults(dialog, section) {
        const client = window.ApiClient;
        const quickButton = section.querySelector('.subtitleAlignerQuickButton');
        setButton(quickButton, 'Loading previous result…', true);
        setButton(section.querySelector('.subtitleAlignerFullButton'), 'Align individual cues', true);
        section.querySelector('.subtitleAlignerEditorPhase').textContent = 'Loading previous result';
        section.querySelector('.subtitleAlignerEditorMessage').textContent = '';
        try {
            if (!client) throw new Error('The Jellyfin API client is not available.');
            const displayedFileName = dialog.querySelector('.pathValue')?.textContent?.trim() || '';
            const itemId = rememberedItemId(displayedFileName);
            const parameters = new URLSearchParams({ fileName: displayedFileName });
            if (itemId) parameters.set('itemId', itemId);
            const url = `${client.getUrl('SubtitleAligner/ResultsByFile')}?${parameters}`;
            const response = await client.ajax({ type: 'GET', url });
            renderSnapshot(section, await response.json());
        } catch (error) {
            section.querySelector('.subtitleAlignerEditorPhase').textContent = 'Previous result unavailable';
            section.querySelector('.subtitleAlignerEditorMessage').textContent = error?.status === 403
                ? 'This account is not allowed to view or edit subtitle results.'
                : 'The saved result could not be loaded. You can still start a new title check.';
            setButton(quickButton, 'Shift entire track', error?.status === 403);
            setButton(section.querySelector('.subtitleAlignerFullButton'), 'Align individual cues', error?.status === 403);
            console.error('Subtitle Aligner could not load the selected title\'s previous result.', error);
        }
    }

    function fileName(path) {
        return String(path || '').split(/[\\/]/).pop() || '';
    }

    function rememberItem(item) {
        const itemFileName = fileName(item?.Path || item?.MediaSources?.[0]?.Path);
        if (item?.Id && itemFileName) recentItemsByFileName.set(itemFileName.toLowerCase(), item.Id);
        return item;
    }

    function rememberedItemId(displayedFileName) {
        return recentItemsByFileName.get(String(displayedFileName || '').toLowerCase()) || '';
    }

    function wrapApiClient() {
        const client = window.ApiClient;
        if (!client || client[clientMarker] || typeof client.getItem !== 'function') return;

        const originalGetItem = client.getItem;
        client.getItem = function (...args) {
            return Promise.resolve(originalGetItem.apply(this, args)).then(rememberItem);
        };
        Object.defineProperty(client, clientMarker, { value: true });
    }

    async function addAction(dialog) {
        const displayedFileName = dialog.querySelector('.pathValue')?.textContent?.trim() || '';
        const existingSection = dialog.querySelector('.subtitleAlignerEditorAction');
        if (!displayedFileName) {
            if (existingSection) existingSection.remove();
            delete dialog.dataset[dialogMarker];
            return;
        }
        if (dialog.dataset[dialogMarker] === 'true'
            && existingSection?.dataset.subtitleAlignerFileName === displayedFileName) return;
        if (dialog.dataset[dialogMarker] === 'pending') return;
        if (existingSection) existingSection.remove();

        dialog.dataset[dialogMarker] = 'pending';
        let installed = false;
        try {
            if (!dialog.isConnected) return;

            ensureStyles();

            const searchForm = dialog.querySelector('.subtitleSearchForm');
            if (!searchForm) return;

            const section = document.createElement('section');
            section.className = 'subtitleAlignerEditorAction';
            section.dataset.subtitleAlignerFileName = displayedFileName;
            section.style.marginBottom = '2em';
            section.innerHTML = `
                <h2>Subtitle Aligner</h2>
                <p class="fieldDescription"><strong>Shift entire track</strong> samples representative windows and moves every cue only when independent speech activity and subtitle-text evidence agree on one consistent offset. <strong>Align individual cues</strong> processes the complete audio and applies local corrections only where timing evidence supports them. Both write a new <strong>.subalign</strong> sidecar; embedded tracks and source files are never changed.</p>
                <div class="subtitleAlignerEditorActions">
                    <button is="emby-button" type="button" class="raised button-submit emby-button subtitleAlignerSyncButton subtitleAlignerQuickButton"><span>Shift entire track</span></button>
                    <button is="emby-button" type="button" class="raised emby-button subtitleAlignerSyncButton subtitleAlignerFullButton"><span>Align individual cues</span></button>
                </div>
                <div class="subtitleAlignerEditorLive" role="status" aria-live="polite" aria-atomic="false">
                    <div class="subtitleAlignerEditorLiveHeader">
                        <div class="subtitleAlignerEditorPhase"></div>
                        <div class="subtitleAlignerEditorElapsed"></div>
                    </div>
                    <div class="fieldDescription subtitleAlignerEditorMessage"></div>
                    <progress class="subtitleAlignerEditorProgress" max="100" aria-label="Subtitle alignment progress" hidden></progress>
                    <div class="subtitleAlignerEditorProgressMeta" hidden>
                        <span class="subtitleAlignerEditorProgressAmount"></span>
                        <span class="subtitleAlignerEditorProgressEta"></span>
                    </div>
                </div>
                <div class="subtitleAlignerEditorResults" aria-live="polite"></div>`;

            let insertionPoint = searchForm.previousElementSibling;
            if (insertionPoint?.classList.contains('originalFile')) {
                insertionPoint = insertionPoint.previousElementSibling;
            }
            searchForm.parentElement.insertBefore(section, insertionPoint || searchForm);

            const startRun = async fullAlignment => {
                const button = section.querySelector(fullAlignment ? '.subtitleAlignerFullButton' : '.subtitleAlignerQuickButton');
                setRunButtons(section, fullAlignment ? 'Full' : 'Quick', true);
                setButton(button, 'Queueing…', true);
                section.querySelector('.subtitleAlignerEditorPhase').textContent = 'Queueing';
                section.querySelector('.subtitleAlignerEditorMessage').textContent = fullAlignment
                    ? 'Preparing to process the complete audio and align individual cues. This takes substantially longer.'
                    : 'Sampling representative windows to find one consistent whole-track shift.';
                section.querySelector('.subtitleAlignerEditorResults').replaceChildren();
                section.querySelector('.subtitleAlignerEditorProgress').hidden = false;
                section.querySelector('.subtitleAlignerEditorProgress').removeAttribute('value');
                section.querySelector('.subtitleAlignerEditorProgressMeta').hidden = false;
                section.querySelector('.subtitleAlignerEditorProgressAmount').textContent = 'Waiting for Jellyfin';
                section.querySelector('.subtitleAlignerEditorProgressEta').textContent = '';
                section.querySelector('.subtitleAlignerEditorElapsed').textContent = '';
                try {
                    const client = window.ApiClient;
                    if (!client) throw new Error('The Jellyfin API client is not available.');
                    const displayedFileName = dialog.querySelector('.pathValue')?.textContent?.trim() || '';
                    const itemId = rememberedItemId(displayedFileName);
                    const response = await client.ajax({
                        type: 'POST',
                        url: client.getUrl(`SubtitleAligner/${fullAlignment ? 'AlignByFile' : 'SyncByFile'}`),
                        contentType: 'application/json',
                        data: JSON.stringify({ FileName: displayedFileName, ItemId: itemId || null })
                    });
                    const accepted = await response.json();
                    const runId = value(accepted, 'runId', 'RunId');
                    if (!runId) throw new Error('Jellyfin did not return a title-check identifier.');
                    section.dataset.subtitleAlignerRunId = runId;
                    await pollRun(section, client, runId);
                } catch (error) {
                    setRunButtons(section, fullAlignment ? 'Full' : 'Quick', false);
                    section.querySelector('.subtitleAlignerEditorPhase').textContent = 'Could not start';
                    section.querySelector('.subtitleAlignerEditorProgress').hidden = true;
                    section.querySelector('.subtitleAlignerEditorProgressMeta').hidden = true;
                    section.querySelector('.subtitleAlignerEditorElapsed').textContent = '';
                    section.querySelector('.subtitleAlignerEditorMessage').textContent = error?.status === 403
                        ? 'This account is not allowed to edit subtitles.'
                        : 'Could not identify or queue this title. Open its details page and try again.';
                    console.error('Subtitle Aligner could not queue the selected title.', error);
                }
            };
            section.querySelector('.subtitleAlignerQuickButton').addEventListener('click', () => startRun(false));
            section.querySelector('.subtitleAlignerFullButton').addEventListener('click', () => startRun(true));
            dialog.dataset[dialogMarker] = 'true';
            installed = true;
            await loadPreviousResults(dialog, section);
        } catch (error) {
            console.error('Subtitle Aligner could not add its subtitle-editor action.', error);
        } finally {
            if (!installed && dialog.dataset[dialogMarker] === 'pending') {
                delete dialog.dataset[dialogMarker];
            }
        }
    }

    function scanDialogs() {
        wrapApiClient();
        document.querySelectorAll('.subtitleEditorDialog').forEach(addAction);
    }

    new MutationObserver(scanDialogs).observe(document.documentElement, { childList: true, subtree: true });
    window.setInterval(scanDialogs, 1000);
    scanDialogs();
})();
