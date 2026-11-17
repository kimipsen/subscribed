// Static contract for the Tailwind 4 + daisyUI 5 styling, run by `npm run test:ci`.
//
// Templates are read through the Angular template parser (`parseTemplate` from @angular/compiler, already installed
// with Angular), so `@if`/`@for`/`@switch` branches are all seen and class contexts are exact. Inline `template:`
// strings and class strings in non-spec `.ts` files are read with the `typescript` package. No regexes run over raw
// template text except on the source of a `[class]`/`[ngClass]` binding expression.
//
// Checks (numbers match the implementation plan):
//  1 no *.scss under src                         2 angular.json has no inlineStyleLanguage, styles is src/styles.css
//  3 styles.css wiring and @apply budget         4 no styleUrl/styleUrls/styles: in component .ts files
//  5 no removed semantic class token             6 no inline style="" attribute
//  7 the nav label is never display:none         8 colours, dark:, daisyUI allow-list, alpha utilities, forbidden list
//  9 class names are complete literals          10 sidebar contract (nav: guard, group, no data-tip)
// 11 control borders and placeholders per element
//
// Checks 1, 4 to 11 need migrated templates, so they are gated by ENABLE_TEMPLATE_CHECKS and a ratchet:
// while the flag is false they run for exactly the files in MIGRATED_FILES; a page commit adds its files in the
// same commit that migrates them. The flag flips to true (and MIGRATED_FILES goes away in favour of the candidate
// set) in the commit that migrates the last token-bearing file.
//
// Removed-selector fixture: scripts/fixtures/removed-scss-selectors.json is the sorted set of class names the old
// SCSS defined. It was generated once from the pre-change sources on master (src/styles.scss, src/app/app.scss and
// the `styles:` blocks of subscriptions/service-logo.ts and subscriptions/payment-option-logo.ts). Only selector
// preludes count (text before `{`, comments stripped; for .ts only inside the `styles:` template literal), because a
// regex over raw text also matches property values and adds junk names. One-liner used (run from frontend/ on master):
//   git ls-tree -r --name-only master -- src | grep -E 'scss|(service|payment-option)-logo.ts'  # then, per file,
//   git show master:./<file>, cut at each `{`, `}` or `;`, and collect /\.([A-Za-z_][\w-]*)/g from each prelude
//   that does not start with `@` (throws if it meets `&`, which neither SCSS file uses).
import assert from 'node:assert/strict';
import { readdirSync } from 'node:fs';
import { test } from 'node:test';
import { parseTemplate } from '@angular/compiler';
import ts from 'typescript';
import { ALPHA_TABLE, read } from './theme-palette.mjs';

// The one switch. See the header: false while some page still uses removed class tokens.
const ENABLE_TEMPLATE_CHECKS = false;

// Files (relative to frontend/) whose templates already satisfy every template check. Token-free templates that
// exist before restyling are listed now; each restyled page adds its files in its own commit.
const MIGRATED_FILES = [
  'src/app/app.html',
  'src/app/app.ts',
  'src/app/foundation-page.ts',
  'src/app/subscriptions/payment-options-page.ts',
  'src/app/subscriptions/subscription-detail-page.html',
  'src/app/subscriptions/subscription-detail-page.ts',
  'src/app/subscriptions/subscription-form-page.html',
  'src/app/subscriptions/subscription-form-page.ts',
  'src/app/subscriptions/subscription-overview.html',
  'src/app/subscriptions/subscription-overview.ts',
  'src/app/ui/form-field.ts',
  'src/app/ui/notice.ts',
  'src/app/ui/panel.ts',
  'src/app/ui/status-badge.ts',
  'src/index.html',
];

const frontend = new URL('../', import.meta.url);

// ---------- the removed-token list (check 5), written out in full ----------

export const REMOVED_TOKENS = [
  'panel', 'panel-narrow', 'field', 'field-error', 'muted', 'secondary', 'actions', 'choice', 'category-group',
  'category-heading', 'totals', 'picker', 'picker-results', 'picker-selected', 'name-cell', 'title-row', 'members',
  'option-list', 'option-text', 'suggestion', 'suggestions', 'empty', 'table-scroll', 'data', 'nav-toggle',
  'nav-label', 'status-paused', 'status-active', 'status-cancelled', 'status-upcoming', 'status-current',
  'collapsed', 'member', 'skip', 'summary', 'notice',
  // found by the pre-change grep of templates and SCSS
  'error', 'brand', 'spacer', 'header-actions', 'preferences', 'pref-label', 'layout', 'version', 'logo-fallback',
  'logo-image', 'eyebrow', 'history-note', 'notes', 'entry-form', 'add-form', 'edit-form', 'member-edit',
  'category-toolbar', 'custom-cadence', 'picker-heading', 'picker-result', 'totals-panel', 'form', 'current',
  'upcoming', 'entry', 'button',
];

// Defined by daisyUI or Tailwind as well and used by the plan, so they are not "removed" (compound tokens such as
// status-active stay on the removed list; `status` is a daisyUI 5 component class like `badge`).
export const REMOVED_EXCLUSIONS = ['skeleton', 'badge', 'menu', 'table', 'card', 'alert', 'active', 'open', 'status'];

// ---------- daisyUI component allow-list (check 8) ----------

// Components whose built-in colours and alpha are covered by pairs in scripts/theme.test.mjs. Anything else that is
// a daisyUI component class fails. `stats`, `label` and `menu-title` are forbidden outright (low built-in alpha).
export const DAISY_ALLOWED = [
  'btn', 'btn-primary', 'btn-neutral', 'btn-outline', 'btn-ghost', 'btn-sm', 'btn-block', 'btn-square', 'btn-circle',
  'input', 'input-sm', 'select', 'select-sm', 'textarea', 'checkbox', 'radio',
  'table', 'table-sm',
  'menu', 'menu-active', 'menu-sm',
  'badge', 'badge-neutral', 'badge-success', 'badge-warning', 'badge-info', 'badge-error', 'badge-sm',
  'alert', 'alert-warning', 'alert-error', 'alert-info', 'alert-success',
  'card', 'card-body', 'card-title',
  'navbar', 'navbar-start', 'navbar-center', 'navbar-end',
  'tooltip', 'tooltip-right', 'tooltip-content',
  'skeleton',
];
export const DAISY_FORBIDDEN = ['stats', 'stat', 'stat-title', 'stat-value', 'stat-desc', 'stat-figure', 'stat-actions', 'label', 'floating-label', 'menu-title'];
// Tailwind utilities that share a name or prefix with a daisyUI component and are not daisyUI.
const TAILWIND_OWNED_EXACT = new Set(['collapse', 'filter', 'table']);
const TAILWIND_OWNED_PREFIX = /^(?:list|mask|table|select)-(?:auto|fixed|row|cell|caption|column|footer|header|none|text|all|disc|decimal|inside|outside|image|position|size|repeat|clip|mode|type)/;

// daisyUI 5.8.1 component roots (class prefixes). Class names come from node_modules/daisyui/components/*.css, so
// the list is checked against the installed version (see the self-test at the end of this file).
const DAISY_ROOTS = new Set([
  'alert', 'avatar', 'badge', 'breadcrumbs', 'btn', 'card', 'carousel', 'chat', 'checkbox', 'collapse',
  'countdown', 'diff', 'divider', 'dock', 'drawer', 'dropdown', 'fab', 'fieldset', 'filter', 'footer', 'hero',
  'indicator', 'input', 'kbd', 'label', 'link', 'list', 'loading', 'mask', 'menu', 'mockup', 'modal', 'navbar', 'otp',
  'progress', 'radial', 'radio', 'range', 'rating', 'select', 'skeleton', 'stack', 'stat', 'stats', 'status', 'steps',
  'swap', 'tab', 'table', 'tabs', 'textarea', 'timeline', 'toast', 'toggle', 'tooltip', 'validator',
]);

const daisyClasses = new Set();
for (const name of readdirSync(new URL('node_modules/daisyui/components/', frontend)).filter((file) => file.endsWith('.css'))) {
  for (const [, className] of read(`node_modules/daisyui/components/${name}`).matchAll(/\.([a-z][a-z0-9]*(?:-[a-z0-9]+)*)(?![\w-]|\\)/g)) daisyClasses.add(className);
}

const COLOR_MODIFIERS = new Set(['primary', 'secondary', 'accent', 'info', 'success', 'warning', 'error']);

// ---------- class-token helpers ----------

/** `nav:group-data-[collapsed=true]:sr-only` => { variants: ['nav', 'group-data-[collapsed=true]'], base: 'sr-only' }. */
export function splitToken(token) {
  const parts = [];
  let depth = 0;
  let current = '';
  for (const char of token) {
    if (char === '[') depth++;
    if (char === ']') depth--;
    if (char === ':' && depth === 0) {
      parts.push(current);
      current = '';
    } else current += char;
  }
  parts.push(current);
  const base = parts.pop().replace(/^!/, '').replace(/!$/, '');
  return { variants: parts, base: base.replace(/^-/, '') };
}

const stringLiterals = (source) =>
  [...source.matchAll(/'([^'\\]*)'|"([^"\\]*)"|`([^`$\\]*)`/g)].map((m) => m[1] ?? m[2] ?? m[3]);
const tokensOf = (text) => text.split(/\s+/).filter(Boolean);

// ---------- template scanning ----------

const BINDING_PROPERTY = 0;
const BINDING_ATTRIBUTE = 1;
const BINDING_CLASS = 2;

/**
 * Walk a parsed template. Returns elements (and ng-templates) with their class tokens, binding sources and
 * parent link (blocks are transparent, so an element inside @if still has its enclosing element as parent).
 */
export function scanTemplate(templateText, file, lineOffset = 0) {
  const parsed = parseTemplate(templateText, file, { preserveWhitespaces: true });
  const problems = [];
  if (parsed.errors?.length) problems.push(...parsed.errors.map((e) => `${file}: template parse error: ${e.msg ?? e}`));
  const elements = [];
  const visit = (node, parent) => {
    if (!node || typeof node !== 'object') return;
    const kind = node.constructor.name;
    let next = parent;
    if (kind === 'Element' || kind === 'Template') {
      const attrs = new Map();
      for (const attr of node.attributes ?? []) attrs.set(attr.name, attr.value);
      const tokens = [];
      const classSources = [];
      const boundAttrs = new Set();
      for (const attr of node.attributes ?? []) if (attr.name === 'class') for (const token of tokensOf(attr.value)) tokens.push({ token, via: 'class' });
      for (const input of node.inputs ?? []) {
        const source = input.value?.source ?? '';
        if (input.type === BINDING_CLASS) tokens.push({ token: input.name, via: '[class.x]' });
        else if (input.type === BINDING_PROPERTY && (input.name === 'class' || input.name === 'ngClass')) {
          classSources.push({ via: `[${input.name}]`, source });
          for (const literal of stringLiterals(source)) for (const token of tokensOf(literal)) tokens.push({ token, via: `[${input.name}]` });
        } else if (input.type === BINDING_ATTRIBUTE) boundAttrs.add(input.name);
        else if (input.type === BINDING_PROPERTY) boundAttrs.add(input.name);
      }
      const element = {
        file,
        tag: kind === 'Element' ? node.name : node.tagName ?? 'ng-template',
        line: node.sourceSpan.start.line + 1 + lineOffset,
        attrs,
        boundAttrs,
        tokens,
        classSources,
        parent,
      };
      elements.push(element);
      next = element;
    }
    for (const key of ['children', 'branches', 'groups', 'empty', 'placeholder', 'loading', 'error']) {
      const value = node[key];
      if (Array.isArray(value)) value.forEach((child) => visit(child, next));
      else if (value) visit(value, next);
    }
  };
  parsed.nodes.forEach((node) => visit(node, null));
  return { elements, problems };
}

const hasAttr = (element, name) => element.attrs.has(name) || element.boundAttrs.has(name);
const classTokens = (element) => element.tokens.map(({ token }) => token);
const baseTokens = (element) => classTokens(element).map((token) => splitToken(token).base);

// ---------- TypeScript scanning (inline templates, class strings, style declarations) ----------

const CLASS_NAMED = /^(?:class|classes|cssClass|[A-Za-z]*Classes|[A-Z_]*CLASSES?)$/;

export function scanTypeScript(text, file) {
  const sourceFile = ts.createSourceFile(file, text, ts.ScriptTarget.Latest, true);
  const lineOf = (node) => sourceFile.getLineAndCharacterOfPosition(node.getStart(sourceFile)).line + 1;
  const result = { file, isComponent: false, templates: [], classStrings: [], styleDeclarations: [], concatenations: [] };
  const propertyName = (property) => (ts.isIdentifier(property.name) || ts.isStringLiteral(property.name) ? property.name.text : '');
  const literalText = (node) => (ts.isStringLiteral(node) || ts.isNoSubstitutionTemplateLiteral(node) ? node.text : undefined);

  const collectClassValue = (node, line) => {
    const direct = literalText(node);
    if (direct !== undefined) {
      result.classStrings.push({ line, tokens: tokensOf(direct).map((token) => ({ token, via: 'ts class string' })), raw: direct });
      return;
    }
    if (ts.isTemplateExpression(node)) {
      result.concatenations.push({ line, source: node.getText(sourceFile) });
      return;
    }
    if (ts.isBinaryExpression(node) && node.operatorToken.kind === ts.SyntaxKind.PlusToken) {
      result.concatenations.push({ line, source: node.getText(sourceFile) });
      return;
    }
    if (ts.isObjectLiteralExpression(node)) {
      for (const property of node.properties) if (ts.isPropertyAssignment(property)) collectClassValue(property.initializer, lineOf(property));
      return;
    }
    if (ts.isArrayLiteralExpression(node)) node.elements.forEach((element) => collectClassValue(element, lineOf(element)));
  };

  const visit = (node) => {
    if (ts.isCallExpression(node) && ts.isIdentifier(node.expression) && node.expression.text === 'Component') {
      result.isComponent = true;
      const [options] = node.arguments;
      if (options && ts.isObjectLiteralExpression(options)) {
        for (const property of options.properties) {
          if (!ts.isPropertyAssignment(property)) continue;
          const name = propertyName(property);
          if (name === 'template') {
            const raw = ts.isTemplateExpression(property.initializer)
              ? property.initializer.head.text + property.initializer.templateSpans.map((s) => `\${}${s.literal.text}`).join('')
              : literalText(property.initializer);
            if (raw !== undefined) result.templates.push({ text: raw, line: lineOf(property.initializer) - 1 });
          } else if (['styleUrl', 'styleUrls', 'styles'].includes(name)) {
            result.styleDeclarations.push({ name, line: lineOf(property) });
          } else if (name === 'host' && ts.isObjectLiteralExpression(property.initializer)) {
            for (const hostProperty of property.initializer.properties) {
              if (ts.isPropertyAssignment(hostProperty) && propertyName(hostProperty) === 'class') collectClassValue(hostProperty.initializer, lineOf(hostProperty));
            }
          }
        }
      }
    }
    if (ts.isVariableDeclaration(node) && ts.isIdentifier(node.name) && CLASS_NAMED.test(node.name.text) && node.initializer) {
      collectClassValue(node.initializer, lineOf(node));
    }
    ts.forEachChild(node, visit);
  };
  visit(sourceFile);
  return result;
}

// ---------- the file set ----------

function walk(directory, predicate) {
  return readdirSync(new URL(directory, frontend), { withFileTypes: true }).flatMap((entry) => {
    const path = `${directory}${entry.name}`;
    if (entry.isDirectory()) return entry.name === 'testing' ? [] : walk(`${path}/`, predicate);
    return predicate(entry.name) ? [path] : [];
  });
}

const isSpec = (name) => name.endsWith('.spec.ts');
const allHtml = walk('src/', (name) => name.endsWith('.html')).sort();
const allTs = walk('src/', (name) => name.endsWith('.ts') && !isSpec(name) && !name.endsWith('.d.ts')).sort();

/** One scan unit per candidate file: its template elements plus ts-level findings. */
function loadUnit(file) {
  const text = read(file);
  if (file.endsWith('.html')) {
    const { elements, problems } = scanTemplate(text, file);
    return { file, elements, classStrings: [], styleDeclarations: [], concatenations: [], problems };
  }
  const info = scanTypeScript(text, file);
  const elements = [];
  const problems = [];
  for (const template of info.templates) {
    const scanned = scanTemplate(template.text, file, template.line);
    elements.push(...scanned.elements);
    problems.push(...scanned.problems);
  }
  return { ...info, elements, problems };
}

/** The candidate set: non-spec, non-testing templates, plus component classes (inline templates, style declarations). */
function candidateUnits() {
  const units = allHtml.map(loadUnit);
  for (const file of allTs) {
    const unit = loadUnit(file);
    if (unit.isComponent || unit.classStrings.length || unit.concatenations.length) units.push(unit);
  }
  return units;
}

// ---------- the checks (each returns a list of readable violations) ----------

const where = (unit, line) => `${unit.file}:${line}`;

/** A removed class token is any whole class token on the list, in a class context. */
const REMOVED_SET = new Set(REMOVED_TOKENS);

function tokenContexts(unit) {
  return [
    ...unit.elements.map((element) => ({ line: element.line, tokens: element.tokens, element })),
    ...unit.classStrings.map(({ line, tokens }) => ({ line, tokens, element: null })),
  ];
}

export const CHECKS = {
  1: (unit) => (unit.file.endsWith('.scss') ? [`${unit.file}: scss file under src`] : []),

  4: (unit) => unit.styleDeclarations.map(({ name, line }) => `${where(unit, line)}: component declares ${name}`),

  5: (unit) => tokenContexts(unit).flatMap(({ line, tokens }) =>
    tokens.filter(({ token }) => REMOVED_SET.has(token)).map(({ token, via }) => `${where(unit, line)}: removed class token "${token}" in ${via}`)),

  6: (unit) => unit.elements.filter((element) => element.attrs.has('style') || element.boundAttrs.has('style'))
    .map((element) => `${where(unit, element.line)}: <${element.tag}> has an inline style attribute`),

  7: (unit) => {
    const findings = [];
    for (const element of unit.elements) {
      if (!hasAttr(element, 'data-nav-label')) continue;
      for (let node = element; node; node = node.parent) {
        if (node.tag === 'nav' && node.attrs.get('id') === 'primary-nav') break;
        const hidden = node.tokens.filter(({ token }) => splitToken(token).base === 'hidden' || /^\[display:none\]$/.test(splitToken(token).base));
        for (const { token } of hidden) findings.push(`${where(unit, node.line)}: "${token}" on <${node.tag}>, an ancestor-or-self of the nav label (${where(unit, element.line)}); use sr-only, never display:none`);
        if (node.attrs.has('hidden') || node.boundAttrs.has('hidden')) findings.push(`${where(unit, node.line)}: hidden attribute on <${node.tag}>, an ancestor-or-self of the nav label`);
      }
    }
    return findings;
  },

  8: (unit) => {
    const findings = [];
    for (const { line, tokens } of tokenContexts(unit)) {
      const bases = tokens.map(({ token }) => splitToken(token).base);
      for (const { token } of tokens) {
        const { variants, base } = splitToken(token);
        const at = where(unit, line);
        if (variants.includes('dark')) findings.push(`${at}: "${token}" uses the dark: variant (it follows prefers-color-scheme, not data-theme)`);
        if (/^(?:bg|text|border|ring|fill|stroke|from|to|via|divide|outline|accent|caret|decoration|shadow)-(?:red|orange|amber|yellow|lime|green|emerald|teal|cyan|sky|blue|indigo|violet|purple|fuchsia|pink|rose|slate|gray|zinc|neutral|stone)-\d+(?:\/\d+)?$/.test(base)) findings.push(`${at}: "${token}" is a Tailwind palette utility; use daisyUI semantic colours`);
        if (/^(?:bg|text|border|ring|fill|stroke|from|to|via|divide|outline|shadow)-(?:white|black)(?:\/\d+)?$/.test(base)) findings.push(`${at}: "${token}" is a fixed colour utility`);
        if (/\[(?:[a-z-]+:)?(?:#|rgba?\(|hsla?\(|oklch\(|oklab\(|lab\(|lch\(|color\()/i.test(base)) findings.push(`${at}: "${token}" is an arbitrary colour value`);
        if (/^(?:white|black|red|green|blue|yellow|orange|purple|pink|gray|grey)$/.test(base)) findings.push(`${at}: "${token}" is a named colour used as a class`);
        const untestedVariant = /-soft$|-dash$/.test(base) || ['badge-outline', 'badge-ghost', 'alert-outline'].includes(base);
        if (untestedVariant) findings.push(`${at}: "${token}" is an untested coloured variant (soft/dash/outline/ghost)`);
        if (/^text-(?:secondary|accent|success|warning|info)$/.test(base)) findings.push(`${at}: "${token}" is an untested text colour pair`);
        const alpha = /^(text|border)-([a-z0-9-]+)\/(\d+)$/.exec(base);
        if (alpha) {
          const [, kind, colour, value] = alpha;
          const role = kind === 'border' ? 'control border' : 'muted text';
          if (colour !== 'base-content') findings.push(`${at}: "${token}" is an untested alpha colour (only base-content/NN from the alpha table is allowed)`);
          else if (!ALPHA_TABLE.some((entry) => entry.role === role && entry.alpha === Number(value))) findings.push(`${at}: "${token}" alpha ${value} is not in the ${role} alpha table (${ALPHA_TABLE.filter((e) => e.role === role).map((e) => e.alpha).join(', ')})`);
        }
        if (DAISY_FORBIDDEN.includes(base)) findings.push(`${at}: daisyUI "${token}" is forbidden (low built-in alpha; use plain markup)`);
        else if (!untestedVariant && !variants.includes('placeholder') && isDaisyComponent(base) && !DAISY_ALLOWED.includes(base)) findings.push(`${at}: daisyUI "${token}" is not on the allow-list (add its colour pairs to scripts/theme.test.mjs first)`);
      }
      for (const component of ['btn']) {
        const outlined = bases.includes(`${component}-outline`) || bases.includes(`${component}-ghost`);
        const coloured = bases.filter((base) => base.startsWith(`${component}-`) && COLOR_MODIFIERS.has(base.slice(component.length + 1)));
        if (outlined && coloured.length) findings.push(`${where(unit, line)}: ${component}-outline/ghost combined with ${coloured.join(', ')} is an untested coloured variant`);
      }
    }
    return findings;
  },

  9: (unit) => {
    const findings = [];
    const prefixes = 'btn|badge|alert|bg|text|border|input|select|tooltip|menu|table|card|status';
    const quoted = new RegExp(`['"][^'"]*\\b(?:${prefixes})-['"]\\s*\\+`);
    const template = new RegExp(`\`[^\`]*\\b(?:${prefixes})-\\$\\{`);
    const concat = /['"`]\s*\+|\+\s*['"`]/;
    for (const element of unit.elements) {
      for (const { via, source } of element.classSources) {
        const at = where(unit, element.line);
        if (quoted.test(source) || template.test(source)) findings.push(`${at}: ${via} builds a class name by concatenation: ${source}`);
        else if (concat.test(source)) findings.push(`${at}: ${via} concatenates strings: ${source}`);
      }
    }
    for (const { line, source } of unit.concatenations) findings.push(`${where(unit, line)}: class string built by concatenation or interpolation: ${source}`);
    return findings;
  },

  10: (unit) => {
    const findings = [];
    for (const element of unit.elements) {
      for (const { token } of element.tokens) {
        const { variants } = splitToken(token);
        const index = variants.findIndex((variant) => variant.startsWith('group-data-[collapsed'));
        if (index !== -1 && !variants.slice(0, index).includes('nav')) findings.push(`${where(unit, element.line)}: "${token}" is not guarded by nav: (a stored collapsed state must have no effect below 900px)`);
      }
      if (hasAttr(element, 'data-collapsed') && !classTokens(element).includes('group')) findings.push(`${where(unit, element.line)}: <${element.tag}> carries data-collapsed but not the group class, so every group-data variant matches nothing`);
      if (hasAttr(element, 'data-tip')) findings.push(`${where(unit, element.line)}: data-tip is not allowed (use an aria-hidden tooltip-content child)`);
    }
    return findings;
  },

  11: (unit) => {
    const findings = [];
    const borderAlphas = ALPHA_TABLE.filter((entry) => entry.role === 'control border').map((entry) => entry.alpha);
    const textAlphas = ALPHA_TABLE.filter((entry) => entry.role === 'muted text').map((entry) => entry.alpha);
    for (const element of unit.elements) {
      const tokens = classTokens(element);
      const control = tokens.find((token) => ['input', 'select', 'textarea', 'checkbox', 'radio'].includes(token));
      if (control) {
        const border = tokens.map((token) => /^border-base-content\/(\d+)$/.exec(token)).find(Boolean);
        if (!border) findings.push(`${where(unit, element.line)}: <${element.tag} class="${control}"> has no border-base-content/NN token (the daisyUI default border is 1.23:1 in light)`);
        else if (!borderAlphas.includes(Number(border[1]))) findings.push(`${where(unit, element.line)}: border alpha ${border[1]} is not in the control border alpha table`);
      }
      if (hasAttr(element, 'placeholder')) {
        const placeholder = tokens.map((token) => /^placeholder:text-base-content\/(\d+)$/.exec(token)).find(Boolean);
        if (!placeholder) findings.push(`${where(unit, element.line)}: <${element.tag}> has a placeholder but no placeholder:text-base-content/NN token (daisyUI placeholder is 40% alpha)`);
        else if (!textAlphas.includes(Number(placeholder[1]))) findings.push(`${where(unit, element.line)}: placeholder alpha ${placeholder[1]} is not in the muted text alpha table`);
      }
    }
    return findings;
  },
};

function isDaisyComponent(base) {
  if (TAILWIND_OWNED_PREFIX.test(base)) return false;
  if (!daisyClasses.has(base) || !DAISY_ROOTS.has(base.split('-')[0])) return false;
  return !TAILWIND_OWNED_EXACT.has(base) || base === 'table';
}

/** Legacy group: a file is "token-free" when checks 4, 5 and 6 report nothing. */
const LEGACY = [4, 5, 6];
const TEMPLATE_CHECKS = [4, 5, 6, 7, 8, 9, 10, 11];

export const runChecks = (unit, numbers = TEMPLATE_CHECKS) => numbers.flatMap((number) => CHECKS[number](unit).map((finding) => `check ${number}: ${finding}`));

// ---------- the ratchet ----------

const units = candidateUnits();
const unitByFile = new Map(units.map((unit) => [unit.file, unit]));
const checkedUnits = () => (ENABLE_TEMPLATE_CHECKS ? units : MIGRATED_FILES.map((file) => {
  assert.ok(unitByFile.has(file), `MIGRATED_FILES lists ${file}, which is not in the candidate set`);
  return unitByFile.get(file);
}));

test('candidate set parses without template errors', () => {
  assert.deepEqual(units.flatMap((unit) => unit.problems), []);
  assert.ok(units.some((unit) => unit.file === 'src/app/app.html'));
});

test('ratchet: MIGRATED_FILES entries are unique, sorted, exist and are in the candidate set', () => {
  assert.deepEqual(MIGRATED_FILES, [...new Set(MIGRATED_FILES)].sort());
  for (const file of MIGRATED_FILES) assert.ok(unitByFile.has(file), `${file} is not in the candidate set`);
});

test('ratchet: while the flag is false, no migrated file still has a legacy finding', () => {
  if (ENABLE_TEMPLATE_CHECKS) return;
  assert.deepEqual(MIGRATED_FILES.flatMap((file) => runChecks(unitByFile.get(file), LEGACY)), []);
});

test('self-enforcing flip: the flag cannot stay false once every file is migrated, and no token-free file is forgotten', () => {
  const legacyFree = units.filter((unit) => runChecks(unit, LEGACY).length === 0).map((unit) => unit.file);
  const legacyBearing = units.filter((unit) => runChecks(unit, LEGACY).length > 0).map((unit) => unit.file);
  if (ENABLE_TEMPLATE_CHECKS) {
    assert.deepEqual(legacyBearing, [], 'ENABLE_TEMPLATE_CHECKS is true, so no file may keep a removed token, style declaration or inline style');
    return;
  }
  assert.ok(legacyBearing.length > 0, 'every file is migrated: set ENABLE_TEMPLATE_CHECKS = true and replace MIGRATED_FILES with the candidate set');
  assert.deepEqual(legacyFree.filter((file) => !MIGRATED_FILES.includes(file)), [], 'token-free files missing from MIGRATED_FILES (a forgotten ratchet entry)');
});

for (const number of TEMPLATE_CHECKS) {
  test(`check ${number}: template contract over the checked files`, () => {
    assert.deepEqual(checkedUnits().flatMap((unit) => CHECKS[number](unit)), []);
  });
}

test('check 1: no *.scss under src (once template checks are enabled)', () => {
  if (!ENABLE_TEMPLATE_CHECKS) return;
  const scss = walk('src/', (name) => name.endsWith('.scss'));
  assert.deepEqual(scss, []);
});

// ---------- spec lint: no optional chaining on query results (always on) ----------

// A migrated spec reads its hooks through query()/queryAll() from src/app/testing/query.ts, which throw on a missing
// element. `query(...)?.x` or `const el = query(...); el?.x` would turn a missing element back into a silent skip.
const QUERY_HELPERS = new Set(['query', 'queryAll']);

/** Findings for `?.` applied to a query()/queryAll() result, directly or through a const bound to one, in one spec's source. */
export function specLint(text, file) {
  const sourceFile = ts.createSourceFile(file, text, ts.ScriptTarget.Latest, true);
  const lineOf = (node) => sourceFile.getLineAndCharacterOfPosition(node.getStart(sourceFile)).line + 1;
  const unwrap = (node) => {
    let current = node;
    while (ts.isParenthesizedExpression(current) || ts.isNonNullExpression(current) || ts.isAsExpression(current) || ts.isElementAccessExpression(current)) current = current.expression;
    return current;
  };
  const isHelperCall = (node) => {
    const target = unwrap(node);
    return ts.isCallExpression(target) && ts.isIdentifier(target.expression) && QUERY_HELPERS.has(target.expression.text);
  };
  const bound = new Set();
  const collect = (node) => {
    if (ts.isVariableDeclaration(node) && ts.isIdentifier(node.name) && node.initializer && isHelperCall(node.initializer)) bound.add(node.name.text);
    ts.forEachChild(node, collect);
  };
  collect(sourceFile);
  const findings = [];
  const visit = (node) => {
    if ((ts.isPropertyAccessExpression(node) || ts.isElementAccessExpression(node) || ts.isCallExpression(node)) && node.questionDotToken) {
      const target = unwrap(node.expression);
      if (isHelperCall(node.expression) || (ts.isIdentifier(target) && bound.has(target.text))) findings.push(`${file}:${lineOf(node)}: "?." on a query()/queryAll() result (a missing element must throw, not skip): ${node.getText(sourceFile)}`);
    }
    ts.forEachChild(node, visit);
  };
  visit(sourceFile);
  return findings;
}

/** Specs held to the lint: the spec of every migrated .ts file, plus any spec that imports the query helpers. */
function migratedSpecs() {
  const specs = walk('src/', isSpec);
  const wanted = new Set(MIGRATED_FILES.filter((file) => file.endsWith('.ts')).map((file) => file.replace(/\.ts$/, '.spec.ts')));
  return specs.filter((file) => wanted.has(file) || /testing\/query['"]/.test(read(file))).sort();
}

test('spec lint: migrated specs never use ?. on a query() or queryAll() result', () => {
  const specs = migratedSpecs();
  assert.ok(specs.some((file) => file.startsWith('src/app/ui/')), 'the ui component specs must be linted');
  assert.deepEqual(specs.flatMap((file) => specLint(read(file), file)), []);
});

test('negative: the spec lint reports ?. on a query result, directly, through a const, an index and a cast, and accepts safe forms', () => {
  const bad = [
    "const a = query(fixture, 'x')?.textContent;",
    "const el = query(fixture, 'x'); expect(el?.id).toBe('y');",
    "const list = queryAll(fixture, 'x', { min: 1 }); list[0]?.click();",
    "(query(fixture, 'x') as HTMLElement)?.focus();",
    "query(fixture, 'x')!?.click?.();",
  ];
  for (const source of bad) assert.ok(specLint(source, 'fixture.spec.ts').length >= 1, source);
  assert.equal(specLint("const el = query(fixture, 'x'); expect(el?.id).toBe('y'); expect(el?.title).toBe('z');", 'fixture.spec.ts').length, 2);
  const good = [
    "const a = query(fixture, 'x').textContent;",
    "const raw = fixture.nativeElement.querySelector('x'); expect(raw?.id).toBe('y');",
    "const el = query(fixture, 'x'); const other = null; expect(other?.id).toBeUndefined();",
    "const list = queryAll(fixture, 'x'); expect(list.length).toBe(1);",
  ];
  for (const source of good) assert.deepEqual(specLint(source, 'fixture.spec.ts'), [], source);
});

// ---------- always-on checks 2 and 3 ----------

test('check 2: angular.json has no inlineStyleLanguage and styles is exactly ["src/styles.css"]', () => {
  const config = JSON.parse(read('angular.json'));
  const stylesLists = [];
  const search = (node, path) => {
    if (node && typeof node === 'object' && !Array.isArray(node)) {
      for (const [key, value] of Object.entries(node)) {
        assert.notEqual(key, 'inlineStyleLanguage', `inlineStyleLanguage at ${path}`);
        if (key === 'styles' && Array.isArray(value)) stylesLists.push({ path, value });
        search(value, `${path}.${key}`);
      }
    }
  };
  search(config, 'angular.json');
  assert.ok(stylesLists.length > 0, 'no styles option found');
  for (const { path, value } of stylesLists) assert.deepEqual(value, ['src/styles.css'], path);
});

const MAX_APPLY = 5;

test('check 3: src/styles.css wires Tailwind and daisyUI with both themes and a small @apply budget', () => {
  const css = read('src/styles.css').replace(/("(?:[^"\\]|\\.)*")|\/\*[\s\S]*?\*\//g, '$1');
  assert.match(css, /@import\s+"tailwindcss"/);
  assert.match(css, /@plugin\s+"daisyui"/);
  assert.match(css, /name:\s*"light"/);
  assert.match(css, /name:\s*"dark"/);
  assert.match(css, /--breakpoint-nav:\s*900px/);
  const applies = css.match(/@apply\b/g) ?? [];
  assert.ok(applies.length <= MAX_APPLY, `${applies.length} @apply rules, budget is ${MAX_APPLY}`);
});

// ---------- removed-selector fixture (always on) ----------

const fixture = JSON.parse(read('scripts/fixtures/removed-scss-selectors.json'));

test('fixture: removed SCSS selectors are present, sorted and contain the known names', () => {
  assert.ok(Array.isArray(fixture) && fixture.length > 0);
  assert.deepEqual(fixture, [...fixture].sort());
  for (const name of ['panel', 'nav-toggle', 'data', 'muted', 'status-paused']) assert.ok(fixture.includes(name), `fixture lacks ${name}`);
});

test('fixture: every class the old SCSS defined is on the removed list or the exclusions (the list cannot drift)', () => {
  const known = new Set([...REMOVED_TOKENS, ...REMOVED_EXCLUSIONS]);
  assert.deepEqual(fixture.filter((name) => !known.has(name)), []);
});

test('removed list and exclusions are disjoint and contain no duplicates', () => {
  assert.equal(new Set(REMOVED_TOKENS).size, REMOVED_TOKENS.length);
  assert.deepEqual(REMOVED_TOKENS.filter((token) => REMOVED_EXCLUSIONS.includes(token)), []);
});

// ---------- negative fixtures: every scan must report what it exists to catch ----------

const scan = (html, numbers) => {
  const { elements, problems } = scanTemplate(html, 'fixture.html');
  assert.deepEqual(problems, []);
  return runChecks({ file: 'fixture.html', elements, classStrings: [], styleDeclarations: [], concatenations: [] }, numbers);
};

test('negative (a): a hidden wrapper above the nav label inside @if fails check 7', () => {
  const findings = scan('<nav id="primary-nav"><ul>@if (ok) {<li><span class="hidden"><a data-nav-label>Home</a></span></li>}</ul></nav>', [7]);
  assert.equal(findings.length, 1);
  assert.match(findings[0], /check 7: .*"hidden" on <span>/);
});

test('negative (b): the <nav id="primary-nav"> element itself with max-nav:hidden passes check 7, the label with it does not', () => {
  assert.deepEqual(scan('<nav id="primary-nav" class="max-nav:hidden"><a data-nav-label class="nav:group-data-[collapsed=true]:sr-only">Home</a></nav>', [7]), []);
  assert.equal(scan('<nav id="primary-nav"><a data-nav-label class="max-nav:hidden">Home</a></nav>', [7]).length, 1);
  assert.equal(scan('<nav id="primary-nav"><a class="hidden"><span data-nav-label>Home</span></a></nav>', [7]).length, 1);
});

test('negative (c): an <input> without the border token inside @for fails check 11, a correct one passes', () => {
  const failing = scan('@for (x of xs; track x) {<input class="input" />}', [11]);
  assert.equal(failing.length, 1);
  assert.match(failing[0], /check 11: .*no border-base-content\/NN/);
  assert.deepEqual(scan('<input class="input border-base-content/55" />', [11]), []);
  assert.equal(scan('<input class="input border-base-content/12" />', [11]).length, 1);
  assert.equal(scan('<input class="input border-base-content/55" placeholder="https://" />', [11]).length, 1);
  assert.deepEqual(scan('<input class="input border-base-content/55 placeholder:text-base-content/70" placeholder="https://" />', [11]), []);
});

test('negative (d): a group-data collapsed variant without nav: fails check 10, and so do a missing group class and data-tip', () => {
  const findings = scan('<div class="group" [attr.data-collapsed]="c"><span class="group-data-[collapsed=true]:sr-only">x</span></div>', [10]);
  assert.equal(findings.length, 1);
  assert.match(findings[0], /not guarded by nav:/);
  assert.deepEqual(scan('<div class="group" [attr.data-collapsed]="c"><span class="nav:group-data-[collapsed=true]:sr-only">x</span></div>', [10]), []);
  assert.equal(scan('<div [attr.data-collapsed]="c"></div>', [10]).length, 1);
  assert.equal(scan('<a data-tip="Home"></a>', [10]).length, 1);
});

test('negative (e): concatenated class strings fail check 9, literal lookups and [class.x] pass', () => {
  assert.equal(scan(`<span [class]="'badge status-' + row.status"></span>`, [9]).length, 1);
  assert.equal(scan('<span [class]="`btn-${kind}`"></span>', [9]).length, 1);
  assert.equal(scan(`<span [class]="a + ' b'"></span>`, [9]).length, 1);
  assert.deepEqual(scan(`<span [class]="open ? 'badge-success' : 'badge-neutral'"></span>`, [9]), []);
  assert.deepEqual(scan('<span [class.badge-success]="ok"></span>', [9]), []);
});

test('negative: removed tokens, dark:, palette utilities, arbitrary colours and inline style are all reported', () => {
  assert.equal(scan('<div class="panel muted"></div>', [5]).length, 2);
  assert.equal(scan('<div [class.collapsed]="c"></div>', [5]).length, 1);
  assert.equal(scan('<p class="error">e</p>', [5]).length, 1);
  assert.deepEqual(scan('<table class="table"><tr class="active"></tr></table><div class="skeleton"></div>', [5]), []);
  assert.equal(scan('<div class="dark:bg-base-100"></div>', [8]).length, 1);
  assert.equal(scan('<div class="bg-blue-500 text-white"></div>', [8]).length, 2);
  assert.equal(scan('<div class="bg-[#fff]"></div>', [8]).length, 1);
  assert.equal(scan('<div style="color: red"></div>', [6]).length, 1);
  assert.deepEqual(scan('<div class="bg-base-100 text-base-content"></div>', [6, 8]), []);
});

test('negative: alpha utilities must be in the table; untested coloured variants, forbidden and unlisted daisyUI classes fail check 8', () => {
  assert.deepEqual(scan('<p class="text-base-content/70 border-base-content/55"></p>', [8]), []);
  assert.equal(scan('<p class="text-base-content/50"></p>', [8]).length, 1);
  assert.equal(scan('<p class="border-base-content/70"></p>', [8]).length, 1);
  assert.equal(scan('<p class="text-primary/60"></p>', [8]).length, 1);
  assert.equal(scan('<div class="stats"></div>', [8]).length, 1);
  assert.equal(scan('<span class="label"></span>', [8]).length, 1);
  assert.equal(scan('<li class="menu-title"></li>', [8]).length, 1);
  assert.equal(scan('<span class="badge badge-soft"></span>', [8]).length, 1);
  assert.equal(scan('<button class="btn btn-outline btn-primary"></button>', [8]).length, 1);
  assert.equal(scan('<div class="drawer"></div>', [8]).length, 1);
  assert.equal(scan('<div class="modal"></div>', [8]).length, 1);
  assert.equal(scan('<button class="btn btn-error"></button>', [8]).length, 1);
  assert.deepEqual(scan('<button class="btn btn-neutral btn-sm"></button><span class="table-auto select-none flex"></span>', [8]), []);
});

test('negative: ts class strings and component declarations are scanned', () => {
  const info = scanTypeScript(
    "@Component({ selector: 'a', template: '<p class=\"panel\">x</p>', styles: ['p { color: red }'], host: { class: 'block text-blue-500' } })\n" +
    "class A {}\nconst LOOKUP_CLASSES = { a: 'badge-success', b: 'bg-' + kind };\n", 'fixture.ts');
  assert.equal(info.isComponent, true);
  assert.equal(info.templates.length, 1);
  const unit = { ...info, elements: scanTemplate(info.templates[0].text, 'fixture.ts').elements, problems: [] };
  assert.equal(runChecks(unit, [4]).length, 1);
  assert.equal(runChecks(unit, [5]).length, 1);
  assert.equal(runChecks(unit, [8]).length, 1);
  assert.equal(runChecks(unit, [9]).length, 1);
});

test('splitToken understands stacked and arbitrary variants', () => {
  assert.deepEqual(splitToken('nav:group-data-[collapsed=true]:sr-only'), { variants: ['nav', 'group-data-[collapsed=true]'], base: 'sr-only' });
  assert.deepEqual(splitToken('!hidden'), { variants: [], base: 'hidden' });
  assert.deepEqual(splitToken('bg-[color:#fff]'), { variants: [], base: 'bg-[color:#fff]' });
});

test('daisyUI roots are read from node_modules and the allow-list only holds real daisyUI classes', () => {
  for (const root of DAISY_ROOTS) assert.ok(daisyClasses.has(root) || [...daisyClasses].some((name) => name.startsWith(`${root}-`)), `daisyUI no longer defines ${root}`);
  for (const name of ['btn', 'btn-primary', 'menu-active', 'tooltip-content', 'drawer', 'modal', 'stats']) assert.ok(isDaisyComponent(name), name);
  for (const name of DAISY_ALLOWED) assert.ok(isDaisyComponent(name), `${name} is not recognised as a daisyUI class`);
  for (const name of ['flex', 'sr-only', 'overflow-x-auto', 'select-none', 'table-auto', 'collapse', 'list-disc']) assert.equal(isDaisyComponent(name), false, name);
});
