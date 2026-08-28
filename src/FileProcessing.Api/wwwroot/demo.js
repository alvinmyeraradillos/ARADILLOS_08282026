/*
 * Demo console for the File Processing API.
 *
 * Plain ES modules-free vanilla JS on purpose: no build step, no dependency to audit, and nothing
 * inline, so the page satisfies a Content-Security-Policy with no 'unsafe-inline'.
 *
 * Everything below is a thin wrapper over fetch. The point of the page is to show the API's real
 * responses — including the failures — rather than to hide them behind a friendly UI.
 */
(function () {
  'use strict';

  // Built-in samples, so the console is usable with no files on disk.
  var SAMPLES = {
    valid: {
      name: 'transactions-valid.csv',
      body: [
        'TransactionId,TransactionDate,Description,Amount,Currency,Category',
        'TXN-1001,2026-07-01,Melbourne to Geelong linehaul,1450.00,AUD,Linehaul',
        'TXN-1002,2026-07-01,"Fuel levy, July",212.55,AUD,Fuel',
        'TXN-1003,2026-07-02,Pallet hire,88.40,AUD,Equipment',
        'TXN-1004,2026-07-03,"Detention charge, Altona North",165.00,AUD,Detention',
        'TXN-1005,2026-07-05,Sydney metro delivery,980.25,AUD,Linehaul',
        'TXN-1006,2026-07-08,Fuel levy adjustment,-45.10,AUD,Fuel',
        'TXN-1007,2026-07-11,Overnight storage,320.00,AUD,Warehousing',
        'TXN-1008,2026-07-15,Auckland cross-dock,610.00,NZD,Linehaul',
        'TXN-1009,2026-07-19,Container lift fee,140.75,AUD,Equipment',
        'TXN-1010,2026-07-22,"Customer credit ""goodwill""",-200.00,AUD,Adjustments'
      ].join('\n')
    },
    errors: {
      name: 'transactions-with-errors.csv',
      body: [
        'TransactionId,TransactionDate,Description,Amount,Currency,Category',
        'TXN-2001,2026-07-01,Valid row,500.00,AUD,Linehaul',
        'TXN-2002,01/07/2026,Date is not ISO 8601,120.00,AUD,Fuel',
        'TXN-2003,2026-07-02,Amount is not a number,twelve,AUD,Fuel',
        'TXN-2004,2026-07-03,Currency is not a valid ISO code,75.00,AUDD,Equipment',
        'TXN-2005,2026-07-04,Category is missing,60.00,AUD,',
        'TXN-2001,2026-07-05,Transaction id repeats an earlier row,90.00,AUD,Linehaul',
        'TXN-2006,2026-07-06,Too many decimal places,10.005,AUD,Fuel',
        'TXN-2007,2026-07-07,This row is missing its trailing columns',
        'TXN-2008,2099-01-01,Dated in the future,45.00,AUD,Adjustments',
        'TXN-2009,2026-07-09,Another valid row,275.50,AUD,Warehousing'
      ].join('\n')
    },
    badHeader: {
      name: 'transactions-bad-header.csv',
      body: [
        'Id,Date,Notes,Value',
        'TXN-3001,2026-07-01,The header does not carry the required columns,100.00'
      ].join('\n')
    }
  };

  var $ = function (id) { return document.getElementById(id); };
  var pendingSample = null;

  function apiKey() {
    var choice = $('key-select').value;
    return choice === 'custom' ? $('key-custom').value.trim() : choice;
  }

  function headers() {
    var key = apiKey();
    return key ? { 'X-Api-Key': key } : {};
  }

  /** Calls the API and always returns {status, ok, body} — errors are data here, not exceptions. */
  function call(path, options) {
    options = options || {};
    options.headers = Object.assign(headers(), options.headers || {});
    return fetch(path, options).then(function (response) {
      return response.text().then(function (text) {
        var body;
        try { body = text ? JSON.parse(text) : null; } catch (e) { body = text; }
        return { status: response.status, ok: response.ok, body: body };
      });
    });
  }

  // ---------------------------------------------------------------------------------------------
  // Rendering helpers. Everything user-facing goes through textContent, never innerHTML, so an
  // API response cannot inject markup into this page.
  // ---------------------------------------------------------------------------------------------

  function el(tag, className, text) {
    var node = document.createElement(tag);
    if (className) { node.className = className; }
    if (text !== undefined && text !== null) { node.textContent = String(text); }
    return node;
  }

  function statusBadge(status) {
    var cls = status < 300 ? 'ok' : (status < 500 ? 'warn' : 'bad');
    return el('span', 'badge ' + cls, 'HTTP ' + status);
  }

  function rawButton(title, payload) {
    var button = el('button', 'ghost', 'raw JSON');
    button.addEventListener('click', function () {
      $('raw-title').textContent = title;
      $('raw-body').textContent = JSON.stringify(payload, null, 2);
      $('raw-dialog').showModal();
    });
    return button;
  }

  function statusLine(target, result, title) {
    var line = el('div', 'status-line');
    line.appendChild(statusBadge(result.status));
    if (result.body && result.body.title && !result.ok) {
      line.appendChild(el('span', null, result.body.title));
      if (result.body.detail) { line.appendChild(el('span', 'badge', result.body.detail)); }
    }
    line.appendChild(rawButton(title, result.body));
    target.appendChild(line);
    return result.ok;
  }

  function tiles(target, pairs) {
    var wrap = el('div', 'tiles');
    pairs.forEach(function (pair) {
      var tile = el('div', 'tile');
      tile.appendChild(el('span', 'k', pair[0]));
      tile.appendChild(el('span', 'v', pair[1]));
      wrap.appendChild(tile);
    });
    target.appendChild(wrap);
  }

  function table(target, columns, rows) {
    var t = el('table');
    var thead = el('thead');
    var hr = el('tr');
    columns.forEach(function (c) { hr.appendChild(el('th', c.num ? 'num' : null, c.label)); });
    thead.appendChild(hr);
    t.appendChild(thead);

    var tbody = el('tbody');
    rows.forEach(function (row) {
      var tr = el('tr');
      columns.forEach(function (c) { tr.appendChild(el('td', c.num ? 'num' : null, c.get(row))); });
      tbody.appendChild(tr);
    });
    t.appendChild(tbody);
    target.appendChild(t);
  }

  function label(target, text) { target.appendChild(el('p', 'section-label', text)); }

  // ---------------------------------------------------------------------------------------------
  // Upload
  // ---------------------------------------------------------------------------------------------

  function chosenFile() {
    var picked = $('file-input').files[0];
    if (picked) { return picked; }
    if (pendingSample) {
      var s = SAMPLES[pendingSample];
      return new File([s.body], s.name, { type: 'text/csv' });
    }
    return null;
  }

  function upload() {
    var target = $('upload-result');
    target.textContent = '';

    var file = chosenFile();
    if (!file) {
      target.appendChild(el('div', 'badge bad', 'Choose a file or pick a sample first.'));
      return;
    }

    var form = new FormData();
    form.append('file', file, file.name);

    call('/api/v1/files', { method: 'POST', body: form }).then(function (result) {
      if (!statusLine(target, result, 'POST /api/v1/files')) { return; }

      var b = result.body;
      var a = b.aggregates || {};
      tiles(target, [
        ['status', b.status],
        ['rows', b.rows.valid + ' / ' + b.rows.total],
        ['total', a.totalAmount],
        ['average', a.averageAmount],
        ['duration', b.durationMilliseconds + ' ms'],
        ['size', b.sizeBytes + ' B']
      ]);

      if (a.byCategory && a.byCategory.length) {
        label(target, 'By category');
        table(target,
          [
            { label: 'Category', get: function (r) { return r.category; } },
            { label: 'Count', num: true, get: function (r) { return r.count; } },
            { label: 'Total', num: true, get: function (r) { return r.totalAmount; } },
            { label: 'Average', num: true, get: function (r) { return r.averageAmount; } }
          ],
          a.byCategory);
      }

      if (b.errors && b.errors.length) {
        label(target, 'Rejected rows (' + b.errors.length + ')');
        table(target,
          [
            { label: 'Line', num: true, get: function (r) { return r.line; } },
            { label: 'Field', get: function (r) { return r.field || '—'; } },
            { label: 'Code', get: function (r) { return r.code; } },
            { label: 'Message', get: function (r) { return r.message; } }
          ],
          b.errors);
      }
    });
  }

  // ---------------------------------------------------------------------------------------------
  // Listing and report
  // ---------------------------------------------------------------------------------------------

  function list() {
    var target = $('list-result');
    target.textContent = '';

    var params = new URLSearchParams();
    if ($('filter-status').value) { params.set('status', $('filter-status').value); }
    params.set('pageSize', $('filter-size').value || '10');

    call('/api/v1/files?' + params.toString()).then(function (result) {
      if (!statusLine(target, result, 'GET /api/v1/files')) { return; }

      var page = result.body;
      tiles(target, [['returned', page.items.length], ['total', page.totalCount], ['pages', page.totalPages]]);

      if (!page.items.length) {
        target.appendChild(el('p', 'hint', 'Nothing tracked yet — upload a file above.'));
        return;
      }

      table(target,
        [
          { label: 'File', get: function (r) { return r.fileName; } },
          { label: 'Client', get: function (r) { return r.clientId; } },
          { label: 'Status', get: function (r) { return r.status; } },
          { label: 'Rows', num: true, get: function (r) { return r.rows.valid + '/' + r.rows.total; } },
          { label: 'Amount', num: true, get: function (r) { return r.totalAmount; } },
          { label: 'ms', num: true, get: function (r) { return r.durationMilliseconds; } },
          { label: 'Received', get: function (r) { return new Date(r.receivedAtUtc).toLocaleString(); } }
        ],
        page.items);
    });
  }

  function report() {
    var target = $('report-result');
    target.textContent = '';

    var params = new URLSearchParams();
    if ($('report-from').value) { params.set('from', $('report-from').value + 'T00:00:00Z'); }
    if ($('report-to').value) { params.set('to', $('report-to').value + 'T23:59:59Z'); }
    var query = params.toString();

    call('/api/v1/reports/summary' + (query ? '?' + query : '')).then(function (result) {
      if (!statusLine(target, result, 'GET /api/v1/reports/summary')) { return; }

      var r = result.body;
      tiles(target, [
        ['files', r.totalFiles],
        ['succeeded', r.succeededFiles],
        ['with errors', r.filesWithErrors],
        ['failed', r.failedFiles],
        ['rows', r.rows.valid + ' / ' + r.rows.total],
        ['total', r.totalAmount],
        ['avg row', r.averageRowAmount],
        ['avg ms', r.averageDurationMilliseconds]
      ]);

      if (r.byClient && r.byClient.length) {
        label(target, 'By client');
        table(target,
          [
            { label: 'Client', get: function (c) { return c.clientId; } },
            { label: 'Files', num: true, get: function (c) { return c.fileCount; } },
            { label: 'Rows', num: true, get: function (c) { return c.totalRows; } },
            { label: 'Amount', num: true, get: function (c) { return c.totalAmount; } }
          ],
          r.byClient);
      }
    });
  }

  function health() {
    [['live', 'badge-live'], ['ready', 'badge-ready']].forEach(function (probe) {
      fetch('/health/' + probe[0])
        .then(function (r) {
          var badge = $(probe[1]);
          badge.textContent = probe[0] + ' ' + r.status;
          badge.className = 'badge ' + (r.ok ? 'ok' : 'bad');
        })
        .catch(function () {
          var badge = $(probe[1]);
          badge.textContent = probe[0] + ' unreachable';
          badge.className = 'badge bad';
        });
    });
  }

  // ---------------------------------------------------------------------------------------------
  // Wiring
  // ---------------------------------------------------------------------------------------------

  $('key-select').addEventListener('change', function () {
    $('custom-key-row').classList.toggle('hidden', this.value !== 'custom');
  });

  Array.prototype.forEach.call(document.querySelectorAll('[data-sample]'), function (button) {
    button.addEventListener('click', function () {
      pendingSample = button.getAttribute('data-sample');
      $('file-input').value = '';
      var target = $('upload-result');
      target.textContent = '';
      target.appendChild(el('div', 'badge', 'Sample ready: ' + SAMPLES[pendingSample].name));
    });
  });

  $('file-input').addEventListener('change', function () { pendingSample = null; });
  $('btn-upload').addEventListener('click', upload);
  $('btn-list').addEventListener('click', list);
  $('btn-report').addEventListener('click', report);
  $('raw-close').addEventListener('click', function () { $('raw-dialog').close(); });

  health();
})();
