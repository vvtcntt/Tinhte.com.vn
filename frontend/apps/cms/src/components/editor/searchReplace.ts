// Tìm và thay thế trong trình soạn (Ctrl+F / Ctrl+H). Tô sáng kết quả bằng decoration — không đổi tài liệu cho tới khi thay.
import { Extension } from '@tiptap/core';
import type { Node as PmNode } from '@tiptap/pm/model';
import { Plugin, PluginKey, type EditorState, type Transaction } from '@tiptap/pm/state';
import { Decoration, DecorationSet } from '@tiptap/pm/view';

export interface SearchMatch {
  from: number;
  to: number;
}

export interface SearchState {
  term: string;
  caseSensitive: boolean;
  wholeWord: boolean;
  matches: SearchMatch[];
  index: number;
  decorations: DecorationSet;
}

export const searchKey = new PluginKey<SearchState>('tinhteSearch');

type Meta = Partial<Pick<SearchState, 'term' | 'caseSensitive' | 'wholeWord' | 'index'>>;

const escapeRegExp = (s: string) => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

/** Tìm trong từng khối văn bản (không vượt qua ranh giới đoạn). */
export function findMatches(doc: PmNode, term: string, caseSensitive: boolean, wholeWord: boolean): SearchMatch[] {
  if (!term) return [];
  const source = wholeWord ? `(?<![\\p{L}\\p{N}_])${escapeRegExp(term)}(?![\\p{L}\\p{N}_])` : escapeRegExp(term);
  const re = new RegExp(source, caseSensitive ? 'gu' : 'giu');
  const matches: SearchMatch[] = [];
  doc.descendants((node, pos) => {
    if (!node.isTextblock) return true;
    // Ghép chữ của khối, nhớ vị trí tài liệu của từng ký tự (nút inline không phải chữ chiếm 1 vị trí).
    let text = '';
    const map: number[] = [];
    node.forEach((child, offset) => {
      const start = pos + 1 + offset;
      if (child.isText) {
        for (let i = 0; i < child.text!.length; i++) map.push(start + i);
        text += child.text;
      } else {
        map.push(start);
        text += '￼';
      }
    });
    for (const m of text.matchAll(re)) {
      if (!m[0].length) continue;
      const from = map[m.index!];
      const to = map[m.index! + m[0].length - 1] + 1;
      matches.push({ from, to });
    }
    return false;
  });
  return matches;
}

function build(doc: PmNode, s: Omit<SearchState, 'matches' | 'decorations'>): SearchState {
  const matches = findMatches(doc, s.term, s.caseSensitive, s.wholeWord);
  const index = matches.length ? Math.min(Math.max(0, s.index), matches.length - 1) : 0;
  const decorations = DecorationSet.create(
    doc,
    matches.map((m, i) => Decoration.inline(m.from, m.to, { class: i === index ? 'search-hit current' : 'search-hit' })),
  );
  return { ...s, matches, index, decorations };
}

export function getSearchState(state: EditorState): SearchState | undefined {
  return searchKey.getState(state);
}

declare module '@tiptap/core' {
  interface Commands<ReturnType> {
    tinhteSearch: {
      setSearch: (meta: Meta) => ReturnType;
      searchStep: (delta: 1 | -1) => ReturnType;
      replaceCurrent: (replacement: string) => ReturnType;
      replaceAll: (replacement: string) => ReturnType;
    };
  }
}

export const SearchReplace = Extension.create({
  name: 'tinhteSearch',

  addCommands() {
    return {
      setSearch: (meta: Meta) => ({ tr, dispatch }: { tr: Transaction; dispatch?: (tr: Transaction) => void }) => {
        if (dispatch) dispatch(tr.setMeta(searchKey, meta));
        return true;
      },
      searchStep: (delta: 1 | -1) => ({ state, tr, dispatch }: { state: EditorState; tr: Transaction; dispatch?: (tr: Transaction) => void }) => {
        const s = searchKey.getState(state);
        if (!s || !s.matches.length) return false;
        const index = (s.index + delta + s.matches.length) % s.matches.length;
        if (dispatch) {
          const m = s.matches[index];
          dispatch(tr.setMeta(searchKey, { index }));
          scrollTo(m);
        }
        return true;
      },
      replaceCurrent: (replacement: string) => ({ state, tr, dispatch }: { state: EditorState; tr: Transaction; dispatch?: (tr: Transaction) => void }) => {
        const s = searchKey.getState(state);
        if (!s || !s.matches.length) return false;
        const m = s.matches[s.index];
        if (dispatch) {
          if (replacement) tr.insertText(replacement, m.from, m.to);
          else tr.delete(m.from, m.to);
          dispatch(tr.setMeta(searchKey, { index: s.index }));
        }
        return true;
      },
      replaceAll: (replacement: string) => ({ state, tr, dispatch }: { state: EditorState; tr: Transaction; dispatch?: (tr: Transaction) => void }) => {
        const s = searchKey.getState(state);
        if (!s || !s.matches.length) return false;
        if (dispatch) {
          // Thay từ cuối lên đầu để vị trí phía trước không lệch.
          for (const m of [...s.matches].reverse()) {
            if (replacement) tr.insertText(replacement, m.from, m.to);
            else tr.delete(m.from, m.to);
          }
          dispatch(tr.setMeta(searchKey, { index: 0 }));
        }
        return true;
      },
    } as never;
  },

  addProseMirrorPlugins() {
    return [
      new Plugin<SearchState>({
        key: searchKey,
        state: {
          init: (_config, state) => build(state.doc, { term: '', caseSensitive: false, wholeWord: false, index: 0 }),
          apply: (tr, prev, _old, state) => {
            const meta = tr.getMeta(searchKey) as Meta | undefined;
            if (!meta && !tr.docChanged) return prev;
            return build(state.doc, { term: prev.term, caseSensitive: prev.caseSensitive, wholeWord: prev.wholeWord, index: prev.index, ...meta });
          },
        },
        props: {
          decorations: (state) => searchKey.getState(state)?.decorations ?? DecorationSet.empty,
        },
      }),
    ];
  },
});

/** Cuộn kết quả hiện tại vào giữa vùng soạn (sau khi decoration đã vẽ). */
function scrollTo(_m: SearchMatch) {
  if (typeof window === 'undefined') return;
  window.requestAnimationFrame(() => document.querySelector('.rich-editor .search-hit.current')?.scrollIntoView({ block: 'center' }));
}
