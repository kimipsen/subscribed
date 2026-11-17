// Panels are full width at 900px and up; fields stay capped at 44rem; pre-session panels stay narrow (#40).
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';

const root = new URL('../', import.meta.url);
const read = (path) => readFileSync(new URL(path, root), 'utf8');

const css = read('src/styles.scss').replace(/\/\*[\s\S]*?\*\//g, '');

/** Splits on commas that are not inside (), [] or quotes. */
function splitTopLevel(text) {
  const parts = [];
  let depth = 0;
  let quote = '';
  let current = '';
  for (const ch of text) {
    if (quote) {
      if (ch === quote) quote = '';
    } else if (ch === '"' || ch === "'") quote = ch;
    else if (ch === '(' || ch === '[') depth++;
    else if (ch === ')' || ch === ']') depth--;
    if (ch === ',' && depth === 0 && !quote) {
      parts.push(current);
      current = '';
    } else current += ch;
  }
  parts.push(current);
  return parts;
}

const normaliseSelector = (s) => s.replace(/"/g, "'").replace(/\s+/g, ' ').trim();

/** Parses rules in order. At-rule bodies are parsed recursively and tagged with their prelude. */
function parse(text, context = '', rules = []) {
  let i = 0;
  while (i < text.length) {
    const open = text.indexOf('{', i);
    if (open === -1) break;
    const prelude = text.slice(i, open).trim();
    let depth = 1;
    let j = open + 1;
    while (j < text.length && depth > 0) {
      if (text[j] === '{') depth++;
      else if (text[j] === '}') depth--;
      j++;
    }
    const body = text.slice(open + 1, j - 1);
    if (prelude.startsWith('@')) {
      const media = prelude.replace(/\s+/g, ' ');
      parse(body, media.startsWith('@media') ? media : context, rules);
    } else {
      const declarations = {};
      for (const decl of body.split(';')) {
        const colon = decl.indexOf(':');
        if (colon > 0) declarations[decl.slice(0, colon).trim()] = decl.slice(colon + 1).trim();
      }
      rules.push({
        selectors: splitTopLevel(prelude).map(normaliseSelector),
        context,
        declarations,
        order: rules.length,
      });
    }
    i = j;
  }
  return rules;
}

const rules = parse(css);
const WIDE = '@media (min-width: 900px)';
const find = (selector, context = '') =>
  rules.filter((r) => r.context === context && r.selectors.includes(selector));

test('base .panel and .skeleton keep max-width 44rem', () => {
  for (const selector of ['.panel', '.skeleton']) {
    const base = find(selector);
    assert.ok(base.length > 0, `${selector} base rule is missing`);
    assert.equal(base.at(-1).declarations['max-width'], '44rem', `${selector} base max-width`);
  }
});

test('the 900px block lifts max-width on .panel and .skeleton, after the base rules', () => {
  for (const selector of ['.panel', '.skeleton']) {
    const wide = find(selector, WIDE);
    assert.ok(wide.length > 0, `${selector} has no rule inside ${WIDE}`);
    assert.equal(wide.at(-1).declarations['max-width'], 'none', `${selector} max-width at 900px`);
    assert.ok(wide.at(-1).order > find(selector).at(-1).order, `${selector} media rule must come after the base rule`);
  }
});

const fieldCap = rules.filter(
  (r) => r.context === '' && r.declarations['max-width'] === '44rem' && r.selectors.some((s) => s.startsWith('.panel input')),
);

test('the field cap covers input, select, textarea and .picker inside .panel at 44rem', () => {
  assert.equal(fieldCap.length, 1, 'expected exactly one field-cap rule');
  const { selectors } = fieldCap[0];
  const input = selectors.find((s) => s.startsWith('.panel input'));
  assert.ok(selectors.includes('.panel select'), '.panel select missing');
  assert.ok(selectors.includes('.panel textarea'), '.panel textarea missing');
  assert.ok(selectors.includes('.panel .picker'), '.panel .picker missing');
  assert.ok(input.includes(":not([type='radio'])"), 'radio exclusion missing');
  assert.ok(input.includes(":not([type='checkbox'])"), 'checkbox exclusion missing');
});

test('.panel-narrow keeps 44rem and is declared after the 900px block', () => {
  const narrow = find('.panel-narrow');
  assert.equal(narrow.length, 1, 'expected one .panel-narrow rule');
  assert.equal(narrow[0].declarations['max-width'], '44rem');
  assert.ok(narrow[0].order > find('.panel', WIDE).at(-1).order, '.panel-narrow must come after the 900px block');
  assert.ok(narrow[0].order > find('.skeleton', WIDE).at(-1).order);
});

test('.custom-cadence keeps its 20rem field cap', () => {
  assert.equal(find('.custom-cadence').at(-1).declarations['max-width'], '20rem');
});

test('session-unavailable uses panel-narrow', () => {
  assert.ok(read('src/app/subscriptions/session-unavailable.ts').includes('class="panel panel-narrow"'));
});

test('signed-out, anonymous and access-pending panels are narrow; household panels are not', () => {
  const html = read('src/app/foundation-page.html');
  const sectionAfter = (anchor) => {
    const at = html.indexOf(anchor);
    assert.ok(at >= 0, `${anchor} not found in foundation-page.html`);
    const match = /<section class="([^"]*)">/.exec(html.slice(at));
    assert.ok(match, `no <section> after ${anchor}`);
    return match[1].split(/\s+/);
  };
  for (const anchor of ["@if (page() === 'signed-out')", "@case ('anonymous')", "@case ('not-member')"]) {
    assert.ok(sectionAfter(anchor).includes('panel-narrow'), `${anchor} panel must carry panel-narrow`);
  }
  assert.ok(!sectionAfter("@if (household(); as household)").includes('panel-narrow'), 'household panel must stay wide');
  assert.ok(!html.slice(html.indexOf("@case ('member')")).includes('panel-narrow'), 'member panels must stay wide');
});

test('.picker still exists in the subscription form template', () => {
  assert.ok(read('src/app/subscriptions/subscription-form-page.html').includes('picker'));
  assert.match(read('src/app/subscriptions/subscription-form-page.html'), /class="[^"]*\bpicker\b[^"]*"/);
});
