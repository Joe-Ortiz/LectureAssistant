/*
 * Lecture Assistant interactive video player (SCORM 1.2 SCO).
 * Plays the lecture's YouTube video, pauses for each question in window.LECTURE_DATA,
 * and reports score / status to the LMS. No dependencies besides the YouTube IFrame API.
 * Question text is always inserted with textContent, never parsed as HTML.
 */
(function () {
  'use strict';

  var POLL_MS = 250;
  var SKIP_TOLERANCE_SECONDS = 1;
  var LOCATION_SAVE_STEP_SECONDS = 5;
  var YOUTUBE_LOAD_TIMEOUT_MS = 20000;
  var SUSPEND_VERSION = 1;

  var data = window.LECTURE_DATA;

  var dom = {
    main: document.getElementById('main'),
    title: document.getElementById('lecture-title'),
    status: document.getElementById('status'),
    notice: document.getElementById('notice'),
    videoMessage: document.getElementById('video-message'),
    progress: document.getElementById('progress'),
    markers: document.getElementById('markers'),
    fullscreen: document.getElementById('fullscreen'),
    app: document.getElementById('app'),
    overlay: document.getElementById('overlay'),
    dialog: document.getElementById('dialog'),
    announcer: document.getElementById('announcer')
  };

  // ---------------------------------------------------------------- helpers

  /** Builds an element. props: text, className, on<event> handlers, or attributes (true = present). */
  function h(tag, props, children) {
    var node = document.createElement(tag);
    if (props) {
      Object.keys(props).forEach(function (key) {
        var value = props[key];
        if (value === null || value === undefined || value === false) return;
        if (key === 'text') node.textContent = String(value);
        else if (key === 'className') node.className = value;
        else if (key.indexOf('on') === 0) node.addEventListener(key.slice(2), value);
        else node.setAttribute(key, value === true ? '' : String(value));
      });
    }
    (children || []).forEach(function (child) {
      if (child === null || child === undefined || child === false) return;
      node.appendChild(typeof child === 'string' ? document.createTextNode(child) : child);
    });
    return node;
  }

  function clear(node) {
    while (node.firstChild) node.removeChild(node.firstChild);
  }

  function pad(n, width) {
    var s = String(n);
    while (s.length < width) s = '0' + s;
    return s;
  }

  /** "m:ss" or "h:mm:ss". */
  function clock(seconds) {
    var total = Math.max(0, Math.floor(seconds || 0));
    var hours = Math.floor(total / 3600);
    var minutes = Math.floor((total % 3600) / 60);
    var secs = total % 60;
    return hours > 0 ? hours + ':' + pad(minutes, 2) + ':' + pad(secs, 2) : minutes + ':' + pad(secs, 2);
  }

  /** Trimmed, whitespace-collapsed, case-insensitive form used to compare typed answers. */
  function normalize(text) {
    var s = String(text === null || text === undefined ? '' : text);
    if (s.normalize) s = s.normalize('NFC');
    return s.replace(/\s+/g, ' ').trim().toLowerCase();
  }

  function hash(text) {
    var value = 5381;
    for (var i = 0; i < text.length; i++) value = ((value * 33) ^ text.charCodeAt(i)) >>> 0;
    return value.toString(36);
  }

  function announce(message) {
    dom.announcer.textContent = '';
    window.setTimeout(function () { dom.announcer.textContent = message; }, 50);
  }

  function plural(n, word) {
    return n + ' ' + word + (n === 1 ? '' : 's');
  }

  function showVideoMessage(message) {
    dom.videoMessage.textContent = message;
    dom.videoMessage.hidden = false;
  }

  if (!data || !data.questions || !(data.videoId || data.videoUrl)) {
    showVideoMessage('This lecture package is incomplete: lecture-data.js is missing or damaged.');
    return;
  }

  var settings = data.settings || {};
  var questions = data.questions;
  var totalPoints = questions.reduce(function (sum, q) { return sum + (q.points > 0 ? q.points : 1); }, 0);
  var questionSetHash = hash(questions.map(function (q) { return q.id; }).join('|'));

  // ---------------------------------------------------------------- SCORM 1.2 runtime

  var Scorm = (function () {
    var api = null;
    var active = false;
    var finished = false;
    var canRecord = true;
    var startedAt = Date.now();

    function isTrue(value) {
      return value === true || value === 'true';
    }

    /** Walks up to 10 parents of win looking for the SCORM 1.2 API object. */
    function findIn(win) {
      for (var depth = 0; win && depth <= 10; depth++) {
        try {
          if (win.API && typeof win.API.LMSInitialize === 'function') return win.API;
        } catch (e) { /* cross-origin frame: keep climbing */ }
        var parent = null;
        try { parent = win.parent; } catch (e) { parent = null; }
        if (!parent || parent === win) break;
        win = parent;
      }
      return null;
    }

    function logError(call) {
      try {
        var code = api.LMSGetLastError();
        if (code !== null && code !== undefined && String(code) !== '0' && String(code) !== '') {
          var text = '';
          try { text = api.LMSGetErrorString(code); } catch (e) { /* optional */ }
          if (window.console) console.warn('SCORM ' + call + ' failed: ' + code + ' ' + text);
        }
      } catch (e) { /* LMS without error reporting */ }
    }

    function init() {
      api = findIn(window);
      if (!api) {
        try { if (window.opener) api = findIn(window.opener); } catch (e) { api = null; }
      }
      if (!api) return false;

      var result = false;
      try { result = api.LMSInitialize(''); } catch (e) { result = false; }
      if (!isTrue(result)) {
        logError('LMSInitialize');
        // 101 = already initialized (e.g. a reload inside the same LMS session): the API is still usable.
        var code = '';
        try { code = String(api.LMSGetLastError()); } catch (e) { code = ''; }
        if (code !== '101') {
          api = null;
          return false;
        }
      }
      active = true;
      var mode = get('cmi.core.lesson_mode');
      canRecord = mode !== 'review' && mode !== 'browse';
      return true;
    }

    function get(name) {
      if (!active) return '';
      try {
        var value = api.LMSGetValue(name);
        if (value === null || value === undefined) {
          logError('LMSGetValue(' + name + ')');
          return '';
        }
        if (value === '') logError('LMSGetValue(' + name + ')');
        return String(value);
      } catch (e) {
        return '';
      }
    }

    function set(name, value) {
      if (!active || !canRecord || finished) return false;
      try {
        var ok = isTrue(api.LMSSetValue(name, String(value)));
        if (!ok) logError('LMSSetValue(' + name + ')');
        return ok;
      } catch (e) {
        return false;
      }
    }

    function commit() {
      if (!active || finished) return;
      try {
        if (!isTrue(api.LMSCommit(''))) logError('LMSCommit');
      } catch (e) { /* ignore */ }
    }

    /** SCORM 1.2 CMITimespan, HHHH:MM:SS.SS. */
    function sessionTime() {
      var centis = Math.floor((Date.now() - startedAt) / 10);
      var hours = Math.min(9999, Math.floor(centis / 360000));
      var minutes = Math.floor((centis % 360000) / 6000);
      var seconds = Math.floor((centis % 6000) / 100);
      return pad(hours, 4) + ':' + pad(minutes, 2) + ':' + pad(seconds, 2) + '.' + pad(centis % 100, 2);
    }

    function finish(complete) {
      if (!active || finished) return;
      set('cmi.core.exit', complete ? '' : 'suspend');
      set('cmi.core.session_time', sessionTime());
      commit();
      finished = true;
      try {
        if (!isTrue(api.LMSFinish(''))) logError('LMSFinish');
      } catch (e) { /* ignore */ }
    }

    return {
      init: init,
      get: get,
      set: set,
      commit: commit,
      finish: finish,
      isActive: function () { return active; },
      canRecord: function () { return active && canRecord; }
    };
  })();

  // ---------------------------------------------------------------- quiz state

  /** results[i]: null (unanswered) or { correct: bool, response: any|undefined (only known this session) }. */
  var results = questions.map(function () { return null; });

  function pointsOf(q) {
    return q.points > 0 ? q.points : 1;
  }

  function earnedPoints() {
    return questions.reduce(function (sum, q, i) {
      return sum + (results[i] && results[i].correct ? pointsOf(q) : 0);
    }, 0);
  }

  function scorePercent() {
    return totalPoints > 0 ? Math.round(earnedPoints() / totalPoints * 100) : 0;
  }

  function answeredCount() {
    return results.filter(function (r) { return r !== null; }).length;
  }

  function allAnswered() {
    return answeredCount() === questions.length;
  }

  function firstUnanswered() {
    for (var i = 0; i < questions.length; i++) if (!results[i]) return i;
    return -1;
  }

  function passed() {
    return scorePercent() >= (Number(settings.passingScorePercent) || 0);
  }

  /** Compact per-question first-attempt results: one character per question ('1' right, '0' wrong, '-' open). */
  function encodeSuspendData() {
    var r = results.map(function (x) { return x ? (x.correct ? '1' : '0') : '-'; }).join('');
    return JSON.stringify({ v: SUSPEND_VERSION, h: questionSetHash, r: r });
  }

  function restoreSuspendData(text) {
    if (!text) return;
    try {
      var saved = JSON.parse(text);
      if (!saved || saved.v !== SUSPEND_VERSION || saved.h !== questionSetHash) return;
      if (typeof saved.r !== 'string' || saved.r.length !== questions.length) return;
      for (var i = 0; i < questions.length; i++) {
        var c = saved.r.charAt(i);
        if (c === '1' || c === '0') results[i] = { correct: c === '1' };
      }
    } catch (e) { /* ignore unreadable data and start fresh */ }
  }

  /** Sends score, status and progress to the LMS and commits. */
  function reportProgress() {
    if (!Scorm.canRecord()) return;
    Scorm.set('cmi.core.score.min', '0');
    Scorm.set('cmi.core.score.max', '100');
    Scorm.set('cmi.core.score.raw', String(scorePercent()));
    Scorm.set('cmi.core.lesson_status', allAnswered() ? (passed() ? 'passed' : 'failed') : 'incomplete');
    Scorm.set('cmi.suspend_data', encodeSuspendData());
    saveLocation(currentTime(), true);
    Scorm.commit();
  }

  var lastSavedLocation = -1;
  function saveLocation(seconds, force) {
    if (!Scorm.canRecord()) return;
    var whole = Math.floor(seconds || 0);
    if (!force && Math.abs(whole - lastSavedLocation) < LOCATION_SAVE_STEP_SECONDS) return;
    lastSavedLocation = whole;
    Scorm.set('cmi.core.lesson_location', String(whole));
  }

  // ---------------------------------------------------------------- grading

  function correctCount(q) {
    return (q.options || []).filter(function (o) { return o.correct; }).length;
  }

  function isMultiSelect(q) {
    return q.type === 'MultipleChoice' && correctCount(q) > 1;
  }

  /** Options shown for choice questions: { text, value, correct }. */
  function choicesOf(q) {
    if (q.type === 'TrueFalse') {
      return [
        { text: 'True', value: 'true', correct: q.correctAnswer === true },
        { text: 'False', value: 'false', correct: q.correctAnswer !== true }
      ];
    }
    return (q.options || []).map(function (o, i) {
      return { text: o.text, value: String(i), correct: !!o.correct, feedback: o.feedback };
    });
  }

  /** MultipleChoice/TrueFalse: array of chosen values. FillInTheBlank: the typed text. */
  function grade(q, response) {
    if (q.type === 'FillInTheBlank') {
      var given = normalize(response);
      return given !== '' && (q.acceptedAnswers || []).some(function (a) { return normalize(a) === given; });
    }
    // All-or-nothing: exactly the correct choices must be selected.
    return choicesOf(q).every(function (c) { return c.correct === (response.indexOf(c.value) >= 0); });
  }

  function correctAnswerText(q) {
    if (q.type === 'FillInTheBlank') {
      var answers = (q.acceptedAnswers || []).filter(function (a) { return normalize(a) !== ''; });
      return (answers.length > 1 ? 'Accepted answers: ' : 'Correct answer: ') + answers.join(' / ');
    }
    var correct = choicesOf(q).filter(function (c) { return c.correct; }).map(function (c) { return c.text; });
    return (correct.length > 1 ? 'Correct answers: ' : 'Correct answer: ') + correct.join('; ');
  }

  /** Splits a fill-in prompt at its blank (a run of 3+ underscores). */
  function splitBlank(prompt) {
    var match = /_{3,}/.exec(prompt);
    if (!match) return [prompt + ' ', ''];
    return [prompt.slice(0, match.index), prompt.slice(match.index + match[0].length)];
  }

  // ---------------------------------------------------------------- dialog

  var dialogState = null; // { closable: bool, onClose: fn, returnFocus: Element }

  function openDialog(title, body, options) {
    options = options || {};
    var returnFocus = dialogState ? dialogState.returnFocus : document.activeElement;
    dialogState = { closable: !!options.closable, onClose: options.onClose, returnFocus: returnFocus };

    clear(dom.dialog);
    var heading = h('h2', { id: 'dialog-title', tabindex: '-1', text: title });
    dom.dialog.appendChild(heading);
    body.forEach(function (node) { if (node) dom.dialog.appendChild(node); });

    dom.overlay.hidden = false;
    dom.main.setAttribute('inert', '');
    dom.main.setAttribute('aria-hidden', 'true');
    dom.dialog.scrollTop = 0;
    (options.focus || heading).focus();
  }

  /** Closes the dialog; restoreFocus=false when another dialog opens immediately. */
  function closeDialog(restoreFocus) {
    if (!dialogState) return;
    var target = dialogState.returnFocus;
    dom.overlay.hidden = true;
    clear(dom.dialog);
    dom.main.removeAttribute('inert');
    dom.main.removeAttribute('aria-hidden');
    if (restoreFocus !== false) {
      dialogState = null;
      if (target && target.focus && document.documentElement.contains(target)) {
        try { target.focus(); } catch (e) { /* ignore */ }
      }
    }
  }

  function isDialogOpen() {
    return !dom.overlay.hidden;
  }

  dom.overlay.addEventListener('keydown', function (event) {
    if (event.key === 'Escape' && dialogState && dialogState.closable) {
      event.preventDefault();
      var onClose = dialogState.onClose;
      closeDialog();
      if (onClose) onClose();
      return;
    }
    if (event.key !== 'Tab') return;
    // Keep focus inside the dialog.
    var focusable = [].slice.call(dom.dialog.querySelectorAll(
      'button:not([disabled]), input:not([disabled]), [href], [tabindex]:not([tabindex="-1"])'));
    if (!focusable.length) {
      event.preventDefault();
      return;
    }
    var first = focusable[0];
    var last = focusable[focusable.length - 1];
    if (event.shiftKey && (document.activeElement === first || !dom.dialog.contains(document.activeElement))) {
      event.preventDefault();
      last.focus();
    } else if (!event.shiftKey && document.activeElement === last) {
      event.preventDefault();
      first.focus();
    }
  });

  // ---------------------------------------------------------------- question UI

  var queue = [];          // question indexes waiting to be shown
  var activeQuestion = -1; // index of the question on screen (ask mode)
  var summaryShown = false;

  /** Builds the answer form. Returns { form, setLocked, showMarks, clearMarks, read }. */
  function buildAnswerForm(q, index, lang) {
    var name = 'q' + index;
    var form = h('form', { className: 'question-form', novalidate: true });

    if (q.type === 'FillInTheBlank') {
      var parts = splitBlank(q.prompt);
      var input = h('input', {
        type: 'text', id: name + '-blank', className: 'blank', autocomplete: 'off',
        autocapitalize: 'off', spellcheck: 'false', 'aria-label': 'Fill in the blank'
      });
      form.appendChild(h('p', { className: 'prompt', lang: lang }, [parts[0], input, parts[1]]));
      return {
        form: form,
        first: input,
        read: function () { return normalize(input.value) ? input.value : null; },
        restore: function (response) { if (typeof response === 'string') input.value = response; },
        setLocked: function (locked) { input.disabled = locked; },
        showMarks: function (correct) { input.className = 'blank ' + (correct ? 'is-correct' : 'is-wrong'); },
        clearMarks: function () { input.className = 'blank'; input.value = ''; }
      };
    }

    var multi = isMultiSelect(q);
    var choices = choicesOf(q);
    var fieldset = h('fieldset', { className: 'choices' }, [h('legend', { className: 'prompt', lang: lang, text: q.prompt })]);
    if (multi) fieldset.appendChild(h('p', { className: 'hint', text: 'Select all that apply.' }));
    var list = h('div', { className: 'options' + (q.type === 'TrueFalse' ? ' options-tf' : '') });
    var rows = choices.map(function (choice, k) {
      var id = name + '-' + k;
      var inputEl = h('input', { type: multi ? 'checkbox' : 'radio', name: name, id: id, value: choice.value });
      var mark = h('span', { className: 'mark' });
      var row = h('div', { className: 'option' }, [inputEl, h('label', { for: id, lang: q.type === 'TrueFalse' ? null : lang, text: choice.text }), mark]);
      list.appendChild(row);
      return { choice: choice, input: inputEl, row: row, mark: mark };
    });
    fieldset.appendChild(list);
    form.appendChild(fieldset);

    return {
      form: form,
      first: rows[0].input,
      read: function () {
        var chosen = rows.filter(function (r) { return r.input.checked; }).map(function (r) { return r.choice.value; });
        return chosen.length ? chosen : null;
      },
      restore: function (response) {
        if (!response || !response.indexOf) return;
        rows.forEach(function (r) { r.input.checked = response.indexOf(r.choice.value) >= 0; });
      },
      setLocked: function (locked) { rows.forEach(function (r) { r.input.disabled = locked; }); },
      showMarks: function (answerCorrect, revealCorrect) {
        rows.forEach(function (r) {
          var chosen = r.input.checked;
          if (chosen && r.choice.correct) {
            r.row.className = 'option is-correct';
            r.mark.textContent = '✓ Your answer, correct';
          } else if (chosen) {
            r.row.className = 'option is-wrong';
            r.mark.textContent = '✗ Your answer, incorrect';
          } else if (r.choice.correct && (answerCorrect || revealCorrect)) {
            r.row.className = 'option is-correct';
            r.mark.textContent = '✓ Correct answer';
          }
        });
      },
      clearMarks: function () {
        rows.forEach(function (r) {
          r.row.className = 'option';
          r.mark.textContent = '';
          r.input.checked = false;
        });
      },
      chosenFeedback: function () {
        return rows.filter(function (r) { return r.input.checked && r.choice.feedback; })
          .map(function (r) { return { text: r.choice.text, feedback: r.choice.feedback }; });
      }
    };
  }

  /** Fills the feedback box. */
  function renderFeedback(box, q, correct, context) {
    clear(box);
    box.className = 'feedback ' + (correct ? 'is-correct' : 'is-wrong');
    var verdict = correct ? 'Correct!' : 'Not quite.';
    if (context.scoredNow === false && !context.review) verdict += ' (Only your first attempt counts toward your score.)';
    if (context.review) verdict = correct ? 'You answered this correctly.' : 'Your first answer was incorrect.';
    box.appendChild(h('p', { className: 'verdict', text: verdict }));

    (context.chosenFeedback || []).forEach(function (f) {
      box.appendChild(h('p', { lang: context.lang, text: f.feedback }));
    });

    var reveal = correct || settings.showCorrectAnswers;
    if (!correct && reveal) box.appendChild(h('p', { lang: context.lang, text: correctAnswerText(q) }));
    if (reveal && q.explanation) box.appendChild(h('p', { lang: context.lang, text: q.explanation }));
    if (!correct && context.canRetry) {
      box.appendChild(h('p', { text: 'You can try again, but only your first answer counts toward your score.' }));
    }
  }

  function questionMeta(q) {
    var p = pointsOf(q);
    return 'At ' + clock(q.time) + ' · ' + plural(p, 'point');
  }

  function askQuestion(index) {
    var q = questions[index];
    activeQuestion = index;
    pauseVideo();
    leaveForeignFullscreen();

    var lang = data.language || null;
    var ui = buildAnswerForm(q, index, lang);
    var feedback = h('div', { className: 'feedback', 'aria-live': 'polite' });
    var actions = h('div', { className: 'actions' });
    var submit = h('button', { type: 'submit', className: 'button', text: 'Submit answer' });
    actions.appendChild(submit);
    ui.form.appendChild(feedback);
    ui.form.appendChild(actions);

    function showButtons(buttons) {
      clear(actions);
      buttons.forEach(function (b) { actions.appendChild(b); });
    }

    var continueButton = h('button', { type: 'button', className: 'button', text: 'Continue video', onclick: finishQuestion });

    ui.form.addEventListener('submit', function (event) {
      event.preventDefault();
      var response = ui.read();
      if (response === null) {
        clear(feedback);
        feedback.className = 'feedback';
        feedback.appendChild(h('p', { text: q.type === 'FillInTheBlank' ? 'Type an answer first.' : 'Choose an answer first.' }));
        return;
      }

      var correct = grade(q, response);
      var firstAttempt = results[index] === null;
      if (firstAttempt) {
        results[index] = { correct: correct, response: response };
        reportProgress();
        updateStatus();
        renderMarkers();
      }

      var canRetry = !correct && !!settings.allowRetry;
      ui.setLocked(true);
      ui.showMarks(correct, !!settings.showCorrectAnswers);
      renderFeedback(feedback, q, correct, {
        scoredNow: firstAttempt,
        canRetry: canRetry,
        lang: lang,
        chosenFeedback: ui.chosenFeedback ? ui.chosenFeedback() : []
      });

      var buttons = [];
      if (canRetry) {
        buttons.push(h('button', {
          type: 'button', className: 'button button-quiet', text: 'Try again',
          onclick: function () {
            ui.clearMarks();
            ui.setLocked(false);
            clear(feedback);
            feedback.className = 'feedback';
            showButtons([submit]);
            ui.first.focus();
          }
        }));
      }
      buttons.push(continueButton);
      showButtons(buttons);
      continueButton.focus();
    });

    openDialog('Question ' + (index + 1) + ' of ' + questions.length, [
      h('p', { className: 'meta', text: questionMeta(q) }),
      ui.form
    ], { closable: false });
  }

  /** After Continue: next queued question, the summary, or back to the video. */
  function finishQuestion() {
    activeQuestion = -1;
    while (queue.length && results[queue[0]]) queue.shift();
    if (queue.length) {
      closeDialog(false);
      askQuestion(queue.shift());
      return;
    }
    if (allAnswered() && !summaryShown) {
      closeDialog(false);
      showSummary();
      return;
    }
    closeDialog();
    if (videoEnded) {
      if (!allAnswered()) showSummary();
      return;
    }
    playVideo();
  }

  function reviewQuestion(index) {
    var q = questions[index];
    var result = results[index];
    var wasPlaying = isPlaying();
    pauseVideo();

    var lang = data.language || null;
    var ui = buildAnswerForm(q, index, lang);
    if (result.response !== undefined) ui.restore(result.response);
    ui.setLocked(true);
    if (result.response !== undefined) ui.showMarks(result.correct, !!settings.showCorrectAnswers);
    var feedback = h('div', { className: 'feedback' });
    renderFeedback(feedback, q, result.correct, { review: true, lang: lang });
    ui.form.appendChild(feedback);

    function done() {
      if (wasPlaying) playVideo();
    }
    var close = h('button', { type: 'button', className: 'button', text: 'Close', onclick: function () { closeDialog(); done(); } });
    ui.form.appendChild(h('div', { className: 'actions' }, [close]));

    openDialog('Review: question ' + (index + 1) + ' of ' + questions.length, [
      h('p', { className: 'meta', text: questionMeta(q) }),
      ui.form
    ], { closable: true, onClose: done });
  }

  function showSummary() {
    summaryShown = true;
    pauseVideo();
    leaveForeignFullscreen();

    var answered = answeredCount();
    var body = [
      h('p', { className: 'summary-score', text: scorePercent() + '%' }),
      h('p', { text: 'You earned ' + earnedPoints() + ' of ' + plural(totalPoints, 'point') + ' and answered ' +
        answered + ' of ' + plural(questions.length, 'question') + '.' })
    ];

    if (allAnswered()) {
      var passing = Number(settings.passingScorePercent) || 0;
      body.push(h('p', { text: passed()
        ? 'You reached the passing score of ' + passing + '%.'
        : 'The passing score is ' + passing + '%.' }));
    } else {
      body.push(h('p', { text: 'Answer the remaining ' + plural(questions.length - answered, 'question') + ' to finish the lecture.' }));
    }

    if (!Scorm.isActive()) {
      body.push(h('p', { className: 'meta', text: 'This lecture was opened outside a learning management system, so your score was not recorded.' }));
    } else if (!Scorm.canRecord()) {
      body.push(h('p', { className: 'meta', text: 'Review mode: your answers were not recorded.' }));
    }

    var buttons = [];
    var next = firstUnanswered();
    if (next >= 0) {
      buttons.push(h('button', {
        type: 'button', className: 'button', text: 'Go to next unanswered question',
        onclick: function () { closeDialog(); goToQuestion(next); }
      }));
    }
    buttons.push(h('button', {
      type: 'button', className: next >= 0 ? 'button button-quiet' : 'button', text: videoEnded ? 'Close' : 'Keep watching',
      onclick: function () { closeDialog(); if (!videoEnded) playVideo(); }
    }));
    body.push(h('div', { className: 'actions' }, buttons));

    openDialog(allAnswered() ? 'Lecture complete' : 'Your progress', body, {
      closable: true,
      onClose: function () { if (!videoEnded) playVideo(); }
    });
  }

  // ---------------------------------------------------------------- status + timeline

  function updateStatus() {
    dom.status.textContent = 'Score: ' + earnedPoints() + ' / ' + plural(totalPoints, 'point') +
      ' (' + scorePercent() + '%) · ' + answeredCount() + ' of ' + questions.length + ' questions answered';
  }

  function timelineDuration() {
    if (duration > 0) return duration;
    var last = questions.length ? questions[questions.length - 1].time : 0;
    return Math.max(60, last * 1.05 + 10);
  }

  function renderMarkers() {
    var focusedIndex = document.activeElement && document.activeElement.getAttribute
      ? document.activeElement.getAttribute('data-index') : null;
    clear(dom.markers);
    var span = timelineDuration();
    questions.forEach(function (q, i) {
      var r = results[i];
      var state = r ? (r.correct ? 'answered correctly' : 'answered incorrectly') : 'not answered yet';
      var left = Math.min(100, Math.max(0, q.time / span * 100));
      var marker = h('button', {
        type: 'button',
        className: 'marker' + (r ? (r.correct ? ' is-correct' : ' is-wrong') : ''),
        'data-index': String(i),
        style: 'left:' + left.toFixed(3) + '%',
        'aria-label': 'Question ' + (i + 1) + ' at ' + clock(q.time) + ', ' + state + (r ? '. Review' : ''),
        title: 'Question ' + (i + 1) + ' (' + clock(q.time) + ')',
        onclick: function () { onMarkerClick(i); }
      }, [h('span', { 'aria-hidden': 'true', text: r ? (r.correct ? '✓' : '✗') : String(i + 1) })]);
      dom.markers.appendChild(marker);
    });
    if (focusedIndex !== null) {
      var again = dom.markers.querySelector('[data-index="' + focusedIndex + '"]');
      if (again) again.focus();
    }
  }

  function onMarkerClick(index) {
    if (results[index]) {
      reviewQuestion(index);
      return;
    }
    if (settings.preventSkippingAhead) {
      var first = firstUnanswered();
      if (first >= 0 && questions[index].time > questions[first].time + SKIP_TOLERANCE_SECONDS) {
        announce('Answer question ' + (first + 1) + ' first.');
        return;
      }
    }
    goToQuestion(index);
  }

  /** Seeks a little before a question and plays up to it. */
  function goToQuestion(index) {
    if (!ready) return;
    var target = Math.max(0, questions[index].time - 2);
    videoEnded = false;
    player.seekTo(target, true);
    lastTime = target;
    playVideo();
  }

  // ---------------------------------------------------------------- video

  var player = null;
  var ready = false;
  var duration = 0;
  var lastTime = -1;
  var resumeAt = 0;
  var videoEnded = false;

  function currentTime() {
    if (!ready) return Math.max(0, lastTime);
    try { return player.getCurrentTime() || 0; } catch (e) { return 0; }
  }

  function isPlaying() {
    try { return ready && player.getPlayerState() === 1; } catch (e) { return false; }
  }

  function pauseVideo() {
    try { if (ready) player.pauseVideo(); } catch (e) { /* ignore */ }
  }

  function playVideo() {
    try { if (ready) player.playVideo(); } catch (e) { /* ignore */ }
  }

  /** Questions can't show over YouTube's own full-screen iframe, so leave it when one appears. */
  function leaveForeignFullscreen() {
    var element = document.fullscreenElement || document.webkitFullscreenElement;
    if (!element || element.contains(dom.overlay)) return;
    var exit = document.exitFullscreen || document.webkitExitFullscreen;
    if (exit) {
      try {
        var result = exit.call(document);
        if (result && result.catch) result.catch(function () { /* ignore */ });
      } catch (e) { /* ignore */ }
    }
  }

  function tick() {
    if (!ready) return;
    var t = currentTime();

    var d = 0;
    try { d = player.getDuration() || 0; } catch (e) { d = 0; }
    if (d > 0 && Math.abs(d - duration) > 0.5) {
      duration = d;
      renderMarkers();
    }
    dom.progress.style.width = Math.min(100, t / timelineDuration() * 100).toFixed(2) + '%';

    if (isDialogOpen()) return;

    if (settings.preventSkippingAhead) {
      var first = firstUnanswered();
      if (first >= 0 && t > questions[first].time + SKIP_TOLERANCE_SECONDS) {
        var at = questions[first].time;
        player.seekTo(at, true);
        lastTime = at;
        announce('You need to answer this question before moving ahead.');
        askQuestion(first);
        return;
      }
    }

    // Questions reached by normal playback since the last poll (a big seek only triggers ones right at the new position).
    var from = Math.max(lastTime, t - 3);
    var due = [];
    for (var i = 0; i < questions.length; i++) {
      var qt = questions[i].time;
      if (!results[i] && qt > from && qt <= t + 0.1) due.push(i);
    }
    if (t > lastTime + 0.01 || t < lastTime - 0.01) saveLocation(t, false);
    lastTime = t;

    if (due.length) {
      queue = due.slice(1);
      askQuestion(due[0]);
    }
  }

  function onEnded() {
    videoEnded = true;
    if (isDialogOpen()) return;
    var end = duration || currentTime();
    var remaining = [];
    for (var i = 0; i < questions.length; i++) {
      if (!results[i] && (settings.preventSkippingAhead || questions[i].time >= end - 2)) remaining.push(i);
    }
    if (remaining.length) {
      queue = remaining.slice(1);
      askQuestion(remaining[0]);
    } else {
      showSummary();
    }
  }

  function onStateChange(event) {
    if (event.data === 1) {
      videoEnded = false;
      if (activeQuestion >= 0) pauseVideo();
    } else if (event.data === 0) {
      onEnded();
    }
  }

  function onError(event) {
    var code = event && event.data;
    var message = 'The video could not be played (YouTube error ' + code + ').';
    if (code === 100) message = 'This video is unavailable. It may have been removed or made private; please tell your instructor.';
    else if (code === 101 || code === 150) message = 'The video owner does not allow this video to be embedded; please tell your instructor.';
    else if (code === 152 || code === 153) message = 'YouTube refused to play the video here. If you opened this page from a file, open it through your course site instead.';
    else if (code === 2) message = 'The video link in this lecture is not valid; please tell your instructor.';
    else if (code === 5) message = 'Your browser could not play this video. Try another browser.';
    showVideoMessage(message);
  }

  function onReady() {
    ready = true;
    try {
      var iframe = player.getIframe();
      iframe.setAttribute('title', 'Lecture video: ' + (data.title || 'video'));
    } catch (e) { /* ignore */ }
    try { duration = player.getDuration() || 0; } catch (e) { duration = 0; }
    renderMarkers();
    if (resumeAt > 0) {
      lastTime = resumeAt;
      announce('Resuming at ' + clock(resumeAt) + '.');
    }
    window.setInterval(tick, POLL_MS);
  }

  /**
   * Plays a video file with a <video> element behind the same small interface as the YouTube player.
   * Only used by the desktop app's student preview (data.videoUrl), so instructors can check a lecture
   * before uploading it; exported packages always use YouTube.
   */
  function createFilePlayer() {
    var video = document.createElement('video');
    video.src = data.videoUrl;
    video.controls = true;
    video.playsInline = true;
    video.preload = 'metadata';
    if (data.captionsUrl) {
      var track = document.createElement('track');
      track.kind = 'captions';
      track.label = 'Captions';
      track.srclang = data.language || 'en';
      track.src = data.captionsUrl;
      track.default = true;
      video.appendChild(track);
    }
    document.getElementById('player').appendChild(video);

    player = {
      getCurrentTime: function () { return video.currentTime; },
      getDuration: function () { return isFinite(video.duration) ? video.duration : 0; },
      getPlayerState: function () { return video.ended ? 0 : (video.paused ? 2 : 1); },
      playVideo: function () {
        var result = video.play();
        if (result && result.catch) result.catch(function () { /* autoplay refused: the student presses play */ });
      },
      pauseVideo: function () { video.pause(); },
      seekTo: function (seconds) { video.currentTime = seconds; },
      getIframe: function () { return video; }
    };

    var readied = false;
    video.addEventListener('loadedmetadata', function () {
      if (readied) return;
      readied = true;
      if (resumeAt > 0) video.currentTime = resumeAt;
      onReady();
    });
    video.addEventListener('play', function () { onStateChange({ data: 1 }); });
    video.addEventListener('ended', function () { onStateChange({ data: 0 }); });
    video.addEventListener('error', function () {
      showVideoMessage('This video file could not be played here. MP4 (H.264) videos work best; students will watch the YouTube version.');
    });
  }

  function createPlayer() {
    if (player) return;
    if (data.videoUrl) {
      createFilePlayer();
      return;
    }
    var vars = {
      rel: 0,
      modestbranding: 1,
      playsinline: 1,
      cc_load_policy: 1,
      enablejsapi: 1
    };
    if (/^https?:$/.test(window.location.protocol)) vars.origin = window.location.origin;
    if (resumeAt > 0) vars.start = Math.floor(resumeAt);

    player = new window.YT.Player('player', {
      host: 'https://www.youtube-nocookie.com',
      videoId: data.videoId,
      width: '100%',
      height: '100%',
      playerVars: vars,
      events: { onReady: onReady, onStateChange: onStateChange, onError: onError }
    });
  }

  function loadYouTube() {
    var previous = window.onYouTubeIframeAPIReady;
    window.onYouTubeIframeAPIReady = function () {
      if (typeof previous === 'function') previous();
      createPlayer();
    };
    if (window.YT && window.YT.Player) {
      createPlayer();
      return;
    }
    var script = document.createElement('script');
    script.src = 'https://www.youtube.com/iframe_api';
    script.async = true;
    script.onerror = function () {
      showVideoMessage('The YouTube player could not be loaded. Check your internet connection, or ask whether your network blocks YouTube.');
    };
    document.head.appendChild(script);
    window.setTimeout(function () {
      if (!player) showVideoMessage('The YouTube player is taking a long time to load. Check your internet connection and reload the page.');
    }, YOUTUBE_LOAD_TIMEOUT_MS);
  }

  // ---------------------------------------------------------------- full screen (includes question dialogs)

  function setUpFullscreen() {
    var request = dom.app.requestFullscreen || dom.app.webkitRequestFullscreen;
    var enabled = document.fullscreenEnabled || document.webkitFullscreenEnabled;
    if (!request || !enabled) return;
    dom.fullscreen.hidden = false;
    dom.fullscreen.addEventListener('click', function () {
      var current = document.fullscreenElement || document.webkitFullscreenElement;
      if (current) {
        (document.exitFullscreen || document.webkitExitFullscreen).call(document);
      } else {
        var result = request.call(dom.app);
        if (result && result.catch) result.catch(function () { /* ignore */ });
      }
    });
    var update = function () {
      var on = (document.fullscreenElement || document.webkitFullscreenElement) === dom.app;
      dom.fullscreen.textContent = on ? 'Exit full screen' : 'Full screen';
    };
    document.addEventListener('fullscreenchange', update);
    document.addEventListener('webkitfullscreenchange', update);
  }

  // ---------------------------------------------------------------- start / exit

  var exited = false;
  function exit() {
    if (exited) return;
    exited = true;
    if (Scorm.canRecord()) {
      if (answeredCount() > 0) Scorm.set('cmi.suspend_data', encodeSuspendData());
      saveLocation(currentTime(), true);
    }
    Scorm.finish(allAnswered());
  }

  function start() {
    document.title = data.title || 'Lecture';
    dom.title.textContent = data.title || 'Lecture';
    if (data.language) dom.title.setAttribute('lang', data.language);

    var inLms = Scorm.init();
    var notices = [];
    if (inLms) {
      restoreSuspendData(Scorm.get('cmi.suspend_data'));
      var location = parseFloat(Scorm.get('cmi.core.lesson_location'));
      if (isFinite(location) && location > 0) resumeAt = location;
      if (Scorm.canRecord()) {
        var status = Scorm.get('cmi.core.lesson_status');
        if (status === '' || status === 'not attempted') {
          Scorm.set('cmi.core.lesson_status', 'incomplete');
          Scorm.commit();
        }
      } else {
        notices.push('Review mode: your answers will not be recorded.');
      }
    } else {
      notices.push('Your score will not be recorded because this lecture was opened outside your course site.');
      if (window.location.protocol === 'file:') {
        notices.push('YouTube may refuse to play videos on pages opened from a file.');
      }
    }

    if (settings.preventSkippingAhead) {
      var first = firstUnanswered();
      if (first >= 0) resumeAt = Math.min(resumeAt, questions[first].time);
    }
    if (allAnswered()) summaryShown = true;

    if (notices.length) {
      dom.notice.textContent = notices.join(' ');
      dom.notice.hidden = false;
    }

    updateStatus();
    renderMarkers();
    setUpFullscreen();
    if (data.videoUrl) createPlayer();
    else loadYouTube();

    window.addEventListener('pagehide', exit);
    window.addEventListener('beforeunload', exit);
  }

  start();
})();
