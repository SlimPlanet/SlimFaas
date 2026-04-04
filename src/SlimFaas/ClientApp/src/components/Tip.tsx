import React from 'react';
import { Tooltip } from 'react-tooltip';

let tipCounter = 0;

interface TipProps {
  /** Text (or JSX) shown as the visible anchor */
  children: React.ReactNode;
  /** Tooltip content — supports newlines via \n */
  text: string;
  /** Show the ⓘ icon next to the children. Defaults to false. */
  showIcon?: boolean;
}

/** Wraps children with a tooltip. Optionally shows an "ℹ" icon. */
const Tip: React.FC<TipProps> = ({ children, text, showIcon = false }) => {
  const id = React.useMemo(() => `tip-${++tipCounter}`, []);
  return (
    <>
      <span data-tooltip-id={id} className="tip-anchor">
        {children}
        {showIcon && <span className="tip-icon" aria-label="info">ⓘ</span>}
      </span>
      <Tooltip id={id} className="tip-popup" place="top" delayShow={150}>
        {text.split('\n').map((line, i) => (
          <React.Fragment key={i}>
            {line}
            {i < text.split('\n').length - 1 && <br />}
          </React.Fragment>
        ))}
      </Tooltip>
    </>
  );
};

export default Tip;

