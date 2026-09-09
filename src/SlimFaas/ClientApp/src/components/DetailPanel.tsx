import { useEffect, useId, useRef, type ReactNode } from 'react';
import Icon from './Icon';

interface Props {
  title: string;
  eyebrow: string;
  children: ReactNode;
  onClose: () => void;
  fill?: boolean;
  fallbackFocus?: () => HTMLElement | null;
}

/** Shared modal drawer. Native dialog supplies focus containment and an inert backdrop. */
export default function DetailPanel({ title, eyebrow, children, onClose, fill = false, fallbackFocus }: Props) {
  const dialog = useRef<HTMLDialogElement>(null);
  const titleId = useId();
  const pointerOutside = useRef(false);
  useEffect(() => {
    const element = dialog.current!;
    const trigger = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    element.showModal();
    return () => {
      element.close();
      const target = trigger?.isConnected && trigger !== document.body ? trigger : fallbackFocus?.();
      target?.focus({ preventScroll: true });
    };
  }, []);

  const outside = (event: { clientX: number; clientY: number }) => {
    const rect = dialog.current!.getBoundingClientRect();
    return event.clientX < rect.left || event.clientX > rect.right || event.clientY < rect.top || event.clientY > rect.bottom;
  };
  return <dialog ref={dialog} className="detail-panel" aria-labelledby={titleId}
    onCancel={event => { event.preventDefault(); onClose(); }}
    onKeyDown={event => { if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); onClose(); } }}
    onPointerDown={event => { pointerOutside.current = event.target === dialog.current && outside(event); }}
    onClick={event => { if (pointerOutside.current && event.target === dialog.current && outside(event)) onClose(); }}>
    <div className="detail-panel__header">
      <div><span className="detail-panel__eyebrow">{eyebrow}</span><h2 className="detail-panel__title" id={titleId}>{title}</h2></div>
      <button className="button button--quiet" type="button" aria-label="Close details" onClick={onClose}><Icon name="close" /></button>
    </div>
    <div className={`detail-panel__body${fill ? ' detail-panel__body--fill' : ''}`}>{children}</div>
  </dialog>;
}
