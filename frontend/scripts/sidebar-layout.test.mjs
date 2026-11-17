// Dependency-free structural checks for the collapsible sidebar styles in src/app/app.scss, run by `npm run test:ci`.
// The styles are parsed as text with a brace-aware block parser (like theme.test.mjs T-H); nothing is rendered here.
// Behaviour (toggle, persistence, labels) is covered by src/app/app.spec.ts.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';

const source = readFileSync(new URL('../src/app/app.scss', import.meta.url), 'utf8')
  .replace(/\/\*[\s\S]*?\*\//g, '')
  .replace(/\/\/[^\n]*/g, '');

const DESKTOP_QUERY = '@media (min-width: 900px)';

/** Parses `prelude { body }` blocks into a tree. Declarations of a block are the text outside its child blocks. */
function parseBlocks(text) {
  const blocks = [];
  let depth = 0;
  let preludeStart = 0;
  let bodyStart = 0;
  const stack = [];
  for (let i = 0; i < text.length; i += 1) {
    const char = text[i];
    if (char === '{') {
      if (depth === 0) {
        preludeStart = Math.max(preludeStart, 0);
        bodyStart = i + 1;
        stack.push(text.slice(preludeStart, i).trim());
      }
      depth += 1;
    } else if (char === '}') {
      depth -= 1;
      assert.ok(depth >= 0, 'unbalanced closing brace in app.scss');
      if (depth === 0) {
        const prelude = stack.pop();
        const body = text.slice(bodyStart, i);
        blocks.push({
          prelude,
          body,
          children: prelude.startsWith('@') ? parseBlocks(body) : [],
        });
        preludeStart = i + 1;
      }
    }
  }
  assert.equal(depth, 0, 'unbalanced braces in app.scss');
  return blocks;
}

const topLevel = parseBlocks(source);
const desktop = topLevel.filter((block) => block.prelude === DESKTOP_QUERY);
const desktopRules = desktop.flatMap((block) => block.children);
const outsideDesktopRules = topLevel.filter((block) => !block.prelude.startsWith('@'))
  .concat(topLevel.filter((block) => block.prelude.startsWith('@') && block.prelude !== DESKTOP_QUERY)
    .flatMap((block) => block.children));

const selectors = (rule) => rule.prelude.split(',').map((part) => part.trim());

function declarations(rule) {
  const map = new Map();
  for (const part of rule.body.split(';')) {
    const index = part.indexOf(':');
    if (index === -1) continue;
    map.set(part.slice(0, index).trim(), part.slice(index + 1).trim());
  }
  return map;
}

const rulesFor = (rules, matcher) => rules.filter((rule) => selectors(rule).some(matcher));
const isNavLabel = (selector) => /(^|\s)\.nav-label$/.test(selector);
const isReveal = (selector) => /\.nav-label$/.test(selector) && /:(hover|focus-visible)\s/.test(selector);

test('the file has exactly one 900px min-width query and balanced braces', () => {
  assert.equal(desktop.length, 1);
  assert.ok(desktopRules.length > 0);
});

test('collapsed rules sit inside @media (min-width: 900px) and none is outside it', () => {
  const collapsedInside = rulesFor(desktopRules, (selector) => selector.includes('.collapsed'));
  assert.ok(collapsedInside.length > 0, 'expected .collapsed rules inside the 900px query');
  assert.deepEqual(rulesFor(outsideDesktopRules, (selector) => selector.includes('.collapsed')).map((rule) => rule.prelude), []);
});

test('the hidden .nav-label uses clip properties and neither display none nor visibility hidden', () => {
  const hiding = rulesFor(desktopRules, (selector) => /\.collapsed\s+\.nav-label$/.test(selector));
  assert.equal(hiding.length, 1, 'expected one .collapsed .nav-label rule');
  const props = declarations(hiding[0]);
  assert.equal(props.get('position'), 'absolute');
  assert.equal(props.get('width'), '1px');
  assert.equal(props.get('height'), '1px');
  assert.equal(props.get('overflow'), 'hidden');
  assert.equal(props.get('clip-path'), 'inset(50%)');
  assert.equal(props.get('white-space'), 'nowrap');
  for (const rule of rulesFor([...desktopRules, ...outsideDesktopRules], isNavLabel)) {
    const own = declarations(rule);
    assert.notEqual(own.get('display'), 'none', rule.prelude);
    assert.notEqual(own.get('visibility'), 'hidden', rule.prelude);
  }
});

test('no .nav-label is hidden by a rule outside the 900px query', () => {
  for (const rule of outsideDesktopRules.filter((candidate) => candidate.prelude.includes('.nav-label'))) {
    const props = declarations(rule);
    assert.notEqual(props.get('display'), 'none', rule.prelude);
    assert.notEqual(props.get('visibility'), 'hidden', rule.prelude);
    assert.equal(props.get('clip-path'), undefined, rule.prelude);
  }
});

test('hover and focus-visible tooltip selectors exist for links and .nav-toggle', () => {
  const reveal = rulesFor(desktopRules, isReveal);
  assert.equal(reveal.length, 1, 'expected one reveal rule');
  const all = selectors(reveal[0]);
  for (const expected of [
    '.collapsed nav a:hover .nav-label',
    '.collapsed nav a:focus-visible .nav-label',
    '.collapsed .nav-toggle:hover .nav-label',
    '.collapsed .nav-toggle:focus-visible .nav-label',
  ]) {
    assert.ok(all.includes(expected), `missing selector ${expected}`);
  }
});

test('the tooltip reveal resets position, clip-path and width', () => {
  const props = declarations(rulesFor(desktopRules, isReveal)[0]);
  assert.equal(props.get('position'), 'absolute');
  assert.ok(props.has('left'), 'the tooltip is positioned beside the rail');
  assert.equal(props.get('clip-path'), 'none');
  assert.equal(props.get('width'), 'auto');
  assert.equal(props.get('height'), 'auto');
  assert.equal(props.get('overflow'), 'visible');
});

test('.nav-toggle is display none by default and display flex only inside the 900px query', () => {
  // The shared `nav a, .nav-toggle` rule sets display: flex first; the later plain `.nav-toggle` rule must win with none.
  const plain = (selector) => selector === '.nav-toggle';
  const displays = (rules) => rulesFor(rules, plain).map((rule) => declarations(rule).get('display')).filter(Boolean);
  const outside = displays(outsideDesktopRules);
  assert.equal(outside.at(-1), 'none', 'the last default .nav-toggle display must be none');
  assert.deepEqual(displays(desktopRules), ['flex']);
});

test('no selector in app.scss targets svg', () => {
  const all = [...desktopRules, ...outsideDesktopRules].flatMap(selectors);
  assert.ok(all.length > 0);
  assert.deepEqual(all.filter((selector) => /(^|[\s>+~(,])svg\b/.test(selector)), []);
});
