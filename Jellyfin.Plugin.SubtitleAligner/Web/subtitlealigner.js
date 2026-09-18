const pluginId = '8f7de91f-ddab-4402-9c9d-f15a36fa119e';
const analysisDefaults = Object.freeze({
    samplePositions: '10,30,50,70,90,20,40,60,80',
    initialSamples: 5,
    maximumSamples: 9,
    clipDuration: 30,
    cueLead: 5,
    matchScore: 0.62,
    matchGap: 0.08,
    matchedWords: 8,
    enableFuzzy: true,
    enableCrossLanguageFuzzy: true,
    crossLanguageCueCoverage: 35,
    maximumIndividualCueOffset: 2.0,
    fuzzySearchRadius: 60,
    fuzzyMatchScore: 0.55,
    fuzzyDistinctiveTokens: 5,
    fuzzyTokenSimilarity: 0.82,
    fuzzyStemming: true,
    fuzzyPhonetic: true,
    fuzzyPhoneticWeight: 0.40,
    fuzzyMatchGap: 0.08,
    residual: 0.50,
    inSyncOffset: 0.35,
    cueDeadBand: 0.10,
    crossLanguageVadDeadBand: 0.75,
    improvement: 40,
    drift: 0.75,
    timingBreak: 1.0,
    highAnchors: 5,
    highWords: 60,
    highCoverage: 60,
    highScore: 0.72,
    mediumAnchors: 4,
    mediumWords: 35,
    mediumCoverage: 40,
    mediumScore: 0.62
});
let managedModelValid = false;
let modelStatusName = '';
let configuredManagedModelName = '';
let compatibleModels = [];
let recommendedModelName = '';
let modelReady = false;
let validatorRunning = false;
let assessmentRunning = false;
let modelDownloadBusy = false;
let selectedItemId = '';
let settingsLoaded = false;

const styles = `
    .subtitleValidatorIntro { max-width: 72ch; }
    .subtitleValidatorIntro, .subtitleValidatorActivity, .subtitleValidatorRecommendation, .subtitleValidatorEvidence, .fieldDescription { overflow-wrap: anywhere; }
    .subtitleValidatorRunStatus { padding-bottom: 1.25em; border-bottom: 1px solid rgba(128, 128, 128, .28); }
    .subtitleValidatorModel { padding: 1.25em 0; border-bottom: 1px solid rgba(128, 128, 128, .28); }
    .subtitleValidatorLibraries { padding: 1.25em 0; border-bottom: 1px solid rgba(128, 128, 128, .28); }
    .subtitleValidatorSingleTitle { padding: 1.25em 0; border-bottom: 1px solid rgba(128, 128, 128, .28); }
    .subtitleValidatorSearchRow { display: grid; grid-template-columns: minmax(0, 1fr) auto; align-items: end; gap: .8em; max-width: 42em; }
    .subtitleValidatorSearchRow > *, .subtitleValidatorTable td { min-width: 0; }
    .subtitleValidatorSearchRow .inputContainer { margin-bottom: 0; }
    .subtitleValidatorSingleTitle .selectContainer { max-width: 42em; margin-top: 1em; }
    .subtitleValidatorSingleTitle select { min-height: 9em; }
    .subtitleValidatorTitleActions { margin-top: 1em; }
    .subtitleValidatorRetranscribe { max-width: 48em; margin-top: 1em; }
    .subtitleValidatorInlineAction { display: flex; flex-wrap: wrap; align-items: center; gap: .8em; margin: .6em 0 1.25em; }
    .subtitleValidatorActivity { font-size: 1.08em; font-weight: 600; margin-bottom: .35em; }
    .subtitleValidatorAssessment { margin-top: 1.5em; padding-top: 1.25em; border-top: 1px solid rgba(128, 128, 128, .28); }
    .subtitleValidatorModelPicker { max-width: 42em; margin-top: 1.5em; }
    .subtitleValidatorHost { display: flex; flex-wrap: wrap; gap: .4em 1.2em; margin-top: .8em; font-size: .92em; font-variant-numeric: tabular-nums; }
    .subtitleValidatorHost span { min-width: 0; }
    .subtitleValidatorRecommendation { margin-top: 1em; font-weight: 600; line-height: 1.45; }
    .subtitleValidatorAssessmentTable { max-width: 48em; font-variant-numeric: tabular-nums; }
    .subtitleValidatorAssessmentNote { max-width: 72ch; margin-top: 1em; }
    .subtitleValidatorInstallHint { max-width: 62ch; margin-top: .65em; }
    .subtitleValidatorSummary { display: flex; flex-wrap: wrap; gap: 1.75em; margin: 1.25em 0 0; }
    .subtitleValidatorSummary div { min-width: 6.5em; }
    .subtitleValidatorSummary dt { font-size: .88em; opacity: .72; }
    .subtitleValidatorSummary dd { margin: .2em 0 0; font-size: 1.35em; font-weight: 600; font-variant-numeric: tabular-nums; }
    .subtitleValidatorTimeGrid, .subtitleValidatorNumberGrid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 1em; max-width: 38em; }
    .subtitleValidatorAdvanced { margin-top: 2em; }
    .subtitleValidatorAdvanced summary { cursor: pointer; font-size: 1.1em; font-weight: 600; margin-bottom: 1em; }
    .subtitleValidatorAdvanced summary:focus-visible { outline: 2px solid currentColor; outline-offset: .3em; }
    .subtitleValidatorAdvanced[open] summary { margin-bottom: .6em; }
    .subtitleValidatorAdvanced h4 { margin: 1.8em 0 .8em; }
    .subtitleValidatorEvidenceGrid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 1em; margin-bottom: 1.25em; }
    .subtitleValidatorEvidenceGrid fieldset { min-width: 0; padding: 1em; border: 1px solid rgba(128, 128, 128, .28); border-radius: .25em; }
    .subtitleValidatorEvidenceGrid legend { padding: 0 .35em; font-weight: 600; }
    .subtitleValidatorActions { display: flex; flex-wrap: wrap; gap: .8em; margin-top: 2em; }
    .subtitleValidatorSaveStatus { align-self: center; min-height: 1.4em; margin: 0; }
    .subtitleValidatorTimeGrid[aria-disabled="true"], #SubtitleAlignerFuzzySettings[aria-disabled="true"], #SubtitleAlignerCacheSettings[aria-disabled="true"] { opacity: .62; }
    .subtitleValidatorCacheStatus { display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: 1em 2em; max-width: 48em; margin-top: .8em; padding-top: 1em; border-top: 1px solid rgba(128, 128, 128, .28); }
    .subtitleValidatorCacheStatus > div { min-width: min(24em, 100%); flex: 1; }
    .subtitleValidatorResultsSection { margin-top: 3em; }
    .subtitleValidatorTableWrap { overflow-x: auto; margin-top: 1em; }
    .subtitleValidatorTable { width: 100%; border-collapse: collapse; text-align: left; }
    .subtitleValidatorTable th, .subtitleValidatorTable td { padding: .85em .75em; border-bottom: 1px solid rgba(128, 128, 128, .28); vertical-align: top; }
    .subtitleValidatorTable th { font-size: .88em; font-weight: 600; opacity: .76; }
    .subtitleValidatorTitle { font-weight: 600; }
    .subtitleValidatorMeta { display: block; margin-top: .2em; font-size: .88em; opacity: .72; }
    .subtitleValidatorResult { font-weight: 600; white-space: nowrap; }
    .subtitleValidatorEvidence { min-width: 18em; line-height: 1.45; }
    .subtitleValidatorProgress { display: block; width: min(15em, 100%); height: .65em; margin: 0 0 .35em; accent-color: currentColor; }
    .subtitleValidatorState { font-weight: 600; }
    .subtitleValidatorEmpty { padding: 1.5em .75em !important; opacity: .72; }
    @media (max-width: 40em) {
        .subtitleValidatorTimeGrid, .subtitleValidatorNumberGrid, .subtitleValidatorEvidenceGrid, .subtitleValidatorSearchRow { grid-template-columns: 1fr; gap: 0; }
        .subtitleValidatorActions { align-items: stretch; flex-direction: column; }
        .subtitleValidatorActions .emby-button { width: 100%; }
        .subtitleValidatorTable, .subtitleValidatorTable tbody, .subtitleValidatorTable tr, .subtitleValidatorTable td { display: block; width: 100%; }
        .subtitleValidatorTable thead { position: absolute; width: 1px; height: 1px; overflow: hidden; clip: rect(0 0 0 0); white-space: nowrap; }
        .subtitleValidatorTable tr { padding: .7em 0; border-bottom: 1px solid rgba(128, 128, 128, .28); }
        .subtitleValidatorTable td { box-sizing: border-box; min-width: 0; padding: .45em .25em; border: 0; }
        .subtitleValidatorTable td::before { content: attr(data-label); display: block; margin-bottom: .18em; font-size: .78em; font-weight: 600; opacity: .7; }
        .subtitleValidatorTable .subtitleValidatorEmpty::before { content: none; }
    }
`;

function ensureStyles() {
    if (document.getElementById('SubtitleAlignerStyles')) return;
    const element = document.createElement('style');
    element.id = 'SubtitleAlignerStyles';
    element.textContent = styles;
    document.head.appendChild(element);
}

function value(object, camel, pascal) {
    return object?.[camel] ?? object?.[pascal];
}

function escapeHtml(input) {
    return String(input ?? '').replace(/[&<>"']/g, character => ({
        '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
    })[character]);
}

function shortFileName(path) {
    return String(path || '').split(/[\\/]/).pop() || '';
}

function formatDate(value) {
    if (!value) return 'Never';
    const date = new Date(value);
    return Number.isNaN(date.getTime()) ? 'Unknown' : date.toLocaleString();
}

function formatDuration(seconds) {
    const numeric = Number(seconds);
    if (!Number.isFinite(numeric) || numeric < 0) return '';
    const rounded = Math.round(numeric);
    const hours = Math.floor(rounded / 3600);
    const minutes = Math.floor((rounded % 3600) / 60);
    const remainingSeconds = rounded % 60;
    if (hours) return `${hours}h ${minutes}m`;
    if (minutes) return `${minutes}m ${remainingSeconds}s`;
    return `${remainingSeconds}s`;
}

function describeError(error, fallback) {
    const detail = error?.responseJSON?.Error
        || error?.responseJSON?.Message
        || error?.statusText
        || error?.message;
    return detail && detail !== fallback ? `${fallback} ${detail}` : fallback;
}

function setSaveStatus(view, message) {
    view.querySelector('#SubtitleAlignerSaveStatus').textContent = message;
}

function markSettingsDirty(view, target) {
    if (!settingsLoaded || ['SubtitleAlignerItemSearch', 'SubtitleAlignerItemResults', 'SubtitleAlignerRetranscribe'].includes(target.id)) {
        return;
    }

    setSaveStatus(view, 'Unsaved changes.');
}

function syncRunWindowFields(view) {
    const enabled = view.querySelector('#SubtitleAlignerUseWindow').checked;
    view.querySelector('#SubtitleAlignerStart').disabled = !enabled;
    view.querySelector('#SubtitleAlignerEnd').disabled = !enabled;
    view.querySelector('#SubtitleAlignerTimeGrid').setAttribute('aria-disabled', enabled ? 'false' : 'true');
}

function syncExternalServerFields(view) {
    const configured = Boolean(view.querySelector('#SubtitleAlignerExternalWhisperUrl').value.trim());
    view.querySelector('#SubtitleAlignerFallbackWhisper').disabled = !configured;
    view.querySelector('#SubtitleAlignerTestWhisper').disabled = !configured;
}

function syncCacheFields(view) {
    const enabled = view.querySelector('#SubtitleAlignerEnableCache').checked;
    const settings = view.querySelector('#SubtitleAlignerCacheSettings');
    settings.setAttribute('aria-disabled', enabled ? 'false' : 'true');
    settings.querySelectorAll('input').forEach(input => {
        input.disabled = !enabled;
    });
}

function syncFuzzyFields(view) {
    const enabled = view.querySelector('#SubtitleAlignerEnableFuzzy').checked;
    const phoneticEnabled = enabled && view.querySelector('#SubtitleAlignerFuzzyPhonetic').checked;
    const settings = view.querySelector('#SubtitleAlignerFuzzySettings');
    settings.setAttribute('aria-disabled', enabled ? 'false' : 'true');
    settings.querySelectorAll('input').forEach(input => {
        input.disabled = !enabled;
    });
    view.querySelector('#SubtitleAlignerFuzzyPhoneticWeight').disabled = !phoneticEnabled;
}

function setAnalysisFields(view, values = analysisDefaults) {
    view.querySelector('#SubtitleAlignerSamplePositions').value = values.samplePositions;
    view.querySelector('#SubtitleAlignerInitialSamples').value = values.initialSamples;
    view.querySelector('#SubtitleAlignerSamples').value = values.maximumSamples;
    view.querySelector('#SubtitleAlignerClipDuration').value = values.clipDuration;
    view.querySelector('#SubtitleAlignerCueLead').value = values.cueLead;
    view.querySelector('#SubtitleAlignerMatchScore').value = values.matchScore;
    view.querySelector('#SubtitleAlignerMatchGap').value = values.matchGap;
    view.querySelector('#SubtitleAlignerMatchedWords').value = values.matchedWords;
    view.querySelector('#SubtitleAlignerEnableFuzzy').checked = values.enableFuzzy;
    view.querySelector('#SubtitleAlignerCrossLanguageFuzzy').checked = values.enableCrossLanguageFuzzy;
    view.querySelector('#SubtitleAlignerCrossLanguageCueCoverage').value = values.crossLanguageCueCoverage;
    view.querySelector('#SubtitleAlignerMaximumIndividualCueOffset').value = values.maximumIndividualCueOffset;
    view.querySelector('#SubtitleAlignerFuzzySearchRadius').value = values.fuzzySearchRadius;
    view.querySelector('#SubtitleAlignerFuzzyMatchScore').value = values.fuzzyMatchScore;
    view.querySelector('#SubtitleAlignerFuzzyDistinctiveTokens').value = values.fuzzyDistinctiveTokens;
    view.querySelector('#SubtitleAlignerFuzzyTokenSimilarity').value = values.fuzzyTokenSimilarity;
    view.querySelector('#SubtitleAlignerFuzzyStemming').checked = values.fuzzyStemming;
    view.querySelector('#SubtitleAlignerFuzzyPhonetic').checked = values.fuzzyPhonetic;
    view.querySelector('#SubtitleAlignerFuzzyPhoneticWeight').value = values.fuzzyPhoneticWeight;
    view.querySelector('#SubtitleAlignerFuzzyMatchGap').value = values.fuzzyMatchGap;
    syncFuzzyFields(view);
    view.querySelector('#SubtitleAlignerResidual').value = values.residual;
    view.querySelector('#SubtitleAlignerInSyncOffset').value = values.inSyncOffset;
    view.querySelector('#SubtitleAlignerCueDeadBand').value = values.cueDeadBand;
    view.querySelector('#SubtitleAlignerCrossLanguageVadDeadBand').value = values.crossLanguageVadDeadBand;
    view.querySelector('#SubtitleAlignerImprovement').value = values.improvement;
    view.querySelector('#SubtitleAlignerDrift').value = values.drift;
    view.querySelector('#SubtitleAlignerBreak').value = values.timingBreak;
    view.querySelector('#SubtitleAlignerHighAnchors').value = values.highAnchors;
    view.querySelector('#SubtitleAlignerHighWords').value = values.highWords;
    view.querySelector('#SubtitleAlignerHighCoverage').value = values.highCoverage;
    view.querySelector('#SubtitleAlignerHighScore').value = values.highScore;
    view.querySelector('#SubtitleAlignerMediumAnchors').value = values.mediumAnchors;
    view.querySelector('#SubtitleAlignerMediumWords').value = values.mediumWords;
    view.querySelector('#SubtitleAlignerMediumCoverage').value = values.mediumCoverage;
    view.querySelector('#SubtitleAlignerMediumScore').value = values.mediumScore;
}

function boundedNumber(view, selector, fallback, minimum, maximum) {
    const parsed = Number(view.querySelector(selector).value);
    return Math.max(minimum, Math.min(maximum, Number.isFinite(parsed) ? parsed : fallback));
}

function updateActionStates(view) {
    const customModel = Boolean(view.querySelector('#SubtitleAlignerModelPath').value.trim());
    const externalInput = view.querySelector('#SubtitleAlignerExternalWhisperUrl');
    const externalServer = Boolean(externalInput.value.trim()) && externalInput.validity.valid;
    const modelSelect = view.querySelector('#SubtitleAlignerManagedModel');
    const selectedModel = modelSelect.value;
    const selectedModelInstalled = Boolean(selectedModel)
        && selectedModel === modelStatusName
        && managedModelValid;
    modelReady = externalServer || customModel || selectedModelInstalled;

    view.querySelector('#SubtitleAlignerRun').disabled = validatorRunning || assessmentRunning || !modelReady;
    view.querySelector('#SubtitleAlignerSyncItem').disabled = validatorRunning || assessmentRunning || !modelReady || !selectedItemId;
    view.querySelector('#SubtitleAlignerAlignItem').disabled = validatorRunning || assessmentRunning || !modelReady || !selectedItemId;
    view.querySelector('#SubtitleAlignerRetranscribe').disabled = validatorRunning || assessmentRunning || !modelReady || !selectedItemId;
    view.querySelector('#SubtitleAlignerAssessHost').disabled = validatorRunning || assessmentRunning || modelDownloadBusy || customModel || !selectedModel;
    const installButton = view.querySelector('#SubtitleAlignerDownloadModel');
    installButton.disabled = modelDownloadBusy || assessmentRunning || customModel || !selectedModel || selectedModelInstalled;
    installButton.querySelector('span').textContent = modelDownloadBusy
        ? 'Installing…'
        : selectedModelInstalled
            ? 'Installed'
            : 'Install selected model';
    modelSelect.disabled = assessmentRunning || modelDownloadBusy || customModel || compatibleModels.length === 0;

    const runHint = view.querySelector('#SubtitleAlignerRunHint');
    if (!modelReady) {
        runHint.textContent = externalInput.value.trim() && !externalInput.validity.valid
            ? 'Enter a valid external server URL, or set up a local model, to enable title checks.'
            : 'Set up a managed model, custom model, or external Whisper server to enable title checks.';
    } else if (validatorRunning) {
        runHint.textContent = 'A subtitle run is already in progress. New title checks will be available when it finishes.';
    } else {
        runHint.textContent = 'Ready. Choose a matching title, then shift the entire track or align individual cues.';
    }

    const hint = view.querySelector('#SubtitleAlignerInstallHint');
    if (customModel) {
        hint.textContent = 'Managed model controls are unavailable while a custom model path is configured.';
    } else if (!selectedModel) {
        hint.textContent = 'No compatible managed model is available for the detected host and engine.';
    } else if (selectedModelInstalled) {
        hint.textContent = 'The selected model is installed and SHA-256 verified.';
    } else {
        hint.textContent = 'Install the selected model before running Subtitle Aligner. Installing replaces the previously managed model.';
    }
}

function renderStatus(view, status) {
    const running = Boolean(value(status, 'running', 'Running'));
    validatorRunning = running;
    const current = value(status, 'currentItem', 'CurrentItem') || '';
    view.querySelector('#SubtitleAlignerActivity').textContent = running
        ? `Running${current ? ` — ${current}` : ''}`
        : 'Idle';
    view.querySelector('#SubtitleAlignerMessage').textContent = value(status, 'lastMessage', 'LastMessage') || '';
    view.querySelector('#SubtitleAlignerLastRun').textContent = `Last run: ${formatDate(value(status, 'lastRunUtc', 'LastRunUtc'))}`;
    view.querySelector('#SubtitleAlignerTotal').textContent = value(status, 'total', 'Total') ?? 0;
    view.querySelector('#SubtitleAlignerInSync').textContent = value(status, 'inSync', 'InSync') ?? 0;
    view.querySelector('#SubtitleAlignerCorrected').textContent = value(status, 'corrected', 'Corrected') ?? 0;
    view.querySelector('#SubtitleAlignerAttention').textContent = value(status, 'needsAttention', 'NeedsAttention') ?? 0;
    view.querySelector('#SubtitleAlignerInconclusive').textContent = value(status, 'insufficientEvidence', 'InsufficientEvidence') ?? 0;
    updateActionStates(view);

    const libraries = value(status, 'libraries', 'Libraries') || [];
    const libraryBody = view.querySelector('#SubtitleAlignerLibraries');
    if (!libraries.length) {
        libraryBody.innerHTML = '<tr><td colspan="4" class="subtitleValidatorEmpty">Library progress will appear after the first run.</td></tr>';
    } else {
        libraryBody.innerHTML = libraries.map(library => {
            const name = value(library, 'libraryName', 'LibraryName') || 'Unnamed library';
            const discovered = Number(value(library, 'discoveredPairs', 'DiscoveredPairs') ?? 0);
            const processed = Math.min(discovered, Number(value(library, 'processedPairs', 'ProcessedPairs') ?? 0));
            const errors = Number(value(library, 'errorPairs', 'ErrorPairs') ?? 0);
            const state = value(library, 'state', 'State') || 'Not scanned';
            const completed = formatDate(value(library, 'lastCompletedUtc', 'LastCompletedUtc'));
            const errorText = errors > 0 ? ` · ${errors} ${errors === 1 ? 'error' : 'errors'}` : '';
            return `<tr>
                <td data-label="Library"><span class="subtitleValidatorTitle">${escapeHtml(name)}</span></td>
                <td data-label="Progress"><progress class="subtitleValidatorProgress" max="${Math.max(1, discovered)}" value="${discovered === 0 ? 1 : processed}" aria-label="${escapeHtml(name)} scan progress"></progress>${processed} of ${discovered} processed${escapeHtml(errorText)}</td>
                <td data-label="State"><span class="subtitleValidatorState">${escapeHtml(state)}</span></td>
                <td data-label="Last completed">${escapeHtml(completed)}</td>
            </tr>`;
        }).join('');
    }

    const results = value(status, 'recent', 'Recent') || [];
    const body = view.querySelector('#SubtitleAlignerResults');
    if (!results.length) {
        body.innerHTML = '<tr><td colspan="4" class="subtitleValidatorEmpty">No results yet. Save the settings and run Subtitle Aligner.</td></tr>';
        return;
    }

    body.innerHTML = results.map(result => {
        const title = value(result, 'itemName', 'ItemName') || 'Untitled';
        const library = value(result, 'libraryName', 'LibraryName') || 'Unknown library';
        const subtitlePath = value(result, 'subtitlePath', 'SubtitlePath') || '';
        const language = value(result, 'language', 'Language') || 'unknown language';
        const statusText = value(result, 'status', 'Status') || 'Unknown';
        const message = value(result, 'message', 'Message') || '';
        const anchors = value(result, 'anchors', 'Anchors') ?? 0;
        const strictAnchors = value(result, 'strictAnchors', 'StrictAnchors') ?? 0;
        const guidedFuzzyAnchors = value(result, 'guidedFuzzyAnchors', 'GuidedFuzzyAnchors') ?? 0;
        const samples = value(result, 'samples', 'Samples') ?? 0;
        const confidence = value(result, 'confidence', 'Confidence') || 'Inconclusive';
        const model = value(result, 'model', 'Model') || '';
        const matchedWords = value(result, 'matchedWords', 'MatchedWords') ?? 0;
        const p90 = value(result, 'p90ResidualSeconds', 'P90ResidualSeconds');
        const elapsedSeconds = Number(value(result, 'analysisElapsedSeconds', 'AnalysisElapsedSeconds') ?? 0);
        const mediaDurationSeconds = Number(value(result, 'mediaDurationSeconds', 'MediaDurationSeconds') ?? 0);
        const analyzed = formatDate(value(result, 'analyzedUtc', 'AnalyzedUtc'));
        const correctionStatus = value(result, 'correctionStatus', 'CorrectionStatus') || '';
        const correctionMessage = value(result, 'correctionMessage', 'CorrectionMessage') || '';
        const correction = correctionStatus && !['Not needed', 'Not eligible'].includes(correctionStatus)
            ? `<span class="subtitleValidatorMeta">Correction: ${escapeHtml(correctionStatus)}${correctionMessage ? ` — ${escapeHtml(correctionMessage)}` : ''}</span>`
            : '';
        const anchorEvidence = guidedFuzzyAnchors > 0
            ? `${strictAnchors} strict + ${guidedFuzzyAnchors} guided anchors`
            : `${anchors} anchors`;
        const fit = [
            `${anchorEvidence} / ${matchedWords} matched words from ${samples} samples`,
            model,
            p90 == null ? '' : `90% within ${Number(p90).toFixed(2)}s`,
            elapsedSeconds > 0
                ? `Scanned in ${formatDuration(elapsedSeconds)}${mediaDurationSeconds > 0
                    ? ` (${((elapsedSeconds / mediaDurationSeconds) * 100).toFixed(1)}% of ${formatDuration(mediaDurationSeconds)} runtime)`
                    : ''}`
                : ''
        ].filter(Boolean).join(' · ');
        return `<tr>
            <td data-label="Title"><span class="subtitleValidatorTitle">${escapeHtml(title)}</span><span class="subtitleValidatorMeta">${escapeHtml(library)}</span></td>
            <td data-label="Subtitle">${escapeHtml(shortFileName(subtitlePath))}<span class="subtitleValidatorMeta">${escapeHtml(language)}</span></td>
            <td data-label="Result"><span class="subtitleValidatorResult">${escapeHtml(statusText)}</span><span class="subtitleValidatorMeta">${escapeHtml(confidence)} confidence</span></td>
            <td data-label="Evidence" class="subtitleValidatorEvidence">${escapeHtml(message)}${correction}<span class="subtitleValidatorMeta">${escapeHtml(fit)} · Checked ${escapeHtml(analyzed)}</span></td>
        </tr>`;
    }).join('');
}

function renderModelStatus(view, status) {
    const installed = Boolean(value(status, 'installed', 'Installed'));
    const valid = Boolean(value(status, 'valid', 'Valid'));
    const busy = Boolean(value(status, 'busy', 'Busy'));
    const custom = Boolean(view.querySelector('#SubtitleAlignerModelPath').value.trim());
    const displayName = value(status, 'modelDisplayName', 'ModelDisplayName') || 'Selected model';
    modelStatusName = value(status, 'modelName', 'ModelName') || '';
    modelDownloadBusy = busy;
    managedModelValid = installed && valid;

    let heading = 'Model not installed';
    if (custom) heading = 'Custom model path configured';
    else if (busy) heading = 'Downloading and verifying model…';
    else if (installed && valid) heading = `${displayName} ready — SHA-256 verified`;
    else if (installed) heading = 'Installed model is invalid';

    view.querySelector('#SubtitleAlignerModelStatus').textContent = heading;
    view.querySelector('#SubtitleAlignerModelMessage').textContent = custom
        ? 'The custom file will be checked for readability when a run starts.'
        : value(status, 'message', 'Message') || '';
    view.querySelector('#SubtitleAlignerModelLive').setAttribute('aria-busy', busy ? 'true' : 'false');
    updateActionStates(view);
}

function formatBytes(bytes) {
    const numeric = Number(bytes || 0);
    if (!numeric) return 'Unknown memory';
    return `${(numeric / (1024 ** 3)).toFixed(1)} GB`;
}

function formatMegabytes(bytes) {
    const numeric = Number(bytes || 0);
    return numeric ? `${(numeric / (1024 ** 2)).toFixed(0)} MB` : 'Unknown size';
}

function formatCacheSize(bytes) {
    const numeric = Number(bytes || 0);
    if (numeric < 1024) return `${numeric} B`;
    if (numeric < 1024 ** 2) return `${(numeric / 1024).toFixed(1)} KB`;
    if (numeric < 1024 ** 3) return `${(numeric / (1024 ** 2)).toFixed(1)} MB`;
    return `${(numeric / (1024 ** 3)).toFixed(2)} GB`;
}

function renderCacheStatus(view, status) {
    const enabled = Boolean(value(status, 'enabled', 'Enabled'));
    const entries = Number(value(status, 'entryCount', 'EntryCount') || 0);
    const size = Number(value(status, 'sizeBytes', 'SizeBytes') || 0);
    const retention = Number(value(status, 'retentionDays', 'RetentionDays') || 7);
    const maximum = Number(value(status, 'maximumBytes', 'MaximumBytes') || 0);
    const oldest = value(status, 'oldestAccessUtc', 'OldestAccessUtc');
    const hits = Number(value(status, 'sessionHits', 'SessionHits') || 0);
    view.querySelector('#SubtitleAlignerCacheSummary').textContent = enabled
        ? `${entries} cached ${entries === 1 ? 'section' : 'sections'} · ${formatCacheSize(size)}`
        : 'Speech analysis cache disabled';
    view.querySelector('#SubtitleAlignerCacheDetails').textContent = entries
        ? `Oldest use: ${formatDate(oldest)} · ${hits} ${hits === 1 ? 'reuse' : 'reuses'} since Jellyfin started · ${retention}-day retention · ${formatCacheSize(maximum)} limit`
        : `No cached sections yet · ${retention}-day retention · ${formatCacheSize(maximum)} limit`;
    view.querySelector('#SubtitleAlignerClearCache').disabled = entries === 0;
}

function renderModelChoice(view) {
    const selected = view.querySelector('#SubtitleAlignerManagedModel').value;
    const option = compatibleModels.find(candidate => value(candidate, 'modelName', 'ModelName') === selected);
    const description = view.querySelector('#SubtitleAlignerManagedModelDescription');
    if (!option) {
        description.textContent = 'No compatible managed models were detected.';
        updateActionStates(view);
        return;
    }

    const displayName = value(option, 'modelDisplayName', 'ModelDisplayName') || 'Selected model';
    const qualityTier = value(option, 'qualityTier', 'QualityTier') || '';
    const explanation = value(option, 'description', 'Description') || '';
    const downloadSize = formatMegabytes(value(option, 'downloadSizeBytes', 'DownloadSizeBytes'));
    const workingMemory = formatMegabytes(value(option, 'estimatedWorkingMemoryBytes', 'EstimatedWorkingMemoryBytes'));
    const recommendation = selected === recommendedModelName ? ' Recommended for this host.' : '';
    description.textContent = `${qualityTier}${qualityTier ? ' — ' : ''}${explanation} ${downloadSize} download; approximately ${workingMemory} working memory.${recommendation}`;
    description.setAttribute('aria-label', `${displayName}. ${description.textContent}`);
    updateActionStates(view);
}

function renderAssessment(view, status) {
    const running = Boolean(value(status, 'running', 'Running'));
    assessmentRunning = running;
    const stage = value(status, 'stage', 'Stage') || 'Not run';
    const message = value(status, 'message', 'Message') || '';
    const host = value(status, 'host', 'Host') || {};
    const features = value(host, 'cpuFeatures', 'CpuFeatures') || [];
    const architecture = value(host, 'architecture', 'Architecture') || 'Unknown architecture';
    const processor = value(host, 'processor', 'Processor') || '';
    const processors = Number(value(host, 'logicalProcessors', 'LogicalProcessors') ?? 0);
    const totalMemory = value(host, 'totalMemoryBytes', 'TotalMemoryBytes');
    const availableMemory = value(host, 'availableMemoryBytes', 'AvailableMemoryBytes');
    const operatingSystem = value(host, 'operatingSystem', 'OperatingSystem') || '';
    const gpuDetected = Boolean(value(host, 'gpuDeviceDetected', 'GpuDeviceDetected'));
    const inferenceBackend = value(host, 'inferenceBackend', 'InferenceBackend') || '';

    view.querySelector('#SubtitleAlignerAssessmentStage').textContent = stage;
    view.querySelector('#SubtitleAlignerAssessmentMessage').textContent = message;
    view.querySelector('#SubtitleAlignerAssessmentLive').setAttribute('aria-busy', running ? 'true' : 'false');
    view.querySelector('#SubtitleAlignerHost').innerHTML = [
        operatingSystem ? `<span>${escapeHtml(operatingSystem)}</span>` : '',
        processor ? `<span>${escapeHtml(processor)}</span>` : '',
        `<span>${escapeHtml(architecture)} · ${processors || 'Unknown'} logical ${processors === 1 ? 'processor' : 'processors'}</span>`,
        `<span>${escapeHtml(formatBytes(availableMemory))} available of ${escapeHtml(formatBytes(totalMemory))}</span>`,
        features.length ? `<span>${escapeHtml(features.join(', '))}</span>` : '<span>No reported SIMD extensions</span>',
        `<span>${gpuDetected ? 'GPU device detected' : 'No GPU device detected'}</span>`,
        inferenceBackend ? `<span>${escapeHtml(inferenceBackend)}</span>` : ''
    ].filter(Boolean).join('');

    compatibleModels = value(status, 'compatibleModels', 'CompatibleModels') || [];
    recommendedModelName = value(status, 'recommendedModelName', 'RecommendedModelName') || '';
    const modelSelect = view.querySelector('#SubtitleAlignerManagedModel');
    const priorSelection = modelSelect.value;
    const optionSignature = compatibleModels
        .map(option => `${value(option, 'modelName', 'ModelName')}:${Boolean(value(option, 'installed', 'Installed'))}`)
        .join('|');
    if (modelSelect.dataset.signature !== optionSignature) {
        modelSelect.replaceChildren(...compatibleModels.map(option => {
            const modelName = value(option, 'modelName', 'ModelName') || '';
            const displayName = value(option, 'modelDisplayName', 'ModelDisplayName') || modelName;
            const qualityTier = value(option, 'qualityTier', 'QualityTier') || '';
            return new Option(`${displayName}${qualityTier ? ` — ${qualityTier}` : ''}`, modelName);
        }));
        modelSelect.dataset.signature = optionSignature;
    }
    const compatibleNames = compatibleModels.map(option => value(option, 'modelName', 'ModelName'));
    const desiredSelection = compatibleNames.includes(priorSelection)
        ? priorSelection
        : compatibleNames.includes(configuredManagedModelName)
            ? configuredManagedModelName
            : recommendedModelName;
    modelSelect.value = compatibleNames.includes(desiredSelection) ? desiredSelection : (compatibleNames[0] || '');

    const recommendation = value(status, 'recommendation', 'Recommendation') || '';
    const recommendationElement = view.querySelector('#SubtitleAlignerRecommendation');
    recommendationElement.textContent = recommendation;
    recommendationElement.classList.toggle('hide', !recommendation);

    const results = value(status, 'results', 'Results') || [];
    const resultsWrap = view.querySelector('#SubtitleAlignerAssessmentResultsWrap');
    resultsWrap.classList.toggle('hide', results.length === 0);
    view.querySelector('#SubtitleAlignerAssessmentResults').innerHTML = results.map(result => {
        const name = value(result, 'modelDisplayName', 'ModelDisplayName') || value(result, 'modelName', 'ModelName') || 'Model';
        const successful = Boolean(value(result, 'successful', 'Successful'));
        const loadSeconds = Number(value(result, 'loadSeconds', 'LoadSeconds') ?? 0);
        const inferenceSeconds = Number(value(result, 'inferenceSeconds', 'InferenceSeconds') ?? 0);
        const realTimeFactor = Number(value(result, 'realTimeFactor', 'RealTimeFactor') ?? 0);
        const runs = Number(value(result, 'runs', 'Runs') ?? 0);
        const error = value(result, 'message', 'Message') || '';
        return `<tr>
            <td data-label="Model"><span class="subtitleValidatorTitle">${escapeHtml(name)}</span>${successful ? '' : `<span class="subtitleValidatorMeta">${escapeHtml(error)}</span>`}</td>
            <td data-label="Load">${successful ? `${loadSeconds.toFixed(1)}s` : 'Failed'}</td>
            <td data-label="Median transcription">${successful ? `${inferenceSeconds.toFixed(1)}s for 11s${runs ? ` · ${runs} runs` : ''}` : '—'}</td>
            <td data-label="Relative speed">${successful ? `${realTimeFactor.toFixed(2)}× real time` : '—'}</td>
        </tr>`;
    }).join('');
    renderModelChoice(view);
}

async function searchItems(view) {
    const search = view.querySelector('#SubtitleAlignerItemSearch').value.trim();
    const status = view.querySelector('#SubtitleAlignerItemSearchStatus');
    const wrap = view.querySelector('#SubtitleAlignerItemResultsWrap');
    const select = view.querySelector('#SubtitleAlignerItemResults');
    selectedItemId = '';
    updateActionStates(view);

    if (search.length < 2) {
        status.textContent = 'Enter at least two characters.';
        wrap.classList.add('hide');
        return;
    }

    status.textContent = 'Searching…';
    view.querySelector('#SubtitleAlignerItemSearchButton').disabled = true;
    try {
        const url = `${ApiClient.getUrl('SubtitleAligner/Items')}?search=${encodeURIComponent(search)}`;
        const response = await ApiClient.ajax({ type: 'GET', url });
        const items = await response.json();
        select.replaceChildren(new Option('Choose a title', ''));
        for (const item of items) {
            const id = value(item, 'itemId', 'ItemId') || '';
            const name = value(item, 'name', 'Name') || 'Untitled';
            const kind = value(item, 'kind', 'Kind') || 'Video';
            const library = value(item, 'libraryName', 'LibraryName') || 'Unknown library';
            const count = Number(value(item, 'subtitleCount', 'SubtitleCount') ?? 0);
            const label = `${name} — ${kind}, ${library} · ${count} ${count === 1 ? 'subtitle' : 'subtitles'}`;
            select.append(new Option(label, id));
        }

        wrap.classList.toggle('hide', items.length === 0);
        status.textContent = items.length === 0
            ? 'No matching titles with supported text subtitles.'
            : `${items.length} matching ${items.length === 1 ? 'title' : 'titles'}.`;
    } catch (error) {
        wrap.classList.add('hide');
        status.textContent = describeError(error, 'Search failed. Check your access and try again.');
    } finally {
        view.querySelector('#SubtitleAlignerItemSearchButton').disabled = false;
    }
}

async function loadModelStatus(view) {
    try {
        const response = await ApiClient.ajax({ type: 'GET', url: ApiClient.getUrl('SubtitleAligner/Model') });
        renderModelStatus(view, await response.json());
    } catch (error) {
        view.querySelector('#SubtitleAlignerModelStatus').textContent = 'Model status unavailable';
        view.querySelector('#SubtitleAlignerModelMessage').textContent = describeError(error, 'Reload this page or check the Jellyfin log.');
    }
}

async function loadAssessment(view) {
    const wasRunning = assessmentRunning;
    try {
        const response = await ApiClient.ajax({ type: 'GET', url: ApiClient.getUrl('SubtitleAligner/Model/Assessment') });
        renderAssessment(view, await response.json());
        if (wasRunning && !assessmentRunning) await loadModelStatus(view);
    } catch (error) {
        view.querySelector('#SubtitleAlignerAssessmentStage').textContent = 'Model advice unavailable';
        view.querySelector('#SubtitleAlignerAssessmentMessage').textContent = describeError(error, 'Reload this page or check the Jellyfin log.');
    }
}

async function loadStatus(view) {
    try {
        const response = await ApiClient.ajax({ type: 'GET', url: ApiClient.getUrl('SubtitleAligner/Status') });
        renderStatus(view, await response.json());
    } catch (error) {
        view.querySelector('#SubtitleAlignerActivity').textContent = 'Status unavailable';
        view.querySelector('#SubtitleAlignerMessage').textContent = describeError(error, 'Reload this page or check the Jellyfin log.');
    }
}

async function loadCacheStatus(view) {
    try {
        const response = await ApiClient.ajax({ type: 'GET', url: ApiClient.getUrl('SubtitleAligner/Cache') });
        renderCacheStatus(view, await response.json());
    } catch (error) {
        view.querySelector('#SubtitleAlignerCacheSummary').textContent = 'Cache status unavailable';
        view.querySelector('#SubtitleAlignerCacheDetails').textContent = describeError(error, 'Reload this page or check the Jellyfin log.');
    }
}

async function load(view) {
    Dashboard.showLoadingMsg();
    settingsLoaded = false;
    try {
        const config = await ApiClient.getPluginConfiguration(pluginId);
        view.querySelector('#SubtitleAlignerUseWindow').checked = config.UseRunWindow === true;
        syncRunWindowFields(view);
        view.querySelector('#SubtitleAlignerStart').value = config.RunWindowStart || '02:00';
        view.querySelector('#SubtitleAlignerEnd').value = config.RunWindowEnd || '06:00';
        view.querySelector('#SubtitleAlignerCreateSidecars').checked = config.CreateCorrectedSidecars === true;
        view.querySelector('#SubtitleAlignerServerPath').value = config.WhisperServerPath || '';
        view.querySelector('#SubtitleAlignerExternalWhisperUrl').value = config.ExternalWhisperUrl || '';
        view.querySelector('#SubtitleAlignerFallbackWhisper').checked = config.FallbackToLocalWhisper !== false;
        syncExternalServerFields(view);
        view.querySelector('#SubtitleAlignerEnableCache').checked = config.EnableTranscriptionCache !== false;
        view.querySelector('#SubtitleAlignerCacheRetention').value = config.TranscriptionCacheRetentionDays > 0
            ? config.TranscriptionCacheRetentionDays
            : 7;
        view.querySelector('#SubtitleAlignerCacheMaximum').value = config.TranscriptionCacheMaximumMegabytes > 0
            ? config.TranscriptionCacheMaximumMegabytes
            : 2048;
        syncCacheFields(view);
        view.querySelector('#SubtitleAlignerModelPath').value = config.WhisperModelPath || '';
        configuredManagedModelName = config.ManagedModelName || '';
        view.querySelector('#SubtitleAlignerFfmpegPath').value = config.FfmpegPath || '';
        setAnalysisFields(view, {
            samplePositions: config.SamplePositions || analysisDefaults.samplePositions,
            initialSamples: config.InitialSamplesPerSubtitle ?? analysisDefaults.initialSamples,
            maximumSamples: config.SamplesPerSubtitle ?? analysisDefaults.maximumSamples,
            clipDuration: config.ClipDurationSeconds ?? analysisDefaults.clipDuration,
            cueLead: config.CueLeadSeconds ?? analysisDefaults.cueLead,
            matchScore: config.MinimumMatchScore ?? analysisDefaults.matchScore,
            matchGap: config.MinimumMatchUniqueness ?? analysisDefaults.matchGap,
            matchedWords: config.MinimumMatchedWords ?? analysisDefaults.matchedWords,
            enableFuzzy: config.EnableGuidedFuzzyMatching ?? analysisDefaults.enableFuzzy,
            enableCrossLanguageFuzzy: config.EnableCrossLanguageFuzzyMatching ?? analysisDefaults.enableCrossLanguageFuzzy,
            crossLanguageCueCoverage: config.CrossLanguageMinimumCueCoverage > 0
                ? config.CrossLanguageMinimumCueCoverage * 100
                : analysisDefaults.crossLanguageCueCoverage,
            maximumIndividualCueOffset: config.MaximumIndividualCueOffsetSeconds > 0
                ? config.MaximumIndividualCueOffsetSeconds
                : analysisDefaults.maximumIndividualCueOffset,
            fuzzySearchRadius: config.FuzzySearchRadiusSeconds ?? analysisDefaults.fuzzySearchRadius,
            fuzzyMatchScore: config.MinimumFuzzyMatchScore ?? analysisDefaults.fuzzyMatchScore,
            fuzzyDistinctiveTokens: config.MinimumFuzzyDistinctiveTokens ?? analysisDefaults.fuzzyDistinctiveTokens,
            fuzzyTokenSimilarity: config.MinimumFuzzyTokenSimilarity ?? analysisDefaults.fuzzyTokenSimilarity,
            fuzzyStemming: config.UseFuzzyStemming ?? analysisDefaults.fuzzyStemming,
            fuzzyPhonetic: config.UseFuzzyPhoneticMatching ?? analysisDefaults.fuzzyPhonetic,
            fuzzyPhoneticWeight: config.FuzzyPhoneticWeight ?? analysisDefaults.fuzzyPhoneticWeight,
            fuzzyMatchGap: config.MinimumFuzzyMatchUniqueness ?? analysisDefaults.fuzzyMatchGap,
            residual: config.ResidualToleranceSeconds ?? analysisDefaults.residual,
            inSyncOffset: config.InSyncOffsetSeconds ?? analysisDefaults.inSyncOffset,
            cueDeadBand: config.CueAlignmentDeadBandSeconds > 0
                ? config.CueAlignmentDeadBandSeconds
                : analysisDefaults.cueDeadBand,
            crossLanguageVadDeadBand: config.CrossLanguageVadDeadBandSeconds > 0
                ? config.CrossLanguageVadDeadBandSeconds
                : analysisDefaults.crossLanguageVadDeadBand,
            improvement: config.ModelImprovementPercent ?? analysisDefaults.improvement,
            drift: config.MinimumDriftAcrossTitleSeconds ?? analysisDefaults.drift,
            timingBreak: config.MinimumTimingBreakSeconds ?? analysisDefaults.timingBreak,
            highAnchors: config.HighConfidenceMinAnchors ?? analysisDefaults.highAnchors,
            highWords: config.HighConfidenceMinWords ?? analysisDefaults.highWords,
            highCoverage: config.HighConfidenceMinCoveragePercent ?? analysisDefaults.highCoverage,
            highScore: config.HighConfidenceMinMatchScore ?? analysisDefaults.highScore,
            mediumAnchors: config.MediumConfidenceMinAnchors ?? analysisDefaults.mediumAnchors,
            mediumWords: config.MediumConfidenceMinWords ?? analysisDefaults.mediumWords,
            mediumCoverage: config.MediumConfidenceMinCoveragePercent ?? analysisDefaults.mediumCoverage,
            mediumScore: config.MediumConfidenceMinMatchScore ?? analysisDefaults.mediumScore
        });
        view.querySelector('#SubtitleAlignerThreads').value = config.WhisperThreads ?? 0;
        await loadModelStatus(view);
        await loadAssessment(view);
        await loadStatus(view);
        await loadCacheStatus(view);
        setSaveStatus(view, '');
    } catch (error) {
        Dashboard.processErrorResponse(error);
    } finally {
        settingsLoaded = true;
        Dashboard.hideLoadingMsg();
    }
}

async function assessHost(view) {
    Dashboard.showLoadingMsg();
    assessmentRunning = true;
    view.querySelector('#SubtitleAlignerAssessmentStage').textContent = 'Starting benchmark…';
    view.querySelector('#SubtitleAlignerAssessmentMessage').textContent = 'Saving the selected model and preparing the temporary test sample.';
    updateActionStates(view);
    try {
        await save(view);
        await ApiClient.ajax({
            type: 'POST',
            url: ApiClient.getUrl('SubtitleAligner/Model/Assessment'),
            contentType: 'application/json',
            data: JSON.stringify({ ModelName: view.querySelector('#SubtitleAlignerManagedModel').value })
        });
        await loadAssessment(view);
    } catch (error) {
        assessmentRunning = false;
        Dashboard.processErrorResponse(error);
        await loadAssessment(view);
    } finally {
        Dashboard.hideLoadingMsg();
    }
}

async function downloadModel(view) {
    Dashboard.showLoadingMsg();
    modelDownloadBusy = true;
    updateActionStates(view);
    view.querySelector('#SubtitleAlignerModelStatus').textContent = 'Downloading and verifying model…';
    view.querySelector('#SubtitleAlignerModelMessage').textContent = 'This may take a few minutes. The model stays on the Jellyfin host.';
    view.querySelector('#SubtitleAlignerModelLive').setAttribute('aria-busy', 'true');
    try {
        await save(view);
        const response = await ApiClient.ajax({ type: 'POST', url: ApiClient.getUrl('SubtitleAligner/Model/Download') });
        renderModelStatus(view, await response.json());
        await loadAssessment(view);
    } catch (error) {
        Dashboard.processErrorResponse(error);
        await loadModelStatus(view);
    } finally {
        modelDownloadBusy = false;
        updateActionStates(view);
        Dashboard.hideLoadingMsg();
    }
}

async function save(view) {
    const externalInput = view.querySelector('#SubtitleAlignerExternalWhisperUrl');
    if (externalInput.value.trim() && !externalInput.reportValidity()) {
        setSaveStatus(view, 'Fix the external server URL before saving.');
        throw new Error('The external Whisper server URL is invalid.');
    }

    const config = await ApiClient.getPluginConfiguration(pluginId);
    config.UseRunWindow = view.querySelector('#SubtitleAlignerUseWindow').checked;
    config.RunWindowStart = view.querySelector('#SubtitleAlignerStart').value || '02:00';
    config.RunWindowEnd = view.querySelector('#SubtitleAlignerEnd').value || '06:00';
    config.CreateCorrectedSidecars = view.querySelector('#SubtitleAlignerCreateSidecars').checked;
    config.WhisperServerPath = view.querySelector('#SubtitleAlignerServerPath').value.trim();
    config.ExternalWhisperUrl = view.querySelector('#SubtitleAlignerExternalWhisperUrl').value.trim();
    config.FallbackToLocalWhisper = view.querySelector('#SubtitleAlignerFallbackWhisper').checked;
    config.EnableTranscriptionCache = view.querySelector('#SubtitleAlignerEnableCache').checked;
    config.TranscriptionCacheRetentionDays = boundedNumber(view, '#SubtitleAlignerCacheRetention', 7, 1, 365);
    config.TranscriptionCacheMaximumMegabytes = boundedNumber(view, '#SubtitleAlignerCacheMaximum', 2048, 128, 16384);
    config.WhisperModelPath = view.querySelector('#SubtitleAlignerModelPath').value.trim();
    config.ManagedModelName = view.querySelector('#SubtitleAlignerManagedModel').value
        || configuredManagedModelName
        || config.ManagedModelName;
    config.FfmpegPath = view.querySelector('#SubtitleAlignerFfmpegPath').value.trim();
    config.SamplePositions = view.querySelector('#SubtitleAlignerSamplePositions').value.trim() || analysisDefaults.samplePositions;
    config.SamplesPerSubtitle = boundedNumber(view, '#SubtitleAlignerSamples', analysisDefaults.maximumSamples, 3, 12);
    config.InitialSamplesPerSubtitle = Math.min(
        config.SamplesPerSubtitle,
        boundedNumber(view, '#SubtitleAlignerInitialSamples', analysisDefaults.initialSamples, 3, 12));
    config.ClipDurationSeconds = boundedNumber(view, '#SubtitleAlignerClipDuration', analysisDefaults.clipDuration, 10, 60);
    config.CueLeadSeconds = boundedNumber(view, '#SubtitleAlignerCueLead', analysisDefaults.cueLead, 0, 15);
    config.MinimumMatchScore = boundedNumber(view, '#SubtitleAlignerMatchScore', analysisDefaults.matchScore, 0.30, 0.95);
    config.MinimumMatchUniqueness = boundedNumber(view, '#SubtitleAlignerMatchGap', analysisDefaults.matchGap, 0.01, 0.50);
    config.MinimumMatchedWords = boundedNumber(view, '#SubtitleAlignerMatchedWords', analysisDefaults.matchedWords, 3, 30);
    config.EnableGuidedFuzzyMatching = view.querySelector('#SubtitleAlignerEnableFuzzy').checked;
    config.EnableCrossLanguageFuzzyMatching = view.querySelector('#SubtitleAlignerCrossLanguageFuzzy').checked;
    config.CrossLanguageMinimumCueCoverage = boundedNumber(
        view,
        '#SubtitleAlignerCrossLanguageCueCoverage',
        analysisDefaults.crossLanguageCueCoverage,
        25,
        100) / 100;
    config.MaximumIndividualCueOffsetSeconds = boundedNumber(
        view,
        '#SubtitleAlignerMaximumIndividualCueOffset',
        analysisDefaults.maximumIndividualCueOffset,
        0.25,
        10);
    config.FuzzySearchRadiusSeconds = boundedNumber(view, '#SubtitleAlignerFuzzySearchRadius', analysisDefaults.fuzzySearchRadius, 15, 180);
    config.MinimumFuzzyMatchScore = boundedNumber(view, '#SubtitleAlignerFuzzyMatchScore', analysisDefaults.fuzzyMatchScore, 0.30, 0.95);
    config.MinimumFuzzyDistinctiveTokens = boundedNumber(view, '#SubtitleAlignerFuzzyDistinctiveTokens', analysisDefaults.fuzzyDistinctiveTokens, 3, 20);
    config.MinimumFuzzyTokenSimilarity = boundedNumber(view, '#SubtitleAlignerFuzzyTokenSimilarity', analysisDefaults.fuzzyTokenSimilarity, 0.60, 0.98);
    config.UseFuzzyStemming = view.querySelector('#SubtitleAlignerFuzzyStemming').checked;
    config.UseFuzzyPhoneticMatching = view.querySelector('#SubtitleAlignerFuzzyPhonetic').checked;
    config.FuzzyPhoneticWeight = boundedNumber(view, '#SubtitleAlignerFuzzyPhoneticWeight', analysisDefaults.fuzzyPhoneticWeight, 0.10, 0.80);
    config.MinimumFuzzyMatchUniqueness = boundedNumber(view, '#SubtitleAlignerFuzzyMatchGap', analysisDefaults.fuzzyMatchGap, 0.01, 0.50);
    config.ResidualToleranceSeconds = boundedNumber(view, '#SubtitleAlignerResidual', analysisDefaults.residual, 0.10, 3);
    config.InSyncOffsetSeconds = boundedNumber(view, '#SubtitleAlignerInSyncOffset', analysisDefaults.inSyncOffset, 0.05, 3);
    config.CueAlignmentDeadBandSeconds = boundedNumber(view, '#SubtitleAlignerCueDeadBand', analysisDefaults.cueDeadBand, 0.05, 1);
    config.CrossLanguageVadDeadBandSeconds = boundedNumber(view, '#SubtitleAlignerCrossLanguageVadDeadBand', analysisDefaults.crossLanguageVadDeadBand, 0.05, 1);
    config.ModelImprovementPercent = boundedNumber(view, '#SubtitleAlignerImprovement', analysisDefaults.improvement, 1, 90);
    config.MinimumDriftAcrossTitleSeconds = boundedNumber(view, '#SubtitleAlignerDrift', analysisDefaults.drift, 0.10, 10);
    config.MinimumTimingBreakSeconds = boundedNumber(view, '#SubtitleAlignerBreak', analysisDefaults.timingBreak, 0.10, 20);
    config.HighConfidenceMinAnchors = boundedNumber(view, '#SubtitleAlignerHighAnchors', analysisDefaults.highAnchors, 3, 12);
    config.HighConfidenceMinWords = boundedNumber(view, '#SubtitleAlignerHighWords', analysisDefaults.highWords, 10, 500);
    config.HighConfidenceMinCoveragePercent = boundedNumber(view, '#SubtitleAlignerHighCoverage', analysisDefaults.highCoverage, 5, 100);
    config.HighConfidenceMinMatchScore = boundedNumber(view, '#SubtitleAlignerHighScore', analysisDefaults.highScore, 0.30, 0.99);
    config.MediumConfidenceMinAnchors = boundedNumber(view, '#SubtitleAlignerMediumAnchors', analysisDefaults.mediumAnchors, 3, 12);
    config.MediumConfidenceMinWords = boundedNumber(view, '#SubtitleAlignerMediumWords', analysisDefaults.mediumWords, 10, 500);
    config.MediumConfidenceMinCoveragePercent = boundedNumber(view, '#SubtitleAlignerMediumCoverage', analysisDefaults.mediumCoverage, 5, 100);
    config.MediumConfidenceMinMatchScore = boundedNumber(view, '#SubtitleAlignerMediumScore', analysisDefaults.mediumScore, 0.30, 0.99);
    config.WhisperThreads = boundedNumber(view, '#SubtitleAlignerThreads', 0, 0, 32);
    const result = await ApiClient.updatePluginConfiguration(pluginId, config);
    configuredManagedModelName = config.ManagedModelName;
    setSaveStatus(view, 'Settings saved.');
    return result;
}

async function runNow(view) {
    Dashboard.showLoadingMsg();
    try {
        await save(view);
        await ApiClient.ajax({ type: 'POST', url: ApiClient.getUrl('SubtitleAligner/Run') });
        setSaveStatus(view, 'Settings saved. Library scan queued.');
        await loadStatus(view);
    } catch (error) {
        Dashboard.processErrorResponse(error);
    } finally {
        Dashboard.hideLoadingMsg();
    }
}

async function syncSelectedItem(view, fullAlignment) {
    if (!selectedItemId) return;
    Dashboard.showLoadingMsg();
    try {
        await save(view);
        const retranscribe = view.querySelector('#SubtitleAlignerRetranscribe').checked;
        const endpoint = `SubtitleAligner/${fullAlignment ? 'Align' : 'Sync'}/${encodeURIComponent(selectedItemId)}`;
        await ApiClient.ajax({
            type: 'POST',
            url: ApiClient.getUrl(endpoint, retranscribe ? { retranscribe: true } : undefined)
        });
        view.querySelector('#SubtitleAlignerRetranscribe').checked = false;
        const queued = fullAlignment
            ? 'Individual-cue alignment queued. Current status will update as it runs.'
            : 'Whole-track shift check queued. Current status will update as it runs.';
        view.querySelector('#SubtitleAlignerItemSearchStatus').textContent = retranscribe
            ? `${queued} Cached speech analysis will be replaced.`
            : queued;
        await loadStatus(view);
    } catch (error) {
        Dashboard.processErrorResponse(error);
    } finally {
        Dashboard.hideLoadingMsg();
    }
}

async function clearCache(view) {
    if (!window.confirm('Clear all cached speech analysis? Future checks will need to transcribe the audio again.')) {
        return;
    }

    const button = view.querySelector('#SubtitleAlignerClearCache');
    const status = view.querySelector('#SubtitleAlignerCacheActionStatus');
    button.disabled = true;
    button.querySelector('span').textContent = 'Clearing…';
    status.textContent = '';
    try {
        const response = await ApiClient.ajax({ type: 'DELETE', url: ApiClient.getUrl('SubtitleAligner/Cache') });
        renderCacheStatus(view, await response.json());
        status.textContent = 'Cached speech analysis cleared.';
    } catch (error) {
        status.textContent = describeError(error, 'The cache could not be cleared.');
    } finally {
        button.querySelector('span').textContent = 'Clear cached speech analysis';
    }
}

async function testExternalWhisper(view) {
    const url = view.querySelector('#SubtitleAlignerExternalWhisperUrl').value.trim();
    const button = view.querySelector('#SubtitleAlignerTestWhisper');
    const status = view.querySelector('#SubtitleAlignerWhisperTestStatus');
    if (!url) {
        status.textContent = 'Enter an external server URL first.';
        return;
    }

    button.disabled = true;
    button.querySelector('span').textContent = 'Testing…';
    status.textContent = 'Connecting for up to five seconds…';
    try {
        const response = await ApiClient.ajax({
            type: 'POST',
            url: ApiClient.getUrl('SubtitleAligner/Whisper/Test'),
            contentType: 'application/json',
            data: JSON.stringify({ Url: url })
        });
        const result = await response.json();
        status.textContent = value(result, 'message', 'Message') || 'The connectivity test finished.';
    } catch (error) {
        status.textContent = describeError(error, 'The connectivity test failed. Check the address, port, and firewall.');
    } finally {
        button.querySelector('span').textContent = 'Test external server';
        syncExternalServerFields(view);
    }
}

export default function (view) {
    ensureStyles();
    let timer;
    let pageVisible = false;
    let refreshRunning = false;

    const poll = async () => {
        if (!pageVisible) return;
        if (!refreshRunning) {
            refreshRunning = true;
            try {
                await Promise.all([loadStatus(view), loadAssessment(view)]);
            } finally {
                refreshRunning = false;
            }
        }

        if (pageVisible) timer = setTimeout(poll, 5000);
    };

    view.addEventListener('viewshow', () => {
        pageVisible = true;
        clearTimeout(timer);
        load(view).finally(() => {
            if (pageVisible) timer = setTimeout(poll, 5000);
        });
    });
    view.addEventListener('viewhide', () => {
        pageVisible = false;
        clearTimeout(timer);
    });

    view.querySelector('#SubtitleAlignerConfigForm').addEventListener('submit', async event => {
        event.preventDefault();
        Dashboard.showLoadingMsg();
        try {
            const result = await save(view);
            Dashboard.processPluginConfigurationUpdateResult(result);
        } catch (error) {
            Dashboard.hideLoadingMsg();
            setSaveStatus(view, 'Settings were not saved. Review the error and try again.');
            Dashboard.processErrorResponse(error);
        }
    });
    view.querySelector('#SubtitleAlignerRun').addEventListener('click', () => runNow(view));
    view.querySelector('#SubtitleAlignerItemSearchButton').addEventListener('click', () => searchItems(view));
    view.querySelector('#SubtitleAlignerItemSearch').addEventListener('keydown', event => {
        if (event.key === 'Enter') {
            event.preventDefault();
            searchItems(view);
        }
    });
    view.querySelector('#SubtitleAlignerItemResults').addEventListener('change', event => {
        selectedItemId = event.target.value;
        updateActionStates(view);
    });
    view.querySelector('#SubtitleAlignerSyncItem').addEventListener('click', () => syncSelectedItem(view));
    view.querySelector('#SubtitleAlignerAlignItem').addEventListener('click', () => syncSelectedItem(view, true));
    view.querySelector('#SubtitleAlignerTestWhisper').addEventListener('click', () => testExternalWhisper(view));
    view.querySelector('#SubtitleAlignerClearCache').addEventListener('click', () => clearCache(view));
    view.querySelector('#SubtitleAlignerDownloadModel').addEventListener('click', () => downloadModel(view));
    view.querySelector('#SubtitleAlignerAssessHost').addEventListener('click', () => assessHost(view));
    view.querySelector('#SubtitleAlignerManagedModel').addEventListener('change', () => renderModelChoice(view));
    view.querySelector('#SubtitleAlignerUseWindow').addEventListener('change', () => syncRunWindowFields(view));
    view.querySelector('#SubtitleAlignerEnableFuzzy').addEventListener('change', () => syncFuzzyFields(view));
    view.querySelector('#SubtitleAlignerEnableCache').addEventListener('change', () => syncCacheFields(view));
    view.querySelector('#SubtitleAlignerFuzzyPhonetic').addEventListener('change', () => syncFuzzyFields(view));
    view.querySelector('#SubtitleAlignerResetAnalysis').addEventListener('click', () => {
        setAnalysisFields(view);
        view.querySelector('#SubtitleAlignerResetMessage').textContent = 'Analysis defaults restored. Save to apply them.';
    });
    view.querySelector('#SubtitleAlignerModelPath').addEventListener('input', () => {
        updateActionStates(view);
    });
    view.querySelector('#SubtitleAlignerExternalWhisperUrl').addEventListener('input', () => {
        view.querySelector('#SubtitleAlignerWhisperTestStatus').textContent = '';
        syncExternalServerFields(view);
        updateActionStates(view);
    });
    view.querySelector('#SubtitleAlignerConfigForm').addEventListener('input', event => markSettingsDirty(view, event.target));
    view.querySelector('#SubtitleAlignerConfigForm').addEventListener('change', event => markSettingsDirty(view, event.target));
}
