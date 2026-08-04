(() => {
  'use strict';

  const storageKey = 'voicescreen.translation-evaluation.v1';
  const scoreLabels = { 1: '不可用', 2: '问题明显', 3: '勉强可用', 4: '很好', 5: '可直接使用' };
  const samples = {
    'zh-en': [
      '敌人可能在三楼右边，先别冲。',
      '我去绕后，你们帮我架住楼梯。',
      '还有十秒刷圈，先把药打满。',
      '他只剩一滴血，别让他跑了。',
      '我的英语不太好，请说慢一点。'
    ],
    'en-zh': [
      "One is flanking from the left. Don't push until I get there.",
      'Hold the angle and watch the stairs for me.',
      'The zone closes in ten seconds, heal up first.',
      'He is one shot. Do not let him get away.',
      'Drop the ammo behind the blue container.'
    ],
    'th-zh': [
      'ศัตรูอยู่ชั้นสอง อย่าเพิ่งบุก',
      'รอฉันก่อน แล้วค่อยไปพร้อมกัน',
      'ช่วยดูบันไดให้หน่อย',
      'เขาเหลือเลือดนิดเดียว',
      'ขอกระสุนหน่อย'
    ]
  };

  const $ = (id) => document.getElementById(id);
  const state = { current: null, score: 0, tags: new Set(), dataset: loadDataset(), providers: new Map() };

  function loadDataset() {
    try {
      const parsed = JSON.parse(localStorage.getItem(storageKey) || '[]');
      return Array.isArray(parsed) ? parsed : [];
    } catch {
      return [];
    }
  }

  function persistDataset() {
    localStorage.setItem(storageKey, JSON.stringify(state.dataset));
    renderHistory();
  }

  function setStatus(status, text) {
    const element = $('service-status');
    element.dataset.state = status;
    element.querySelector('span:last-child').textContent = text;
  }

  async function checkHealth() {
    try {
      const response = await fetch('/health', { cache: 'no-store' });
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      const health = await response.json();
      setStatus('ready', health.translation ? '自托管模型已就绪' : '服务已就绪');
    } catch {
      setStatus('error', '模型服务不可用');
    }
  }

  async function loadProviders() {
    try {
      const response = await fetch('/providers', { cache: 'no-store' });
      if (!response.ok) return;
      const payload = await response.json();
      const select = $('provider');
      select.replaceChildren();
      state.providers.clear();
      payload.providers.forEach((provider) => {
        state.providers.set(provider.id, provider);
        const option = document.createElement('option');
        option.value = provider.id;
        option.textContent = `${provider.name}${provider.tts ? ' · 含 TTS' : ''}`;
        select.appendChild(option);
      });
      updateProviderControls();
    } catch {
      // Keep the built-in local provider option when discovery is unavailable.
    }
  }

  function updateProviderControls() {
    const provider = state.providers.get($('provider').value);
    const isOnline = provider?.kind === 'online';
    const ttsEnabled = Boolean(provider?.tts);
    $('include-tts').disabled = !ttsEnabled;
    if (!ttsEnabled) $('include-tts').checked = false;
    $('voice-row').classList.toggle('hidden', !ttsEnabled);
    $('voice').classList.toggle('hidden', !ttsEnabled);
    const voiceSelect = $('voice');
    voiceSelect.replaceChildren();
    const voices = provider?.voices?.[$('direction').value] || [];
    voices.forEach((voice) => {
      const option = document.createElement('option');
      option.value = voice;
      const label = provider.voiceLabels?.[voice] || voice;
      const license = provider.voiceLicenses?.[voice];
      const available = provider.voiceAvailability?.[voice] !== false;
      option.textContent = `${label}${license ? ` · ${license}` : ''}${available ? '' : ' · 未安装'}`;
      option.disabled = !available;
      voiceSelect.appendChild(option);
    });
    const banner = $('privacy-banner');
    banner.querySelector('strong').textContent = isOnline ? '在线服务隐私提示' : '自托管隐私边界';
    $('privacy-text').textContent = isOnline
      ? '原文会发送给 MyMemory，译文会在启用 TTS 时发送给 Microsoft Edge 在线语音；不要输入敏感信息。评分数据仍只保存在当前浏览器。'
      : '原文只发送到 VoiceScreen 自托管 OPUS-MT，不转发第三方；当前 HTTP 88 传输未加密，请勿输入敏感信息。评分数据仅保存在浏览器。';
  }

  async function evaluate(payload) {
    const response = await fetch('/evaluate', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload)
    });
    const body = await response.json().catch(() => ({ error: `HTTP ${response.status}` }));
    if (!response.ok) throw new Error(body.error || `HTTP ${response.status}`);
    return body;
  }

  function requestPayload(useGlossary, includeTts = false) {
    return {
      provider: $('provider').value,
      text: $('source-text').value.trim(),
      direction: $('direction').value,
      useGlossary,
      beamSize: Number($('beam-size').value),
      maxDecodingLength: Number($('max-length').value),
      includeTts,
      voice: includeTts ? $('voice').value : null
    };
  }

  async function runEvaluation() {
    const source = $('source-text').value.trim();
    if (!source) {
      showError('请先输入测试语句。');
      $('source-text').focus();
      return;
    }

    showError('');
    const button = $('run-button');
    button.disabled = true;
    button.querySelector('span:first-child').textContent = '模型推理中…';
    $('latency').textContent = '运行中';
    const compare = $('compare-glossary').checked;

    try {
      const includeTts = $('include-tts').checked;
      let raw = await evaluate(requestPayload(false, false));
      let enhanced = null;
      if (compare && raw.glossaryAvailable) {
        enhanced = await evaluate(requestPayload(true, includeTts));
      } else if (includeTts) {
        raw = await evaluate(requestPayload(false, true));
      }
      state.current = { raw, enhanced };
      state.score = 0;
      state.tags.clear();
      resetAnnotationControls();
      renderResults(raw, enhanced);
      setStatus('ready', '本地模型已就绪');
    } catch (error) {
      showError(error instanceof Error ? error.message : String(error));
      setStatus('error', '本次推理失败');
      $('latency').textContent = '失败';
    } finally {
      button.disabled = false;
      button.querySelector('span:first-child').textContent = '运行翻译评测';
      updateSaveAvailability();
    }
  }

  function renderResults(raw, enhanced) {
    $('empty-state').classList.add('hidden');
    $('result-content').classList.remove('hidden');
    $('raw-output').textContent = raw.translatedText;
    $('raw-model').textContent = raw.model;
    $('raw-latency').textContent = `${raw.latencyMs} ms`;
    renderBridge('raw', raw.bridgeText);
    const displayed = enhanced || raw;
    $('metric-strip').classList.remove('hidden');
    $('metric-translation').textContent = `${displayed.translationLatencyMs ?? displayed.latencyMs} ms`;
    $('metric-tts').textContent = displayed.tts ? `${displayed.tts.latencyMs} ms` : '未启用';
    $('metric-audio').textContent = displayed.tts ? `${displayed.tts.audioDurationMs} ms` : '—';
    $('metric-rtf').textContent = displayed.tts ? String(displayed.tts.realTimeFactor) : '—';
    $('secondary-metrics').classList.remove('hidden');
    $('metric-first-byte').textContent = displayed.tts?.firstByteLatencyMs ? `${displayed.tts.firstByteLatencyMs} ms` : '—';
    $('metric-pipeline').textContent = displayed.totalPipelineLatencyMs ? `${displayed.totalPipelineLatencyMs} ms` : `${displayed.translationLatencyMs ?? displayed.latencyMs} ms`;
    $('metric-throughput').textContent = displayed.qualitySignals?.sourceCharactersPerSecond ? `${displayed.qualitySignals.sourceCharactersPerSecond} 字符/s` : '—';
    $('metric-length-ratio').textContent = displayed.qualitySignals?.outputSourceLengthRatio ?? '—';
    const player = $('tts-player');
    if (displayed.tts?.audioUrl) {
      player.src = displayed.tts.audioUrl;
      player.classList.remove('hidden');
    } else {
      player.pause();
      player.removeAttribute('src');
      player.classList.add('hidden');
    }

    const enhancedCard = $('enhanced-card');
    if (enhanced) {
      enhancedCard.classList.remove('hidden');
      $('enhanced-output').textContent = enhanced.translatedText;
      $('normalized-input').textContent = enhanced.normalizedText;
      $('enhanced-latency').textContent = `${enhanced.latencyMs} ms`;
      renderBridge('enhanced', enhanced.bridgeText);
      $('latency').textContent = `总计 ${(raw.latencyMs + enhanced.latencyMs).toFixed(2)} ms`;
    } else {
      enhancedCard.classList.add('hidden');
      $('latency').textContent = `${raw.latencyMs} ms`;
    }
  }

  function renderBridge(prefix, bridgeText) {
    const row = $(`${prefix}-bridge-row`);
    if (bridgeText) {
      $(`${prefix}-bridge`).textContent = bridgeText;
      row.classList.remove('hidden');
    } else {
      row.classList.add('hidden');
    }
  }

  function showError(message) {
    const element = $('request-error');
    element.textContent = message;
    element.classList.toggle('hidden', !message);
  }

  function resetAnnotationControls() {
    document.querySelectorAll('#rating button, #issue-tags button').forEach((button) => button.classList.remove('active'));
    $('score-label').textContent = '未评分';
    $('expected-text').value = '';
    $('notes').value = '';
    $('save-message').classList.add('hidden');
  }

  function updateSaveAvailability() {
    $('save-sample').disabled = !state.current || state.score === 0;
  }

  function saveSample() {
    if (!state.current || !state.score) return;
    const result = state.current.enhanced || state.current.raw;
    const sample = {
      schemaVersion: 1,
      id: crypto.randomUUID ? crypto.randomUUID() : `${Date.now()}-${Math.random()}`,
      createdAt: new Date().toISOString(),
      direction: result.direction,
      sourceText: result.sourceText,
      rawTranslation: state.current.raw.translatedText,
      enhancedTranslation: state.current.enhanced?.translatedText || null,
      normalizedText: state.current.enhanced?.normalizedText || null,
      bridgeText: result.bridgeText || null,
      expectedText: $('expected-text').value.trim(),
      score: state.score,
      issueTags: [...state.tags],
      notes: $('notes').value.trim(),
      parameters: {
        providerId: result.providerId,
        beamSize: result.beamSize,
        maxDecodingLength: result.maxDecodingLength,
        model: result.model
      },
      latencyMs: {
        raw: state.current.raw.latencyMs,
        enhanced: state.current.enhanced?.latencyMs || null,
        pipeline: result.totalPipelineLatencyMs || result.latencyMs,
        tts: result.tts || null
      },
      qualitySignals: result.qualitySignals || null
    };
    state.dataset.unshift(sample);
    persistDataset();
    const message = $('save-message');
    message.textContent = '已加入本地评测集。评分和修订不会上传。';
    message.classList.remove('hidden');
  }

  function renderHistory() {
    $('dataset-count').textContent = String(state.dataset.length);
    const body = $('history-body');
    body.replaceChildren();
    $('history-empty').classList.toggle('hidden', state.dataset.length > 0);
    state.dataset.slice(0, 50).forEach((sample) => {
      const row = document.createElement('tr');
      [sample.direction, sample.sourceText, sample.enhancedTranslation || sample.rawTranslation, sample.expectedText || '—', `${sample.score}/5`]
        .forEach((value) => {
          const cell = document.createElement('td');
          cell.textContent = value;
          cell.title = value;
          row.appendChild(cell);
        });
      const actionCell = document.createElement('td');
      const remove = document.createElement('button');
      remove.type = 'button';
      remove.textContent = '删除';
      remove.addEventListener('click', () => {
        state.dataset = state.dataset.filter((entry) => entry.id !== sample.id);
        persistDataset();
      });
      actionCell.appendChild(remove);
      row.appendChild(actionCell);
      body.appendChild(row);
    });
  }

  function download(name, content, type) {
    const url = URL.createObjectURL(new Blob([content], { type }));
    const link = document.createElement('a');
    link.href = url;
    link.download = name;
    document.body.appendChild(link);
    link.click();
    link.remove();
    URL.revokeObjectURL(url);
  }

  function exportJsonl() {
    if (!state.dataset.length) return showError('评测集为空，暂无可导出的样本。');
    const content = state.dataset.map((item) => JSON.stringify(item)).join('\n') + '\n';
    download(`voicescreen-evaluation-${dateStamp()}.jsonl`, content, 'application/x-ndjson;charset=utf-8');
  }

  function csvCell(value) {
    const text = Array.isArray(value) ? value.join('|') : String(value ?? '');
    const safeText = /^[=+\-@]/.test(text.trimStart()) ? `'${text}` : text;
    return `"${safeText.replaceAll('"', '""')}"`;
  }

  function exportCsv() {
    if (!state.dataset.length) return showError('评测集为空，暂无可导出的样本。');
    const fields = ['createdAt', 'direction', 'sourceText', 'rawTranslation', 'enhancedTranslation', 'expectedText', 'score', 'issueTags', 'notes'];
    const rows = [fields.join(','), ...state.dataset.map((item) => fields.map((field) => csvCell(item[field])).join(','))];
    download(`voicescreen-evaluation-${dateStamp()}.csv`, '\ufeff' + rows.join('\r\n'), 'text/csv;charset=utf-8');
  }

  function dateStamp() {
    return new Date().toISOString().slice(0, 10);
  }

  $('run-button').addEventListener('click', runEvaluation);
  $('source-text').addEventListener('input', () => {
    $('source-count').textContent = `${$('source-text').value.length} / 1000`;
  });
  $('source-text').addEventListener('keydown', (event) => {
    if ((event.ctrlKey || event.metaKey) && event.key === 'Enter') runEvaluation();
  });
  $('sample-button').addEventListener('click', () => {
    const list = samples[$('direction').value];
    $('source-text').value = list[Math.floor(Math.random() * list.length)];
    $('source-text').dispatchEvent(new Event('input'));
  });
  $('direction').addEventListener('change', () => {
    $('source-text').value = '';
    $('source-text').dispatchEvent(new Event('input'));
    updateProviderControls();
  });
  $('provider').addEventListener('change', updateProviderControls);
  document.querySelectorAll('[data-copy]').forEach((button) => button.addEventListener('click', async () => {
    const value = $(button.dataset.copy).textContent;
    await navigator.clipboard.writeText(value);
    button.textContent = '已复制';
    setTimeout(() => { button.textContent = '复制'; }, 900);
  }));
  document.querySelectorAll('#rating button').forEach((button) => button.addEventListener('click', () => {
    state.score = Number(button.dataset.score);
    document.querySelectorAll('#rating button').forEach((item) => item.classList.toggle('active', item === button));
    $('score-label').textContent = scoreLabels[state.score];
    updateSaveAvailability();
  }));
  document.querySelectorAll('#issue-tags button').forEach((button) => button.addEventListener('click', () => {
    const tag = button.dataset.tag;
    if (state.tags.has(tag)) state.tags.delete(tag); else state.tags.add(tag);
    button.classList.toggle('active', state.tags.has(tag));
  }));
  $('save-sample').addEventListener('click', saveSample);
  $('export-jsonl').addEventListener('click', exportJsonl);
  $('export-csv').addEventListener('click', exportCsv);
  $('clear-dataset').addEventListener('click', () => {
    if (!state.dataset.length || !window.confirm('确定清空当前浏览器中的全部 VoiceScreen 评测样本吗？')) return;
    state.dataset = [];
    persistDataset();
  });

  renderHistory();
  checkHealth();
  loadProviders();
})();
