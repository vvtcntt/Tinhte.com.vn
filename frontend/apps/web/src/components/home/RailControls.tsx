'use client';

import { useEffect, useState } from 'react';

type RailState = { scrollable: boolean; atStart: boolean; atEnd: boolean };

/**
 * Nút ‹ › cho dải cuộn ngang (`targetId`). Chỉ hiện khi đã chạy JS và dải thực sự tràn;
 * không có JS thì dải vẫn cuộn được bằng chạm/bàn phím (FR-VID-011: không autoplay).
 */
export function RailControls({ targetId, label }: { targetId: string; label: string }) {
  const [state, setState] = useState<RailState>({ scrollable: false, atStart: true, atEnd: true });

  useEffect(() => {
    const rail = document.getElementById(targetId);
    if (!rail) return;
    const update = () => {
      const max = rail.scrollWidth - rail.clientWidth;
      setState({ scrollable: max > 1, atStart: rail.scrollLeft <= 1, atEnd: rail.scrollLeft >= max - 1 });
    };
    update();
    rail.addEventListener('scroll', update, { passive: true });
    const observer = new ResizeObserver(update);
    observer.observe(rail);
    return () => {
      rail.removeEventListener('scroll', update);
      observer.disconnect();
    };
  }, [targetId]);

  if (!state.scrollable) return null;

  const scroll = (direction: 1 | -1) => {
    const rail = document.getElementById(targetId);
    if (!rail) return;
    const reduceMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    rail.scrollBy({ left: direction * rail.clientWidth * 0.9, behavior: reduceMotion ? 'auto' : 'smooth' });
  };

  return (
    <div className="rail-controls" role="group" aria-label={label}>
      <button type="button" className="rail-controls__button" aria-controls={targetId} disabled={state.atStart} onClick={() => scroll(-1)}>
        <Chevron direction="left" />
        <span className="visually-hidden">Trước</span>
      </button>
      <button type="button" className="rail-controls__button" aria-controls={targetId} disabled={state.atEnd} onClick={() => scroll(1)}>
        <Chevron direction="right" />
        <span className="visually-hidden">Sau</span>
      </button>
    </div>
  );
}

function Chevron({ direction }: { direction: 'left' | 'right' }) {
  return (
    <svg width="18" height="18" viewBox="0 0 24 24" aria-hidden="true" focusable="false">
      <path
        fill="none"
        stroke="currentColor"
        strokeWidth="2.25"
        strokeLinecap="round"
        strokeLinejoin="round"
        d={direction === 'left' ? 'm15 6-6 6 6 6' : 'm9 6 6 6-6 6'}
      />
    </svg>
  );
}
